import {
  PHOTON_CAD_PROJECT_CONTRACT_VERSION,
  PHOTON_CAD_PROJECT_LIMITS,
  isPhotonCadProjectHandle,
  isPhotonCadReopenHandle,
  isPhotonCadSaveReceiptHandle,
  isPhotonCadWorkspaceHandle,
  validatePhotonCadProjectCloseRequest,
  validatePhotonCadProjectCreateRequest,
  validatePhotonCadProjectDocumentMetadata,
  validatePhotonCadProjectOpenRequest,
  validatePhotonCadProjectPickerRequest,
  validatePhotonCadProjectRefreshRequest,
  validatePhotonCadProjectReopenRequest,
  validatePhotonCadProjectSaveAsRequest,
  validatePhotonCadProjectSaveRequest,
  validatePhotonCadReopenMetadata,
  validatePhotonCadSaveReceiptBinding,
  type PhotonCadProjectCloseRequest,
  type PhotonCadProjectCloseResult,
  type PhotonCadProjectController,
  type PhotonCadProjectCreateRequest,
  type PhotonCadProjectDocument,
  type PhotonCadProjectLoadRequest,
  type PhotonCadProjectLoadResult,
  type PhotonCadProjectOpenRequest,
  type PhotonCadProjectOperation,
  type PhotonCadProjectPickerRequest,
  type PhotonCadProjectPickerResult,
  type PhotonCadProjectRefreshRequest,
  type PhotonCadProjectReopenMetadata,
  type PhotonCadProjectReopenRequest,
  type PhotonCadProjectSaveAsRequest,
  type PhotonCadProjectSaveReceipt,
  type PhotonCadProjectSaveRequest,
  type PhotonCadProjectSaveResult,
} from './PhotonCadProjectContract'
import {
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  isPhotonCadUtcTimestamp,
  type PhotonCadBomRow,
} from './PhotonCadContract'
import { normalizePhotonCadProjectSnapshot } from './DesktopPhotonCadClient'

export const PHOTON_CAD_PROJECT_DESKTOP_PROTOCOL_VERSION = 1 as const

type ProjectRequestKind = PhotonCadProjectOperation
type ProjectRequest =
  | PhotonCadProjectPickerRequest
  | PhotonCadProjectLoadRequest
  | PhotonCadProjectSaveRequest
  | PhotonCadProjectSaveAsRequest
  | PhotonCadProjectCloseRequest
type ProjectResult = PhotonCadProjectPickerResult | PhotonCadProjectLoadResult | PhotonCadProjectSaveResult | PhotonCadProjectCloseResult
type PendingLane = 'picker' | 'open' | 'write'

const DEFAULT_TIMEOUTS: Record<ProjectRequestKind, number> = {
  picker: 600_000,
  create: 600_000,
  open: 600_000,
  reopen: 600_000,
  refresh: 120_000,
  save: 600_000,
  'save-as': 600_000,
  close: 120_000,
}
const MAXIMUM_TIMEOUT_MS = 600_000
const MAXIMUM_REQUEST_HISTORY = 50_000

export type PhotonCadProjectWebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

export type PhotonCadProjectDesktopRequest =
  | ({ type: 'photonCad.project.picker'; version: 1 } & PhotonCadProjectPickerRequest)
  | ({ type: 'photonCad.project.create'; version: 1 } & PhotonCadProjectCreateRequest)
  | ({ type: 'photonCad.project.open'; version: 1 } & PhotonCadProjectOpenRequest)
  | ({ type: 'photonCad.project.reopen'; version: 1 } & PhotonCadProjectReopenRequest)
  | ({ type: 'photonCad.project.refresh'; version: 1 } & PhotonCadProjectRefreshRequest)
  | ({ type: 'photonCad.project.save'; version: 1 } & PhotonCadProjectSaveRequest)
  | ({ type: 'photonCad.project.saveAs'; version: 1 } & PhotonCadProjectSaveAsRequest)
  | ({ type: 'photonCad.project.close'; version: 1 } & PhotonCadProjectCloseRequest)
  | { type: 'photonCad.project.cancel'; version: 1; requestId: string; targetRequestId: string; operation: ProjectRequestKind }

