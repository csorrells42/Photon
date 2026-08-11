import { DEVELOPER_SERVICES_PROTOCOL_VERSION, normalizeWorkspaceRelativePath } from './DesktopDeveloperServicesClient'
import type { RoslynDocumentSession } from './DesktopRoslynDiagnosticsClient'

const maximumResultCharacters = 1024 * 1024
const maximumRevision = 1_000_000_000

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

export type RoslynLanguageOperation = 'completion' | 'hover' | 'definition' | 'references' | 'rename' | 'code-actions'

export type RoslynLanguageRequest = {
  operation: RoslynLanguageOperation
  line: number
  character: number
  endLine?: number
  endCharacter?: number
  newName?: string
  includeDeclaration?: boolean
}

export type RoslynLanguageResponse = RoslynDocumentSession & {
  operation: RoslynLanguageOperation
  result: unknown | null
}

type RoslynLanguageFrame =
  | { type: 'result'; requestId: string; value: RoslynLanguageResponse }
  | { type: 'error'; requestId: string; message: string }

type Pending = {
  operation: RoslynLanguageOperation
  session: RoslynDocumentSession
  resolve(value: RoslynLanguageResponse): void
  reject(reason: Error): void
  abort?: () => void
  signal?: AbortSignal
}

function bridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function validRequestId(value: unknown) {
  return typeof value === 'string' && /^[a-zA-Z0-9_:-]{1,128}$/.test(value) ? value : null
}

function validSessionId(value: unknown) {
  return typeof value === 'string' && /^[a-f0-9]{48}$/.test(value) ? value : null
}

function validRevision(value: unknown) {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= maximumRevision ? value : null
}

function validOperation(value: unknown): RoslynLanguageOperation | null {
  return value === 'completion' || value === 'hover' || value === 'definition' || value === 'references'
    || value === 'rename' || value === 'code-actions' ? value : null
}

function validDocumentPath(value: unknown) {
  if (typeof value !== 'string') return null
  const path = normalizeWorkspaceRelativePath(value)
  return path?.toLowerCase().endsWith('.cs') ? path : null
}

function boundedResult(value: unknown) {
  if (value === null) return null
  try {
    const serialized = JSON.stringify(value)
    return serialized.length <= maximumResultCharacters ? value : undefined
  } catch {
    return undefined
  }
}

export function normalizeRoslynLanguageFrame(value: unknown): RoslynLanguageFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== DEVELOPER_SERVICES_PROTOCOL_VERSION) return null
  const requestId = validRequestId(raw.requestId)
  if (!requestId) return null
  if (raw.type === 'developerServices.error') {
    const message = typeof raw.message === 'string' ? raw.message.slice(0, 1_024) : ''
    return message ? { type: 'error', requestId, message } : null
  }
  if (raw.type !== 'developerServices.language.result') return null
  const sessionId = validSessionId(raw.sessionId)
  const documentPath = validDocumentPath(raw.documentPath)
  const revision = validRevision(raw.revision)
  const operation = validOperation(raw.operation)
  const result = boundedResult(raw.result)
  if (!sessionId || !documentPath || revision === null || !operation || result === undefined) return null
  return { type: 'result', requestId, value: { sessionId, documentPath, revision, operation, result } }
}

export class DesktopRoslynLanguageClient {
  private webview: WebViewBridge | null = null
  private sequence = 0
  private readonly pending = new Map<string, Pending>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeRoslynLanguageFrame(event.data)
    if (!frame) return
    const pending = this.pending.get(frame.requestId)
    if (!pending) return
    if (frame.type === 'error') {
      this.complete(frame.requestId)
      pending.reject(new Error(frame.message))
      return
    }
    if (frame.value.operation !== pending.operation
      || frame.value.sessionId !== pending.session.sessionId
      || frame.value.documentPath !== pending.session.documentPath
      || frame.value.revision !== pending.session.revision) return
    this.complete(frame.requestId)
    pending.resolve(frame.value)
  }

  get available() { return bridge() !== null }

  request(session: RoslynDocumentSession, request: RoslynLanguageRequest, signal?: AbortSignal) {
    const documentPath = validDocumentPath(session.documentPath)
    if (!documentPath || !validSessionId(session.sessionId) || validRevision(session.revision) === null)
      return Promise.reject(new Error('The C# language session is invalid.'))
    const normalized = validateRequest(request)
    if (signal?.aborted) return Promise.reject(new Error('The C# language request was cancelled.'))
    const webview = this.connect()
    if (!webview) return Promise.reject(new Error('C# language features are available in the Hermes desktop app.'))
    const requestId = this.nextId(normalized.operation)
    return new Promise<RoslynLanguageResponse>((resolve, reject) => {
      const pending: Pending = { operation: normalized.operation, session: { ...session, documentPath }, resolve, reject }
      if (signal) {
        pending.abort = () => {
          if (!this.pending.has(requestId)) return
          this.complete(requestId)
          try {
            webview.postMessage({
              type: 'developerServices.language.cancel', version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
              requestId: this.nextId('language-cancel'), targetRequestId: requestId,
            })
          } catch { /* The local request is already cancelled. */ }
          reject(new Error('The C# language request was cancelled.'))
        }
        signal.addEventListener('abort', pending.abort, { once: true })
        pending.signal = signal
      }
      this.pending.set(requestId, pending)
      try {
        webview.postMessage({
          type: 'developerServices.language.request', version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
          requestId, sessionId: session.sessionId, revision: session.revision, documentPath,
          operation: normalized.operation, line: normalized.line, character: normalized.character,
          endLine: normalized.endLine, endCharacter: normalized.endCharacter,
          includeDeclaration: normalized.includeDeclaration, ...(normalized.newName ? { newName: normalized.newName } : {}),
        })
      } catch (reason) {
        this.complete(requestId)
        reject(reason instanceof Error ? reason : new Error('The C# language request could not be sent.'))
      }
    })
  }

  private complete(requestId: string) {
    const pending = this.pending.get(requestId)
    this.pending.delete(requestId)
    if (pending?.abort) {
      pending.signal?.removeEventListener('abort', pending.abort)
    }
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
    return `roslyn-${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}`
  }
}

function validateRequest(request: RoslynLanguageRequest) {
  const operation = validOperation(request.operation)
  const coordinate = (value: unknown) => typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 1_000_000
  if (!operation || !coordinate(request.line) || !coordinate(request.character)) throw new Error('The C# language position is invalid.')
  const endLine = request.endLine ?? request.line
  const endCharacter = request.endCharacter ?? request.character
  if (!coordinate(endLine) || !coordinate(endCharacter) || endLine < request.line
    || (endLine === request.line && endCharacter < request.character)) throw new Error('The C# language range is invalid.')
  const newName = request.newName?.trim()
  if (operation === 'rename' && (!newName || newName.length > 512 || newName.includes('\0'))) throw new Error('The C# rename target is invalid.')
  return { operation, line: request.line, character: request.character, endLine, endCharacter, newName, includeDeclaration: request.includeDeclaration !== false }
}

export const desktopRoslynLanguageClient = new DesktopRoslynLanguageClient()
