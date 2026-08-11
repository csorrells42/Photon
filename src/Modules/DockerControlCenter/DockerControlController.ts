import {
  DOCKER_CONTROL_LIMITS,
  DOCKER_CONTROL_PROTOCOL_VERSION,
  type DockerBoundReview,
  type DockerControlAdapter,
  type DockerControlState,
  type DockerMutationCommitResult,
  type DockerMutationIntent,
  type DockerProductService,
} from './contracts'
import { DockerControlOperationGate } from './DockerControlOperationGate'
import {
  normalizeDockerLogs,
  normalizeDockerMessage,
  normalizeDockerReview,
  normalizeDockerSnapshot,
} from './normalization'

type Listener = () => void
type RequestLane = { abort: AbortController; generation: number }

const unavailableMessages = {
  'not-registered': 'The trusted Docker desktop bridge is not registered.',
  'engine-unavailable': 'The Docker engine is unavailable.',
  unsupported: 'Docker Control Center is unsupported on this host.',
  disabled: 'Docker Control Center is disabled.',
} as const

const unavailableOperations = {
  startStack: false,
  stopStack: false,
  restartService: false,
  update: false,
} as const

export type DockerControlControllerOptions = {
  adapter?: DockerControlAdapter
  now?(): number
  createRequestId?(): string
}

export class DockerControlController {
  private readonly adapter?: DockerControlAdapter
  private readonly now: () => number
  private readonly createRequestId: () => string
  private readonly gate = new DockerControlOperationGate()
  private readonly listeners = new Set<Listener>()
  private requestSequence = 0
  private refreshGeneration = 0
  private logsGeneration = 0
  private refreshLane?: RequestLane
  private logsLane?: RequestLane
  private mutationLane?: RequestLane
  private closed = false
  private state: DockerControlState

  constructor(options: DockerControlControllerOptions = {}) {
    this.adapter = options.adapter
    this.now = options.now ?? Date.now
    this.createRequestId = options.createRequestId ?? (() => `docker-control:${++this.requestSequence}`)
    const availability = this.adapter?.availability ?? { state: 'unavailable', reason: 'not-registered' as const }
    this.state = {
      status: availability.state === 'available' ? 'idle' : 'unavailable',
      mutationStatus: 'idle',
      logsStatus: 'idle',
      snapshot: null,
      selectedService: 'hermes',
      logs: [],
      logsTruncated: false,
      review: null,
      operations: unavailableOperations,
      updateReason: null,
      message: availability.state === 'available' ? 'Refresh to inspect the approved Photon stack.' : unavailableMessages[availability.reason],
    }
  }

  readonly subscribe = (listener: Listener) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  readonly getSnapshot = () => this.state

  private update(patch: Partial<DockerControlState>) {
    if (this.closed) return
    const next = patch.message === undefined ? patch : { ...patch, message: normalizeDockerMessage(patch.message, 'Docker operation status unavailable.') }
    this.state = { ...this.state, ...next }
    for (const listener of this.listeners) listener()
  }

  async refresh() {
    if (!this.available() || this.state.mutationStatus === 'committing') return false
    this.invalidateReview()
    this.cancelRefresh()
    this.cancelLogs()
    this.cancelMutation()
    const lane = { abort: new AbortController(), generation: ++this.refreshGeneration }
    this.refreshLane = lane
    this.update({ status: 'refreshing', logsStatus: 'idle', logs: [], logsTruncated: false, mutationStatus: 'idle', message: 'Reading Docker state…' })
    try {
      const description = await this.adapter!.describe({ signal: lane.abort.signal })
      if (!this.isCurrentRefresh(lane)) return false
      if (description.availability.state !== 'available') {
        this.update({
          status: 'unavailable',
          snapshot: null,
          operations: unavailableOperations,
          updateReason: description.updateReason,
          message: unavailableMessages[description.availability.reason],
        })
        return false
      }
      const raw = await this.adapter!.refresh({ signal: lane.abort.signal })
      if (!this.isCurrentRefresh(lane)) return false
      const snapshot = normalizeDockerSnapshot(raw)
      if (!snapshot) {
        this.update({ status: 'error', message: 'The Docker bridge returned an invalid or unsafe snapshot.' })
        return false
      }
      this.gate.setSnapshotRevision(snapshot.revision)
      const selectedService = snapshot.services.some((service) => service.id === this.state.selectedService)
        ? this.state.selectedService
        : snapshot.services[0]?.id ?? 'hermes'
      this.update({
        status: 'ready',
        snapshot,
        selectedService,
        operations: description.operations,
        updateReason: description.updateReason,
        message: `Docker state observed at ${snapshot.observedAtUtc}.`,
      })
      return true
    } catch {
      if (!this.isCurrentRefresh(lane)) return false
      this.update({ status: 'error', message: 'Docker state could not be read safely.' })
      return false
    } finally {
      if (this.refreshLane === lane) this.refreshLane = undefined
    }
  }

