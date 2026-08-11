import {
  PHOTON_CAD_CONTRACT_VERSION,
  canonicalPhotonCadReleaseBinding,
  isPhotonCadIdentifier,
  validatePhotonCadOperationRequest,
  type PhotonCadController,
  type PhotonCadInputValue,
  type PhotonCadIssue,
  type PhotonCadOperationRequest,
  type PhotonCadOperationResult,
  type PhotonCadPreviewReceipt,
  type PhotonCadProjectSnapshot,
  type PhotonCadReleaseCommitResult,
  type PhotonCadReleaseFormat,
  type PhotonCadReleaseReviewRequest,
  type PhotonCadReleaseReviewResult,
  type PhotonCadRuntimeDescription,
  type PhotonCadVerificationRequest,
  type PhotonCadVerificationResult,
} from './PhotonCadContract'
import { normalizePhotonCadProjectSnapshot } from './DesktopPhotonCadClient'
import {
  PhotonCadOperationGate,
  type PhotonCadBoundReview,
  type PhotonCadExclusiveKind,
  type PhotonCadExclusiveToken,
} from './PhotonCadOperationGate'

const RELEASE_FORMATS = new Set<PhotonCadReleaseFormat>(['step-ap214', 'step-ap242', 'stl', 'dxf', 'svg'])
const DESTINATION_HANDLE = /^cad-destination:[A-Za-z0-9_-]{32,160}$/u

type LifecyclePhotonCadController = PhotonCadController & {
  cancelPending?: () => void
  close?: () => void
}

export type PhotonCadOperationDraft = {
  capabilityId: string
  inputs: Record<string, PhotonCadInputValue>
  targetEntityIds: string[]
}

export type PhotonCadWorkspaceBusyState = {
  describe: boolean
  preview: boolean
  exclusive: PhotonCadExclusiveKind | null
}

export type PhotonCadWorkspaceState = {
  runtime: PhotonCadRuntimeDescription
  snapshot: PhotonCadProjectSnapshot | null
  preview: PhotonCadPreviewReceipt | null
  verification: PhotonCadVerificationResult | null
  releaseReview: PhotonCadReleaseReviewResult | null
  releaseCommit: PhotonCadReleaseCommitResult | null
  issues: PhotonCadIssue[]
  reason: string
  busy: PhotonCadWorkspaceBusyState
  closed: boolean
}

export type PhotonCadWorkspaceControllerOptions = {
  controller: PhotonCadController
  initialSnapshot?: PhotonCadProjectSnapshot | null
  createRequestId?: (kind: string, sequence: number) => string
  now?: () => number
}

export class PhotonCadWorkspaceController {
  private controller: LifecyclePhotonCadController
  private readonly gate = new PhotonCadOperationGate()
  private readonly createId: (kind: string, sequence: number) => string
  private readonly now: () => number
  private readonly requestIds = new Set<string>()
  private readonly listeners = new Set<(state: Readonly<PhotonCadWorkspaceState>) => void>()
  private sequence = 0
  private stateValue: PhotonCadWorkspaceState

  public constructor(options: PhotonCadWorkspaceControllerOptions) {
    this.controller = options.controller
    const nonce = controllerNonce()
    this.createId = options.createRequestId ?? ((kind, sequence) => `cad-workspace-${kind}:${nonce}:${sequence.toString(36)}`)
    this.now = options.now ?? Date.now
    const snapshot = options.initialSnapshot === undefined || options.initialSnapshot === null
      ? null
      : requireSnapshot(options.initialSnapshot)
    if (snapshot) this.gate.transitionContext(contextOf(snapshot))
    this.stateValue = {
      runtime: { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'checking', reason: 'checking' },
      snapshot,
      preview: null,
      verification: null,
      releaseReview: null,
      releaseCommit: null,
      issues: snapshot?.issues ?? [],
      reason: 'checking',
      busy: { describe: false, preview: false, exclusive: null },
      closed: false,
    }
  }

  public get state(): Readonly<PhotonCadWorkspaceState> {
    return this.stateValue
  }

  public subscribe(listener: (state: Readonly<PhotonCadWorkspaceState>) => void) {
    this.listeners.add(listener)
    listener(this.stateValue)
    return () => this.listeners.delete(listener)
  }