export type PhotonCadProjectHostFrame =
  | { type: 'picker'; value: PhotonCadProjectPickerResult }
  | { type: 'create' | 'open' | 'reopen' | 'refresh'; value: PhotonCadProjectLoadResult }
  | { type: 'save' | 'save-as'; value: PhotonCadProjectSaveResult }
  | { type: 'close'; value: PhotonCadProjectCloseResult }
  | { type: 'error'; requestId: string; code: string; retryable: boolean }

type PendingRequest = {
  kind: ProjectRequestKind
  lane: PendingLane
  request: ProjectRequest
  resolve: (result: ProjectResult) => void
  reject: (error: Error) => void
  timeout: ReturnType<typeof setTimeout>
}

export type DesktopPhotonCadProjectClientOptions = {
  getBridge?: () => PhotonCadProjectWebViewBridge | null
  createRequestId?: (kind: string, sequence: number) => string
  requestTimeoutMs?: number | Partial<Record<ProjectRequestKind, number>>
}

function browserBridge(): PhotonCadProjectWebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: PhotonCadProjectWebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function revision(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null
}

function reason(value: unknown): string | null {
  return isPhotonCadIdentifier(value) ? value : null
}

function displayLabel(value: unknown, maximum: number = PHOTON_CAD_PROJECT_LIMITS.pickerLabel): string | null {
  if (!isPhotonCadSafeText(value, maximum, true) || /[\\/]/u.test(value) || /^[A-Za-z]:/u.test(value)) return null
  return value
}

function normalizeBomRow(value: unknown): PhotonCadBomRow | null {
  const raw = record(value)
  if (!raw || !isPhotonCadSafeText(raw.partNumber, PHOTON_CAD_PROJECT_LIMITS.title, true)
    || !isPhotonCadSafeText(raw.description, 2_048)
    || typeof raw.quantity !== 'number' || !Number.isFinite(raw.quantity) || raw.quantity <= 0 || raw.quantity > 1_000_000_000
    || (raw.unit !== 'each' && raw.unit !== 'length') || !isPhotonCadIdentifier(raw.sourceEntityId)) return null
  return { partNumber: raw.partNumber, description: raw.description, quantity: raw.quantity, unit: raw.unit, sourceEntityId: raw.sourceEntityId }
}

export function normalizePhotonCadProjectDocument(value: unknown): PhotonCadProjectDocument | null {
  const raw = record(value)
  const snapshot = normalizePhotonCadProjectSnapshot(raw?.snapshot)
  const lastSavedRevision = revision(raw?.lastSavedRevision)
  const displayName = displayLabel(raw?.displayName, PHOTON_CAD_PROJECT_LIMITS.title)
  if (!raw || raw.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION
    || !isPhotonCadWorkspaceHandle(raw.workspaceHandle) || !isPhotonCadProjectHandle(raw.projectHandle)
    || !displayName || !snapshot
    || !isPhotonCadDigest(raw.contentDigest) || !isPhotonCadDigest(raw.lastSavedContentDigest) || !isPhotonCadDigest(raw.bomDigest)
    || !isPhotonCadUtcTimestamp(raw.openedAtUtc) || lastSavedRevision === null
    || !Array.isArray(raw.bom) || raw.bom.length > PHOTON_CAD_PROJECT_LIMITS.bomRows) return null
  const bom = raw.bom.map(normalizeBomRow)
  if (bom.some((row) => row === null)) return null
  const document: PhotonCadProjectDocument = {
    contractVersion: PHOTON_CAD_PROJECT_CONTRACT_VERSION,
    workspaceHandle: raw.workspaceHandle,
    projectHandle: raw.projectHandle,
    displayName,
    snapshot,
    contentDigest: raw.contentDigest.toLowerCase(),
    lastSavedContentDigest: raw.lastSavedContentDigest.toLowerCase(),
    bomDigest: raw.bomDigest.toLowerCase(),
    bom: bom as PhotonCadBomRow[],
    openedAtUtc: raw.openedAtUtc,
    lastSavedRevision,
  }
  return validatePhotonCadProjectDocumentMetadata(document) ? document : null
}

