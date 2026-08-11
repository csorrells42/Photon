import {
  PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
  canonicalPhotonCadCommercialDraft,
  validatePhotonCadBomExportReviewRequest,
  validatePhotonCadCommercialReviewRequest,
  type PhotonCadBomExportCommitResult,
  type PhotonCadBomExportFormat,
  type PhotonCadBomExportReviewResult,
  type PhotonCadCommercialApprovalResult,
  type PhotonCadCommercialCommitResult,
  type PhotonCadCommercialController as PhotonCadCommercialTransport,
  type PhotonCadCommercialDraft,
  type PhotonCadCommercialOutputAction,
  type PhotonCadCommercialPreviewPage,
  type PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'
import { isPhotonCadDigest, isPhotonCadIdentifier } from './PhotonCadContract'
import {
  PhotonCadCommercialOperationGate,
  canonicalBomRequest,
  type PhotonCadCommercialContext,
  type PhotonCadCommercialExclusiveKind,
  type PhotonCadCommercialToken,
  type PhotonCadInvalidatedCommercialBindings,
} from './PhotonCadCommercialOperationGate'

const MAXIMUM_REQUEST_HISTORY = 50_000

type LifecycleCommercialTransport = PhotonCadCommercialTransport & {
  cancelPending?: () => void
  close?: () => void
}

export type PhotonCadCommercialState = {
  context: PhotonCadCommercialContext
  bomReview: PhotonCadBomExportReviewResult | null
  bomCommit: PhotonCadBomExportCommitResult | null
  documentReview: PhotonCadCommercialReviewResult | null
  renderedPageNumbers: number[]
  documentApproval: PhotonCadCommercialApprovalResult | null
  documentCommit: PhotonCadCommercialCommitResult | null
  reason: string
  busy: PhotonCadCommercialExclusiveKind | null
  closed: boolean
}

export type PhotonCadCommercialControllerOptions = {
  controller: PhotonCadCommercialTransport
  context: PhotonCadCommercialContext
  createRequestId?: (kind: string, sequence: number) => string
  now?: () => number
}

export class PhotonCadCommercialController {
  private controller: LifecycleCommercialTransport
  private readonly gate = new PhotonCadCommercialOperationGate()
  private readonly createId: (kind: string, sequence: number) => string
  private readonly now: () => number
  private readonly requestIds = new Set<string>()
  private readonly listeners = new Set<(state: Readonly<PhotonCadCommercialState>) => void>()
  private sequence = 0
  private stateValue: PhotonCadCommercialState

  public constructor(options: PhotonCadCommercialControllerOptions) {
    this.controller = options.controller
    const nonce = commercialControllerNonce()
    this.createId = options.createRequestId ?? ((kind, sequence) => `cad-commercial-workspace-${kind}:${nonce}:${sequence.toString(36)}`)
    this.now = options.now ?? Date.now
    this.gate.transitionContext(options.context)
    this.stateValue = {
      context: normalizedContext(options.context),
      bomReview: null,
      bomCommit: null,
      documentReview: null,
      renderedPageNumbers: [],
      documentApproval: null,
      documentCommit: null,
      reason: 'ready',
      busy: null,
      closed: false,
    }
  }

  public get state(): Readonly<PhotonCadCommercialState> {
    return this.stateValue
  }

  public subscribe(listener: (state: Readonly<PhotonCadCommercialState>) => void) {
    this.listeners.add(listener)
    listener(this.stateValue)
    return () => this.listeners.delete(listener)
  }

  public async prepareBomExport(format: PhotonCadBomExportFormat, destinationHandle: string): Promise<PhotonCadBomExportReviewResult | null> {
    if (this.stateValue.closed) return this.reportUnavailable('client-closed')
    const previous = this.gate.invalidateBomReview()
    if (previous) this.discardBom(previous.reviewHandle, this.controller)
    const context = this.stateValue.context
    const request = {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: this.nextRequestId('bom-review'),
      projectId: context.projectId,
      projectRevision: context.projectRevision,
      bomDigest: context.bomDigest,
      format,
      destinationHandle,
    } as const
    if (!validatePhotonCadBomExportReviewRequest(request)) return this.reportUnavailable('invalid-bom-export-request')
    const expectedBinding = canonicalBomRequest(request)
    const token = this.gate.start('bom-review', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({ bomReview: null, bomCommit: null, reason: 'bom-review-running', busy: 'bom-review' })
    try {
      const result = await operationController.reviewBomExport(request)
      const settlement = this.gate.settle(token)
      if (settlement !== 'accepted' || !matchesBomReview(request, result)) {
        if (result.status === 'ready' && result.reviewHandle) this.discardBom(result.reviewHandle, operationController)
        return null
      }
      if (result.status === 'ready') {
        const bound = this.gate.bindBomReview(request, result)
        if (!bound || bound.requestBinding !== expectedBinding) {
          if (result.reviewHandle) this.discardBom(result.reviewHandle, operationController)
          this.update({ reason: 'bom-review-invalid' })
          return null
        }
      }
      this.update({ bomReview: result, reason: result.reason })
      return result
    } catch {
      if (this.gate.fail(token)) this.update({ reason: 'bom-review-failed' })
      return null
    } finally {
      this.clearBusyIf(token)
    }
  }

  public async commitReviewedBomExport(): Promise<PhotonCadBomExportCommitResult | null> {
    if (this.stateValue.closed) return this.reportUnavailable('client-closed')
    const review = this.gate.currentBomReview()
    if (!review) return this.reportUnavailable('bom-review-required')
    const request = {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: this.nextRequestId('bom-commit'),
      reviewHandle: review.reviewHandle,
      exportFingerprint: review.exportFingerprint,
    } as const
    const token = this.gate.start('bom-commit', request.requestId)
    if (!token) return this.reportBusy()
    const consumed = this.gate.consumeBomReview()
    if (!consumed) {
      this.gate.fail(token)
      return this.reportUnavailable('bom-review-required')
    }
    const operationController = this.controller
    this.update({ bomReview: null, bomCommit: null, reason: 'bom-commit-running', busy: 'bom-commit' })
    try {
      const result = await operationController.commitBomExport(request)
      const settlement = this.gate.settle(token)
      if (settlement !== 'accepted' || !matchesBomCommit(request, result)) {
        this.discardBom(consumed.reviewHandle, operationController)
        return null
      }
      if (result.status !== 'exported') this.discardBom(consumed.reviewHandle, operationController)
      this.update({ bomCommit: result, reason: result.reason })
      return result
    } catch {
      this.discardBom(consumed.reviewHandle, operationController)
      if (this.gate.fail(token)) this.update({ reason: 'bom-commit-failed' })
      return null
    } finally {
      this.clearBusyIf(token)
    }
  }

  public async prepareCommercialDocument(
    draft: PhotonCadCommercialDraft,
    action: PhotonCadCommercialOutputAction,
  ): Promise<PhotonCadCommercialReviewResult | null> {
    if (this.stateValue.closed) return this.reportUnavailable('client-closed')
    this.discardDocumentInvalidation(this.gate.invalidateDocumentBindings(), this.controller)
    const request = {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: this.nextRequestId('document-review'),
      draft: cloneDraft(draft),
      action: cloneAction(action),
    } as const
    if (!validatePhotonCadCommercialReviewRequest(request)
      || request.draft.projectId !== this.stateValue.context.projectId
      || request.draft.projectRevision !== this.stateValue.context.projectRevision
      || request.draft.bomDigest.toLowerCase() !== this.stateValue.context.bomDigest) {
      return this.reportUnavailable('invalid-commercial-review-request')
    }
    const expectedDraftBinding = canonicalPhotonCadCommercialDraft(request.draft)
    const token = this.gate.start('document-review', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({
      documentReview: null,
      renderedPageNumbers: [],
      documentApproval: null,
      documentCommit: null,
      reason: 'document-review-running',
      busy: 'document-review',
    })
    try {
      const result = await operationController.reviewCommercialDocument(request)
      const settlement = this.gate.settle(token)
      if (settlement !== 'accepted' || result.requestId !== request.requestId) {
        if (result.status === 'ready' && result.reviewHandle) this.discardDocument(result.reviewHandle, operationController)
        return null
      }
      if (result.status === 'ready') {
        const bound = this.gate.bindDocumentReview(request, result, this.now())
        if (!bound || bound.draftBinding !== expectedDraftBinding) {
          if (result.reviewHandle) this.discardDocument(result.reviewHandle, operationController)
          this.update({ reason: 'document-review-invalid' })
          return null
        }
      }
      this.update({ documentReview: result, reason: result.reason })
      return result
    } catch {
      if (this.gate.fail(token)) this.update({ reason: 'document-review-failed' })
      return null
    } finally {
      this.clearBusyIf(token)
    }
  }

  public recordRenderedPage(page: PhotonCadCommercialPreviewPage) {
    if (this.stateValue.closed || !this.gate.recordRenderedPage(page)) return false
    const review = this.gate.currentDocumentReview(this.now())
    if (!review) return false
    const renderedPageNumbers = review.pages
      .filter((candidate) => review.renderedPageKeys.has(`${candidate.pageNumber}:${candidate.contentDigest.toLowerCase()}`))
      .map((candidate) => candidate.pageNumber)
    this.update({ renderedPageNumbers, reason: this.gate.allPagesRendered(this.now()) ? 'all-pages-reviewed' : 'page-reviewed' })
    return true
  }

  public async approveRenderedCommercialDocument(): Promise<PhotonCadCommercialApprovalResult | null> {
    if (this.stateValue.closed) return this.reportUnavailable('client-closed')
    const now = this.now()
    const review = this.gate.currentDocumentReview(now)
    const viewedPageDigests = this.gate.orderedRenderedPageDigests(now)
    if (!review || !viewedPageDigests) {
      if (this.stateValue.documentReview?.status === 'ready' && !review) {
        this.invalidateExpiredDocumentReview()
        return null
      }
      return this.reportUnavailable(review ? 'all-pages-review-required' : 'document-review-required')
    }
    const request = {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: this.nextRequestId('document-approve'),
      reviewHandle: review.reviewHandle,
      documentFingerprint: review.documentFingerprint,
      actionFingerprint: review.actionFingerprint,
      viewedPageDigests,
    } as const
    const token = this.gate.start('document-approve', request.requestId)
    if (!token) return this.reportBusy()
    const operationController = this.controller
    this.update({ documentApproval: null, documentCommit: null, reason: 'document-approval-running', busy: 'document-approve' })
    try {
      const result = await operationController.approveCommercialDocument(request)
      const settlement = this.gate.settle(token)
      if (settlement !== 'accepted' || result.requestId !== request.requestId) {
        if (result.status === 'approved' && result.approvalHandle) this.discardDocument(result.approvalHandle, operationController)
        return null
      }
      if (result.status === 'approved') {
        const bound = this.gate.bindDocumentApproval(request, result, this.now())
        if (!bound) {
          if (result.approvalHandle) this.discardDocument(result.approvalHandle, operationController)
          this.discardDocumentInvalidation(this.gate.invalidateDocumentBindings(), operationController)
          this.update({ documentReview: null, renderedPageNumbers: [], documentApproval: null })
          this.update({ reason: 'document-approval-invalid' })
          return null
        }
      } else {
        const invalidated = this.gate.invalidateDocumentBindings()
        this.discardDocumentInvalidation(invalidated, operationController)
      }
      this.update({
        documentReview: null,
        renderedPageNumbers: [],
        documentApproval: result,
        reason: result.reason,
      })
      return result
    } catch {
      if (this.gate.fail(token)) {
        this.discardDocumentInvalidation(this.gate.invalidateDocumentBindings(), operationController)
        this.update({ documentReview: null, renderedPageNumbers: [], documentApproval: null, reason: 'document-approval-failed' })
      }
      return null
    } finally {
      this.clearBusyIf(token)
    }
  }

  public async commitApprovedCommercialDocument(): Promise<PhotonCadCommercialCommitResult | null> {
    if (this.stateValue.closed) return this.reportUnavailable('client-closed')
    const approval = this.gate.currentDocumentApproval(this.now())
    if (!approval) {
      if (this.stateValue.documentApproval?.status === 'approved') {
        this.invalidateExpiredDocumentApproval()
        return null
      }
      return this.reportUnavailable('document-approval-required')
    }
    const expectedStatus = this.gate.expectedCommitStatus()
    const request = {
      contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
      requestId: this.nextRequestId('document-commit'),
      approvalHandle: approval.approvalHandle,
      documentFingerprint: approval.documentFingerprint,
      actionFingerprint: approval.actionFingerprint,
    } as const
    const token = this.gate.start('document-commit', request.requestId)
    if (!token) return this.reportBusy()
    const consumed = this.gate.consumeDocumentApproval()
    if (!consumed || !expectedStatus) {
      this.gate.fail(token)
      return this.reportUnavailable('document-approval-required')
    }
    const operationController = this.controller
    this.update({ documentApproval: null, documentCommit: null, reason: 'document-commit-running', busy: 'document-commit' })
    try {
      const result = await operationController.commitCommercialDocument(request)
      const settlement = this.gate.settle(token)
      if (settlement !== 'accepted' || !matchesDocumentCommit(request, result, expectedStatus)) {
        this.discardDocument(consumed.approvalHandle, operationController)
        return null
      }
      if (result.status !== expectedStatus) this.discardDocument(consumed.approvalHandle, operationController)
      this.update({ documentCommit: result, reason: result.reason })
      return result
    } catch {
      this.discardDocument(consumed.approvalHandle, operationController)
      if (this.gate.fail(token)) this.update({ reason: 'document-commit-failed' })
      return null
    } finally {
      this.clearBusyIf(token)
    }
  }

  public markEdited() {
    if (this.stateValue.closed) return
    this.controller.cancelPending?.()
    this.discardInvalidated(this.gate.markEdited(), this.controller)
    this.clearCommercialState('commercial-draft-edited')
  }

  public setContext(next: PhotonCadCommercialContext) {
    if (this.stateValue.closed) return
    const normalized = normalizedContext(next)
    if (sameContext(this.stateValue.context, normalized)) return
    this.controller.cancelPending?.()
    this.discardInvalidated(this.gate.transitionContext(normalized), this.controller)
    this.stateValue = { ...this.stateValue, context: normalized }
    this.clearCommercialState('commercial-context-changed')
  }

  public replaceController(next: PhotonCadCommercialTransport) {
    if (this.stateValue.closed || next === this.controller) return
    const previous = this.controller
    previous.cancelPending?.()
    this.discardInvalidated(this.gate.replaceController(), previous)
    this.controller = next
    this.clearCommercialState('commercial-controller-changed')
  }

  public close() {
    if (this.stateValue.closed) return
    const operationController = this.controller
    operationController.cancelPending?.()
    this.discardInvalidated(this.gate.close(), operationController)
    operationController.close?.()
    this.update({
      bomReview: null,
      documentReview: null,
      renderedPageNumbers: [],
      documentApproval: null,
      reason: 'client-closed',
      busy: null,
      closed: true,
    })
    this.listeners.clear()
  }

  private invalidateExpiredDocumentReview() {
    const invalidated = this.gate.invalidateDocumentBindings()
    this.discardDocumentInvalidation(invalidated, this.controller)
    this.update({ documentReview: null, renderedPageNumbers: [], documentApproval: null, reason: 'document-review-expired' })
  }

  private invalidateExpiredDocumentApproval() {
    const invalidated = this.gate.invalidateDocumentBindings()
    this.discardDocumentInvalidation(invalidated, this.controller)
    this.update({ documentApproval: null, reason: 'document-approval-expired' })
  }

  private clearCommercialState(reason: string) {
    this.update({
      bomReview: null,
      bomCommit: null,
      documentReview: null,
      renderedPageNumbers: [],
      documentApproval: null,
      documentCommit: null,
      reason,
      busy: null,
    })
  }

  private nextRequestId(kind: string) {
    this.sequence += 1
    const requestId = this.createId(kind, this.sequence)
    if (!isPhotonCadIdentifier(requestId) || this.requestIds.has(requestId)
      || this.requestIds.size >= MAXIMUM_REQUEST_HISTORY) throw new Error('Photon CAD commercial request identity is invalid, repeated, or exhausted.')
    this.requestIds.add(requestId)
    return requestId
  }

  private clearBusyIf(token: PhotonCadCommercialToken) {
    if (this.gate.active) return
    if (this.stateValue.busy === token.kind) this.update({ busy: null })
  }

  private discardInvalidated(invalidated: PhotonCadInvalidatedCommercialBindings, controller: PhotonCadCommercialTransport) {
    if (invalidated.bom) this.discardBom(invalidated.bom.reviewHandle, controller)
    this.discardDocumentInvalidation(invalidated, controller)
  }

  private discardDocumentInvalidation(
    invalidated: Pick<PhotonCadInvalidatedCommercialBindings, 'documentReview' | 'documentApproval'>,
    controller: PhotonCadCommercialTransport,
  ) {
    if (invalidated.documentReview) this.discardDocument(invalidated.documentReview.reviewHandle, controller)
    if (invalidated.documentApproval) this.discardDocument(invalidated.documentApproval.approvalHandle, controller)
  }

  private discardBom(handle: string, controller: PhotonCadCommercialTransport) {
    try {
      void Promise.resolve(controller.discardBomExport(handle)).catch(() => undefined)
    } catch {
      // Opaque review handles are unusable after local invalidation and expire at the host.
    }
  }

  private discardDocument(handle: string, controller: PhotonCadCommercialTransport) {
    try {
      void Promise.resolve(controller.discardCommercialDocument(handle)).catch(() => undefined)
    } catch {
      // Opaque review and approval handles are unusable after local invalidation and expire at the host.
    }
  }

  private reportUnavailable(reason: string): null {
    this.update({ reason })
    return null
  }

  private reportBusy(): null {
    this.update({ reason: 'commercial-operation-busy' })
    return null
  }

  private update(patch: Partial<PhotonCadCommercialState>) {
    this.stateValue = { ...this.stateValue, ...patch }
    this.listeners.forEach((listener) => listener(this.stateValue))
  }
}

function normalizedContext(context: PhotonCadCommercialContext): PhotonCadCommercialContext {
  if (!isPhotonCadIdentifier(context.sessionId) || !isPhotonCadIdentifier(context.projectId)
    || !Number.isSafeInteger(context.projectRevision) || context.projectRevision < 0) {
    throw new Error('Photon CAD commercial context is invalid.')
  }
  if (!isPhotonCadDigest(context.bomDigest)) throw new Error('Photon CAD commercial BOM identity is invalid.')
  return { ...context, bomDigest: context.bomDigest.toLowerCase() }
}

function sameContext(left: PhotonCadCommercialContext, right: PhotonCadCommercialContext) {
  return left.sessionId === right.sessionId && left.projectId === right.projectId
    && left.projectRevision === right.projectRevision && left.bomDigest === right.bomDigest
}

function matchesBomReview(
  request: { requestId: string; projectId: string; projectRevision: number },
  result: PhotonCadBomExportReviewResult,
) {
  return result.requestId === request.requestId && result.projectId === request.projectId && result.projectRevision === request.projectRevision
}

function matchesBomCommit(
  request: { requestId: string; exportFingerprint: string },
  result: PhotonCadBomExportCommitResult,
) {
  return result.requestId === request.requestId
    && (result.exportFingerprint === undefined || result.exportFingerprint.toLowerCase() === request.exportFingerprint.toLowerCase())
}

function matchesDocumentCommit(
  request: { requestId: string; documentFingerprint: string; actionFingerprint: string },
  result: PhotonCadCommercialCommitResult,
  expectedStatus: 'exported' | 'printed',
) {
  if (result.requestId !== request.requestId) return false
  if (result.status === 'exported' || result.status === 'printed') {
    return result.status === expectedStatus
      && result.documentFingerprint?.toLowerCase() === request.documentFingerprint.toLowerCase()
      && result.actionFingerprint?.toLowerCase() === request.actionFingerprint.toLowerCase()
  }
  return result.documentFingerprint === undefined && result.actionFingerprint === undefined
}

function cloneDraft(draft: PhotonCadCommercialDraft): PhotonCadCommercialDraft {
  return {
    ...draft,
    seller: { ...draft.seller, addressLines: [...draft.seller.addressLines] },
    customer: { ...draft.customer, addressLines: [...draft.customer.addressLines] },
    lines: draft.lines.map((line) => ({ ...line })),
  }
}

function cloneAction(action: PhotonCadCommercialOutputAction): PhotonCadCommercialOutputAction {
  return action.kind === 'print'
    ? { kind: 'print', printerHandle: action.printerHandle, copies: action.copies }
    : { kind: 'export-pdf', destinationHandle: action.destinationHandle }
}

let fallbackNonce = 0
function commercialControllerNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackNonce += 1
  return `${Date.now().toString(36)}-${fallbackNonce.toString(36)}`
}