  public async refreshRuntime(): Promise<PhotonCadRuntimeDescription | null> {
    const token = this.gate.beginLatest('describe')
    if (!token) return null
    const operationController = this.controller
    this.update({
      runtime: { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'checking', reason: 'checking' },
      reason: 'checking',
      busy: { ...this.stateValue.busy, describe: true },
    })
    try {
      const description = await operationController.describe()
      if (!this.gate.isCurrentLatest(token)) return null
      const runtime = normalizeRuntime(description)
      this.update({ runtime, reason: runtime.reason })
      return runtime
    } catch {
      if (!this.gate.isCurrentLatest(token)) return null
      const runtime: PhotonCadRuntimeDescription = { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'error', reason: 'runtime-check-failed' }
      this.update({ runtime, reason: runtime.reason })
      return runtime
    } finally {
      if (this.gate.isCurrentLatest(token)) this.update({ busy: { ...this.stateValue.busy, describe: false } })
    }
  }

  public async previewOperation(draft: PhotonCadOperationDraft): Promise<PhotonCadOperationResult | null> {
    const snapshot = this.stateValue.snapshot
    if (!snapshot || !this.capabilityAvailable(draft.capabilityId, true)) return this.reportUnavailable()
    const token = this.gate.beginLatest('preview')
    if (!token) return null
    const request = this.createOperationRequest(snapshot, draft, 'suggest', 'preview')
    const operationController = this.controller
    this.update({ preview: null, reason: 'preview-running', busy: { ...this.stateValue.busy, preview: true } })
    try {
      const result = await operationController.execute(request)
      if (!this.gate.isCurrentLatest(token) || !matchesOperationResult(request, result) || result.stale) return null
      if (result.status === 'accepted' && result.preview) {
        this.update({ preview: result.preview, issues: result.issues, reason: result.reason })
      } else {
        this.update({ preview: null, issues: result.issues, reason: result.reason })
      }
      return result
    } catch {
      if (this.gate.isCurrentLatest(token)) this.update({ preview: null, reason: 'preview-failed' })
      return null
    } finally {
      if (this.gate.isCurrentLatest(token)) this.update({ busy: { ...this.stateValue.busy, preview: false } })
    }
  }

