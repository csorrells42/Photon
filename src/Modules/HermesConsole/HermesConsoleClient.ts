import { normalizeHermesWsTicket } from '../HermesGateway/HermesGatewayClient'

export const HERMES_CONSOLE_ADAPTER_VERSION = 1

export type HermesConsoleConnectionState = 'idle' | 'connecting' | 'open' | 'closed' | 'error'

export type HermesConsoleFrame = {
  type: 'ready' | 'output' | 'error' | 'complete' | 'confirm_required' | 'clear' | 'pong' | 'unknown'
  id?: number
  stream?: 'stdout' | 'stderr'
  data?: string
  message?: string
  command?: string
  prompt?: string
  profile?: string
  status?: string
}

type RawConsoleFrame = Record<string, unknown>

function optionalString(value: unknown) {
  return typeof value === 'string' ? value : undefined
}

export function normalizeConsoleFrame(value: unknown): HermesConsoleFrame | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as RawConsoleFrame
  const knownTypes = new Set(['ready', 'output', 'error', 'complete', 'confirm_required', 'clear', 'pong'])
  const rawType = optionalString(raw.type)?.trim().toLowerCase() ?? ''
  const type = knownTypes.has(rawType) ? rawType as HermesConsoleFrame['type'] : 'unknown'
  return {
    type,
    id: typeof raw.id === 'number' && Number.isFinite(raw.id) ? raw.id : undefined,
    stream: raw.stream === 'stderr' ? 'stderr' : raw.stream === 'stdout' ? 'stdout' : undefined,
    data: optionalString(raw.data),
    message: optionalString(raw.message),
    command: optionalString(raw.command),
    prompt: optionalString(raw.prompt),
    profile: optionalString(raw.profile),
    status: optionalString(raw.status),
  }
}

export class HermesConsoleClient {
  private socket: WebSocket | null = null
  private frameListeners = new Set<(frame: HermesConsoleFrame) => void>()
  private stateListeners = new Set<(state: HermesConsoleConnectionState) => void>()
  private state: HermesConsoleConnectionState = 'idle'

  get connectionState() { return this.state }

  onFrame(listener: (frame: HermesConsoleFrame) => void) {
    this.frameListeners.add(listener)
    return () => this.frameListeners.delete(listener)
  }

  onState(listener: (state: HermesConsoleConnectionState) => void) {
    this.stateListeners.add(listener)
    listener(this.state)
    return () => this.stateListeners.delete(listener)
  }

  async connect() {
    if (this.socket?.readyState === WebSocket.OPEN || this.state === 'connecting') return
    this.setState('connecting')
    try {
      const ticketResponse = await fetch('/api/auth/ws-ticket', { method: 'POST', credentials: 'include' })
      if (!ticketResponse.ok) throw new Error(ticketResponse.status === 401
        ? 'Sign in to Hermes before opening the console.'
        : `Hermes console authentication returned HTTP ${ticketResponse.status}.`)
      const ticket = normalizeHermesWsTicket(await ticketResponse.json())
      if (!ticket) throw new Error('Hermes did not issue a console ticket.')

      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
      const socket = new WebSocket(`${protocol}//${window.location.host}/api/console?ticket=${encodeURIComponent(ticket)}`)
      this.socket = socket
      socket.addEventListener('message', (event) => this.handleMessage(String(event.data)))
      socket.addEventListener('close', () => {
        if (this.socket !== socket) return
        this.socket = null
        this.setState('closed')
      })

      await new Promise<void>((resolve, reject) => {
        const timer = window.setTimeout(() => reject(new Error('Hermes console connection timed out.')), 15_000)
        socket.addEventListener('open', () => {
          window.clearTimeout(timer)
          this.setState('open')
          resolve()
        }, { once: true })
        socket.addEventListener('error', () => {
          window.clearTimeout(timer)
          reject(new Error('Could not connect to Hermes Console.'))
        }, { once: true })
      })
    } catch (reason) {
      const failedSocket = this.socket
      this.socket = null
      failedSocket?.close()
      this.setState('error')
      throw reason
    }
  }

  run(command: string) { this.send({ type: 'command', command }) }
  confirm(command: string) { this.send({ type: 'confirm', command }) }
  cancel() { this.send({ type: 'cancel' }) }
  ping() { this.send({ type: 'ping' }) }

  close() {
    const socket = this.socket
    this.socket = null
    socket?.close()
    this.setState('closed')
  }

  private send(frame: Record<string, unknown>) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) throw new Error('Hermes Console is not connected.')
    this.socket.send(JSON.stringify(frame))
  }

  private handleMessage(raw: string) {
    let parsed: unknown
    try { parsed = JSON.parse(raw) } catch { return }
    const frame = normalizeConsoleFrame(parsed)
    if (frame) this.frameListeners.forEach((listener) => listener(frame))
  }

  private setState(state: HermesConsoleConnectionState) {
    this.state = state
    this.stateListeners.forEach((listener) => listener(state))
  }
}
