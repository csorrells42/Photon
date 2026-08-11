import { getDesktopWebView } from '../NativeTerminal/DesktopHostTerminalClient'

export const CODEX_APP_SERVER_ADAPTER_VERSION = 1

export type JsonRpcId = number | string
export type JsonObject = Record<string, unknown>
export type CodexConnectionState = 'browser' | 'connecting' | 'open' | 'unavailable' | 'closed' | 'error'

export type CodexHostFrame = {
  type: 'ready' | 'protocol' | 'unavailable' | 'exit' | 'error' | 'unknown'
  version?: number
  processId?: number
  executable?: string
  cwd?: string
  initialized?: boolean
  exitCode?: number
  message?: string
  payload?: JsonObject
}

type PendingRequest = {
  method: string
  resolve: (value: unknown) => void
  reject: (error: Error) => void
}

function optionalString(value: unknown) { return typeof value === 'string' ? value : undefined }
function optionalNumber(value: unknown) { return typeof value === 'number' && Number.isFinite(value) ? value : undefined }
function optionalBoolean(value: unknown) { return typeof value === 'boolean' ? value : undefined }
function optionalObject(value: unknown) { return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : undefined }

export function normalizeCodexHostFrame(value: unknown): CodexHostFrame | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as JsonObject
  const rawType = optionalString(raw.type)?.toLowerCase() ?? ''
  const supported = new Set(['codex.ready', 'codex.protocol', 'codex.unavailable', 'codex.exit', 'codex.error'])
  const type = supported.has(rawType) ? rawType.replace('codex.', '') as CodexHostFrame['type'] : 'unknown'
  return {
    type,
    version: optionalNumber(raw.version),
    processId: optionalNumber(raw.processId),
    executable: optionalString(raw.executable),
    cwd: optionalString(raw.cwd),
    initialized: optionalBoolean(raw.initialized),
    exitCode: optionalNumber(raw.exitCode),
    message: optionalString(raw.message),
    payload: optionalObject(raw.payload),
  }
}

export class CodexAppServerClient {
  private bridge = getDesktopWebView()
  private listeners = new Set<(frame: CodexHostFrame) => void>()
  private stateListeners = new Set<(state: CodexConnectionState) => void>()
  private pending = new Map<string, PendingRequest>()
  private nextRequestId = 1
  private state: CodexConnectionState = this.bridge ? 'connecting' : 'browser'
  private listening = false

  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeCodexHostFrame(event.data)
    if (!frame || frame.type === 'unknown') return
    if (frame.version !== undefined && frame.version !== CODEX_APP_SERVER_ADAPTER_VERSION) {
      this.setState('error')
      this.emit({ type: 'error', message: `Codex host adapter ${frame.version} is not supported by this Workbench.` })
      return
    }
    if (frame.type === 'ready') this.setState('open')
    if (frame.type === 'unavailable') this.setState('unavailable')
    if (frame.type === 'exit') this.setState('closed')
    if (frame.type === 'error') this.setState('error')
    if (frame.type === 'protocol' && frame.payload) this.resolveResponse(frame.payload)
    this.emit(frame)
  }

  get connectionState() { return this.state }

  onFrame(listener: (frame: CodexHostFrame) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  onState(listener: (state: CodexConnectionState) => void) {
    this.stateListeners.add(listener)
    listener(this.state)
    return () => this.stateListeners.delete(listener)
  }

  start() {
    this.bridge = getDesktopWebView()
    if (!this.bridge) {
      this.setState('browser')
      return false
    }
    if (!this.listening) {
      this.bridge.addEventListener('message', this.receive)
      this.listening = true
    }
    this.setState('connecting')
    this.bridge.postMessage({ type: 'codex.start', version: CODEX_APP_SERVER_ADAPTER_VERSION })
    return true
  }

  request<T = unknown>(method: string, params?: unknown): Promise<T> {
    const id = this.nextRequestId++
    return new Promise<T>((resolve, reject) => {
      if (!this.bridge) {
        reject(new Error('Codex is only available in the Hermes desktop app.'))
        return
      }
      this.pending.set(String(id), { method, resolve: resolve as (value: unknown) => void, reject })
      this.sendProtocol({ method, id, ...(params === undefined ? {} : { params }) })
    })
  }

  notify(method: string, params?: unknown) {
    this.sendProtocol({ method, ...(params === undefined ? {} : { params }) })
  }

  respond(id: JsonRpcId, result: unknown) {
    this.sendProtocol({ id, result })
  }

  stop() { this.bridge?.postMessage({ type: 'codex.stop', version: CODEX_APP_SERVER_ADAPTER_VERSION }) }

  close() {
    if (this.bridge && this.listening) this.bridge.removeEventListener('message', this.receive)
    this.listening = false
    this.bridge = null
    this.rejectPending('Codex panel disconnected.')
    this.setState('browser')
  }

  private sendProtocol(payload: JsonObject) {
    this.bridge?.postMessage({ type: 'codex.send', version: CODEX_APP_SERVER_ADAPTER_VERSION, payload })
  }

  private resolveResponse(payload: JsonObject) {
    if ('method' in payload || !('id' in payload)) return
    const pending = this.pending.get(String(payload.id))
    if (!pending) return
    this.pending.delete(String(payload.id))
    if (payload.error && typeof payload.error === 'object') {
      const message = optionalString((payload.error as JsonObject).message) ?? `${pending.method} failed.`
      pending.reject(new Error(message))
      return
    }
    pending.resolve(payload.result)
  }

  private rejectPending(message: string) {
    this.pending.forEach(({ reject }) => reject(new Error(message)))
    this.pending.clear()
  }

  private emit(frame: CodexHostFrame) { this.listeners.forEach((listener) => listener(frame)) }
  private setState(state: CodexConnectionState) {
    this.state = state
    this.stateListeners.forEach((listener) => listener(state))
    if (state === 'closed' || state === 'unavailable') this.rejectPending('Codex app-server is not available.')
  }
}