  public async applyOperation(draft: PhotonCadOperationDraft): Promise<PhotonCadOperationResult | null> {
    const snapshot = this.stateValue.snapshot
    if (!snapshot || !this.capabilityAvailable(draft.capabilityId, false)) return this.reportUnavailable()
    const request = this.createOperationRequest(snapshot, draft, 'scratch', 'apply')
    if (this.gate.active) return this.reportBusy()
    this.invalidateForEdit()
    const token = this.gate.startExclusive('apply', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({ reason: 'operation-running', busy: { ...this.stateValue.busy, exclusive: 'apply' } })
    try {
      const result = await operationController.execute(request)
      const settlement = this.gate.settleExclusive(token)
      if (settlement !== 'accepted' || !matchesOperationResult(request, result) || result.stale) return null
      if (result.status === 'accepted' && result.snapshot) {
        this.acceptSnapshot(result.snapshot)
      } else {
        this.update({ issues: result.issues, reason: result.reason })
      }
      return result
    } catch {
      if (this.gate.failExclusive(token)) this.update({ reason: 'operation-failed' })
      return null
    } finally {
      this.clearExclusiveIf(token)
    }
  }

  public async verify(checks: PhotonCadVerificationRequest['checks']): Promise<PhotonCadVerificationResult | null> {
    const snapshot = this.stateValue.snapshot
    const normalizedChecks = normalizeChecks(checks)
    if (!snapshot || !normalizedChecks) return this.reportUnavailable()
    const request: PhotonCadVerificationRequest = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: this.nextRequestId('verify'),
      sessionId: snapshot.sessionId,
      projectId: snapshot.projectId,
      revision: snapshot.revision,
      checks: normalizedChecks,
    }
    const token = this.gate.startExclusive('verify', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({ reason: 'verification-running', busy: { ...this.stateValue.busy, exclusive: 'verify' } })
    try {
      const result = await operationController.verify(request)
      const settlement = this.gate.settleExclusive(token)
      if (settlement !== 'accepted' || !matchesVerification(request, result) || result.stale) return null
      this.update({ verification: result, issues: result.issues, reason: result.status === 'passed' ? 'verification-passed' : 'validation-failed' })
      return result
    } catch {
      if (this.gate.failExclusive(token)) this.update({ reason: 'verification-failed' })
      return null
    } finally {
      this.clearExclusiveIf(token)
    }
  }

  public async prepareRelease(formats: PhotonCadReleaseFormat[], destinationHandle: string): Promise<PhotonCadReleaseReviewResult | null> {
    const snapshot = this.stateValue.snapshot
    const normalizedFormats = normalizeFormats(formats)
    if (!snapshot || !normalizedFormats || !DESTINATION_HANDLE.test(destinationHandle)) return this.reportUnavailable('invalid-release-request')
    this.discardReview(this.gate.invalidateReview(), this.controller)
    this.update({ releaseReview: null, releaseCommit: null })
    const request: PhotonCadReleaseReviewRequest = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: this.nextRequestId('review'),
      sessionId: snapshot.sessionId,
      projectId: snapshot.projectId,
      revision: snapshot.revision,
      formats: normalizedFormats,
      destinationHandle,
    }
    const token = this.gate.startExclusive('review', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({ reason: 'review-running', busy: { ...this.stateValue.busy, exclusive: 'review' } })
    try {
      const result = await operationController.reviewRelease(request)
      const settlement = this.gate.settleExclusive(token)
      if (settlement !== 'accepted' || !matchesReview(request, result)) {
        if (result.status === 'ready' && result.reviewHandle) this.discardReviewHandle(result.reviewHandle, operationController)
        return null
      }
      if (result.status === 'ready') {
        const bound = this.gate.bindReview(request, result, this.now())
        if (!bound || bound.requestBinding !== canonicalPhotonCadReleaseBinding(request)) {
          if (result.reviewHandle) this.discardReviewHandle(result.reviewHandle, operationController)
          this.update({ releaseReview: null, reason: 'review-invalid' })
          return null
        }
      }
      this.update({ releaseReview: result, issues: result.issues, reason: result.reason })
      return result
    } catch {
      if (this.gate.failExclusive(token)) this.update({ reason: 'review-failed' })
      return null
    } finally {
      this.clearExclusiveIf(token)
    }
  }

  public async commitRelease(): Promise<PhotonCadReleaseCommitResult | null> {
    const now = this.now()
    if (this.gate.reviewIsExpired(now)) {
      this.discardReview(this.gate.invalidateReview(), this.controller)
      this.update({ releaseReview: null, reason: 'review-expired' })
      return null
    }
    const review = this.gate.currentReview(now)
    if (!review) return this.reportUnavailable('review-required')
    const request = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: this.nextRequestId('commit'),
      reviewHandle: review.reviewHandle,
      packageFingerprint: review.packageFingerprint,
    } as const
    const token = this.gate.startExclusive('commit', request.requestId)
    if (!token) return this.reportBusy()
    const consumed = this.gate.consumeReview()
    if (!consumed) {
      this.gate.failExclusive(token)
      return this.reportUnavailable('review-required')
    }
    const operationController = this.controller
    this.update({ releaseReview: null, releaseCommit: null, reason: 'commit-running', busy: { ...this.stateValue.busy, exclusive: 'commit' } })
    try {
      const result = await operationController.commitRelease(request)
      const settlement = this.gate.settleExclusive(token)
      if (settlement !== 'accepted' || result.requestId !== request.requestId
        || (result.packageFingerprint !== undefined && result.packageFingerprint.toLowerCase() !== consumed.packageFingerprint)) return null
      if (result.status !== 'committed') this.discardReview(consumed, operationController)
      this.update({ releaseCommit: result, reason: result.reason })
      return result
    } catch {
      this.discardReview(consumed, operationController)
      if (this.gate.failExclusive(token)) this.update({ reason: 'commit-failed' })
      return null
    } finally {
      this.clearExclusiveIf(token)
    }
  }

  public markEdited() {
    if (this.stateValue.closed) return
    this.controller.cancelPending?.()
    this.invalidateForEdit()
  }

