import { describe, expect, it } from 'vitest'
import { CONSOLE_COPY_MAX_LENGTH, CONSOLE_FIND_LINE_MAX_LENGTH, CONSOLE_FIND_MAX_MATCHES, CONSOLE_FIND_QUERY_MAX_LENGTH, boundedConsoleCopyText, consoleViewLinesAfterClear, copyConsoleText, findConsoleMatchResult, findConsoleMatches, isConsoleNearBottom, isConsoleSelectionContained, nextConsoleMatchIndex, nextConsoleViewClearBoundary, normalizeConsoleFindQuery, shouldFollowConsoleUpdate } from './HermesConsoleErgonomics'

const lines = [{ id: 'one', text: 'Alpha [a-z]+ alpha' }, { id: 'two', text: 'ALPHA' }]

describe('Hermes Console ergonomics', () => {
  it('finds plain text case-insensitively and treats regex punctuation literally', () => {
    expect(findConsoleMatches(lines, 'alpha')).toEqual([{ lineId: 'one', start: 0, end: 5 }, { lineId: 'one', start: 13, end: 18 }, { lineId: 'two', start: 0, end: 5 }])
    expect(findConsoleMatches(lines, '[a-z]+')).toEqual([{ lineId: 'one', start: 6, end: 12 }])
  })
  it('wraps find navigation in both directions and leaves an empty query inert', () => {
    expect(nextConsoleMatchIndex(2, 3, 'next')).toBe(0); expect(nextConsoleMatchIndex(0, 3, 'previous')).toBe(2)
    expect(nextConsoleMatchIndex(-1, 3, 'next')).toBe(0); expect(nextConsoleMatchIndex(-1, 3, 'previous')).toBe(2)
    expect(nextConsoleMatchIndex(0, 0, 'next')).toBe(-1); expect(findConsoleMatches(lines, '')).toEqual([])
  })
  it('bounds oversized find queries without interpreting user input', () => {
    const query = '[x]'.repeat(CONSOLE_FIND_QUERY_MAX_LENGTH)
    expect(normalizeConsoleFindQuery(query)).toHaveLength(CONSOLE_FIND_QUERY_MAX_LENGTH); expect(findConsoleMatches(lines, query)).toEqual([])
  })
  it('caps retained matches and per-line search work', () => {
    const manyMatches = findConsoleMatchResult([{ id: 'large', text: 'a'.repeat(CONSOLE_FIND_LINE_MAX_LENGTH + 100) }], 'a')
    expect(manyMatches.matches).toHaveLength(CONSOLE_FIND_MAX_MATCHES)
    expect(manyMatches.truncated).toBe(true)
    const afterLimit = findConsoleMatchResult([{ id: 'large', text: `${'x'.repeat(CONSOLE_FIND_LINE_MAX_LENGTH)}needle` }], 'needle')
    expect(afterLimit).toEqual({ matches: [], truncated: true })
  })
  it('pauses follow when the reader leaves the bottom and resumes on explicit jump', () => {
    expect(isConsoleNearBottom({ scrollHeight: 1000, scrollTop: 904, clientHeight: 0 })).toBe(true)
    expect(isConsoleNearBottom({ scrollHeight: 1000, scrollTop: 903, clientHeight: 0 })).toBe(false)
    expect(shouldFollowConsoleUpdate(false)).toBe(false); expect(shouldFollowConsoleUpdate(true)).toBe(true)
  })
  it('copies bounded text with explicit success and failure outcomes', async () => {
    const copied: string[] = []; await expect(copyConsoleText('visible text', async (text) => { copied.push(text) })).resolves.toEqual({ ok: true, text: 'visible text' })
    expect(copied).toEqual(['visible text']); await expect(copyConsoleText('no permission', async () => { throw new Error('denied') })).resolves.toEqual({ ok: false, text: '' })
    expect(boundedConsoleCopyText('x'.repeat(CONSOLE_COPY_MAX_LENGTH + 1))).toHaveLength(CONSOLE_COPY_MAX_LENGTH)
  })
  it('copies a selection only when both selection endpoints stay inside the transcript', () => {
    const insideA = {} as Node
    const insideB = {} as Node
    const outside = {} as Node
    const output = { contains: (node: Node | null) => node === insideA || node === insideB }
    expect(isConsoleSelectionContained(output, { isCollapsed: false, anchorNode: insideA, focusNode: insideB })).toBe(true)
    expect(isConsoleSelectionContained(output, { isCollapsed: false, anchorNode: insideA, focusNode: outside })).toBe(false)
    expect(isConsoleSelectionContained(output, { isCollapsed: true, anchorNode: insideA, focusNode: insideB })).toBe(false)
  })
  it('clears the renderer view only and permits later transport output to appear', () => {
    expect(consoleViewLinesAfterClear(lines, 'one')).toEqual([{ id: 'two', text: 'ALPHA' }])
    expect(consoleViewLinesAfterClear([{ id: 'later', text: 'new output' }], 'one')).toEqual([{ id: 'later', text: 'new output' }])
  })
  it('advances the renderer-only clear boundary on every non-empty clear action', () => {
    const firstBoundary = nextConsoleViewClearBoundary(lines, null)
    expect(firstBoundary).toBe('two')
    const laterOutput = [{ id: 'later-one', text: 'later output' }, { id: 'later-two', text: 'newest output' }]
    expect(nextConsoleViewClearBoundary(laterOutput, firstBoundary)).toBe('later-two')
    expect(nextConsoleViewClearBoundary([], 'later-two')).toBe('later-two')
  })
})
