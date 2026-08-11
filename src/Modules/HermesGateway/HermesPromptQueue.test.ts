import { describe, expect, it } from 'vitest'
import {
  enqueueHermesPrompt,
  HERMES_PROMPT_QUEUE_ADAPTER_VERSION,
  HERMES_PROMPT_QUEUE_LIMIT,
  promoteHermesQueuedPrompt,
  removeHermesQueuedPrompt,
  shouldAutoDrainHermesQueue,
  updateHermesQueuedPrompt,
} from './HermesPromptQueue'

describe('Hermes prompt queue adapter v1', () => {
  const add = (entries: ReturnType<typeof enqueueHermesPrompt<string>>, text: string, id: string) =>
    enqueueHermesPrompt(entries, { text, attachments: [`${id}.txt`] }, { createId: () => id, now: () => 42 })

  it('keeps FIFO text and attachment snapshots', () => {
    expect(HERMES_PROMPT_QUEUE_ADAPTER_VERSION).toBe(1)
    const sourceAttachments = ['one.txt']
    const first = enqueueHermesPrompt([], { text: ' first ', attachments: sourceAttachments }, { createId: () => 'one', now: () => 42 })
    const second = add(first, 'second', 'two')
    expect(second).toEqual([
      { id: 'one', text: 'first', attachments: ['one.txt'], queuedAt: 42 },
      { id: 'two', text: 'second', attachments: ['two.txt'], queuedAt: 42 },
    ])
    expect(first[0].attachments).not.toBe(sourceAttachments)
  })

  it('edits, promotes, and removes without mutating the source array', () => {
    const source = add(add([], 'one', 'one'), 'two', 'two')
    const edited = updateHermesQueuedPrompt(source, 'two', ' revised ')
    const promoted = promoteHermesQueuedPrompt(edited, 'two')
    const removed = removeHermesQueuedPrompt(promoted, 'one')
    expect(source.map((entry) => entry.text)).toEqual(['one', 'two'])
    expect(promoted.map((entry) => entry.id)).toEqual(['two', 'one'])
    expect(removed).toEqual([{ ...promoted[0] }])
  })

  it('rejects overflow while ignoring an empty prompt', () => {
    expect(enqueueHermesPrompt([], { text: ' ', attachments: [] })).toEqual([])
    const full = Array.from({ length: HERMES_PROMPT_QUEUE_LIMIT }, (_, index) => ({
      id: String(index), text: 'queued', attachments: [], queuedAt: index,
    }))
    expect(() => enqueueHermesPrompt(full, { text: 'overflow', attachments: [] })).toThrow('up to 20')
  })

  it('invokes a supplied ID factory without changing its receiver', () => {
    const factory = { prefix: 'queue', createId() { return `${this.prefix}-1` } }
    const queued = enqueueHermesPrompt([], { text: 'test', attachments: [] }, { createId: () => factory.createId() })
    expect(queued[0].id).toBe('queue-1')
  })

  it('auto-drains only after a successful completion edge', () => {
    const ready = { busy: false, completionAdvanced: true, connectionOpen: true, parked: false, queueLength: 1 }
    expect(shouldAutoDrainHermesQueue(ready)).toBe(true)
    expect(shouldAutoDrainHermesQueue({ ...ready, completionAdvanced: false })).toBe(false)
    expect(shouldAutoDrainHermesQueue({ ...ready, parked: true })).toBe(false)
    expect(shouldAutoDrainHermesQueue({ ...ready, connectionOpen: false })).toBe(false)
    expect(shouldAutoDrainHermesQueue({ ...ready, busy: true })).toBe(false)
  })
})