  public setSnapshot(value: PhotonCadProjectSnapshot) {
    if (this.stateValue.closed) return
    const snapshot = requireSnapshot(value)
    const changed = !sameSnapshotIdentity(this.stateValue.snapshot, snapshot)
    if (changed) this.controller.cancelPending?.()
    const invalidated = this.gate.transitionContext(contextOf(snapshot))
    this.discardReview(invalidated, this.controller)
    this.update({
      snapshot,
      preview: changed ? null : this.stateValue.preview,
      verification: changed ? null : this.stateValue.verification,
      releaseReview: invalidated ? null : this.stateValue.releaseReview,
      releaseCommit: changed ? null : this.stateValue.releaseCommit,
      issues: snapshot.issues,
      reason: changed ? 'project-updated' : this.stateValue.reason,
      busy: changed ? { ...this.stateValue.busy, preview: false } : this.stateValue.busy,
    })
  }

  public replaceController(value: PhotonCadController) {
    if (this.stateValue.closed || value === this.controller) return
    const previous = this.controller
    previous.cancelPending?.()
    const invalidated = this.gate.replaceController()
    this.discardReview(invalidated, previous)
    this.controller = value
    this.update({
      runtime: { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'checking', reason: 'checking' },
      preview: null,
      verification: null,
      releaseReview: null,
      releaseCommit: null,
      reason: 'controller-changed',
      busy: { describe: false, preview: false, exclusive: null },
    })
  }

  public close() {
    if (this.stateValue.closed) return
    const controller = this.controller
    controller.cancelPending?.()
    this.discardReview(this.gate.close(), controller)
    controller.close?.()
    this.update({
      preview: null,
      releaseReview: null,
      reason: 'client-closed',
      busy: { describe: false, preview: false, exclusive: null },
      closed: true,
    })
    this.listeners.clear()
  }

  private createOperationRequest(snapshot: PhotonCadProjectSnapshot, draft: PhotonCadOperationDraft, mode: PhotonCadOperationRequest['mode'], kind: string) {
    const request: PhotonCadOperationRequest = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: this.nextRequestId(kind),
      sessionId: snapshot.sessionId,
      projectId: snapshot.projectId,
      baseRevision: snapshot.revision,
      mode,
      capabilityId: draft.capabilityId,
      inputs: Object.fromEntries(Object.entries(draft.inputs).map(([key, value]) => [key, cloneInput(value)])),
      targetEntityIds: [...draft.targetEntityIds],
    }
    const issues = validatePhotonCadOperationRequest(request)
    if (issues.length) throw new Error(`Invalid Photon CAD operation request (${issues.join(',')}).`)
    return request
  }

  private nextRequestId(kind: string) {
    this.sequence += 1
    const requestId = this.createId(kind, this.sequence)
    if (!isPhotonCadIdentifier(requestId) || this.requestIds.has(requestId)) throw new Error('Photon CAD request identity is invalid or repeated.')
    this.requestIds.add(requestId)
    return requestId
  }

  private capabilityAvailable(capabilityId: string, requirePreview: boolean) {
    if (this.stateValue.runtime.status !== 'available' || !isPhotonCadIdentifier(capabilityId)) return false
    const capability = this.stateValue.runtime.catalog?.capabilities.find((item) => item.id === capabilityId)
    return capability !== undefined && (!requirePreview || capability.previewSupported)
  }

  private invalidateForEdit() {
    const invalidated = this.gate.markEdited()
    this.discardReview(invalidated, this.controller)
    this.update({ preview: null, releaseReview: null, releaseCommit: null, reason: 'project-edited', busy: { ...this.stateValue.busy, preview: false } })
  }

  private acceptSnapshot(value: PhotonCadProjectSnapshot) {
    const snapshot = requireSnapshot(value)
    const invalidated = this.gate.transitionContext(contextOf(snapshot))
    this.discardReview(invalidated, this.controller)
    this.controller.cancelPending?.()
    this.update({
      snapshot,
      preview: null,
      verification: null,
      releaseReview: null,
      releaseCommit: null,
      issues: snapshot.issues,
      reason: 'operation-accepted',
    })
  }

  private clearExclusiveIf(token: PhotonCadExclusiveToken) {
    if (this.gate.active?.operationGeneration === token.operationGeneration) return
    if (this.stateValue.busy.exclusive === token.kind) this.update({ busy: { ...this.stateValue.busy, exclusive: null } })
  }

  private discardReview(review: PhotonCadBoundReview | null, controller: PhotonCadController) {
    if (review) this.discardReviewHandle(review.reviewHandle, controller)
  }

  private discardReviewHandle(reviewHandle: string, controller: PhotonCadController) {
    try {
      void Promise.resolve(controller.discardRelease(reviewHandle)).catch(() => undefined)
    } catch {
      // Review handles are opaque and short-lived; invalidation remains local even if discard transport fails.
    }
  }

  private reportUnavailable(reason = 'unavailable'): null {
    this.update({ reason })
    return null
  }

  private reportBusy(): null {
    this.update({ reason: 'operation-busy' })
    return null
  }

  private update(patch: Partial<PhotonCadWorkspaceState>) {
    this.stateValue = { ...this.stateValue, ...patch }
    this.listeners.forEach((listener) => listener(this.stateValue))
  }
}

