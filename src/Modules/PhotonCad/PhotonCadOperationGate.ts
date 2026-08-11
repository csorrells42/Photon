import {
  canonicalPhotonCadReleaseBinding,
  isOpaquePhotonCadReviewHandle,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  type PhotonCadReleaseReviewRequest,
  type PhotonCadReleaseReviewResult,
} from './PhotonCadContract'

export type PhotonCadLatestLane = 'describe' | 'preview'
export type PhotonCadExclusiveKind = 'apply' | 'verify' | 'review' | 'commit'
export type PhotonCadGateContext = { sessionId: string; projectId: string; revision: number }
export type PhotonCadLatestToken = {
  lane: PhotonCadLatestLane
  generation: number
  controllerGeneration: number
  contextGeneration: number
}
export type PhotonCadExclusiveToken = {
  kind: PhotonCadExclusiveKind
  requestId: string
  sessionId: string
  projectId: string
  revision: number
  operationGeneration: number
  controllerGeneration: number
  contextGeneration: number
}
export type PhotonCadGateSettlement = 'accepted' | 'stale' | 'foreign'
export type PhotonCadBoundReview = {
  reviewHandle: string
  packageFingerprint: string
  expiresAtUtc: string
  requestBinding: string
  sessionId: string
  projectId: string
  revision: number
  controllerGeneration: number
  contextGeneration: number
}

export class PhotonCadOperationGate {
  private context: PhotonCadGateContext | null = null
  private controllerGeneration = 0
  private contextGeneration = 0
  private operationGeneration = 0
  private readonly latestGeneration: Record<PhotonCadLatestLane, number> = { describe: 0, preview: 0 }
  private exclusive: PhotonCadExclusiveToken | null = null
  private review: PhotonCadBoundReview | null = null
  private closed = false

  public get active(): Readonly<PhotonCadExclusiveToken> | null {
    return this.exclusive
  }

  public get currentContext(): Readonly<PhotonCadGateContext> | null {
    return this.context
  }

  public transitionContext(next: PhotonCadGateContext): PhotonCadBoundReview | null {
    validateContext(next)
    if (sameContext(this.context, next)) return null
    this.context = { ...next }
    this.contextGeneration += 1
    this.latestGeneration.preview += 1
    return this.invalidateReview()
  }

  public markEdited(): PhotonCadBoundReview | null {
    if (this.closed) return null
    this.contextGeneration += 1
    this.latestGeneration.preview += 1
    return this.invalidateReview()
  }

  public replaceController(): PhotonCadBoundReview | null {
    if (this.closed) return null
    const invalidated = this.invalidateReview()
    this.controllerGeneration += 1
    this.contextGeneration += 1
    this.latestGeneration.describe += 1
    this.latestGeneration.preview += 1
    this.exclusive = null
    return invalidated
  }

  public beginLatest(lane: PhotonCadLatestLane): PhotonCadLatestToken | null {
    if (this.closed) return null
    this.latestGeneration[lane] += 1
    return {
      lane,
      generation: this.latestGeneration[lane],
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
    }
  }

  public isCurrentLatest(token: PhotonCadLatestToken) {
    return !this.closed
      && token.generation === this.latestGeneration[token.lane]
      && token.controllerGeneration === this.controllerGeneration
      && token.contextGeneration === this.contextGeneration
  }

  public invalidateLatest(lane: PhotonCadLatestLane) {
    this.latestGeneration[lane] += 1
  }

  public startExclusive(kind: PhotonCadExclusiveKind, requestId: string): PhotonCadExclusiveToken | null {
    if (this.closed || this.exclusive || !this.context || !isPhotonCadIdentifier(requestId)) return null
    this.operationGeneration += 1
    this.exclusive = {
      kind,
      requestId,
      sessionId: this.context.sessionId,
      projectId: this.context.projectId,
      revision: this.context.revision,
      operationGeneration: this.operationGeneration,
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
    }
    return this.exclusive
  }

  public settleExclusive(token: PhotonCadExclusiveToken): PhotonCadGateSettlement {
    if (!sameExclusive(this.exclusive, token)) return 'foreign'
    this.exclusive = null
    return this.isCurrentContext(token) ? 'accepted' : 'stale'
  }

