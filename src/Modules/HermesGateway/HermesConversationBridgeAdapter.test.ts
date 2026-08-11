import { describe, expect, it } from 'vitest'
import { createHermesBridgeSnapshot } from './HermesConversationBridgeAdapter'

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
})
