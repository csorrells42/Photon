export const NATIVE_TERMINAL_ADAPTER_VERSION = 1

export type NativeTerminalConnectionState = 'browser' | 'connecting' | 'open' | 'closed' | 'error'
export type NativeTerminalCommandRequest = { nonce: number; command: string }

export type NativeTerminalFrame = {
  type: 'ready' | 'output' | 'exit' | 'error' | 'pong' | 'unknown'
  version?: number
  sessionId?: string
  processId?: number
  shell?: string
  cwd?: string
  data?: string
  exitCode?: number
  message?: string
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type RawFrame = Record<string, unknown>

function optionalString(value: unknown) { return typeof value === 'string' ? value : undefined }
function optionalNumber(value: unknown) { return typeof value === 'number' && Number.isFinite(value) ? value : undefined }

export function getDesktopWebView(): WebViewBridge | null {
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

export function isNativeTerminalAvailable() { return getDesktopWebView() !== null }

export function normalizeNativeTerminalFrame(value: unknown): NativeTerminalFrame | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as RawFrame
  const rawType = optionalString(raw.type)?.toLowerCase() ?? ''
  const type = new Set(['terminal.ready', 'terminal.output', 'terminal.exit', 'terminal.error', 'host.pong']).has(rawType)
    ? rawType.replace('terminal.', '').replace('host.', '') as NativeTerminalFrame['type']
    : 'unknown'
  return {
    type,
    version: optionalNumber(raw.version),
    sessionId: optionalString(raw.sessionId),
    processId: optionalNumber(raw.processId),
    shell: optionalString(raw.shell),
    cwd: optionalString(raw.cwd),
    data: optionalString(raw.data),
    exitCode: optionalNumber(raw.exitCode),
    message: optionalString(raw.message),
  }
}

export class DesktopHostTerminalClient {
  private bridge: WebViewBridge | null = null
  private listeners = new Set<(frame: NativeTerminalFrame) => void>()
  private stateListeners = new Set<(state: NativeTerminalConnectionState) => void>()
  private state: NativeTerminalConnectionState = 'browser'
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeNativeTerminalFrame(event.data)
    if (!frame || frame.type === 'unknown') return
    if (frame.version !== undefined && frame.version !== NATIVE_TERMINAL_ADAPTER_VERSION) {
      this.setState('error')
      this.emit({ type: 'error', message: `Terminal protocol ${frame.version} is not supported by this Workbench.` })
      return
    }
    if (frame.type === 'ready') this.setState('open')
    if (frame.type === 'exit') this.setState('closed')
    if (frame.type === 'error') this.setState('error')
    this.emit(frame)
  }

  get connectionState() { return this.state }

  onFrame(listener: (frame: NativeTerminalFrame) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  onState(listener: (state: NativeTerminalConnectionState) => void) {
    this.stateListeners.add(listener)
    listener(this.state)
    return () => this.stateListeners.delete(listener)
  }

  start(columns: number, rows: number) {
    const bridge = getDesktopWebView()
    if (!bridge)
    {
      this.setState('browser')
      return false
    }
    if (this.bridge !== bridge) {
      this.bridge?.removeEventListener('message', this.receive)
      this.bridge = bridge
      bridge.addEventListener('message', this.receive)
    }
    this.setState('connecting')
    bridge.postMessage({ type: 'terminal.start', version: NATIVE_TERMINAL_ADAPTER_VERSION, columns, rows })
    return true
  }

  write(data: string) { this.bridge?.postMessage({ type: 'terminal.input', version: NATIVE_TERMINAL_ADAPTER_VERSION, data }) }
  resize(columns: number, rows: number) { this.bridge?.postMessage({ type: 'terminal.resize', version: NATIVE_TERMINAL_ADAPTER_VERSION, columns, rows }) }
  stop() { this.bridge?.postMessage({ type: 'terminal.stop', version: NATIVE_TERMINAL_ADAPTER_VERSION }) }

  close() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    this.setState('browser')
  }

  private emit(frame: NativeTerminalFrame) { this.listeners.forEach((listener) => listener(frame)) }
  private setState(state: NativeTerminalConnectionState) {
    this.state = state
    this.stateListeners.forEach((listener) => listener(state))
  }
}