  selectService(service: DockerProductService) {
    if (!this.state.snapshot?.services.some((candidate) => candidate.id === service)) return false
    if (service === this.state.selectedService) return true
    this.cancelLogs()
    this.update({ selectedService: service, logsStatus: 'idle', logs: [], logsTruncated: false })
    return true
  }

  async readLogs(service: DockerProductService = this.state.selectedService) {
    if (!this.available() || !this.state.snapshot?.services.some((candidate) => candidate.id === service)) return false
    this.cancelLogs()
    const lane = { abort: new AbortController(), generation: ++this.logsGeneration }
    this.logsLane = lane
    const requestId = this.createRequestId()
    this.update({ selectedService: service, logsStatus: 'loading', logs: [], logsTruncated: false, message: `Reading bounded ${serviceLabel(service)} logs…` })
    try {
      const raw = await this.adapter!.readLogs({
        protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
        requestId,
        service,
        maxLines: DOCKER_CONTROL_LIMITS.requestedLogLines,
      }, { signal: lane.abort.signal })
      if (!this.isCurrentLogs(lane, service)) return false
      const result = normalizeDockerLogs(raw, requestId, service)
      if (!result) {
        this.update({ logsStatus: 'error', message: 'The Docker bridge returned invalid or unsafe log data.' })
        return false
      }
      this.update({ logsStatus: 'ready', logs: result.entries, logsTruncated: result.truncated, message: `${result.entries.length} bounded log line${result.entries.length === 1 ? '' : 's'} loaded.` })
      return true
    } catch {
      if (!this.isCurrentLogs(lane, service)) return false
      this.update({ logsStatus: 'error', message: 'Docker logs could not be read safely.' })
      return false
    } finally {
      if (this.logsLane === lane) this.logsLane = undefined
    }
  }

  async requestMutation(intent: DockerMutationIntent) {
    if (!this.available() || !this.state.snapshot || this.state.status !== 'ready') return false
    if (!this.operationAvailable(intent)) {
      this.update({
        message: intent.kind === 'request-update'
          ? 'Docker update review is unavailable because the trusted updater is not integrated.'
          : 'That Docker operation is unavailable according to the trusted host capability description.',
      })
      return false
    }
    if (intent.kind === 'restart-service' && !this.state.snapshot.services.some((service) => service.id === intent.service)) return false
    if ((intent.kind === 'start-stack' || intent.kind === 'stop-stack')
      && (!this.state.snapshot.services.some((service) => service.id === 'hermes')
        || !this.state.snapshot.services.some((service) => service.id === 'serena'))) {
      this.update({ message: 'The approved Hermes and Serena stack is not fully described; refresh before requesting this operation.' })
      return false
    }
    const requestId = this.createRequestId()
    const operation = this.gate.beginReview(requestId, intent)
    if (!operation) {
      this.update({ message: 'Finish or cancel the pending Docker operation first.' })
      return false
    }
    const lane = { abort: new AbortController(), generation: operation.generation }
    this.mutationLane = lane
    this.update({ mutationStatus: 'reviewing', review: null, message: 'Preparing an exact Docker operation review…' })
    try {
      const raw = await this.adapter!.reviewMutation({
        protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
        requestId,
        snapshotRevision: operation.snapshotRevision,
        intent,
      }, { signal: lane.abort.signal })
      if (!this.isCurrentMutation(lane)) return false
      const review = normalizeDockerReview(raw, requestId, operation.snapshotRevision, intent)
      if (!review) {
        this.gate.rejectReview(operation)
        this.update({ mutationStatus: 'idle', message: 'The Docker bridge returned an invalid operation review.' })
        return false
      }
      if (review.status === 'rejected') {
        this.gate.rejectReview(operation)
        this.update({ mutationStatus: 'idle', message: review.message })
        return false
      }
      const settlement = this.gate.finishReview(operation, review, this.now())
      if (settlement !== 'accepted') {
        void this.discardToken(review.reviewToken)
        this.update({ mutationStatus: 'idle', message: 'The Docker review became stale before it could be shown.' })
        return false
      }
      this.update({ mutationStatus: 'awaiting-confirmation', review: this.gate.review as DockerBoundReview, message: 'Review the exact affected services before continuing.' })
      return true
    } catch {
      if (!this.isCurrentMutation(lane)) return false
      this.gate.fail(operation)
      this.update({ mutationStatus: 'idle', message: 'The Docker operation review failed safely.' })
      return false
    } finally {
      if (this.mutationLane === lane) this.mutationLane = undefined
    }
  }

