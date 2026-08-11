import {
  assertSafeChangeIntent,
  type ConnectionChangeIntent,
  type ConnectionClientResult,
  type ConnectionMetadata,
  type ConnectionReview,
  type ConnectionsHostFrame,
  hermesConnectionsProtocolVersion,
  normalizeConnectionMetadata,
  normalizeConnectionRef,
  normalizeConnectionReview,
  normalizeIdentifier,
  normalizeReviewHandle,
} from './contracts'

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type DesktopWindow = Window & {
  __HERMES_DESKTOP_HOST__?: { capabilities?: { connections?: boolean; connectionsVersion?: number } }
  chrome?: { webview?: WebViewBridge }
}

export interface HermesConnectionsClient {
  available(): boolean
  list(signal?: AbortSignal): Promise<ConnectionClientResult<ConnectionMetadata[]>>
  beginChange(intent: ConnectionChangeIntent, signal?: AbortSignal): Promise<ConnectionClientResult<ConnectionReview>>
  beginRemove(connectionRef: string, expectedRevision: number, signal?: AbortSignal): Promise<ConnectionClientResult<ConnectionReview>>
  commit(reviewHandle: string, signal?: AbortSignal): Promise<ConnectionClientResult<ConnectionMetadata | null>>
  cancel(reviewHandle: string, signal?: AbortSignal): Promise<ConnectionClientResult<null>>
}

export class DesktopHermesConnectionsClient implements HermesConnectionsClient {
  available() {
    return this.bridge() !== null
  }

  list(signal?: AbortSignal) {
    return this.request<ConnectionMetadata[]>('connections.list', {}, (frame) => {
      if (frame.type !== 'connections.list.result' || !Array.isArray(frame.entries)) return null
      const entries = frame.entries.map(normalizeConnectionMetadata)
      return entries.some((entry) => entry === null) ? null : entries as ConnectionMetadata[]
    }, signal)
  }

  beginChange(intent: ConnectionChangeIntent, signal?: AbortSignal) {
    const safe = assertSafeChangeIntent(intent)
    return this.request<ConnectionReview>('connections.change.begin', safe, (frame) =>
      frame.type === 'connections.review.ready' ? normalizeConnectionReview(frame.review) : null, signal)
  }

  beginRemove(connectionRef: string, expectedRevision: number, signal?: AbortSignal) {
    const safeRef = normalizeConnectionRef(connectionRef)
    if (!safeRef || !Number.isSafeInteger(expectedRevision) || expectedRevision <= 0) {
      return Promise.resolve<ConnectionClientResult<ConnectionReview>>({ kind: 'failure', code: 'invalid_request', message: 'The connection removal request is invalid.', retryable: false })
    }
    return this.request<ConnectionReview>('connections.remove.review', { connectionRef: safeRef, expectedRevision }, (frame) =>
      frame.type === 'connections.review.ready' ? normalizeConnectionReview(frame.review) : null, signal)
  }

  commit(reviewHandle: string, signal?: AbortSignal) {
    const safeHandle = normalizeReviewHandle(reviewHandle)
    if (!safeHandle) return Promise.resolve<ConnectionClientResult<ConnectionMetadata | null>>({ kind: 'failure', code: 'invalid_request', message: 'The review handle is invalid.', retryable: false })
    return this.request<ConnectionMetadata | null>('connections.review.commit', { reviewHandle: safeHandle }, (frame) => {
      if (frame.type === 'connections.removed') return null
      if (frame.type !== 'connections.changed') return undefined
      return normalizeConnectionMetadata(frame.entry) ?? undefined
    }, signal, true)
  }

  cancel(reviewHandle: string, signal?: AbortSignal) {
    const safeHandle = normalizeReviewHandle(reviewHandle)
    if (!safeHandle) return Promise.resolve<ConnectionClientResult<null>>({ kind: 'failure', code: 'invalid_request', message: 'The review handle is invalid.', retryable: false })
    return this.request<null>('connections.review.cancel', { reviewHandle: safeHandle }, (frame) =>
      frame.type === 'connections.review.cancelled' ? null : undefined, signal, true)
  }

  private request<T>(
    type: string,
    payload: Record<string, unknown>,
    normalize: (frame: ConnectionsHostFrame) => T | null | undefined,
    signal?: AbortSignal,
    nullIsSuccess = false,
  ): Promise<ConnectionClientResult<T>> {
    const bridge = this.bridge()
    if (!bridge) return Promise.resolve({ kind: 'unavailable', message: 'Native Connections & Credentials is not registered in this build.' })
    if (signal?.aborted) return Promise.resolve({ kind: 'failure', code: 'cancelled', message: 'The connection request was cancelled.', retryable: true })
    const requestId = this.requestId()
    return new Promise((resolve) => {
      let settled = false
      const finish = (result: ConnectionClientResult<T>) => {
        if (settled) return
        settled = true
        window.clearTimeout(timeout)
        signal?.removeEventListener('abort', abort)
        bridge.removeEventListener('message', receive)
        resolve(result)
      }
      const abort = () => finish({ kind: 'failure', code: 'cancelled', message: 'The connection request was cancelled.', retryable: true })
      const receive = (event: MessageEvent) => {
        const frame = event.data as ConnectionsHostFrame
        if (frame?.version !== hermesConnectionsProtocolVersion || frame.requestId !== requestId) return
        if (frame.type === 'connections.error') {
          finish({
            kind: 'failure',
            code: normalizeIdentifier(frame.code, 64) ?? 'native_error',
            message: typeof frame.message === 'string' && frame.message.length <= 300 ? frame.message : 'Native credential handling failed.',
            retryable: frame.retryable === true,
          })
          return
        }
        const value = normalize(frame)
        if (value !== undefined && (value !== null || nullIsSuccess)) finish({ kind: 'success', value: value as T })
      }
      const timeout = window.setTimeout(() => finish({ kind: 'failure', code: 'timeout', message: 'The native credential request timed out.', retryable: true }), 10_000)
      signal?.addEventListener('abort', abort, { once: true })
      bridge.addEventListener('message', receive)
      try {
        bridge.postMessage({ type, version: hermesConnectionsProtocolVersion, requestId, ...payload })
      } catch {
        finish({ kind: 'failure', code: 'bridge_unavailable', message: 'The native credential bridge is unavailable.', retryable: true })
      }
    })
  }

  private bridge(): WebViewBridge | null {
    if (typeof window === 'undefined') return null
    const desktop = window as DesktopWindow
    const capabilities = desktop.__HERMES_DESKTOP_HOST__?.capabilities
    if (capabilities?.connections !== true || capabilities.connectionsVersion !== hermesConnectionsProtocolVersion) return null
    return desktop.chrome?.webview ?? null
  }

  private requestId() {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return `connections-${crypto.randomUUID()}`
    return `connections-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  }
}
