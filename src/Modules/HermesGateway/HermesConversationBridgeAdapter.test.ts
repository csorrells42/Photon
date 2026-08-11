import { describe, expect, it, vi } from 'vitest'
import { createHermesBridgeSnapshot, normalizeHermesBridgeObservation, registerHermesConversationBridge } from './HermesConversationBridgeAdapter'

describe('Hermes conversation bridge adapter', () => {
  it('maps the visible dock into the authenticated bridge contract', () => {
    const snapshot = createHermesBridgeSnapshot('open', true, 'stored-1', [
      { id: 'u1', author: 'you', body: 'Please inspect the workspace.' },
      { id: 'a1', author: 'hermes', body: 'I am checking it now.', streaming: true },
    ], [
      { id: 't1', name: 'serena_search', phase: 'complete', output: 'Found the symbol.' },
    ])

    expect(snapshot).toMatchObject({
      assistant: 'Hermes',
      generation: 'renderer:unbound',
      state: 'open',
      isBusy: true,
      sessionId: 'stored-1',
      messages: [
        { id: 'u1', role: 'user', text: 'Please inspect the workspace.' },
        { id: 'a1', role: 'assistant', text: 'I am checking it now.' },
      ],
      activities: [{ id: 't1', name: 'serena_search', phase: 'complete', detail: 'Found the symbol.' }],
    })
  })

  it('projects an explicit renderer generation for peer lifecycle binding', () => {
    expect(createHermesBridgeSnapshot('open', false, 'session-1', [], [], 'renderer:abc123').generation)
      .toBe('renderer:abc123')
  })

  it('bounds external snapshot history and tool detail', () => {
    const messages = Array.from({ length: 250 }, (_, index) => ({ id: `m${index}`, author: 'hermes' as const, body: `message ${index}` }))
    const snapshot = createHermesBridgeSnapshot('open', false, null, messages, [
      { id: 'large', name: 'execute', phase: 'complete', output: 'x'.repeat(9_000) },
    ])

    expect(snapshot.messages).toHaveLength(200)
    expect(snapshot.messages[0].id).toBe('m50')
    expect(snapshot.activities[0].detail?.length).toBeLessThan(8 * 1024 + 20)
    expect(snapshot.activities[0].detail).toContain('truncated')
  })

  it('normalizes an exact sender-first passive observation without invoking a turn', () => {
    expect(normalizeHermesBridgeObservation({
      messageId: 'bus:12345',
      sender: 'Codex',
      recipient: 'Ali',
      body: 'Review the persisted result.',
    })).toEqual({
      messageId: 'bus:12345',
      sender: 'Codex',
      recipient: 'Ali',
      body: 'Review the persisted result.',
      header: 'Codex->Ali',
    })
  })

  it('rejects forged or self-addressed passive observations', () => {
    expect(() => normalizeHermesBridgeObservation({ messageId: 'bus:12345', sender: 'Everyone', recipient: 'Photon', body: 'bad' })).toThrow()
    expect(() => normalizeHermesBridgeObservation({ messageId: 'bus:12345', sender: 'Photon', recipient: 'Photon', body: 'bad' })).toThrow()
    expect(() => normalizeHermesBridgeObservation({ messageId: 'x', sender: 'Codex', recipient: 'Photon', body: 'bad' })).toThrow()
  })

  it('routes a passive observation to observe only and returns a correlated reply', async () => {
    let listener: ((event: MessageEvent) => void) | null = null
    const postMessage = vi.fn()
    const webview = {
      postMessage,
      addEventListener: (_type: 'message', next: (event: MessageEvent) => void) => { listener = next },
      removeEventListener: () => { listener = null },
    }
    const previous = Object.getOwnPropertyDescriptor(globalThis, 'window')
    Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview } } })
    const snapshot = createHermesBridgeSnapshot('open', false, 'session-1', [], [])
    const submitTurn = vi.fn(async () => snapshot)
    const observe = vi.fn(async () => snapshot)
    try {
      const unregister = registerHermesConversationBridge({ snapshot: () => snapshot, submitTurn, interrupt: async () => snapshot, observe })
      expect(listener).not.toBeNull()
      const dispatch = listener as unknown as (event: MessageEvent) => void
      dispatch({ data: {
        type: 'conversationBridge.request', version: 1, requestId: 'request-1', operation: 'observe',
        payload: { messageId: 'bus:12345', sender: 'Codex', recipient: 'Ali', body: 'Passive only.' },
      } } as MessageEvent)
      await vi.waitFor(() => expect(postMessage).toHaveBeenCalled())
      expect(observe).toHaveBeenCalledWith(expect.objectContaining({ header: 'Codex->Ali' }))
      expect(submitTurn).not.toHaveBeenCalled()
      expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({ requestId: 'request-1', ok: true }))
      unregister()
    } finally {
      if (previous) Object.defineProperty(globalThis, 'window', previous)
      else Reflect.deleteProperty(globalThis, 'window')
    }
  })
})
