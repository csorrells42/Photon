import {
  DOCKER_CONTROL_PROTOCOL_VERSION,
  type DockerControlAdapter,
  type DockerControlDescription,
  type DockerControlExecution,
  type DockerLogsRequest,
  type DockerMutationCommitRequest,
  type DockerMutationCommitResult,
  type DockerMutationReviewRequest,
  type DockerProductService,
} from './contracts'
import { normalizeDockerLogs, normalizeDockerMessage, normalizeDockerReview, normalizeDockerSnapshot } from './normalization'
import { isOpaqueDockerReviewToken } from './normalization'

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

type PendingKind = 'describe' | 'snapshot' | 'logs' | 'review' | 'commit' | 'discard'
type Pending = {
  kind: PendingKind
  service?: DockerProductService
  review?: DockerMutationReviewRequest
  resolve(value: unknown): void
  reject(error: Error): void
  signal?: AbortSignal
  abort?: () => void
  timeout?: ReturnType<typeof setTimeout>
}

type UnknownRecord = Record<string, unknown>

function getBridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): UnknownRecord | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as UnknownRecord : null
}

function requestId(value: unknown): string | null {
  return typeof value === 'string' && value.length >= 8 && value.length <= 128 && /^[A-Za-z0-9:._-]+$/u.test(value) ? value : null
}

function normalizeDescription(value: unknown): DockerControlDescription | null {
  const raw = record(value)
  const availability = record(raw?.availability)
  const operations = record(raw?.operations)
  if (!raw || raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || !availability || !operations
    || !Array.isArray(raw.services) || raw.services.length > 3
    || raw.updateReason !== 'derived-runtime-updater-not-integrated') return null
  const services = raw.services.filter(isService)
  if (services.length !== raw.services.length || new Set(services).size !== services.length) return null
  const normalizedAvailability = availability.state === 'available'
    ? { state: 'available' as const }
    : availability.state === 'unavailable' && (availability.reason === 'engine-unavailable' || availability.reason === 'unsupported' || availability.reason === 'disabled')
      ? { state: 'unavailable' as const, reason: availability.reason as 'engine-unavailable' | 'unsupported' | 'disabled' }
      : null
  if (!normalizedAvailability || typeof operations.startStack !== 'boolean' || typeof operations.stopStack !== 'boolean'
    || typeof operations.startService !== 'boolean' || typeof operations.stopService !== 'boolean'
    || typeof operations.restartService !== 'boolean' || operations.loadModel !== false
    || typeof operations.unloadModel !== 'boolean' || operations.update !== false) return null
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    availability: normalizedAvailability,
    services,
    operations: {
      startStack: operations.startStack,
      stopStack: operations.stopStack,
      startService: operations.startService,
      stopService: operations.stopService,
      restartService: operations.restartService,
      loadModel: false,
      unloadModel: operations.unloadModel,
      update: false,
    },
    updateReason: 'derived-runtime-updater-not-integrated',
  }
}

function normalizeCommit(value: unknown, expectedRequestId: string): DockerMutationCommitResult | null {
  const raw = record(value)
  if (!raw || raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || raw.requestId !== expectedRequestId
    || !['succeeded', 'failed', 'stale', 'expired'].includes(String(raw.status))) return null
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    requestId: expectedRequestId,
    status: raw.status as DockerMutationCommitResult['status'],
    message: normalizeDockerMessage(raw.message, 'Docker operation completed without a status message.'),
    snapshot: raw.snapshot,
  }
}

export class DesktopDockerControlAdapter implements DockerControlAdapter {
  private bridge: WebViewBridge | null = null
  private sequence = 0
  private readonly pending = new Map<string, Pending>()
  private readonly receive = (event: MessageEvent) => {
    const frame = record(event.data)
    if (!frame || frame.version !== DOCKER_CONTROL_PROTOCOL_VERSION) return
    const id = requestId(frame.requestId)
    if (!id) return
    const pending = this.pending.get(id)
    if (!pending) return
    if (frame.type === 'dockerControl.error') {
      this.finish(id, pending, undefined, new Error(normalizeDockerMessage(frame.message, 'Docker Control Center failed safely.')))
      return
    }
    const value = frame.value
    let normalized: unknown = null
    let matched = false
    if (pending.kind === 'describe' && frame.type === 'dockerControl.describe.result') { matched = true; normalized = normalizeDescription(value) }
    else if (pending.kind === 'snapshot' && frame.type === 'dockerControl.snapshot.result') { matched = true; normalized = normalizeDockerSnapshot(value) }
    else if (pending.kind === 'logs' && frame.type === 'dockerControl.logs.result' && pending.service) { matched = true; normalized = normalizeDockerLogs(value, id, pending.service) }
    else if (pending.kind === 'review' && frame.type === 'dockerControl.review.result' && pending.review) {
      matched = true
      normalized = normalizeDockerReview(value, id, pending.review.snapshotRevision, pending.review.intent)
    } else if (pending.kind === 'commit' && frame.type === 'dockerControl.commit.result') { matched = true; normalized = normalizeCommit(value, id) }
    else if (pending.kind === 'discard' && frame.type === 'dockerControl.discard.result') {
      matched = true
      const discard = record(value)
      normalized = discard?.discarded === true ? undefined : null
      if (discard?.discarded === false) normalized = undefined
    }
    if (!matched) return
    if (normalized === null) {
      this.finish(id, pending, undefined, new Error('The Docker host returned an invalid response.'))
      return
    }
    this.finish(id, pending, normalized)
  }

  get availability() {
    return getBridge() ? { state: 'available' as const } : { state: 'unavailable' as const, reason: 'not-registered' as const }
  }

