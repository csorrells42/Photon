import {
  canonicalPhotonCadCommercialDraft,
  isPhotonCadBomReviewHandle,
  isPhotonCadCommercialApprovalHandle,
  isPhotonCadCommercialReviewHandle,
  validatePhotonCadCommercialApprovalRequest,
  validatePhotonCadCommercialReviewResult,
  type PhotonCadBomExportReviewRequest,
  type PhotonCadBomExportReviewResult,
  type PhotonCadCommercialApprovalRequest,
  type PhotonCadCommercialApprovalResult,
  type PhotonCadCommercialCommitResult,
  type PhotonCadCommercialOutputAction,
  type PhotonCadCommercialPreviewPage,
  type PhotonCadCommercialReviewRequest,
  type PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'
import { isPhotonCadDigest, isPhotonCadIdentifier } from './PhotonCadContract'

export type PhotonCadCommercialContext = {
  sessionId: string
  projectId: string
  projectRevision: number
  bomDigest: string
}

export type PhotonCadCommercialExclusiveKind = 'bom-review' | 'bom-commit' | 'document-review' | 'document-approve' | 'document-commit'
export type PhotonCadCommercialToken = {
  kind: PhotonCadCommercialExclusiveKind
  requestId: string
  operationGeneration: number
  controllerGeneration: number
  contextGeneration: number
  context: PhotonCadCommercialContext
}
export type PhotonCadCommercialSettlement = 'accepted' | 'stale' | 'foreign'

export type PhotonCadBoundBomReview = {
  reviewHandle: string
  exportFingerprint: string
  requestBinding: string
  format: PhotonCadBomExportReviewRequest['format']
  destinationHandle: string
  context: PhotonCadCommercialContext
  controllerGeneration: number
  contextGeneration: number
}

export type PhotonCadBoundDocumentReview = {
  reviewHandle: string
  documentFingerprint: string
  actionFingerprint: string
  expiresAtUtc: string
  draftBinding: string
  actionBinding: string
  action: PhotonCadCommercialOutputAction
  pages: PhotonCadCommercialPreviewPage[]
  renderedPageKeys: Set<string>
  context: PhotonCadCommercialContext
  controllerGeneration: number
  contextGeneration: number
}

export type PhotonCadBoundDocumentApproval = {
  approvalHandle: string
  documentFingerprint: string
  actionFingerprint: string
  expiresAtUtc: string
  draftBinding: string
  actionBinding: string
  action: PhotonCadCommercialOutputAction
  context: PhotonCadCommercialContext
  controllerGeneration: number
  contextGeneration: number
}

export type PhotonCadInvalidatedCommercialBindings = {
  bom: PhotonCadBoundBomReview | null
  documentReview: PhotonCadBoundDocumentReview | null
  documentApproval: PhotonCadBoundDocumentApproval | null
}

export class PhotonCadCommercialOperationGate {
  private context: PhotonCadCommercialContext | null = null
  private controllerGeneration = 0
  private contextGeneration = 0
  private operationGeneration = 0
  private activeOperation: PhotonCadCommercialToken | null = null
  private bomReview: PhotonCadBoundBomReview | null = null
  private documentReview: PhotonCadBoundDocumentReview | null = null
  private documentApproval: PhotonCadBoundDocumentApproval | null = null
  private closed = false

  public get active(): Readonly<PhotonCadCommercialToken> | null {
    return this.activeOperation
  }

  public get currentContext(): Readonly<PhotonCadCommercialContext> | null {
    return this.context
  }

  public transitionContext(next: PhotonCadCommercialContext): PhotonCadInvalidatedCommercialBindings {
    validateContext(next)
    if (sameContext(this.context, next)) return emptyInvalidation()
    const invalidated = this.invalidateAll()
    this.context = { ...next, bomDigest: next.bomDigest.toLowerCase() }
    this.contextGeneration += 1
    return invalidated
  }

  public markEdited(): PhotonCadInvalidatedCommercialBindings {
    if (this.closed) return emptyInvalidation()
    const invalidated = this.invalidateAll()
    this.contextGeneration += 1
    return invalidated
  }

  public replaceController(): PhotonCadInvalidatedCommercialBindings {
    if (this.closed) return emptyInvalidation()
    const invalidated = this.invalidateAll()
    this.controllerGeneration += 1
    this.contextGeneration += 1
    this.activeOperation = null
    return invalidated
  }

  public start(kind: PhotonCadCommercialExclusiveKind, requestId: string): PhotonCadCommercialToken | null {
    if (this.closed || !this.context || this.activeOperation || !isPhotonCadIdentifier(requestId)) return null
    this.operationGeneration += 1
    this.activeOperation = {
      kind,
      requestId,
      operationGeneration: this.operationGeneration,
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
      context: { ...this.context },
    }
    return this.activeOperation
  }

  public settle(token: PhotonCadCommercialToken): PhotonCadCommercialSettlement {
    if (!sameToken(this.activeOperation, token)) return 'foreign'
    this.activeOperation = null
    return this.isCurrentToken(token) ? 'accepted' : 'stale'
  }

  public fail(token: PhotonCadCommercialToken) {
    if (!sameToken(this.activeOperation, token)) return false
    this.activeOperation = null
    return true
  }

  public bindBomReview(request: PhotonCadBomExportReviewRequest, result: PhotonCadBomExportReviewResult): PhotonCadBoundBomReview | null {
    if (this.closed || this.bomReview || !this.context || result.status !== 'ready'
      || !isPhotonCadBomReviewHandle(result.reviewHandle) || !isPhotonCadDigest(result.exportFingerprint)
      || result.requestId !== request.requestId
      || request.projectId !== this.context.projectId || request.projectRevision !== this.context.projectRevision
      || request.bomDigest.toLowerCase() !== this.context.bomDigest
      || result.projectId !== request.projectId || result.projectRevision !== request.projectRevision) return null
    this.bomReview = {
      reviewHandle: result.reviewHandle,
      exportFingerprint: result.exportFingerprint.toLowerCase(),
      requestBinding: canonicalBomRequest(request),
      format: request.format,
      destinationHandle: request.destinationHandle,
      context: { ...this.context },
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
    }
    return this.bomReview
  }

  public currentBomReview(): Readonly<PhotonCadBoundBomReview> | null {
    return this.bomReview && this.isCurrentBinding(this.bomReview) ? this.bomReview : null
  }

  public consumeBomReview() {
    const review = this.bomReview
    this.bomReview = null
    return review
  }

  public invalidateBomReview() {
    const review = this.bomReview
    this.bomReview = null
    return review
  }

  public bindDocumentReview(
    request: PhotonCadCommercialReviewRequest,
    result: PhotonCadCommercialReviewResult,
    nowEpochMilliseconds: number,
  ): PhotonCadBoundDocumentReview | null {
    if (this.closed || this.documentReview || this.documentApproval || !this.context || result.status !== 'ready'
      || !validatePhotonCadCommercialReviewResult(result)
      || result.requestId !== request.requestId
      || !isPhotonCadCommercialReviewHandle(result.reviewHandle)
      || !isPhotonCadDigest(result.documentFingerprint) || !isPhotonCadDigest(result.actionFingerprint)
      || !result.expiresAtUtc || !Number.isFinite(Date.parse(result.expiresAtUtc)) || Date.parse(result.expiresAtUtc) <= nowEpochMilliseconds
      || request.draft.projectId !== this.context.projectId || request.draft.projectRevision !== this.context.projectRevision
      || request.draft.bomDigest.toLowerCase() !== this.context.bomDigest) return null
    this.documentReview = {
      reviewHandle: result.reviewHandle,
      documentFingerprint: result.documentFingerprint.toLowerCase(),
      actionFingerprint: result.actionFingerprint.toLowerCase(),
      expiresAtUtc: result.expiresAtUtc,
      draftBinding: canonicalPhotonCadCommercialDraft(request.draft),
      actionBinding: canonicalCommercialAction(request.action),
      action: cloneAction(request.action),
      pages: result.pages.map((page) => ({ ...page, contentDigest: page.contentDigest.toLowerCase() })),
      renderedPageKeys: new Set<string>(),
      context: { ...this.context },
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
    }
    return this.documentReview
  }

  public recordRenderedPage(page: PhotonCadCommercialPreviewPage) {
    const review = this.documentReview
    if (!review || !this.isCurrentBinding(review)) return false
    const expected = review.pages[page.pageNumber - 1]
    if (!expected || expected.pageNumber !== page.pageNumber || expected.previewHandle !== page.previewHandle
      || expected.contentDigest !== page.contentDigest.toLowerCase()) return false
    review.renderedPageKeys.add(pageKey(expected))
    return true
  }

  public currentDocumentReview(nowEpochMilliseconds: number): Readonly<PhotonCadBoundDocumentReview> | null {
    if (!this.documentReview || !this.isCurrentBinding(this.documentReview)) return null
    return Date.parse(this.documentReview.expiresAtUtc) > nowEpochMilliseconds ? this.documentReview : null
  }

  public allPagesRendered(nowEpochMilliseconds: number) {
    const review = this.currentDocumentReview(nowEpochMilliseconds)
    return review !== null && review.pages.length > 0
      && review.pages.every((page) => review.renderedPageKeys.has(pageKey(page)))
  }

  public orderedRenderedPageDigests(nowEpochMilliseconds: number): string[] | null {
    const review = this.currentDocumentReview(nowEpochMilliseconds)
    return review && this.allPagesRendered(nowEpochMilliseconds) ? review.pages.map((page) => page.contentDigest) : null
  }

  public bindDocumentApproval(
    request: PhotonCadCommercialApprovalRequest,
    result: PhotonCadCommercialApprovalResult,
    nowEpochMilliseconds: number,
  ): PhotonCadBoundDocumentApproval | null {
    const review = this.documentReview
    if (!review || !this.isCurrentBinding(review) || result.status !== 'approved'
      || !this.allPagesRendered(nowEpochMilliseconds)
      || !validatePhotonCadCommercialApprovalRequest(request, review.pages)
      || result.requestId !== request.requestId
      || !isPhotonCadCommercialApprovalHandle(result.approvalHandle)
      || !isPhotonCadDigest(result.documentFingerprint) || !isPhotonCadDigest(result.actionFingerprint)
      || result.documentFingerprint.toLowerCase() !== review.documentFingerprint
      || result.actionFingerprint.toLowerCase() !== review.actionFingerprint
      || !result.expiresAtUtc || !Number.isFinite(Date.parse(result.expiresAtUtc)) || Date.parse(result.expiresAtUtc) <= nowEpochMilliseconds) return null
    this.documentApproval = {
      approvalHandle: result.approvalHandle,
      documentFingerprint: review.documentFingerprint,
      actionFingerprint: review.actionFingerprint,
      expiresAtUtc: result.expiresAtUtc,
      draftBinding: review.draftBinding,
      actionBinding: review.actionBinding,
      action: cloneAction(review.action),
      context: { ...review.context },
      controllerGeneration: review.controllerGeneration,
      contextGeneration: review.contextGeneration,
    }
    this.documentReview = null
    return this.documentApproval
  }

  public currentDocumentApproval(nowEpochMilliseconds: number): Readonly<PhotonCadBoundDocumentApproval> | null {
    if (!this.documentApproval || !this.isCurrentBinding(this.documentApproval)) return null
    return Date.parse(this.documentApproval.expiresAtUtc) > nowEpochMilliseconds ? this.documentApproval : null
  }

  public expectedCommitStatus(): Extract<PhotonCadCommercialCommitResult['status'], 'exported' | 'printed'> | null {
    return this.documentApproval?.action.kind === 'print' ? 'printed' : this.documentApproval ? 'exported' : null
  }

  public consumeDocumentApproval() {
    const approval = this.documentApproval
    this.documentApproval = null
    return approval
  }

  public invalidateDocumentBindings() {
    const invalidated = { documentReview: this.documentReview, documentApproval: this.documentApproval }
    this.documentReview = null
    this.documentApproval = null
    return invalidated
  }

  public invalidateAll(): PhotonCadInvalidatedCommercialBindings {
    const invalidated = { bom: this.bomReview, documentReview: this.documentReview, documentApproval: this.documentApproval }
    this.bomReview = null
    this.documentReview = null
    this.documentApproval = null
    return invalidated
  }

  public close(): PhotonCadInvalidatedCommercialBindings {
    if (this.closed) return emptyInvalidation()
    const invalidated = this.invalidateAll()
    this.closed = true
    this.activeOperation = null
    return invalidated
  }

  private isCurrentToken(token: PhotonCadCommercialToken) {
    return !this.closed && token.controllerGeneration === this.controllerGeneration
      && token.contextGeneration === this.contextGeneration
      && sameContext(this.context, token.context)
  }

  private isCurrentBinding(binding: { context: PhotonCadCommercialContext; controllerGeneration: number; contextGeneration: number }) {
    return !this.closed && binding.controllerGeneration === this.controllerGeneration
      && binding.contextGeneration === this.contextGeneration
      && sameContext(this.context, binding.context)
  }
}

export function canonicalBomRequest(request: PhotonCadBomExportReviewRequest) {
  return JSON.stringify({
    contractVersion: request.contractVersion,
    projectId: request.projectId,
    projectRevision: request.projectRevision,
    bomDigest: request.bomDigest.toLowerCase(),
    format: request.format,
    destinationHandle: request.destinationHandle,
  })
}

export function canonicalCommercialAction(action: PhotonCadCommercialOutputAction) {
  return JSON.stringify(action.kind === 'print'
    ? { kind: action.kind, printerHandle: action.printerHandle, copies: action.copies }
    : { kind: action.kind, destinationHandle: action.destinationHandle })
}

function cloneAction(action: PhotonCadCommercialOutputAction): PhotonCadCommercialOutputAction {
  return action.kind === 'print'
    ? { kind: 'print', printerHandle: action.printerHandle, copies: action.copies }
    : { kind: 'export-pdf', destinationHandle: action.destinationHandle }
}

function validateContext(context: PhotonCadCommercialContext) {
  if (!isPhotonCadIdentifier(context.sessionId) || !isPhotonCadIdentifier(context.projectId)
    || !Number.isSafeInteger(context.projectRevision) || context.projectRevision < 0
    || !isPhotonCadDigest(context.bomDigest)) throw new Error('Photon CAD commercial context is invalid.')
}

function sameContext(left: PhotonCadCommercialContext | null, right: PhotonCadCommercialContext) {
  if (!left) return false
  return left.sessionId === right.sessionId
    && left.projectId === right.projectId
    && left.projectRevision === right.projectRevision
    && left.bomDigest.toLowerCase() === right.bomDigest.toLowerCase()
}

function sameToken(left: PhotonCadCommercialToken | null, right: PhotonCadCommercialToken) {
  return left?.operationGeneration === right.operationGeneration && left.kind === right.kind && left.requestId === right.requestId
}

function pageKey(page: Pick<PhotonCadCommercialPreviewPage, 'pageNumber' | 'contentDigest'>) {
  return `${page.pageNumber}:${page.contentDigest.toLowerCase()}`
}

function emptyInvalidation(): PhotonCadInvalidatedCommercialBindings {
  return { bom: null, documentReview: null, documentApproval: null }
}