  public failExclusive(token: PhotonCadExclusiveToken) {
    if (!sameExclusive(this.exclusive, token)) return false
    this.exclusive = null
    return true
  }

  public bindReview(
    request: PhotonCadReleaseReviewRequest,
    result: PhotonCadReleaseReviewResult,
    nowEpochMilliseconds: number,
  ): PhotonCadBoundReview | null {
    if (this.closed || this.review || !this.context || result.status !== 'ready'
      || !isOpaquePhotonCadReviewHandle(result.reviewHandle) || !isPhotonCadDigest(result.packageFingerprint)
      || typeof result.expiresAtUtc !== 'string') return null
    const expires = Date.parse(result.expiresAtUtc)
    if (!Number.isFinite(expires) || expires <= nowEpochMilliseconds
      || request.sessionId !== this.context.sessionId || request.projectId !== this.context.projectId
      || request.revision !== this.context.revision || result.projectId !== request.projectId || result.revision !== request.revision) return null
    this.review = {
      reviewHandle: result.reviewHandle,
      packageFingerprint: result.packageFingerprint.toLowerCase(),
      expiresAtUtc: result.expiresAtUtc,
      requestBinding: canonicalPhotonCadReleaseBinding(request),
      sessionId: request.sessionId,
      projectId: request.projectId,
      revision: request.revision,
      controllerGeneration: this.controllerGeneration,
      contextGeneration: this.contextGeneration,
    }
    return this.review
  }

  public currentReview(nowEpochMilliseconds: number): Readonly<PhotonCadBoundReview> | null {
    if (!this.review || !this.isCurrentReview(this.review)) return null
    const expires = Date.parse(this.review.expiresAtUtc)
    return Number.isFinite(expires) && expires > nowEpochMilliseconds ? this.review : null
  }

  public reviewIsExpired(nowEpochMilliseconds: number) {
    return this.review !== null && Date.parse(this.review.expiresAtUtc) <= nowEpochMilliseconds
  }

  public consumeReview(): PhotonCadBoundReview | null {
    const current = this.review
    this.review = null
    return current
  }

  public invalidateReview(): PhotonCadBoundReview | null {
    return this.consumeReview()
  }

  public close(): PhotonCadBoundReview | null {
    if (this.closed) return null
    const invalidated = this.invalidateReview()
    this.closed = true
    this.latestGeneration.describe += 1
    this.latestGeneration.preview += 1
    this.exclusive = null
    return invalidated
  }

  private isCurrentContext(token: Pick<PhotonCadExclusiveToken, 'controllerGeneration' | 'contextGeneration' | 'sessionId' | 'projectId' | 'revision'>) {
    return !this.closed && this.context !== null
      && token.controllerGeneration === this.controllerGeneration
      && token.contextGeneration === this.contextGeneration
      && token.sessionId === this.context.sessionId
      && token.projectId === this.context.projectId
      && token.revision === this.context.revision
  }

  private isCurrentReview(review: PhotonCadBoundReview) {
    return this.context !== null
      && review.controllerGeneration === this.controllerGeneration
      && review.contextGeneration === this.contextGeneration
      && review.sessionId === this.context.sessionId
      && review.projectId === this.context.projectId
      && review.revision === this.context.revision
  }
}

function validateContext(context: PhotonCadGateContext) {
  if (!isPhotonCadIdentifier(context.sessionId) || !isPhotonCadIdentifier(context.projectId)
    || !Number.isSafeInteger(context.revision) || context.revision < 0) throw new Error('Photon CAD project context is invalid.')
}

function sameContext(left: PhotonCadGateContext | null, right: PhotonCadGateContext) {
  return left?.sessionId === right.sessionId && left.projectId === right.projectId && left.revision === right.revision
}

function sameExclusive(left: PhotonCadExclusiveToken | null, right: PhotonCadExclusiveToken) {
  return left?.operationGeneration === right.operationGeneration
    && left.requestId === right.requestId
    && left.kind === right.kind
}
