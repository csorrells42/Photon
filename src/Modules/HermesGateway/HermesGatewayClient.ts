export type HermesConnectionState = 'idle' | 'connecting' | 'reconnecting' | 'open' | 'closed' | 'error'

export class HermesAuthenticationError extends Error {
  override name = 'HermesAuthenticationError'
}

export class HermesConnectionCancelledError extends Error {
  override name = 'HermesConnectionCancelledError'
}

export function isHermesConnectionCancelledError(reason: unknown) {
  return reason instanceof Error && reason.name === 'HermesConnectionCancelledError'
}

export type HermesGatewayEvent = {
  type: string
  session_id?: string
  payload?: Record<string, unknown>
}

type JsonRpcFrame = {
  id?: string
  method?: string
  params?: HermesGatewayEvent
  result?: unknown
  error?: { message?: string }
}

export type HermesDecodedGatewayFrame =
  | { kind: 'response'; id: string; result?: unknown; error?: string }
  | { kind: 'event'; event: HermesGatewayEvent }

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function boundedText(value: unknown, maximum: number) {
  if (typeof value !== 'string') return ''
  const result = value.trim()
  return result.length <= maximum ? result : ''
}

export function normalizeHermesWsTicket(value: unknown): string | null {
  const ticket = boundedText(record(value).ticket, 8_192)
  return ticket && !/\s/.test(ticket) ? ticket : null
}

export function decodeHermesGatewayFrames(raw: string): HermesDecodedGatewayFrame[] {
  return raw.split('\n').flatMap((line): HermesDecodedGatewayFrame[] => {
    if (!line.trim()) return []
    let parsed: unknown
    try { parsed = JSON.parse(line) } catch { return [] }
    const frame = record(parsed) as JsonRpcFrame
    const id = boundedText(frame.id, 256)
    if (id) {
      const errorRecord = record(frame.error)
      const error = boundedText(errorRecord.message, 2_000)
      return [{ kind: 'response', id, ...(error ? { error } : { result: frame.result }) }]
    }

    const candidate = Object.keys(record(frame.params)).length ? record(frame.params) : record(parsed)
    const type = boundedText(candidate.type, 256) || boundedText(frame.method, 256)
    if (!type) return []
    const sessionId = boundedText(candidate.session_id, 512)
    const payload = record(candidate.payload)
    return [{
      kind: 'event',
      event: {
        type,
        ...(sessionId ? { session_id: sessionId } : {}),
        ...(Object.keys(payload).length ? { payload } : {}),
      },
    }]
  })
}

type PendingCall = {
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  timer: number
}

export type HermesStatus = {
  version?: string
  active_sessions?: number
}

export class HermesGatewayClient {
  private socket: WebSocket | null = null
  private nextId = 0
  private pending = new Map<string, PendingCall>()
  private eventListeners = new Set<(event: HermesGatewayEvent) => void>()
  private stateListeners = new Set<(state: HermesConnectionState) => void>()
  private state: HermesConnectionState = 'idle'
  private connectPromise: Promise<void> | null = null
  private connectGeneration = 0
  private ticketAbort: AbortController | null = null

  get connectionState() {
    return this.state
  }

  onEvent(listener: (event: HermesGatewayEvent) => void) {
    this.eventListeners.add(listener)
    return () => this.eventListeners.delete(listener)
  }

  onState(listener: (state: HermesConnectionState) => void) {
    this.stateListeners.add(listener)
    listener(this.state)
    return () => this.stateListeners.delete(listener)
  }

  async status(): Promise<HermesStatus> {
    const response = await fetch('/api/status', { credentials: 'include' })
    if (!response.ok) throw new Error(`Hermes status returned HTTP ${response.status}`)
    return response.json() as Promise<HermesStatus>
  }

  async connect(reconnecting = false) {
    if (this.socket?.readyState === WebSocket.OPEN) return
    if (this.connectPromise) return this.connectPromise

    const generation = ++this.connectGeneration
    const promise = this.connectSocket(reconnecting, generation)
    this.connectPromise = promise
    try { await promise }
    finally {
      if (this.connectPromise === promise) this.connectPromise = null
    }
  }

