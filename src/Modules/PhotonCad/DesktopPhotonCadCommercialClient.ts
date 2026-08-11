import {
  PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
  PHOTON_CAD_COMMERCIAL_LIMITS,
  isPhotonCadBomReviewHandle,
  isPhotonCadCommercialApprovalHandle,
  isPhotonCadCommercialReviewHandle,
  validatePhotonCadBomExportReviewRequest,
  validatePhotonCadCommercialApprovalRequest,
  validatePhotonCadCommercialCommitRequest,
  validatePhotonCadCommercialReviewRequest,
  validatePhotonCadCommercialReviewResult,
  type PhotonCadBomExportCommitRequest,
  type PhotonCadBomExportCommitResult,
  type PhotonCadBomExportReviewRequest,
  type PhotonCadBomExportReviewResult,
  type PhotonCadCommercialApprovalRequest,
  type PhotonCadCommercialApprovalResult,
  type PhotonCadCommercialCommitRequest,
  type PhotonCadCommercialCommitResult,
  type PhotonCadCommercialController,
  type PhotonCadCommercialOutputAction,
  type PhotonCadCommercialReviewRequest,
  type PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'
import {
  PHOTON_CAD_LIMITS,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadUtcTimestamp,
  normalizePhotonCadRelativePath,
} from './PhotonCadContract'

export const PHOTON_CAD_COMMERCIAL_DESKTOP_PROTOCOL_VERSION = 1 as const

type CommercialRequestKind = 'bom-review' | 'bom-commit' | 'document-review' | 'document-approve' | 'document-commit'
type CancellableCommercialRequestKind = Extract<CommercialRequestKind, 'bom-review' | 'document-review' | 'document-approve'>

const DEFAULT_TIMEOUTS: Record<CommercialRequestKind, number> = {
  'bom-review': 120_000,
  'bom-commit': 120_000,
  'document-review': 600_000,
  'document-approve': 120_000,
  'document-commit': 120_000,
}
const MAXIMUM_TIMEOUT_MS = 600_000
const MAXIMUM_REQUEST_HISTORY = 50_000
const ISO_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u

export type PhotonCadCommercialWebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

export type PhotonCadCommercialDesktopRequest =
  | ({ type: 'photonCad.commercial.bom.review'; version: 1 } & PhotonCadBomExportReviewRequest)
  | ({ type: 'photonCad.commercial.bom.commit'; version: 1 } & PhotonCadBomExportCommitRequest)
  | { type: 'photonCad.commercial.bom.discard'; version: 1; requestId: string; reviewHandle: string }
  | ({ type: 'photonCad.commercial.document.review'; version: 1 } & PhotonCadCommercialReviewRequest)
  | ({ type: 'photonCad.commercial.document.approve'; version: 1 } & PhotonCadCommercialApprovalRequest)
  | ({ type: 'photonCad.commercial.document.commit'; version: 1 } & PhotonCadCommercialCommitRequest)
  | { type: 'photonCad.commercial.document.discard'; version: 1; requestId: string; handle: string }
  | { type: 'photonCad.commercial.cancel'; version: 1; requestId: string; targetRequestId: string; operation: CancellableCommercialRequestKind }

export type PhotonCadCommercialHostFrame =
  | { type: 'bom-review'; value: PhotonCadBomExportReviewResult }
  | { type: 'bom-commit'; value: PhotonCadBomExportCommitResult }
  | { type: 'document-review'; value: PhotonCadCommercialReviewResult }
  | { type: 'document-approve'; value: PhotonCadCommercialApprovalResult }
  | { type: 'document-commit'; value: PhotonCadCommercialCommitResult }
  | { type: 'error'; requestId: string; code: string; retryable: boolean }

type CommercialRequest =
  | PhotonCadBomExportReviewRequest
  | PhotonCadBomExportCommitRequest
  | PhotonCadCommercialReviewRequest
  | PhotonCadCommercialApprovalRequest
  | PhotonCadCommercialCommitRequest

type PendingRequest = {
  kind: CommercialRequestKind
  requestId: string
  request: CommercialRequest
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  timeout: ReturnType<typeof setTimeout>
}

type BomReviewBinding = { request: PhotonCadBomExportReviewRequest; result: PhotonCadBomExportReviewResult }
type DocumentReviewBinding = { request: PhotonCadCommercialReviewRequest; result: PhotonCadCommercialReviewResult }
type DocumentApprovalBinding = {
  action: PhotonCadCommercialOutputAction
  result: PhotonCadCommercialApprovalResult
  documentFingerprint: string
  actionFingerprint: string
}

export type DesktopPhotonCadCommercialClientOptions = {
  getBridge?: () => PhotonCadCommercialWebViewBridge | null
  createRequestId?: (kind: string, sequence: number) => string
  requestTimeoutMs?: number | Partial<Record<CommercialRequestKind, number>>
  now?: () => number
}

function browserBridge(): PhotonCadCommercialWebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: PhotonCadCommercialWebViewBridge } }).chrome?.webview ?? null
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