function normalizePickerResult(value: unknown): PhotonCadProjectPickerResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['selected', 'cancelled', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'selected') {
    const label = displayLabel(raw.label)
    if (!isPhotonCadWorkspaceHandle(raw.workspaceHandle) || !label) return null
    return { contractVersion: 1, requestId: raw.requestId, status: 'selected', reason: raw.reason as string, workspaceHandle: raw.workspaceHandle, label }
  }
  if (raw.workspaceHandle !== undefined || raw.label !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadProjectPickerResult['status'], reason: raw.reason as string }
}

function normalizeLoadResult(value: unknown): PhotonCadProjectLoadResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['opened', 'cancelled', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'opened') {
    const document = normalizePhotonCadProjectDocument(raw.document)
    if (!document || !sourceHandle(raw.sourceHandle)) return null
    return { contractVersion: 1, requestId: raw.requestId, status: 'opened', reason: raw.reason as string, sourceHandle: raw.sourceHandle, document }
  }
  if (raw.sourceHandle !== undefined || raw.document !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadProjectLoadResult['status'], reason: raw.reason as string }
}

function normalizeSaveReceipt(value: unknown): PhotonCadProjectSaveReceipt | null {
  const raw = record(value)
  const baseRevision = revision(raw?.baseRevision)
  const savedRevision = revision(raw?.savedRevision)
  if (!raw || !isPhotonCadSaveReceiptHandle(raw.receiptHandle) || !isPhotonCadProjectHandle(raw.sourceProjectHandle)
    || !isPhotonCadProjectHandle(raw.projectHandle) || !isPhotonCadIdentifier(raw.sessionId) || !isPhotonCadIdentifier(raw.projectId)
    || baseRevision === null || savedRevision === null || !isPhotonCadDigest(raw.contentDigest)
    || !isPhotonCadUtcTimestamp(raw.savedAtUtc) || raw.atomic !== true) return null
  return {
    receiptHandle: raw.receiptHandle,
    sourceProjectHandle: raw.sourceProjectHandle,
    projectHandle: raw.projectHandle,
    sessionId: raw.sessionId,
    projectId: raw.projectId,
    baseRevision,
    savedRevision,
    contentDigest: raw.contentDigest.toLowerCase(),
    savedAtUtc: raw.savedAtUtc,
    atomic: true,
  }
}

function normalizeSaveResult(value: unknown): PhotonCadProjectSaveResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['saved', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'saved') {
    const receipt = normalizeSaveReceipt(raw.receipt)
    const document = normalizePhotonCadProjectDocument(raw.document)
    if (!receipt || !document) return null
    return { contractVersion: 1, requestId: raw.requestId, status: 'saved', reason: raw.reason as string, receipt, document }
  }
  if (raw.receipt !== undefined || raw.document !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadProjectSaveResult['status'], reason: raw.reason as string }
}

function normalizeReopenMetadata(value: unknown): PhotonCadProjectReopenMetadata | null {
  const raw = record(value)
  const lastSavedRevision = revision(raw?.lastSavedRevision)
  const displayName = displayLabel(raw?.displayName, PHOTON_CAD_PROJECT_LIMITS.title)
  if (!raw || !isPhotonCadReopenHandle(raw.reopenHandle) || !displayName
    || !isPhotonCadIdentifier(raw.projectId) || lastSavedRevision === null || !isPhotonCadDigest(raw.contentDigest)
    || !isPhotonCadUtcTimestamp(raw.closedAtUtc)) return null
  const metadata = {
    reopenHandle: raw.reopenHandle,
    displayName,
    projectId: raw.projectId,
    lastSavedRevision,
    contentDigest: raw.contentDigest.toLowerCase(),
    closedAtUtc: raw.closedAtUtc,
  }
  return validatePhotonCadReopenMetadata(metadata) ? metadata : null
}

