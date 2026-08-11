import { describe, expect, it } from 'vitest'
import { applyCompletion, createCompletionSession, handleCompletionKey } from './completions'
import { HERMES_COMPOSER_LIMITS } from './types'

const catalogs = {
  mentions: [
    { id: 'workspace', label: 'Workspace', description: 'Current workspace context' },
    { id: 'web', label: 'WebResearch', description: 'Research context' },
  ],
  commands: [
    { id: 'review', name: 'review', description: 'Review selected work', usage: '/review focused' },
    { id: 'run', name: 'run', description: 'Run a task' },
  ],
}

describe('Hermes composer completions', () => {
  it('finds bounded mention and command sessions at the cursor', () => {
    const mention = createCompletionSession('Ask @wor', 8, catalogs)
    expect(mention?.kind).toBe('mention')
    expect(mention?.options.map((option) => option.value)).toEqual(['Workspace'])

    const command = createCompletionSession('/r', 2, catalogs)
    expect(command?.kind).toBe('command')
    expect(command?.options.map((option) => option.value)).toEqual(['review', 'run'])
    expect(createCompletionSession('email@example', 13, catalogs)).toBeNull()
  })

  it('supports wrapping keyboard navigation, direct edges, selection, and closing', () => {
    const session = createCompletionSession('/', 1, catalogs)
    if (!session) throw new Error('Expected completion session.')
    expect(handleCompletionKey(session, 'ArrowUp')).toEqual({ action: 'navigate', selectedIndex: 1 })
    expect(handleCompletionKey(session, 'ArrowDown')).toEqual({ action: 'navigate', selectedIndex: 1 })
    expect(handleCompletionKey({ ...session, selectedIndex: 1 }, 'ArrowDown')).toEqual({ action: 'navigate', selectedIndex: 0 })
    expect(handleCompletionKey(session, 'End')).toEqual({ action: 'navigate', selectedIndex: 1 })
    expect(handleCompletionKey({ ...session, selectedIndex: 1 }, 'Home')).toEqual({ action: 'navigate', selectedIndex: 0 })
    expect(handleCompletionKey(session, 'Enter')).toEqual({ action: 'select', option: session.options[0] })
    expect(handleCompletionKey(session, 'Tab')).toEqual({ action: 'select', option: session.options[0] })
    expect(handleCompletionKey(session, 'Escape')).toEqual({ action: 'close' })
    expect(handleCompletionKey(session, 'a')).toEqual({ action: 'none' })
  })

  it('inserts the selected option as literal prompt text and restores a cursor position', () => {
    const text = 'Use @wor today'
    const session = createCompletionSession(text, 8, catalogs)
    if (!session) throw new Error('Expected completion session.')
    expect(applyCompletion(text, session, session.options[0])).toEqual({ text: 'Use @Workspace  today', cursor: 15 })
  })

  it('rejects oversized, duplicated, and structurally invalid catalogs', () => {
    expect(() => createCompletionSession('@', 1, {
      mentions: Array.from({ length: HERMES_COMPOSER_LIMITS.catalogItems + 1 }, (_, index) => ({ id: String(index), label: String(index) })),
    })).toThrow('up to 100')
    expect(() => createCompletionSession('@', 1, { mentions: [{ id: 'same', label: 'One' }, { id: 'same', label: 'Two' }] })).toThrow('unique')
    expect(() => createCompletionSession('/', 1, { commands: [{ id: 'bad', name: 'bad command' }] })).toThrow('whitespace')
  })
})