function normalizeBomReviewResult(value: unknown): PhotonCadBomExportReviewResult | null {
  const raw = record(value)
  const projectRevision = revision(raw?.projectRevision)
  if (!raw || raw.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
    || !isPhotonCadIdentifier(raw.requestId) || !isPhotonCadIdentifier(raw.projectId) || projectRevision === null
    || !['ready', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)
    || !Array.isArray(raw.files) || raw.files.length > PHOTON_CAD_LIMITS.packageFiles) return null
  const files = raw.files.map((value) => {
    const file = record(value)
    const relativePath = normalizePhotonCadRelativePath(file?.relativePath)
    return file && (file.role === 'bom' || file.role === 'validation-report') && relativePath
      ? { role: file.role, relativePath }
      : null
  })
  if (files.some((file) => file === null)) return null
  const paths = files.map((file) => file?.relativePath.toLowerCase())
  if (new Set(paths).size !== paths.length) return null
  if (raw.status === 'ready') {
    const roles = files.map((file) => file?.role)
    if (!isPhotonCadBomReviewHandle(raw.reviewHandle) || !isPhotonCadDigest(raw.exportFingerprint)
      || roles.filter((role) => role === 'bom').length !== 1
      || roles.filter((role) => role === 'validation-report').length > 1) return null
    return {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: raw.requestId,
      projectId: raw.projectId,
      projectRevision,
      status: 'ready',
      reason: raw.reason as string,
      reviewHandle: raw.reviewHandle,
      exportFingerprint: raw.exportFingerprint.toLowerCase(),
      files: files as PhotonCadBomExportReviewResult['files'],
    }
  }
  if (raw.reviewHandle !== undefined || raw.exportFingerprint !== undefined || files.length !== 0) return null
  return {
    contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
    requestId: raw.requestId,
    projectId: raw.projectId,
    projectRevision,
    status: raw.status as PhotonCadBomExportReviewResult['status'],
    reason: raw.reason as string,
    files: [],
  }
}

function normalizeBomCommitResult(value: unknown): PhotonCadBomExportCommitResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['exported', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'exported') {
    if (!isPhotonCadDigest(raw.exportFingerprint)) return null
    return { contractVersion: 1, requestId: raw.requestId, status: 'exported', reason: raw.reason as string, exportFingerprint: raw.exportFingerprint.toLowerCase() }
  }
  if (raw.exportFingerprint !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadBomExportCommitResult['status'], reason: raw.reason as string }
}

function normalizeDocumentReviewResult(value: unknown): PhotonCadCommercialReviewResult | null {
  if (!validatePhotonCadCommercialReviewResult(value as PhotonCadCommercialReviewResult)) return null
  const raw = value as PhotonCadCommercialReviewResult
  if (raw.status !== 'ready') return { contractVersion: 1, requestId: raw.requestId, status: raw.status, reason: raw.reason, pages: [] }
  if (!raw.expiresAtUtc || !ISO_UTC.test(raw.expiresAtUtc)) return null
  return {
    contractVersion: 1,
    requestId: raw.requestId,
    status: 'ready',
    reason: raw.reason,
    reviewHandle: raw.reviewHandle,
    documentFingerprint: raw.documentFingerprint?.toLowerCase(),
    actionFingerprint: raw.actionFingerprint?.toLowerCase(),
    expiresAtUtc: raw.expiresAtUtc,
    pages: raw.pages.map((page) => ({ pageNumber: page.pageNumber, previewHandle: page.previewHandle, contentDigest: page.contentDigest.toLowerCase() })),
    totals: raw.totals ? { ...raw.totals } : undefined,
  }
}