function normalizeCloseResult(value: unknown): PhotonCadProjectCloseResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['closed', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'closed') {
    const reopen = normalizeReopenMetadata(raw.reopen)
    if (!isPhotonCadProjectHandle(raw.projectHandle) || !reopen) return null
    return { contractVersion: 1, requestId: raw.requestId, status: 'closed', reason: raw.reason as string, projectHandle: raw.projectHandle, reopen }
  }
  if (raw.projectHandle !== undefined || raw.reopen !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadProjectCloseResult['status'], reason: raw.reason as string }
}

export function normalizePhotonCadProjectHostFrame(value: unknown): PhotonCadProjectHostFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== PHOTON_CAD_PROJECT_DESKTOP_PROTOCOL_VERSION) return null
  if (raw.type === 'photonCad.project.error') {
    return isPhotonCadIdentifier(raw.requestId) && isPhotonCadIdentifier(raw.code) && typeof raw.retryable === 'boolean'
      ? { type: 'error', requestId: raw.requestId, code: raw.code, retryable: raw.retryable }
      : null
  }
  if (raw.type === 'photonCad.project.picker.result') {
    const result = normalizePickerResult(raw.value)
    return result ? { type: 'picker', value: result } : null
  }
  const loadKinds = {
    'photonCad.project.create.result': 'create',
    'photonCad.project.open.result': 'open',
    'photonCad.project.reopen.result': 'reopen',
    'photonCad.project.refresh.result': 'refresh',
  } as const
  const loadKind = loadKinds[raw.type as keyof typeof loadKinds]
  if (loadKind) {
    const result = normalizeLoadResult(raw.value)
    return result ? { type: loadKind, value: result } : null
  }
  if (raw.type === 'photonCad.project.save.result' || raw.type === 'photonCad.project.saveAs.result') {
    const result = normalizeSaveResult(raw.value)
    return result ? { type: raw.type === 'photonCad.project.save.result' ? 'save' : 'save-as', value: result } : null
  }
  if (raw.type === 'photonCad.project.close.result') {
    const result = normalizeCloseResult(raw.value)
    return result ? { type: 'close', value: result } : null
  }
  return null
}

