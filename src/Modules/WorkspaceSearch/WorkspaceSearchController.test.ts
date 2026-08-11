import { describe, expect, it, vi } from 'vitest'
import { WORKSPACE_SEARCH_LIMITS, WORKSPACE_SEARCH_PROTOCOL_VERSION, type LiteralWorkspaceSearchProvider, type SemanticWorkspaceSearchProvider, type WorkspaceSearchExecution } from './contracts'
import { WorkspaceSearchController } from './WorkspaceSearchController'

const available = { state: 'available' } as const

function providerOutput(requestId: string, path = 'src/result.ts', additions: Record<string, unknown> = {}) {
  return {
    protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
    requestId,
    results: [{ path, pathKind: 'regular-file', line: 4, column: 2, preview: path, matches: [] }],
    truncated: false,
    ...additions,
  }
}

describe('WorkspaceSearchController cancellation and freshness', () => {
  it('aborts and generation-invalidates a search on query change, then ignores its late progress and result', async () => {
    const calls: Array<{
      requestId: string
      execution: WorkspaceSearchExecution
      resolve(value: unknown): void
    }> = []
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: (request, execution) => new Promise((resolve) => calls.push({ requestId: request.requestId, execution, resolve })),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider })

    controller.setQuery('first')
    const first = controller.search()
    expect(calls).toHaveLength(1)
    calls[0].execution.reportProgress({ completedFiles: 1, totalFiles: 3 })
    expect(controller.getSnapshot().progress).toEqual({ completedFiles: 1, totalFiles: 3 })

    controller.setQuery('second')
    expect(calls[0].execution.signal.aborted).toBe(true)
    expect(controller.getSnapshot()).toMatchObject({ query: 'second', status: 'idle', results: [], progress: null })
    const second = controller.search()
    expect(calls).toHaveLength(2)
    calls[1].resolve(providerOutput(calls[1].requestId, 'src/second.ts'))
    await second

    calls[0].execution.reportProgress({ completedFiles: 3, totalFiles: 3 })
    calls[0].resolve(providerOutput(calls[0].requestId, 'src/late-first.ts'))
    await first
    expect(controller.getSnapshot().results.map((item) => item.path)).toEqual(['src/second.ts'])
    expect(controller.getSnapshot().history.map((item) => item.query)).toEqual(['second'])
  })

  it('clears ready results and invalidates work even when the query is already empty', async () => {
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: async (request) => providerOutput(request.requestId),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider })
    controller.setQuery('ready')
    await controller.search()
    expect(controller.getSnapshot().status).toBe('ready')

    controller.clearQuery()
    expect(controller.getSnapshot()).toMatchObject({ query: '', status: 'idle', results: [], selectedIndex: -1, attribution: null })
    expect(controller.getSnapshot().message).toBe('Enter a search query.')
    controller.clearQuery()
    expect(controller.getSnapshot().results).toEqual([])
  })

  it('reports explicit cancellation without leaking a provider error', async () => {
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: (_request, execution) => new Promise((_resolve, reject) => {
        execution.signal.addEventListener('abort', () => reject(new Error('private provider detail')), { once: true })
      }),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider })
    controller.setQuery('cancel')
    const pending = controller.search()
    controller.cancel()
    await pending
    expect(controller.getSnapshot()).toMatchObject({ status: 'cancelled', progress: null, message: 'Search cancelled.' })
    expect(JSON.stringify(controller.getSnapshot())).not.toContain('private provider detail')
  })
})

describe('WorkspaceSearchController trust, states, and keyboard behavior', () => {
  it('ignores forged Serena evidence and labels Serena only from trusted root wiring', async () => {
    const semanticProvider: SemanticWorkspaceSearchProvider = {
      availability: available,
      searchSemantic: async (request) => providerOutput(request.requestId, 'src/semantic.ts', {
        evidence: { kind: 'trusted-provider-evidence', provider: 'serena', requestId: request.requestId, served: true },
      }),
    }
    const ordinary = new WorkspaceSearchController({ semanticProvider })
    ordinary.setQuery('intent')
    await ordinary.search()
    expect(ordinary.getSnapshot().attribution).toBe('semantic')

    const trusted = new WorkspaceSearchController({ semanticProvider, trustedSemanticProvider: 'serena' })
    trusted.setQuery('intent')
    await trusted.search()
    expect(trusted.getSnapshot().attribution).toBe('serena')
  })

  it('uses fixed error and unavailable messages and fails closed malformed output', async () => {
    const invalid = new WorkspaceSearchController({
      literalProvider: { availability: available, searchLiteral: async (request) => ({ ...providerOutput(request.requestId), truncated: 'false' }) },
    })
    invalid.setQuery('invalid')
    await invalid.search()
    expect(invalid.getSnapshot()).toMatchObject({ status: 'error', message: 'The search provider returned invalid results.' })

    const failed = new WorkspaceSearchController({
      literalProvider: { availability: available, searchLiteral: async () => { throw new Error('secret stack') } },
    })
    failed.setQuery('failure')
    await failed.search()
    expect(failed.getSnapshot()).toMatchObject({ status: 'error', message: 'Search failed safely. Try again.' })
    expect(JSON.stringify(failed.getSnapshot())).not.toContain('secret stack')

    const unavailable = new WorkspaceSearchController()
    expect(unavailable.getSnapshot()).toMatchObject({ status: 'unavailable', message: 'This search provider is not configured.' })
  })

  it('supports bounded history, wraparound keyboard selection, Escape, and typed activation', async () => {
    const activated = vi.fn()
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: async (request) => ({
        ...providerOutput(request.requestId),
        results: [
          { path: 'a.ts', pathKind: 'regular-file', line: 1, column: 2, preview: 'a', matches: [] },
          { path: 'b.ts', pathKind: 'regular-file', line: 3, column: 4, preview: 'b', matches: [] },
        ],
      }),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider, onActivate: activated })
    for (let index = 0; index < WORKSPACE_SEARCH_LIMITS.retainedHistory + 2; index += 1) {
      controller.setQuery(`query-${index}`)
      await controller.search()
    }
    expect(controller.getSnapshot().history).toHaveLength(WORKSPACE_SEARCH_LIMITS.retainedHistory)
    expect(controller.getSnapshot().selectedIndex).toBe(0)
    expect(controller.handleKey('ArrowUp')).toBe(true)
    expect(controller.getSnapshot().selectedIndex).toBe(1)
    expect(controller.handleKey('Enter')).toBe(true)
    expect(activated).toHaveBeenCalledWith({ path: 'b.ts', line: 3, column: 4 })
    expect(controller.handleKey('Escape')).toBe(true)
    expect(controller.getSnapshot().selectedIndex).toBe(-1)
  })
})