function normalizeApprovalResult(value: unknown): PhotonCadCommercialApprovalResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['approved', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'approved') {
    if (!isPhotonCadCommercialApprovalHandle(raw.approvalHandle) || !isPhotonCadDigest(raw.documentFingerprint)
      || !isPhotonCadDigest(raw.actionFingerprint) || !isPhotonCadUtcTimestamp(raw.expiresAtUtc)) return null
    return {
      contractVersion: 1,
      requestId: raw.requestId,
      status: 'approved',
      reason: raw.reason as string,
      approvalHandle: raw.approvalHandle,
      documentFingerprint: raw.documentFingerprint.toLowerCase(),
      actionFingerprint: raw.actionFingerprint.toLowerCase(),
      expiresAtUtc: raw.expiresAtUtc,
    }
  }
  if (raw.approvalHandle !== undefined || raw.documentFingerprint !== undefined || raw.actionFingerprint !== undefined || raw.expiresAtUtc !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadCommercialApprovalResult['status'], reason: raw.reason as string }
}

function normalizeDocumentCommitResult(value: unknown): PhotonCadCommercialCommitResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['exported', 'printed', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason(raw.reason)) return null
  if (raw.status === 'exported' || raw.status === 'printed') {
    if (!isPhotonCadDigest(raw.documentFingerprint) || !isPhotonCadDigest(raw.actionFingerprint)) return null
    return {
      contractVersion: 1,
      requestId: raw.requestId,
      status: raw.status,
      reason: raw.reason as string,
      documentFingerprint: raw.documentFingerprint.toLowerCase(),
      actionFingerprint: raw.actionFingerprint.toLowerCase(),
    }
  }
  if (raw.documentFingerprint !== undefined || raw.actionFingerprint !== undefined) return null
  return { contractVersion: 1, requestId: raw.requestId, status: raw.status as PhotonCadCommercialCommitResult['status'], reason: raw.reason as string }
}

export function normalizePhotonCadCommercialHostFrame(value: unknown): PhotonCadCommercialHostFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== PHOTON_CAD_COMMERCIAL_DESKTOP_PROTOCOL_VERSION) return null
  if (raw.type === 'photonCad.commercial.error') {
    return isPhotonCadIdentifier(raw.requestId) && isPhotonCadIdentifier(raw.code) && typeof raw.retryable === 'boolean'
      ? { type: 'error', requestId: raw.requestId, code: raw.code, retryable: raw.retryable }
      : null
  }
  const mappings = {
    'photonCad.commercial.bom.review.result': ['bom-review', normalizeBomReviewResult],
    'photonCad.commercial.bom.commit.result': ['bom-commit', normalizeBomCommitResult],
    'photonCad.commercial.document.review.result': ['document-review', normalizeDocumentReviewResult],
    'photonCad.commercial.document.approve.result': ['document-approve', normalizeApprovalResult],
    'photonCad.commercial.document.commit.result': ['document-commit', normalizeDocumentCommitResult],
  } as const
  const mapping = mappings[raw.type as keyof typeof mappings]
  if (!mapping) return null
  const normalized = mapping[1](raw.value as never)
  return normalized ? { type: mapping[0], value: normalized } as PhotonCadCommercialHostFrame : null
}

