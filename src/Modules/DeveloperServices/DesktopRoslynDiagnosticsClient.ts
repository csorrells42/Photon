import {
  DEVELOPER_SERVICES_PROTOCOL_VERSION,
  normalizeWorkspaceRelativePath,
  type DeveloperDiagnostic,
} from './DesktopDeveloperServicesClient'

const maximumDocumentCharacters = 4 * 1024 * 1024
const maximumDiagnostics = 2_000
const maximumRevision = 1_000_000_000

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

export type RoslynDocumentSession = {
  sessionId: string
  documentPath: string
  revision: number
}

export type RoslynDocumentDiagnostics = RoslynDocumentSession & {
  diagnostics: DeveloperDiagnostic[]
}

type RoslynFrame =
  | { type: 'opened' | 'changed'; requestId: string; value: RoslynDocumentSession }
  | { type: 'closed'; requestId: string; sessionId: string }
  | { type: 'diagnostics'; value: RoslynDocumentDiagnostics }
  | { type: 'error'; requestId: string; message: string }

type Pending = {
  kind: 'open' | 'change' | 'close'
  sessionId?: string
  documentPath: string
  revision?: number
  resolve(value: RoslynDocumentSession | void): void
  reject(reason: Error): void
}

function bridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function boundedText(value: unknown, maximum: number) {
  return typeof value === 'string' ? value.slice(0, maximum) : ''
}

function requestId(value: unknown) {
  return typeof value === 'string' && /^[a-zA-Z0-9_:-]{1,128}$/.test(value) ? value : null
}

function sessionId(value: unknown) {
  return typeof value === 'string' && /^[a-f0-9]{48}$/.test(value) ? value : null
}

function revision(value: unknown) {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= maximumRevision ? value : null
}

function documentPath(value: unknown) {
  if (typeof value !== 'string') return null
  const path = normalizeWorkspaceRelativePath(value)
  return path && path.toLowerCase().endsWith('.cs') ? path : null
}

function diagnostic(value: unknown, expectedPath: string): DeveloperDiagnostic | null {
  const raw = record(value)
  const range = record(raw?.range)
  const start = record(range?.start)
  const end = record(range?.end)
  if (!raw || !range || !start || !end || documentPath(raw.filePath) !== expectedPath
    || (raw.severity !== 'error' && raw.severity !== 'warning' && raw.severity !== 'info')
    || raw.source !== 'roslyn' || typeof raw.message !== 'string' || !raw.message) return null
  const positive = (candidate: unknown) => typeof candidate === 'number' && Number.isInteger(candidate) && candidate >= 1 && candidate <= 1_000_000
    ? candidate
    : null
  const startLine = positive(start.line)
  const startColumn = positive(start.column)
  const endLine = positive(end.line)
  const endColumn = positive(end.column)
  if (startLine === null || startColumn === null || endLine === null || endColumn === null || endLine < startLine) return null
  return {
    filePath: expectedPath,
    severity: raw.severity,
    code: boundedText(raw.code, 128),
    message: boundedText(raw.message, 2_048),
    source: 'roslyn',
    range: { start: { line: startLine, column: startColumn }, end: { line: endLine, column: endColumn } },
  }
}

export function normalizeRoslynFrame(value: unknown): RoslynFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== DEVELOPER_SERVICES_PROTOCOL_VERSION) return null
  if (raw.type === 'developerServices.language.diagnostics') {
    const id = sessionId(raw.sessionId)
    const path = documentPath(raw.documentPath)
    const documentRevision = revision(raw.revision)
    if (!id || !path || documentRevision === null || !Array.isArray(raw.diagnostics) || raw.diagnostics.length > maximumDiagnostics) return null
    const diagnostics = raw.diagnostics.map((item) => diagnostic(item, path))
    if (diagnostics.some((item) => item === null)) return null
    return { type: 'diagnostics', value: { sessionId: id, documentPath: path, revision: documentRevision, diagnostics: diagnostics as DeveloperDiagnostic[] } }
  }
  const id = requestId(raw.requestId)
  if (!id) return null
  if (raw.type === 'developerServices.error') {
    const message = boundedText(raw.message, 1_024)
    return message ? { type: 'error', requestId: id, message } : null
  }
  if (raw.type === 'developerServices.language.close.result') {
    const documentSessionId = sessionId(raw.sessionId)
    return documentSessionId ? { type: 'closed', requestId: id, sessionId: documentSessionId } : null
  }
  if (raw.type === 'developerServices.language.open.result' || raw.type === 'developerServices.language.change.result') {
    const documentSessionId = sessionId(raw.sessionId)
    const path = documentPath(raw.documentPath)
    const documentRevision = revision(raw.revision)
    if (!documentSessionId || !path || documentRevision === null) return null
    return {
      type: raw.type.endsWith('open.result') ? 'opened' : 'changed',
      requestId: id,
      value: { sessionId: documentSessionId, documentPath: path, revision: documentRevision },
    }
  }
  return null
}