export class DesktopPhotonCadProjectClient implements PhotonCadProjectController {
  private readonly getBridge: () => PhotonCadProjectWebViewBridge | null
  private readonly createId: (kind: string, sequence: number) => string
  private readonly timeouts: Record<ProjectRequestKind, number>
  private bridge: PhotonCadProjectWebViewBridge | null = null
  private sequence = 0
  private closed = false
  private activePickerId: string | null = null
  private activeOpenId: string | null = null
  private activeWriteId: string | null = null
  private readonly pending = new Map<string, PendingRequest>()
  private readonly usedRequestIds = new Set<string>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizePhotonCadProjectHostFrame(event.data)
    if (!frame) return
    if (frame.type === 'error') {
      this.fail(frame.requestId, new Error(`Photon CAD project host rejected the request (${frame.code}).`))
      return
    }
    const pending = this.pending.get(frame.value.requestId)
    if (!pending || pending.kind !== frame.type || !this.matches(pending, frame)) return
    this.finish(pending)
    pending.resolve(frame.value)
  }

  public constructor(options: DesktopPhotonCadProjectClientOptions = {}) {
    this.getBridge = options.getBridge ?? browserBridge
    const nonce = projectClientNonce()
    this.createId = options.createRequestId ?? ((kind, sequence) => `cad-project-${kind}:${nonce}:${sequence.toString(36)}`)
    this.timeouts = normalizeTimeouts(options.requestTimeoutMs)
  }

  public get available() {
    return !this.closed && this.getBridge() !== null
  }

  public chooseWorkspace(request: PhotonCadProjectPickerRequest): Promise<PhotonCadProjectPickerResult> {
    if (!validatePhotonCadProjectPickerRequest(request)) return Promise.reject(new Error('Invalid Photon CAD workspace-picker request.'))
    if (!this.available) return Promise.resolve(pickerUnavailable(request.requestId, this.closed))
    return this.send('picker', 'picker', request, { type: 'photonCad.project.picker', version: 1, ...request }, true)
  }

  public createProject(request: PhotonCadProjectCreateRequest): Promise<PhotonCadProjectLoadResult> {
    if (!validatePhotonCadProjectCreateRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-create request.'))
    if (!this.available) return Promise.resolve(loadUnavailable(request.requestId, this.closed))
    return this.send('create', 'write', request, { type: 'photonCad.project.create', version: 1, ...request })
  }

  public openProject(request: PhotonCadProjectOpenRequest): Promise<PhotonCadProjectLoadResult> {
    if (!validatePhotonCadProjectOpenRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-open request.'))
    if (!this.available) return Promise.resolve(loadUnavailable(request.requestId, this.closed))
    return this.send('open', 'open', request, { type: 'photonCad.project.open', version: 1, ...request }, true)
  }

  public reopenProject(request: PhotonCadProjectReopenRequest): Promise<PhotonCadProjectLoadResult> {
    if (!validatePhotonCadProjectReopenRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-reopen request.'))
    if (!this.available) return Promise.resolve(loadUnavailable(request.requestId, this.closed))
    return this.send('reopen', 'open', request, { type: 'photonCad.project.reopen', version: 1, ...request }, true)
  }

  public refreshProject(request: PhotonCadProjectRefreshRequest): Promise<PhotonCadProjectLoadResult> {
    if (!validatePhotonCadProjectRefreshRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-refresh request.'))
    if (!this.available) return Promise.resolve(loadUnavailable(request.requestId, this.closed))
    return this.send('refresh', 'open', request, { type: 'photonCad.project.refresh', version: 1, ...request }, true)
  }

  public saveProject(request: PhotonCadProjectSaveRequest): Promise<PhotonCadProjectSaveResult> {
    if (!validatePhotonCadProjectSaveRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-save request.'))
    if (!this.available) return Promise.resolve(saveUnavailable(request.requestId, this.closed))
    return this.send('save', 'write', request, { type: 'photonCad.project.save', version: 1, ...request })
  }

  public saveProjectAs(request: PhotonCadProjectSaveAsRequest): Promise<PhotonCadProjectSaveResult> {
    if (!validatePhotonCadProjectSaveAsRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-save-as request.'))
    if (!this.available) return Promise.resolve(saveUnavailable(request.requestId, this.closed))
    return this.send('save-as', 'write', request, { type: 'photonCad.project.saveAs', version: 1, ...request })
  }

  public closeProject(request: PhotonCadProjectCloseRequest): Promise<PhotonCadProjectCloseResult> {
    if (!validatePhotonCadProjectCloseRequest(request)) return Promise.reject(new Error('Invalid Photon CAD project-close request.'))
    if (!this.available) return Promise.resolve(closeUnavailable(request.requestId, this.closed))
    return this.send('close', 'write', request, { type: 'photonCad.project.close', version: 1, ...request })
  }

  public cancelPending() {
    for (const pending of [...this.pending.values()]) {
      this.postCancellation(pending, this.bridge)
      this.finish(pending)
      pending.reject(new Error('Photon CAD project request was invalidated before completion.'))
    }
  }

  public close() {
    if (this.closed) return
    this.cancelPending()
    this.closed = true
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
  }

  private send<T extends ProjectResult>(
    kind: ProjectRequestKind,
    lane: PendingLane,
    request: ProjectRequest,
    message: PhotonCadProjectDesktopRequest,
    latestWins = false,
  ): Promise<T> {
    if (lane === 'picker' && (this.activeOpenId || this.activeWriteId)) return Promise.reject(new Error('Another Photon CAD project transition is active.'))
    if (lane === 'open' && this.activeWriteId) return Promise.reject(new Error('A Photon CAD project write is active.'))
    if (lane === 'write' && (this.activeWriteId || this.activeOpenId || this.activePickerId)) return Promise.reject(new Error('Another Photon CAD project transition is active.'))
    const existing = lane === 'picker' ? this.activePickerId : lane === 'open' ? this.activeOpenId : null
    if (existing && latestWins) this.supersede(existing)
    else if (existing) return Promise.reject(new Error('Another Photon CAD project request is active.'))
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Photon CAD project desktop host is unavailable.'))
    try {
      this.reserveRequestId(request.requestId)
    } catch (error) {
      return Promise.reject(error)
    }
    return new Promise<T>((resolve, reject) => {
      const timeout = setTimeout(() => {
        const pending = this.pending.get(request.requestId)
        if (!pending) return
        this.postCancellation(pending, this.bridge)
        this.finish(pending)
        pending.reject(new Error('Photon CAD project host request timed out; no success was recorded.'))
      }, this.timeouts[kind])
      const pending: PendingRequest = {
        kind,
        lane,
        request,
        resolve: resolve as (result: ProjectResult) => void,
        reject,
        timeout,
      }
      this.pending.set(request.requestId, pending)
      this.setActive(lane, request.requestId)
      try {
        bridge.postMessage(message)
      } catch {
        this.finish(pending)
        reject(new Error('Photon CAD project host could not receive the request.'))
      }
    })
  }

  private connect() {
    if (this.closed) return null
    const next = this.getBridge()
    if (next === this.bridge) return next
    const previous = this.bridge
    if (previous) {
      for (const pending of this.pending.values()) this.postCancellation(pending, previous)
      previous.removeEventListener('message', this.receive)
      this.rejectAll(new Error('Photon CAD project desktop host session changed.'))
    }
    this.bridge = next
    this.bridge?.addEventListener('message', this.receive)
    return this.bridge
  }

  private matches(pending: PendingRequest, frame: Exclude<PhotonCadProjectHostFrame, { type: 'error' }>) {
    const request = pending.request
    if (frame.type === 'picker' && pending.kind === 'picker') return true
    if ((frame.type === 'create' || frame.type === 'open' || frame.type === 'reopen' || frame.type === 'refresh')
      && pending.kind === frame.type) return matchesLoad(request as PhotonCadProjectLoadRequest, frame.value, frame.type)
    if (frame.type === 'save' && pending.kind === 'save') return frame.value.status !== 'saved'
      || validatePhotonCadSaveReceiptBinding(request as PhotonCadProjectSaveRequest, frame.value)
    if (frame.type === 'save-as' && pending.kind === 'save-as') return frame.value.status !== 'saved'
      || validatePhotonCadSaveReceiptBinding(request as PhotonCadProjectSaveAsRequest, frame.value)
    if (frame.type === 'close' && pending.kind === 'close') {
      const close = request as PhotonCadProjectCloseRequest
      return frame.value.status !== 'closed'
        || (frame.value.projectHandle === close.projectHandle && frame.value.reopen?.projectId === close.projectId
          && frame.value.reopen.lastSavedRevision === close.lastSavedRevision
          && frame.value.reopen.contentDigest.toLowerCase() === (close.discardUnsavedChanges
            ? close.lastSavedContentDigest.toLowerCase()
            : close.contentDigest.toLowerCase()))
    }
    return false
  }

  private supersede(requestId: string) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.postCancellation(pending, this.bridge)
    this.finish(pending)
    pending.reject(new Error('Photon CAD project request was superseded by a newer request.'))
  }

  private postCancellation(pending: PendingRequest, bridge: PhotonCadProjectWebViewBridge | null) {
    if (!bridge) return
    try {
      const requestId = this.reserveGeneratedId('cancel')
      bridge.postMessage({
        type: 'photonCad.project.cancel',
        version: PHOTON_CAD_PROJECT_DESKTOP_PROTOCOL_VERSION,
        requestId,
        targetRequestId: pending.request.requestId,
        operation: pending.kind,
      } satisfies PhotonCadProjectDesktopRequest)
    } catch {
      // Cancellation is best effort; local correlation has already failed closed.
    }
  }

  private reserveGeneratedId(kind: string) {
    this.sequence += 1
    const requestId = this.createId(kind, this.sequence)
    this.reserveRequestId(requestId)
    return requestId
  }

  private reserveRequestId(requestId: string) {
    if (!isPhotonCadIdentifier(requestId)) throw new Error('Photon CAD project request identifier is invalid.')
    if (this.usedRequestIds.has(requestId)) throw new Error('Photon CAD project request identifier was already used.')
    if (this.usedRequestIds.size >= MAXIMUM_REQUEST_HISTORY) throw new Error('Photon CAD project request identity capacity was reached; create a new client session.')
    this.usedRequestIds.add(requestId)
  }

  private setActive(lane: PendingLane, requestId: string | null) {
    if (lane === 'picker') this.activePickerId = requestId
    else if (lane === 'open') this.activeOpenId = requestId
    else this.activeWriteId = requestId
  }

  private finish(pending: PendingRequest) {
    clearTimeout(pending.timeout)
    this.pending.delete(pending.request.requestId)
    const active = pending.lane === 'picker' ? this.activePickerId : pending.lane === 'open' ? this.activeOpenId : this.activeWriteId
    if (active === pending.request.requestId) this.setActive(pending.lane, null)
  }

  private fail(requestId: string, error: Error) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.finish(pending)
    pending.reject(error)
  }

  private rejectAll(error: Error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout)
      pending.reject(error)
    }
    this.pending.clear()
    this.activePickerId = null
    this.activeOpenId = null
    this.activeWriteId = null
  }
}

function matchesLoad(request: PhotonCadProjectLoadRequest, result: PhotonCadProjectLoadResult, kind: 'create' | 'open' | 'reopen' | 'refresh') {
  if (result.status !== 'opened') return true
  if (!result.document || !result.sourceHandle) return false
  if (kind === 'create' || kind === 'open') {
    const source = (request as PhotonCadProjectCreateRequest | PhotonCadProjectOpenRequest).workspaceHandle
    return result.sourceHandle === source && result.document.workspaceHandle === source
  }
  if (kind === 'reopen') return result.sourceHandle === (request as PhotonCadProjectReopenRequest).reopenHandle
  const refresh = request as PhotonCadProjectRefreshRequest
  return result.sourceHandle === refresh.projectHandle
    && result.document.projectHandle === refresh.projectHandle
    && result.document.snapshot.sessionId === refresh.sessionId
    && result.document.snapshot.projectId === refresh.projectId
    && result.document.snapshot.revision >= refresh.knownRevision
}

function sourceHandle(value: unknown): value is string {
  return isPhotonCadWorkspaceHandle(value) || isPhotonCadProjectHandle(value) || isPhotonCadReopenHandle(value)
}

function pickerUnavailable(requestId: string, closed: boolean): PhotonCadProjectPickerResult {
  return { contractVersion: 1, requestId, status: 'unavailable', reason: closed ? 'client-closed' : 'desktop-host-unavailable' }
}

function loadUnavailable(requestId: string, closed: boolean): PhotonCadProjectLoadResult {
  return { contractVersion: 1, requestId, status: 'unavailable', reason: closed ? 'client-closed' : 'desktop-host-unavailable' }
}

function saveUnavailable(requestId: string, closed: boolean): PhotonCadProjectSaveResult {
  return { contractVersion: 1, requestId, status: 'unavailable', reason: closed ? 'client-closed' : 'desktop-host-unavailable' }
}

function closeUnavailable(requestId: string, closed: boolean): PhotonCadProjectCloseResult {
  return { contractVersion: 1, requestId, status: 'unavailable', reason: closed ? 'client-closed' : 'desktop-host-unavailable' }
}

function normalizeTimeouts(value: DesktopPhotonCadProjectClientOptions['requestTimeoutMs']) {
  const normalize = (candidate: unknown, fallback: number) => typeof candidate === 'number' && Number.isFinite(candidate) && candidate > 0
    ? Math.min(MAXIMUM_TIMEOUT_MS, Math.trunc(candidate))
    : fallback
  if (typeof value === 'number') {
    const timeout = normalize(value, DEFAULT_TIMEOUTS.open)
    return Object.fromEntries(Object.keys(DEFAULT_TIMEOUTS).map((key) => [key, timeout])) as Record<ProjectRequestKind, number>
  }
  return Object.fromEntries(Object.entries(DEFAULT_TIMEOUTS).map(([key, fallback]) => [key, normalize(value?.[key as ProjectRequestKind], fallback)])) as Record<ProjectRequestKind, number>
}

let fallbackNonce = 0
function projectClientNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackNonce += 1
  return `${Date.now().toString(36)}-${fallbackNonce.toString(36)}`
}

export const desktopPhotonCadProjectClient = new DesktopPhotonCadProjectClient()