export class DesktopPhotonCadCommercialClient implements PhotonCadCommercialController {
  private readonly getBridge: () => PhotonCadCommercialWebViewBridge | null
  private readonly createId: (kind: string, sequence: number) => string
  private readonly timeouts: Record<CommercialRequestKind, number>
  private readonly now: () => number
  private bridge: PhotonCadCommercialWebViewBridge | null = null
  private sequence = 0
  private closed = false
  private activeRequestId: string | null = null
  private readonly pending = new Map<string, PendingRequest>()
  private readonly usedRequestIds = new Set<string>()
  private bomReview: BomReviewBinding | null = null
  private documentReview: DocumentReviewBinding | null = null
  private documentApproval: DocumentApprovalBinding | null = null
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizePhotonCadCommercialHostFrame(event.data)
    if (!frame) return
    if (frame.type === 'error') {
      this.fail(frame.requestId, new Error(`Photon CAD commercial host rejected the request (${frame.code}).`))
      return
    }
    const pending = this.pending.get(frame.value.requestId)
    if (!pending || pending.kind !== frame.type || !this.matches(pending, frame)) return
    this.captureBinding(pending, frame)
    this.finish(pending)
    pending.resolve(frame.value)
  }

  public constructor(options: DesktopPhotonCadCommercialClientOptions = {}) {
    this.getBridge = options.getBridge ?? browserBridge
    const nonce = defaultNonce()
    this.createId = options.createRequestId ?? ((kind, sequence) => `cad-commercial-${kind}:${nonce}:${sequence.toString(36)}`)
    this.timeouts = normalizeTimeouts(options.requestTimeoutMs)
    this.now = options.now ?? Date.now
  }

  public get available() {
    return !this.closed && this.getBridge() !== null
  }

  public reviewBomExport(request: PhotonCadBomExportReviewRequest): Promise<PhotonCadBomExportReviewResult> {
    if (!validatePhotonCadBomExportReviewRequest(request)) return Promise.reject(new Error('Invalid Photon CAD BOM export review request.'))
    this.invalidateBomBinding()
    if (!this.available) return Promise.resolve({ contractVersion: 1, requestId: request.requestId, projectId: request.projectId, projectRevision: request.projectRevision, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable', files: [] })
    return this.send('bom-review', request, { type: 'photonCad.commercial.bom.review', version: 1, ...request })
  }

  public commitBomExport(request: PhotonCadBomExportCommitRequest): Promise<PhotonCadBomExportCommitResult> {
    if (request.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
      || !isPhotonCadIdentifier(request.requestId) || !isPhotonCadBomReviewHandle(request.reviewHandle) || !isPhotonCadDigest(request.exportFingerprint)) {
      return Promise.reject(new Error('Invalid Photon CAD BOM export commit request.'))
    }
    const binding = this.bomReview
    if (!binding || binding.result.reviewHandle !== request.reviewHandle
      || binding.result.exportFingerprint?.toLowerCase() !== request.exportFingerprint.toLowerCase()) {
      return Promise.reject(new Error('Photon CAD BOM export requires the exact current review binding.'))
    }
    if (!this.available) {
      this.invalidateBomBinding()
      return Promise.resolve({ contractVersion: 1, requestId: request.requestId, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable' })
    }
    return this.send('bom-commit', request, { type: 'photonCad.commercial.bom.commit', version: 1, ...request })
  }

  public discardBomExport(reviewHandle: string) {
    if (!isPhotonCadBomReviewHandle(reviewHandle)) return
    if (this.bomReview?.result.reviewHandle === reviewHandle) this.bomReview = null
    this.postDiscardBom(reviewHandle)
  }

  public reviewCommercialDocument(request: PhotonCadCommercialReviewRequest): Promise<PhotonCadCommercialReviewResult> {
    if (!validatePhotonCadCommercialReviewRequest(request)) return Promise.reject(new Error('Invalid Photon CAD commercial review request.'))
    this.invalidateDocumentBindings()
    if (!this.available) return Promise.resolve({ contractVersion: 1, requestId: request.requestId, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable', pages: [] })
    return this.send('document-review', request, { type: 'photonCad.commercial.document.review', version: 1, ...request })
  }

  public approveCommercialDocument(request: PhotonCadCommercialApprovalRequest): Promise<PhotonCadCommercialApprovalResult> {
    const binding = this.documentReview
    if (!binding || !validatePhotonCadCommercialApprovalRequest(request, binding.result.pages)
      || request.reviewHandle !== binding.result.reviewHandle
      || request.documentFingerprint.toLowerCase() !== binding.result.documentFingerprint?.toLowerCase()
      || request.actionFingerprint.toLowerCase() !== binding.result.actionFingerprint?.toLowerCase()) {
      return Promise.reject(new Error('Commercial approval requires every ordered page from the exact current review.'))
    }
    if (!binding.result.expiresAtUtc || Date.parse(binding.result.expiresAtUtc) <= this.now()) {
      this.invalidateDocumentBindings()
      return Promise.reject(new Error('The commercial document review expired before approval.'))
    }
    if (!this.available) {
      this.invalidateDocumentBindings()
      return Promise.resolve({ contractVersion: 1, requestId: request.requestId, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable' })
    }
    return this.send('document-approve', request, { type: 'photonCad.commercial.document.approve', version: 1, ...request })
  }

  public commitCommercialDocument(request: PhotonCadCommercialCommitRequest): Promise<PhotonCadCommercialCommitResult> {
    if (!validatePhotonCadCommercialCommitRequest(request)) return Promise.reject(new Error('Invalid Photon CAD commercial commit request.'))
    const binding = this.documentApproval
    if (!binding || binding.result.approvalHandle !== request.approvalHandle
      || binding.documentFingerprint !== request.documentFingerprint.toLowerCase()
      || binding.actionFingerprint !== request.actionFingerprint.toLowerCase()) {
      return Promise.reject(new Error('Commercial output requires the exact current human-approval binding.'))
    }
    if (!binding.result.expiresAtUtc || Date.parse(binding.result.expiresAtUtc) <= this.now()) {
      this.invalidateDocumentBindings()
      return Promise.reject(new Error('The commercial document approval expired before output.'))
    }
    if (!this.available) {
      this.invalidateDocumentBindings()
      return Promise.resolve({ contractVersion: 1, requestId: request.requestId, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable' })
    }
    return this.send('document-commit', request, { type: 'photonCad.commercial.document.commit', version: 1, ...request })
  }

  public discardCommercialDocument(handle: string) {
    if (!isPhotonCadCommercialReviewHandle(handle) && !isPhotonCadCommercialApprovalHandle(handle)) return
    if (this.documentReview?.result.reviewHandle === handle) this.documentReview = null
    if (this.documentApproval?.result.approvalHandle === handle) this.documentApproval = null
    this.postDiscardDocument(handle)
  }

  public invalidateBindings() {
    this.cancelPending()
    this.invalidateBomBinding()
    this.invalidateDocumentBindings()
  }

  public cancelPending() {
    const pending = [...this.pending.values()]
    for (const item of pending) {
      if (isCancellable(item.kind)) this.postCancellation(item, this.bridge)
      this.finish(item)
      this.invalidateForKind(item.kind)
      item.reject(new Error('Photon CAD commercial request was invalidated before completion.'))
    }
  }

  public close() {
    if (this.closed) return
    this.cancelPending()
    this.invalidateBomBinding()
    this.invalidateDocumentBindings()
    this.closed = true
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
  }

  private send<T>(kind: CommercialRequestKind, request: CommercialRequest, message: PhotonCadCommercialDesktopRequest): Promise<T> {
    if (this.activeRequestId) return Promise.reject(new Error('Another Photon CAD commercial operation is already active.'))
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Photon CAD commercial desktop host is unavailable.'))
    try {
      this.reserveRequestId(request.requestId)
    } catch (error) {
      return Promise.reject(error)
    }
    return new Promise<T>((resolve, reject) => {
      const timeout = setTimeout(() => {
        const pending = this.pending.get(request.requestId)
        if (!pending) return
        if (isCancellable(pending.kind)) this.postCancellation(pending, this.bridge)
        this.finish(pending)
        this.invalidateForKind(kind)
        pending.reject(new Error('Photon CAD commercial host request timed out.'))
      }, this.timeouts[kind])
      const pending: PendingRequest = { kind, requestId: request.requestId, request, resolve: resolve as (value: unknown) => void, reject, timeout }
      this.pending.set(request.requestId, pending)
      this.activeRequestId = request.requestId
      try {
        bridge.postMessage(message)
      } catch {
        this.finish(pending)
        this.invalidateForKind(kind)
        reject(new Error('Photon CAD commercial host could not receive the request.'))
      }
    })
  }

  private connect() {
    if (this.closed) return null
    const next = this.getBridge()
    if (next === this.bridge) return next
    const previous = this.bridge
    if (previous) {
      for (const pending of this.pending.values()) if (isCancellable(pending.kind)) this.postCancellation(pending, previous)
      const bomHandle = this.bomReview?.result.reviewHandle
      const reviewHandle = this.documentReview?.result.reviewHandle
      const approvalHandle = this.documentApproval?.result.approvalHandle
      this.bomReview = null
      this.documentReview = null
      this.documentApproval = null
      if (bomHandle) this.postDiscardBom(bomHandle, previous)
      if (reviewHandle) this.postDiscardDocument(reviewHandle, previous)
      if (approvalHandle) this.postDiscardDocument(approvalHandle, previous)
      previous.removeEventListener('message', this.receive)
      this.rejectAll(new Error('Photon CAD commercial desktop host session changed.'))
    }
    this.bridge = next
    this.bridge?.addEventListener('message', this.receive)
    return this.bridge
  }

  private matches(pending: PendingRequest, frame: Exclude<PhotonCadCommercialHostFrame, { type: 'error' }>) {
    if (frame.type === 'bom-review' && pending.kind === 'bom-review') {
      const request = pending.request as PhotonCadBomExportReviewRequest
      return frame.value.projectId === request.projectId && frame.value.projectRevision === request.projectRevision
    }
    if (frame.type === 'bom-commit' && pending.kind === 'bom-commit') {
      const request = pending.request as PhotonCadBomExportCommitRequest
      return frame.value.exportFingerprint === undefined || frame.value.exportFingerprint === request.exportFingerprint.toLowerCase()
    }
    if (frame.type === 'document-review' && pending.kind === 'document-review') {
      return frame.value.status !== 'ready' || (!!frame.value.expiresAtUtc && Date.parse(frame.value.expiresAtUtc) > this.now())
    }
    if (frame.type === 'document-approve' && pending.kind === 'document-approve') {
      const request = pending.request as PhotonCadCommercialApprovalRequest
      return (frame.value.status !== 'approved' || (!!frame.value.expiresAtUtc && Date.parse(frame.value.expiresAtUtc) > this.now()))
        && (frame.value.documentFingerprint === undefined || frame.value.documentFingerprint === request.documentFingerprint.toLowerCase())
        && (frame.value.actionFingerprint === undefined || frame.value.actionFingerprint === request.actionFingerprint.toLowerCase())
    }
    if (frame.type === 'document-commit' && pending.kind === 'document-commit') {
      const request = pending.request as PhotonCadCommercialCommitRequest
      const binding = this.documentApproval
      const expectedStatus = binding?.action.kind === 'print' ? 'printed' : 'exported'
      if (frame.value.status === 'printed' || frame.value.status === 'exported') {
        if (frame.value.status !== expectedStatus) return false
      }
      return (frame.value.documentFingerprint === undefined || frame.value.documentFingerprint === request.documentFingerprint.toLowerCase())
        && (frame.value.actionFingerprint === undefined || frame.value.actionFingerprint === request.actionFingerprint.toLowerCase())
    }
    return false
  }

  private captureBinding(pending: PendingRequest, frame: Exclude<PhotonCadCommercialHostFrame, { type: 'error' }>) {
    if (frame.type === 'bom-review') {
      this.bomReview = frame.value.status === 'ready'
        ? { request: pending.request as PhotonCadBomExportReviewRequest, result: frame.value }
        : null
      return
    }
    if (frame.type === 'bom-commit') {
      this.bomReview = null
      return
    }
    if (frame.type === 'document-review') {
      this.documentReview = frame.value.status === 'ready'
        ? { request: pending.request as PhotonCadCommercialReviewRequest, result: frame.value }
        : null
      this.documentApproval = null
      return
    }
    if (frame.type === 'document-approve') {
      const review = this.documentReview
      this.documentReview = null
      this.documentApproval = frame.value.status === 'approved' && review
        ? {
          action: review.request.action,
          result: frame.value,
          documentFingerprint: frame.value.documentFingerprint!.toLowerCase(),
          actionFingerprint: frame.value.actionFingerprint!.toLowerCase(),
        }
        : null
      return
    }
    if (frame.type === 'document-commit') this.documentApproval = null
  }

  private invalidateForKind(kind: CommercialRequestKind) {
    if (kind === 'bom-review' || kind === 'bom-commit') this.invalidateBomBinding()
    else this.invalidateDocumentBindings()
  }

  private invalidateBomBinding() {
    const handle = this.bomReview?.result.reviewHandle
    const owningBridge = this.bridge
    this.bomReview = null
    if (handle) this.postDiscardBom(handle, owningBridge ?? undefined)
  }

  private invalidateDocumentBindings() {
    const reviewHandle = this.documentReview?.result.reviewHandle
    const approvalHandle = this.documentApproval?.result.approvalHandle
    const owningBridge = this.bridge
    this.documentReview = null
    this.documentApproval = null
    if (reviewHandle) this.postDiscardDocument(reviewHandle, owningBridge ?? undefined)
    if (approvalHandle) this.postDiscardDocument(approvalHandle, owningBridge ?? undefined)
  }

  private postDiscardBom(reviewHandle: string, bridgeOverride?: PhotonCadCommercialWebViewBridge | null) {
    const bridge = bridgeOverride === undefined ? this.connect() : bridgeOverride
    if (!bridge || this.closed) return
    try {
      const requestId = this.reserveGeneratedId('bom-discard')
      bridge.postMessage({ type: 'photonCad.commercial.bom.discard', version: 1, requestId, reviewHandle } satisfies PhotonCadCommercialDesktopRequest)
    } catch {
      // Opaque review handles expire and cannot be used without the exact fingerprint.
    }
  }

  private postDiscardDocument(handle: string, bridgeOverride?: PhotonCadCommercialWebViewBridge | null) {
    const bridge = bridgeOverride === undefined ? this.connect() : bridgeOverride
    if (!bridge || this.closed) return
    try {
      const requestId = this.reserveGeneratedId('document-discard')
      bridge.postMessage({ type: 'photonCad.commercial.document.discard', version: 1, requestId, handle } satisfies PhotonCadCommercialDesktopRequest)
    } catch {
      // Opaque commercial handles expire and local binding is already gone.
    }
  }

  private postCancellation(pending: PendingRequest, bridge: PhotonCadCommercialWebViewBridge | null) {
    if (!bridge || !isCancellable(pending.kind)) return
    try {
      const requestId = this.reserveGeneratedId('cancel')
      bridge.postMessage({ type: 'photonCad.commercial.cancel', version: 1, requestId, targetRequestId: pending.requestId, operation: pending.kind } satisfies PhotonCadCommercialDesktopRequest)
    } catch {
      // Cancellation is best effort. Local settlement still fails closed.
    }
  }

  private reserveGeneratedId(kind: string) {
    this.sequence += 1
    const requestId = this.createId(kind, this.sequence)
    this.reserveRequestId(requestId)
    return requestId
  }

  private reserveRequestId(requestId: string) {
    if (!isPhotonCadIdentifier(requestId)) throw new Error('Photon CAD commercial request identifier is invalid.')
    if (this.usedRequestIds.has(requestId)) throw new Error('Photon CAD commercial request identifier was already used.')
    if (this.usedRequestIds.size >= MAXIMUM_REQUEST_HISTORY) throw new Error('Photon CAD commercial request identity capacity was reached; create a new client session.')
    this.usedRequestIds.add(requestId)
  }

  private finish(pending: PendingRequest) {
    clearTimeout(pending.timeout)
    this.pending.delete(pending.requestId)
    if (this.activeRequestId === pending.requestId) this.activeRequestId = null
  }

  private fail(requestId: string, error: Error) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.finish(pending)
    this.invalidateForKind(pending.kind)
    pending.reject(error)
  }

  private rejectAll(error: Error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout)
      pending.reject(error)
    }
    this.pending.clear()
    this.activeRequestId = null
  }
}

function isCancellable(kind: CommercialRequestKind): kind is CancellableCommercialRequestKind {
  return kind === 'bom-review' || kind === 'document-review' || kind === 'document-approve'
}

function normalizeTimeouts(value: DesktopPhotonCadCommercialClientOptions['requestTimeoutMs']): Record<CommercialRequestKind, number> {
  const normalize = (candidate: unknown, fallback: number) => typeof candidate === 'number' && Number.isFinite(candidate) && candidate > 0
    ? Math.min(MAXIMUM_TIMEOUT_MS, Math.trunc(candidate))
    : fallback
  if (typeof value === 'number') {
    const timeout = normalize(value, DEFAULT_TIMEOUTS['document-review'])
    return { 'bom-review': timeout, 'bom-commit': timeout, 'document-review': timeout, 'document-approve': timeout, 'document-commit': timeout }
  }
  return {
    'bom-review': normalize(value?.['bom-review'], DEFAULT_TIMEOUTS['bom-review']),
    'bom-commit': normalize(value?.['bom-commit'], DEFAULT_TIMEOUTS['bom-commit']),
    'document-review': normalize(value?.['document-review'], DEFAULT_TIMEOUTS['document-review']),
    'document-approve': normalize(value?.['document-approve'], DEFAULT_TIMEOUTS['document-approve']),
    'document-commit': normalize(value?.['document-commit'], DEFAULT_TIMEOUTS['document-commit']),
  }
}

let fallbackNonce = 0
function defaultNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackNonce += 1
  return `${Date.now().toString(36)}-${fallbackNonce.toString(36)}`
}

export const desktopPhotonCadCommercialClient = new DesktopPhotonCadCommercialClient()