function normalizeRuntime(value: PhotonCadRuntimeDescription): PhotonCadRuntimeDescription {
  if (value.contractVersion !== PHOTON_CAD_CONTRACT_VERSION
    || !['available', 'unavailable', 'checking', 'error'].includes(value.status)
    || !isPhotonCadIdentifier(value.reason)) {
    return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'error', reason: 'malformed-runtime-description' }
  }
  if (value.status === 'available' && (!value.geometryBundleId || !value.catalog)) {
    return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'error', reason: 'malformed-runtime-description' }
  }
  return value
}

function requireSnapshot(value: PhotonCadProjectSnapshot) {
  const snapshot = normalizePhotonCadProjectSnapshot(value)
  if (!snapshot) throw new Error('Photon CAD project snapshot is invalid.')
  return snapshot
}

function contextOf(snapshot: PhotonCadProjectSnapshot) {
  return { sessionId: snapshot.sessionId, projectId: snapshot.projectId, revision: snapshot.revision }
}

function sameSnapshotIdentity(left: PhotonCadProjectSnapshot | null, right: PhotonCadProjectSnapshot) {
  return left?.sessionId === right.sessionId && left.projectId === right.projectId && left.revision === right.revision
}

function cloneInput(value: PhotonCadInputValue): PhotonCadInputValue {
  if (Array.isArray(value)) return [...value]
  if (value && typeof value === 'object') return { ...value }
  return value
}

function matchesOperationResult(request: PhotonCadOperationRequest, result: PhotonCadOperationResult) {
  if (result.requestId !== request.requestId || result.projectId !== request.projectId || result.baseRevision !== request.baseRevision) return false
  if (request.mode === 'suggest') return result.resultingRevision === request.baseRevision
    && (!result.preview || result.preview.revision === request.baseRevision)
  if (result.status === 'accepted') return result.resultingRevision > request.baseRevision
    && result.snapshot?.sessionId === request.sessionId
    && result.snapshot.projectId === request.projectId
    && result.snapshot.revision === result.resultingRevision
  return result.resultingRevision === request.baseRevision && result.snapshot === undefined && result.preview === undefined
}

function matchesVerification(request: PhotonCadVerificationRequest, result: PhotonCadVerificationResult) {
  return result.requestId === request.requestId && result.projectId === request.projectId && result.revision === request.revision
}

function matchesReview(request: PhotonCadReleaseReviewRequest, result: PhotonCadReleaseReviewResult) {
  return result.requestId === request.requestId && result.projectId === request.projectId && result.revision === request.revision
}

function normalizeChecks(checks: PhotonCadVerificationRequest['checks']) {
  const allowed = new Set<PhotonCadVerificationRequest['checks'][number]>(['valid-solids', 'interference', 'dimensions', 'assembly-structure', 'export-readiness'])
  const normalized = [...new Set(checks)]
  return normalized.length && normalized.every((check) => allowed.has(check)) ? normalized : null
}

function normalizeFormats(formats: PhotonCadReleaseFormat[]) {
  const normalized = [...new Set(formats)].sort()
  return normalized.length && normalized.every((format) => RELEASE_FORMATS.has(format)) ? normalized : null
}

let fallbackControllerNonce = 0
function controllerNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackControllerNonce += 1
  return `${Date.now().toString(36)}-${fallbackControllerNonce.toString(36)}`
}
