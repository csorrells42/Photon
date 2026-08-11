import { describe, expect, it } from 'vitest'
import { WORKSPACE_SEARCH_LIMITS, WORKSPACE_SEARCH_PROTOCOL_VERSION, type WorkspaceSearchProviderResult } from './contracts'
import {
  isSafeWorkspaceSearchPath,
  normalizeWorkspaceSearchMessage,
  normalizeWorkspaceSearchOutput,
  normalizeWorkspaceSearchProgress,
  normalizeWorkspaceSearchQuery,
} from './safety'

function result(path: string, additions: Partial<WorkspaceSearchProviderResult> = {}): WorkspaceSearchProviderResult {
  return {
    path,
    pathKind: 'regular-file',
    line: 1,
    column: 1,
    preview: path,
    matches: [],
    ...additions,
  }
}

function output(results: readonly unknown[], truncated: unknown = false) {
  return {
    protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
    requestId: 'request-1',
    results,
    truncated,
  }
}

describe('Workspace Search path safety', () => {
  it.each([
    'C:\\workspace\\file.ts',
    '\\\\server\\share\\file.ts',
    '/absolute/file.ts',
    '../escape.ts',
    'src/../escape.ts',
    'src/file.ts:private-stream',
    'src/trailing.',
    'src/trailing ',
    'CON',
    'nul.txt',
    'src/COM1.ts',
    'src/lpt¹.log',
    'src/a<b.ts',
    '.git/config',
    'nested/.ENV.production',
    'logs/runtime.log',
    'keys/client.PFX',
  ])('rejects unsafe Windows, escaped, secret, or generated path %s', (candidate) => {
    expect(isSafeWorkspaceSearchPath(candidate)).toBe(false)
  })

  it.each(['src/file.ts', 'src/components/Search Panel.tsx', 'docs/connections.md'])
    ('accepts an ordinary workspace-relative file path %s', (candidate) => {
      expect(isSafeWorkspaceSearchPath(candidate)).toBe(true)
    })
})

describe('Workspace Search normalization', () => {
  it('sanitizes and bounds query and status text', () => {
    expect(normalizeWorkspaceSearchQuery(`safe\u202esecret\n${'x'.repeat(600)}`)).not.toMatch(/[\u202e\n]/u)
    expect(normalizeWorkspaceSearchQuery('x'.repeat(600))).toHaveLength(WORKSPACE_SEARCH_LIMITS.queryCharacters)
    expect(normalizeWorkspaceSearchMessage('x'.repeat(600))).toHaveLength(WORKSPACE_SEARCH_LIMITS.statusMessageCharacters)
  })

  it('sorts literal results deterministically', () => {
    const normalized = normalizeWorkspaceSearchOutput(output([
      result('zeta.ts', { line: 2 }),
      result('Alpha.ts', { line: 3 }),
      result('alpha.ts', { line: 1 }),
    ]), 'request-1', 'literal')

    expect(normalized?.results.map((item) => `${item.path}:${item.line}`)).toEqual([
      'Alpha.ts:3', 'alpha.ts:1', 'zeta.ts:2',
    ])
  })

  it('preserves semantic provider ranking while keeping the first duplicate', () => {
    const normalized = normalizeWorkspaceSearchOutput(output([
      result('ranked-third-alphabetically.ts', { line: 9, preview: 'best' }),
      result('alpha.ts', { line: 1, preview: 'second' }),
      result('ranked-third-alphabetically.ts', { line: 9, preview: 'best' }),
    ]), 'request-1', 'semantic')

    expect(normalized?.results.map((item) => item.preview)).toEqual(['best', 'second'])
    expect(normalized?.truncated).toBe(true)
  })

  it('enforces per-file and global result caps', () => {
    const perFile = normalizeWorkspaceSearchOutput(output(Array.from({ length: WORKSPACE_SEARCH_LIMITS.resultsPerFile + 2 }, (_, index) =>
      result('same.ts', { line: index + 1, preview: `same-${index}` }))), 'request-1', 'semantic')
    expect(perFile?.results).toHaveLength(WORKSPACE_SEARCH_LIMITS.resultsPerFile)
    expect(perFile?.truncated).toBe(true)

    const global = normalizeWorkspaceSearchOutput(output(Array.from({ length: WORKSPACE_SEARCH_LIMITS.results + 1 }, (_, index) =>
      result(`src/file-${index}.ts`))), 'request-1', 'semantic')
    expect(global?.results).toHaveLength(WORKSPACE_SEARCH_LIMITS.results)
    expect(global?.truncated).toBe(true)
  })

  it('omits malformed, unsafe, and non-regular rows and reports truncation', () => {
    const normalized = normalizeWorkspaceSearchOutput(output([
      result('safe.ts'),
      result('linked.ts', { pathKind: 'reparse-point' }),
      result('directory', { pathKind: 'directory' }),
      result('src/file.ts:ads'),
      result('bad-line.ts', { line: 0 }),
    ]), 'request-1', 'literal')

    expect(normalized?.results.map((item) => item.path)).toEqual(['safe.ts'])
    expect(normalized?.results[0].pathKind).toBe('regular-file')
    expect(normalized?.truncated).toBe(true)
  })

  it.each([undefined, null, 0, 1, 'false'])('fails closed when truncated is malformed: %s', (truncated) => {
    expect(normalizeWorkspaceSearchOutput({ ...output([]), truncated }, 'request-1', 'literal')).toBeNull()
  })

  it('rejects malformed envelopes and oversized provider batches', () => {
    expect(normalizeWorkspaceSearchOutput({ ...output([]), requestId: 'wrong' }, 'request-1', 'literal')).toBeNull()
    expect(normalizeWorkspaceSearchOutput(output(Array.from({ length: WORKSPACE_SEARCH_LIMITS.providerResults + 1 }, () => result('same.ts'))), 'request-1', 'literal')).toBeNull()
  })

  it('validates progress and match ranges against their bounds', () => {
    expect(normalizeWorkspaceSearchProgress({ completedFiles: 2, totalFiles: 5 })).toEqual({ completedFiles: 2, totalFiles: 5 })
    expect(normalizeWorkspaceSearchProgress({ completedFiles: 5, totalFiles: 2 })).toBeNull()
    expect(normalizeWorkspaceSearchProgress({ completedFiles: WORKSPACE_SEARCH_LIMITS.progressFiles + 1 })).toBeNull()

    const invalidRange = normalizeWorkspaceSearchOutput(output([
      result('range.ts', { preview: 'short', matches: [{ start: 0, end: 20 }] }),
    ]), 'request-1', 'literal')
    expect(invalidRange?.results).toEqual([])
    expect(invalidRange?.truncated).toBe(true)
  })
})