export class DesktopRoslynDiagnosticsClient {
  private webview: WebViewBridge | null = null
  private sequence = 0
  private readonly pending = new Map<string, Pending>()
  private readonly listeners = new Set<(diagnostics: RoslynDocumentDiagnostics) => void>()
  private readonly latest = new Map<string, RoslynDocumentDiagnostics>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeRoslynFrame(event.data)
    if (!frame) return
    if (frame.type === 'diagnostics') {
      const previous = this.latest.get(frame.value.sessionId)
      if (previous && previous.revision > frame.value.revision) return
      this.latest.set(frame.value.sessionId, frame.value)
      this.listeners.forEach((listener) => listener(frame.value))
      return
    }
    const pending = this.pending.get(frame.requestId)
    if (!pending) return
    if (frame.type === 'error') {
      this.pending.delete(frame.requestId)
      pending.reject(new Error(frame.message))
      return
    }
    if (frame.type === 'opened' && pending.kind === 'open'
      && frame.value.documentPath === pending.documentPath && frame.value.revision === pending.revision) {
      this.pending.delete(frame.requestId)
      pending.resolve(frame.value)
    } else if (frame.type === 'changed' && pending.kind === 'change'
      && frame.value.sessionId === pending.sessionId && frame.value.documentPath === pending.documentPath
      && frame.value.revision === pending.revision) {
      this.pending.delete(frame.requestId)
      pending.resolve(frame.value)
    } else if (frame.type === 'closed' && pending.kind === 'close' && frame.sessionId === pending.sessionId) {
      this.pending.delete(frame.requestId)
      this.latest.delete(frame.sessionId)
      pending.resolve()
    }
  }

  get available() { return bridge() !== null }

  onDiagnostics(listener: (diagnostics: RoslynDocumentDiagnostics) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  latestDiagnostics(session: RoslynDocumentSession) {
    const latest = this.latest.get(session.sessionId)
    return latest && latest.documentPath === session.documentPath ? latest : null
  }

  open(document: string, text: string, documentRevision = 1) {
    const path = validateDocument(document, text, documentRevision)
    const id = this.nextId('language-open')
    return this.request('open', id, path, documentRevision, undefined, {
      type: 'developerServices.language.open', version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
      requestId: id, revision: documentRevision, documentPath: path, text,
    }) as Promise<RoslynDocumentSession>
  }

  change(session: RoslynDocumentSession, text: string, documentRevision: number) {
    const path = validateDocument(session.documentPath, text, documentRevision)
    if (!sessionId(session.sessionId)) return Promise.reject(new Error('The C# language-session identifier is invalid.'))
    const id = this.nextId('language-change')
    return this.request('change', id, path, documentRevision, session.sessionId, {
      type: 'developerServices.language.change', version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
      requestId: id, sessionId: session.sessionId, revision: documentRevision, documentPath: path, text,
    }) as Promise<RoslynDocumentSession>
  }

  close(session: RoslynDocumentSession) {
    const path = documentPath(session.documentPath)
    if (!path || !sessionId(session.sessionId)) return Promise.reject(new Error('The C# language session is invalid.'))
    const id = this.nextId('language-close')
    return this.request('close', id, path, undefined, session.sessionId, {
      type: 'developerServices.language.close', version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
      requestId: id, sessionId: session.sessionId, documentPath: path,
    }) as Promise<void>
  }

  private request(kind: Pending['kind'], id: string, path: string, documentRevision: number | undefined, documentSessionId: string | undefined, message: unknown) {
    const webview = this.connect()
    if (!webview) return Promise.reject(new Error('Live C# diagnostics are available in the Hermes desktop app.'))
    return new Promise<RoslynDocumentSession | void>((resolve, reject) => {
      this.pending.set(id, { kind, documentPath: path, revision: documentRevision, sessionId: documentSessionId, resolve, reject })
      try { webview.postMessage(message) }
      catch (reason) {
        this.pending.delete(id)
        reject(reason instanceof Error ? reason : new Error('The C# language request could not be sent.'))
      }
    })
  }

  private connect() {
    const next = bridge()
    if (next && next !== this.webview) {
      this.webview?.removeEventListener('message', this.receive)
      this.webview = next
      next.addEventListener('message', this.receive)
    }
    return next
  }

  private nextId(kind: string) {
    this.sequence = (this.sequence + 1) % Number.MAX_SAFE_INTEGER
    return `${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}`
  }
}

function validateDocument(pathValue: string, text: string, documentRevision: number) {
  const path = documentPath(pathValue)
  if (!path) throw new Error('Select a workspace-relative C# document.')
  if (typeof text !== 'string' || text.length > maximumDocumentCharacters || text.includes('\0')) throw new Error('The C# document text exceeds the safe language-service boundary.')
  if (revision(documentRevision) === null) throw new Error('The C# document revision is invalid.')
  return path
}

export const desktopRoslynDiagnosticsClient = new DesktopRoslynDiagnosticsClient()