  async commitReviewed() {
    if (!this.available()) return false
    const requestId = this.createRequestId()
    const started = this.gate.beginCommit(requestId, this.now())
    if (!started) {
      const expired = this.state.review !== null && Date.parse(this.state.review.expiresAtUtc) <= this.now()
      this.invalidateReview()
      this.update({ mutationStatus: 'idle', message: expired ? 'The Docker review expired. Prepare a new review.' : 'No current Docker review can be committed.' })
      return false
    }
    const lane = { abort: new AbortController(), generation: started.operation.generation }
    this.mutationLane = lane
    this.update({ mutationStatus: 'committing', review: null, message: 'Applying the reviewed Docker operation…' })
    try {
      const raw = await this.adapter!.commitMutation({
        protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
        requestId,
        reviewToken: started.review.reviewToken,
      }, { signal: lane.abort.signal })
      if (!this.isCurrentMutation(lane)) return false
      const result = normalizeCommitResult(raw, requestId)
      const settlement = this.gate.finishCommit(started.operation)
      if (!result || settlement !== 'accepted') {
        this.update({ mutationStatus: 'idle', message: 'The Docker commit result was invalid or stale.' })
        return false
      }
      const snapshot = result.snapshot === undefined ? null : normalizeDockerSnapshot(result.snapshot)
      if (result.status === 'succeeded' && result.snapshot !== undefined && !snapshot) {
        this.update({ mutationStatus: 'idle', message: 'Docker reported success with an invalid snapshot; refresh before trusting state.' })
        return false
      }
      if (snapshot) {
        this.gate.setSnapshotRevision(snapshot.revision)
        this.update({ snapshot, status: 'ready' })
      } else if (result.status === 'succeeded') {
        this.update({ snapshot: null, status: 'idle', logs: [], logsStatus: 'idle', logsTruncated: false })
      }
      this.update({ mutationStatus: 'idle', message: result.message })
      return result.status === 'succeeded'
    } catch {
      if (!this.isCurrentMutation(lane)) return false
      this.gate.fail(started.operation)
      this.update({ mutationStatus: 'idle', message: 'The reviewed Docker operation failed safely. Refresh before retrying.' })
      return false
    } finally {
      if (this.mutationLane === lane) this.mutationLane = undefined
    }
  }

  cancelReview() {
    if (this.state.mutationStatus === 'reviewing') this.cancelMutation()
    this.invalidateReview()
    this.update({ mutationStatus: 'idle', review: null, message: 'Docker operation review cancelled.' })
  }

  dispose() {
    if (this.closed) return
    const review = this.gate.close()
    if (review) void this.discardToken(review.reviewToken)
    this.cancelRefresh()
    this.cancelLogs()
    this.cancelMutation()
    this.listeners.clear()
    this.closed = true
  }

  private available() {
    return !this.closed && this.adapter?.availability.state === 'available'
  }

  private operationAvailable(intent: DockerMutationIntent) {
    if (intent.kind === 'start-stack') return this.state.operations.startStack
    if (intent.kind === 'stop-stack') return this.state.operations.stopStack
    if (intent.kind === 'restart-service') return this.state.operations.restartService
    return this.state.operations.update
  }

  private invalidateReview() {
    const review = this.gate.invalidateReview()
    if (review) void this.discardToken(review.reviewToken)
    if (this.state.review) this.state = { ...this.state, review: null, mutationStatus: 'idle' }
  }

  private async discardToken(token: string) {
    try { await this.adapter?.discardReview?.(token) } catch { /* best-effort invalidation; host expiry remains authoritative */ }
  }

  private cancelRefresh() {
    this.refreshGeneration += 1
    this.refreshLane?.abort.abort()
    this.refreshLane = undefined
  }

  private cancelLogs() {
    this.logsGeneration += 1
    this.logsLane?.abort.abort()
    this.logsLane = undefined
  }

  private cancelMutation() {
    this.mutationLane?.abort.abort()
    this.mutationLane = undefined
    this.gate.cancelActive()
  }

  private isCurrentRefresh(lane: RequestLane) {
    return !this.closed && this.refreshLane === lane && lane.generation === this.refreshGeneration && !lane.abort.signal.aborted
  }

  private isCurrentLogs(lane: RequestLane, service: DockerProductService) {
    return !this.closed && this.logsLane === lane && lane.generation === this.logsGeneration
      && !lane.abort.signal.aborted && this.state.selectedService === service
  }

  private isCurrentMutation(lane: RequestLane) {
    return !this.closed && this.mutationLane === lane && !lane.abort.signal.aborted
  }
}

function serviceLabel(service: DockerProductService) {
  return service === 'hermes' ? 'Hermes' : service === 'serena' ? 'Serena' : 'Model Runner'
}

function normalizeCommitResult(value: unknown, requestId: string): DockerMutationCommitResult | null {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  if (raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || raw.requestId !== requestId
    || !['succeeded', 'failed', 'stale', 'expired'].includes(String(raw.status))) return null
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    requestId,
    status: raw.status as DockerMutationCommitResult['status'],
    message: normalizeDockerMessage(raw.message, 'Docker operation completed without a status message.'),
    snapshot: raw.snapshot,
  }
}
