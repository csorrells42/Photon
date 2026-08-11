import { afterEach, describe, expect, it, vi } from 'vitest'
import { HermesConnectionCancelledError, HermesGatewayClient } from './HermesGatewayClient'

class FakeWebSocket {
  static OPEN = 1
  readyState = 0
  readonly listeners = new Map<string, Array<() => void>>()
  constructor(readonly url: string) { FakeWebSocket.instances.push(this) }
  static instances: FakeWebSocket[] = []
  addEventListener(type: string, listener: () => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  emit(type: string) { for (const listener of this.listeners.get(type) ?? []) listener() }
  close() { this.emit('close') }
  send() {}
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}

describe('Hermes gateway connection lifecycle', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    FakeWebSocket.instances = []
  })

  it('does not create a socket when closed during ticket acquisition', async () => {
    const ticket = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(() => ticket.promise))
    vi.stubGlobal('window', { location: { protocol: 'http:', host: '127.0.0.1:4173' }, setTimeout, clearTimeout })
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const client = new HermesGatewayClient()

    const connecting = client.connect()
    client.close()
    ticket.resolve(new Response(JSON.stringify({ ticket: 'late-ticket' }), { status: 200 }))

    await expect(connecting).rejects.toBeInstanceOf(HermesConnectionCancelledError)
    expect(FakeWebSocket.instances).toHaveLength(0)
    expect(client.connectionState).toBe('closed')
  })

  it('cannot reopen a socket after close invalidates an in-flight connection', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ticket: 'ticket' }), { status: 200 })))
    vi.stubGlobal('window', { location: { protocol: 'http:', host: '127.0.0.1:4173' }, setTimeout, clearTimeout })
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const client = new HermesGatewayClient()

    const connecting = client.connect()
    await vi.waitFor(() => expect(FakeWebSocket.instances).toHaveLength(1))
    const socket = FakeWebSocket.instances[0]
    client.close()
    socket.readyState = FakeWebSocket.OPEN
    socket.emit('open')

    await expect(connecting).rejects.toBeInstanceOf(HermesConnectionCancelledError)
    expect(client.connectionState).toBe('closed')
  })
})
