import type {
  DockerBoundReview,
  DockerMutationIntent,
  DockerMutationReviewResult,
} from './contracts'

export type DockerGateOperation = {
  phase: 'review' | 'commit'
  requestId: string
  generation: number
  controllerGeneration: number
  snapshotRevision: number
  intent: DockerMutationIntent
}

export type DockerGateSettlement = 'accepted' | 'stale' | 'foreign'

export class DockerControlOperationGate {
  private controllerGeneration = 0
  private operationGeneration = 0
  private snapshotRevision: number | null = null
  private activeOperation: DockerGateOperation | null = null
  private boundReview: DockerBoundReview | null = null
  private closed = false

  get active(): Readonly<DockerGateOperation> | null {
    return this.activeOperation
  }

  get review(): Readonly<DockerBoundReview> | null {
    return this.boundReview
  }

  setSnapshotRevision(revision: number): DockerBoundReview | null {
    if (!Number.isSafeInteger(revision) || revision < 0) throw new Error('Docker snapshot revision is invalid.')
    if (this.snapshotRevision === revision) return null
    this.snapshotRevision = revision
    this.controllerGeneration += 1
    return this.invalidateReview()
  }

  beginReview(requestId: string, intent: DockerMutationIntent): DockerGateOperation | null {
    if (this.closed || this.activeOperation || this.boundReview || this.snapshotRevision === null || !isRequestId(requestId)) return null
    return this.setActive('review', requestId, intent)
  }

  finishReview(operation: DockerGateOperation, result: Extract<DockerMutationReviewResult, { status: 'ready' }>, now: number): DockerGateSettlement {
    if (!sameOperation(this.activeOperation, operation)) return 'foreign'
    this.activeOperation = null
    if (!this.isCurrent(operation)) return 'stale'
    const expires = Date.parse(result.expiresAtUtc)
    if (!Number.isFinite(expires) || expires <= now || result.snapshotRevision !== operation.snapshotRevision) return 'stale'
    this.boundReview = { ...result, intent: operation.intent, controllerGeneration: operation.controllerGeneration }
    return 'accepted'
  }

  rejectReview(operation: DockerGateOperation): DockerGateSettlement {
    if (!sameOperation(this.activeOperation, operation)) return 'foreign'
    this.activeOperation = null
    return this.isCurrent(operation) ? 'accepted' : 'stale'
  }

  beginCommit(requestId: string, now: number): { operation: DockerGateOperation; review: DockerBoundReview } | null {
    const review = this.currentReview(now)
    if (this.closed || this.activeOperation || !review || !isRequestId(requestId)) return null
    this.boundReview = null
    return { operation: this.setActive('commit', requestId, review.intent), review: { ...review } }
  }

  finishCommit(operation: DockerGateOperation): DockerGateSettlement {
    if (!sameOperation(this.activeOperation, operation)) return 'foreign'
    this.activeOperation = null
    return this.isCurrent(operation) ? 'accepted' : 'stale'
  }

  fail(operation: DockerGateOperation): boolean {
    if (!sameOperation(this.activeOperation, operation)) return false
    this.activeOperation = null
    return true
  }

  cancelActive(): DockerGateOperation | null {
    const current = this.activeOperation
    this.activeOperation = null
    this.controllerGeneration += 1
    return current
  }

  currentReview(now: number): Readonly<DockerBoundReview> | null {
    if (!this.boundReview || this.boundReview.controllerGeneration !== this.controllerGeneration
      || this.boundReview.snapshotRevision !== this.snapshotRevision) return null
    const expires = Date.parse(this.boundReview.expiresAtUtc)
    return Number.isFinite(expires) && expires > now ? this.boundReview : null
  }

  invalidateReview(): DockerBoundReview | null {
    const current = this.boundReview
    this.boundReview = null
    return current
  }

  close(): DockerBoundReview | null {
    if (this.closed) return null
    const review = this.invalidateReview()
    this.cancelActive()
    this.closed = true
    return review
  }

  private setActive(phase: DockerGateOperation['phase'], requestId: string, intent: DockerMutationIntent): DockerGateOperation {
    this.operationGeneration += 1
    const operation = {
      phase,
      requestId,
      generation: this.operationGeneration,
      controllerGeneration: this.controllerGeneration,
      snapshotRevision: this.snapshotRevision!,
      intent,
    }
    this.activeOperation = operation
    return operation
  }

  private isCurrent(operation: DockerGateOperation) {
    return !this.closed
      && operation.controllerGeneration === this.controllerGeneration
      && operation.snapshotRevision === this.snapshotRevision
  }
}

function isRequestId(value: string) {
  return value.length >= 8 && value.length <= 128 && /^[A-Za-z0-9:._-]+$/u.test(value)
}

function sameOperation(left: DockerGateOperation | null, right: DockerGateOperation) {
  return left?.generation === right.generation
    && left.phase === right.phase
    && left.requestId === right.requestId
}