  private async connectSocket(reconnecting: boolean, generation: number) {
    this.setState(reconnecting ? 'reconnecting' : 'connecting')
    let socket: WebSocket | null = null
    const ticketAbort = new AbortController()
    this.ticketAbort = ticketAbort

    try {
      const ticketResponse = await fetch('/api/auth/ws-ticket', {
        method: 'POST',
        credentials: 'include',
        signal: ticketAbort.signal,
      })
      this.assertCurrentConnect(generation)
      if (!ticketResponse.ok) {
        if (ticketResponse.status === 401 || ticketResponse.status === 403) {
          throw new HermesAuthenticationError('Sign in to Hermes inside Workbench, then reconnect.')
        }
        throw new Error(`Hermes authentication returned HTTP ${ticketResponse.status}`)
      }

      const ticket = normalizeHermesWsTicket(await ticketResponse.json())
      this.assertCurrentConnect(generation)
      if (!ticket) throw new Error('Hermes did not issue a WebSocket ticket.')

      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
      const createdSocket = new WebSocket(`${protocol}//${window.location.host}/api/ws?ticket=${encodeURIComponent(ticket)}`)
      this.assertCurrentConnect(generation)
      socket = createdSocket
      this.socket = createdSocket

      createdSocket.addEventListener('message', (message) => this.handleFrame(String(message.data)))
      createdSocket.addEventListener('close', () => {
        if (this.socket !== createdSocket) return
        this.socket = null
        this.setState('closed')
        this.rejectPending(new Error('Hermes gateway connection closed.'))
      })

      await new Promise<void>((resolve, reject) => {
        let settled = false
        const finish = (callback: () => void) => {
          if (settled) return
          settled = true
          window.clearTimeout(timer)
          callback()
        }
        const timer = window.setTimeout(() => finish(() => reject(new Error('Hermes gateway connection timed out.'))), 15_000)
        createdSocket.addEventListener('open', () => {
          finish(() => {
            try {
              this.assertCurrentConnect(generation)
              if (this.socket !== createdSocket) throw new HermesConnectionCancelledError('Hermes connection was cancelled.')
              this.setState('open')
              resolve()
            } catch (reason) { reject(reason) }
          })
        }, { once: true })
        createdSocket.addEventListener('error', () => {
          finish(() => reject(new Error('Could not connect to the Hermes gateway.')))
        }, { once: true })
        createdSocket.addEventListener('close', () => {
          finish(() => reject(new HermesConnectionCancelledError('Hermes connection was cancelled.')))
        }, { once: true })
      })
    } catch (error) {
      if (socket && this.socket === socket) {
        this.socket = null
        socket.close()
      }
      if (generation !== this.connectGeneration || ticketAbort.signal.aborted) {
        throw new HermesConnectionCancelledError('Hermes connection was cancelled.')
      }
      this.setState('error')
      throw error
    } finally {
      if (this.ticketAbort === ticketAbort) this.ticketAbort = null
    }
  }

  close() {
    ++this.connectGeneration
    this.ticketAbort?.abort()
    this.ticketAbort = null
    this.connectPromise = null
    this.socket?.close()
    this.socket = null
    this.rejectPending(new Error('Hermes gateway connection closed.'))
    this.setState('closed')
  }

  private assertCurrentConnect(generation: number) {
    if (generation !== this.connectGeneration) {
      throw new HermesConnectionCancelledError('Hermes connection was cancelled.')
    }
  }

  request<T>(method: string, params: Record<string, unknown> = {}, timeoutMs = 120_000): Promise<T> {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error('Hermes gateway is not connected.'))
    }

    const id = `workbench-${++this.nextId}`
    return new Promise<T>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`Hermes request timed out: ${method}`))
      }, timeoutMs)

      this.pending.set(id, { resolve: (value) => resolve(value as T), reject, timer })
      this.socket?.send(JSON.stringify({ jsonrpc: '2.0', id, method, params }))
    })
  }

  private handleFrame(raw: string) {
    for (const frame of decodeHermesGatewayFrames(raw)) {
      if (frame.kind === 'response') {
        const call = this.pending.get(frame.id)
        if (!call) continue
        window.clearTimeout(call.timer)
        this.pending.delete(frame.id)
        if (frame.error) call.reject(new Error(frame.error))
        else call.resolve(frame.result)
        continue
      }
      this.eventListeners.forEach((listener) => listener(frame.event))
    }
  }

  private setState(state: HermesConnectionState) {
    this.state = state
    this.stateListeners.forEach((listener) => listener(state))
  }

  private rejectPending(error: Error) {
    for (const call of this.pending.values()) {
      window.clearTimeout(call.timer)
      call.reject(error)
    }
    this.pending.clear()
  }
}

export const hermesGateway = new HermesGatewayClient()