  describe(execution: DockerControlExecution = { signal: new AbortController().signal }): Promise<DockerControlDescription> {
    const id = this.nextId('describe')
    return this.request('describe', id, { type: 'dockerControl.describe', version: 1, requestId: id }, execution) as Promise<DockerControlDescription>
  }

  refresh(execution: DockerControlExecution): Promise<unknown> {
    const id = this.nextId('snapshot')
    return this.request('snapshot', id, { type: 'dockerControl.snapshot', version: 1, requestId: id }, execution)
  }

  readLogs(request: DockerLogsRequest, execution: DockerControlExecution): Promise<unknown> {
    if (!isService(request.service) || !Number.isSafeInteger(request.maxLines) || request.maxLines < 1 || request.maxLines > 200) {
      return Promise.reject(new Error('The bounded Docker logs request is invalid.'))
    }
    return this.request('logs', request.requestId, {
      type: 'dockerControl.logs', version: 1, requestId: request.requestId,
      service: request.service, maxLines: request.maxLines,
    }, execution, request.service)
  }

  reviewMutation(request: DockerMutationReviewRequest, execution: DockerControlExecution): Promise<unknown> {
    const intent = projectIntent(request.intent)
    if (!intent || !Number.isSafeInteger(request.snapshotRevision) || request.snapshotRevision < 0) {
      return Promise.reject(new Error('The typed Docker operation review is invalid.'))
    }
    const projected = { ...request, intent }
    // MainWindow v1 forwards the existing typed `service` slot. For model unload it carries the
    // reviewed model reference; the host accepts it only when it is present in its loaded-model snapshot.
    const hostIntent = intent.kind === 'unload-model' ? { kind: intent.kind, service: intent.model } : intent
    return this.request('review', request.requestId, {
      type: 'dockerControl.review', version: 1, requestId: request.requestId,
      snapshotRevision: request.snapshotRevision, intent: hostIntent,
    }, execution, undefined, projected)
  }

  commitMutation(request: DockerMutationCommitRequest, execution: DockerControlExecution): Promise<unknown> {
    if (!isOpaqueDockerReviewToken(request.reviewToken)) return Promise.reject(new Error('The Docker review token is invalid.'))
    return this.request('commit', request.requestId, {
      type: 'dockerControl.commit', version: 1, requestId: request.requestId, reviewToken: request.reviewToken,
    }, execution)
  }

  discardReview(reviewToken: string): Promise<void> {
    if (!isOpaqueDockerReviewToken(reviewToken)) return Promise.reject(new Error('The Docker review token is invalid.'))
    const id = this.nextId('discard')
    return this.request('discard', id, {
      type: 'dockerControl.discard', version: 1, requestId: id, reviewToken,
    }, { signal: new AbortController().signal }) as Promise<void>
  }

  close() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    for (const [id, pending] of this.pending) this.finish(id, pending, undefined, new Error('Docker Control Center disconnected.'))
  }

  private request(
    kind: PendingKind,
    id: string,
    message: unknown,
    execution: DockerControlExecution,
    service?: DockerProductService,
    review?: DockerMutationReviewRequest,
  ) {
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Docker Control Center is available in the trusted desktop app.'))
    if (execution.signal.aborted) return Promise.reject(new DOMException('Docker Control Center request aborted.', 'AbortError'))
    return new Promise<unknown>((resolve, reject) => {
      if (this.pending.has(id)) { reject(new Error('A Docker Control Center request with this identifier is already pending.')); return }
      const pending: Pending = { kind, service, review, resolve, reject, signal: execution.signal }
      pending.abort = () => this.finish(id, pending, undefined, new DOMException('Docker Control Center request aborted.', 'AbortError'))
      execution.signal.addEventListener('abort', pending.abort, { once: true })
      pending.timeout = setTimeout(() => this.finish(id, pending, undefined, new Error('Docker Control Center request timed out.')), 45_000)
      this.pending.set(id, pending)
      try { bridge.postMessage(message) }
      catch (error) { this.finish(id, pending, undefined, error instanceof Error ? error : new Error('Docker Control Center request failed.')) }
    })
  }

  private finish(id: string, pending: Pending, value?: unknown, error?: Error) {
    if (this.pending.get(id) !== pending) return
    this.pending.delete(id)
    if (pending.timeout) clearTimeout(pending.timeout)
    if (pending.abort) pending.signal?.removeEventListener('abort', pending.abort)
    if (error) pending.reject(error)
    else pending.resolve(value)
  }

  private connect() {
    const next = getBridge()
    if (next && next !== this.bridge) {
      this.bridge?.removeEventListener('message', this.receive)
      this.bridge = next
      next.addEventListener('message', this.receive)
    }
    return next
  }

  private nextId(kind: string) {
    this.sequence = (this.sequence + 1) % Number.MAX_SAFE_INTEGER
    return `docker-control:${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}`
  }
}

function isService(value: unknown): value is DockerProductService {
  return value === 'hermes' || value === 'memory-vector' || value === 'serena' || value === 'model-runner'
}

function projectIntent(value: unknown): DockerMutationReviewRequest['intent'] | null {
  const raw = record(value)
  if (!raw) return null
  if (raw.kind === 'start-stack' || raw.kind === 'stop-stack' || raw.kind === 'request-update') return { kind: raw.kind }
  if ((raw.kind === 'start-service' || raw.kind === 'stop-service' || raw.kind === 'restart-service') && isService(raw.service)) {
    return { kind: raw.kind, service: raw.service }
  }
  return raw.kind === 'unload-model' && typeof raw.model === 'string'
    ? { kind: 'unload-model', model: raw.model }
    : null
}

export const desktopDockerControlAdapter = new DesktopDockerControlAdapter()
