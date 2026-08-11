import {
  SOURCE_CONTROL_PROTOCOL_VERSION,
  type GitExtensionsOpenResult,
  type GitStatusResult,
  type RepositoryResolution,
  type SourceControlDescription,
  type SourceControlFrame,
  normalizeSourceControlFrame,
} from './contracts'

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type PendingValue = SourceControlDescription | RepositoryResolution | GitStatusResult | GitExtensionsOpenResult
type PendingKind = SourceControlFrame['type']
type PendingRequest = {
  kind: PendingKind
  resolve: (value: PendingValue) => void
  reject: (reason: Error) => void
}

function getBridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

export class DesktopSourceControlClient {
  private bridge: WebViewBridge | null = null
  private sequence = 0
  private readonly pending = new Map<string, PendingRequest>()
  private readonly listeners = new Set<(frame: SourceControlFrame) => void>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeSourceControlFrame(event.data)
    if (!frame) return
    this.listeners.forEach((listener) => listener(frame))
    if (frame.type === 'error') {
      this.fail(frame.requestId, new Error(frame.value.message))
      return
    }
    this.complete(frame.requestId, frame.type, frame.value)
  }

  get available() { return getBridge() !== null }

  onFrame(listener: (frame: SourceControlFrame) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  describe() {
    const requestId = this.nextRequestId('describe')
    return this.request<SourceControlDescription>('description', requestId, {
      type: 'sourceControl.describe', version: SOURCE_CONTROL_PROTOCOL_VERSION, requestId,
    })
  }

  resolveRepository(workspaceRelativePath = '.') {
    if (!workspaceRelativePath || workspaceRelativePath.length > 1_024 || workspaceRelativePath.includes('\0')) {
      return Promise.reject(new Error('The repository path is invalid.'))
    }
    const requestId = this.nextRequestId('resolve')
    return this.request<RepositoryResolution>('repository', requestId, {
      type: 'sourceControl.repository.resolve', version: SOURCE_CONTROL_PROTOCOL_VERSION, requestId, workspaceRelativePath,
    })
  }

  getStatus(repositoryId: string) {
    return this.status(repositoryId).promise
  }

  status(repositoryId: string) {
    const requestId = this.nextRequestId('status')
    const promise = this.request<GitStatusResult>('status', requestId, {
      type: 'sourceControl.status', version: SOURCE_CONTROL_PROTOCOL_VERSION, requestId, repositoryId,
    })
    return { requestId, promise, cancel: () => this.cancel(requestId) }
  }

  cancel(targetRequestId: string) {
    this.connect()?.postMessage({
      type: 'sourceControl.cancel', version: SOURCE_CONTROL_PROTOCOL_VERSION, requestId: this.nextRequestId('cancel'), targetRequestId,
    })
  }

  openGitExtensions(repositoryId: string, surface: 'browse' = 'browse') {
    const requestId = this.nextRequestId('gitExtensions')
    return this.request<GitExtensionsOpenResult>('gitExtensions', requestId, {
      type: 'sourceControl.gitExtensions.open',
      version: SOURCE_CONTROL_PROTOCOL_VERSION,
      requestId,
      repositoryId,
      surface,
    })
  }

  close() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    this.pending.forEach((pending) => pending.reject(new Error('Source control disconnected.')))
    this.pending.clear()
    this.listeners.clear()
  }

  private request<T extends PendingValue>(kind: PendingKind, requestId: string, message: unknown) {
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Source control is available in the Hermes desktop app.'))
    return new Promise<T>((resolve, reject) => {
      this.pending.set(requestId, { kind, resolve: resolve as (value: PendingValue) => void, reject })
      bridge.postMessage(message)
    })
  }

  private connect() {
    const bridge = getBridge()
    if (bridge && bridge !== this.bridge) {
      this.bridge?.removeEventListener('message', this.receive)
      this.bridge = bridge
      bridge.addEventListener('message', this.receive)
    }
    return bridge
  }

  private complete(requestId: string, kind: PendingKind, value: PendingValue) {
    const pending = this.pending.get(requestId)
    if (!pending || pending.kind !== kind) return
    this.pending.delete(requestId)
    pending.resolve(value)
  }

  private fail(requestId: string, error: Error) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.pending.delete(requestId)
    pending.reject(error)
  }

  private nextRequestId(kind: string) {
    this.sequence = (this.sequence + 1) % Number.MAX_SAFE_INTEGER
    return `${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}`
  }
}

export const desktopSourceControlClient = new DesktopSourceControlClient()
