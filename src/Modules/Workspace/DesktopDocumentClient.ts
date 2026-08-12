export const DOCUMENT_PROTOCOL_VERSION = 1

type DocumentResult = { cancelled: boolean; path?: string; sha256?: string }
type Bridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}
type Pending = { resolve: (value: DocumentResult) => void; reject: (reason: Error) => void }

function bridge() {
  return (window as Window & { chrome?: { webview?: Bridge } }).chrome?.webview ?? null
}

function bounded(value: unknown, maximum: number) {
  return typeof value === 'string' && value.length <= maximum ? value : ''
}

export class DesktopDocumentClient {
  private sequence = 0
  private connected: Bridge | null = null
  private readonly pending = new Map<string, Pending>()
  private readonly receive = (event: MessageEvent) => {
    const raw = event.data as Record<string, unknown> | null
    if (!raw || raw.version !== DOCUMENT_PROTOCOL_VERSION) return
    const requestId = bounded(raw.requestId, 128)
    const pending = this.pending.get(requestId)
    if (!pending) return
    if (raw.type === 'document.error') {
      this.pending.delete(requestId)
      pending.reject(new Error(bounded(raw.message, 1_024) || 'The desktop document operation failed.'))
      return
    }
    if (raw.type !== 'document.pick.result' && raw.type !== 'document.save.result' && raw.type !== 'document.repository.result') return
    const path = bounded(raw.path, 2_048) || undefined
    const sha256 = bounded(raw.sha256, 64) || undefined
    if (raw.cancelled !== true && !path) return
    if (raw.cancelled !== true && (!sha256 || !/^[a-f0-9]{64}$/u.test(sha256))) return
    this.pending.delete(requestId)
    pending.resolve({ cancelled: raw.cancelled === true, path, sha256 })
  }

  get available() { return bridge() !== null }
  pickFile() { return this.request('document.pick', {}) }
  pickRepository() { return this.request('document.repository.pick', {}) }
  save(path: string, content: string, expectedSha256: string) { return this.request('document.save', { path, content, expectedSha256 }) }
  saveAs(path: string, content: string) { return this.request('document.saveAs', { path, content }) }

  private request(type: string, fields: Record<string, unknown>) {
    const host = bridge()
    if (!host) return Promise.reject(new Error('File operations require the Photos Agape Aphthartos desktop app.'))
    if (host !== this.connected) {
      this.connected?.removeEventListener('message', this.receive)
      this.connected = host
      host.addEventListener('message', this.receive)
    }
    const requestId = `document:${Date.now().toString(36)}:${(++this.sequence).toString(36)}`
    return new Promise<DocumentResult>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject })
      host.postMessage({ type, version: DOCUMENT_PROTOCOL_VERSION, requestId, ...fields })
    })
  }
}

export const desktopDocumentClient = new DesktopDocumentClient()
