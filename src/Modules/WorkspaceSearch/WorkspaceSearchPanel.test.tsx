import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  WORKSPACE_SEARCH_PROTOCOL_VERSION,
  type LiteralWorkspaceSearchProvider,
  type SemanticWorkspaceSearchProvider,
} from './contracts'
import { WorkspaceSearchController } from './WorkspaceSearchController'
import { WorkspaceSearchPanel } from './WorkspaceSearchPanel'

const available = { state: 'available' } as const

function literalProvider(results = [{
  path: 'src/hostile-preview.ts',
  pathKind: 'regular-file' as const,
  line: 7,
  column: 3,
  preview: '<script>alert(1)</script>',
  matches: [{ start: 0, end: 8 }],
}]): LiteralWorkspaceSearchProvider {
  return {
    availability: available,
    searchLiteral: async (request) => ({
      protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
      requestId: request.requestId,
      results,
      truncated: results.length > 0,
    }),
  }
}

describe('WorkspaceSearchPanel', () => {
  it('projects literal results with the selected mode, evidence hierarchy, and bounded-result truthfulness', async () => {
    const controller = new WorkspaceSearchController({ literalProvider: literalProvider() })
    controller.setQuery('script')
    await controller.search()

    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} />)
    for (const expected of [
      'aria-label="Workspace search"',
      'workspace-search--ready',
      'role="search"',
      'role="combobox"',
      'aria-describedby=',
      'aria-controls=',
      'aria-activedescendant=',
      'role="status"',
      'aria-live="polite"',
      'Search mode',
      'workspace-search__mode-options',
      'workspace-search__mode',
      'Exact text',
      'Intent-based',
      'role="listbox"',
      'role="option"',
      'aria-selected="true"',
      'tabindex="0"',
      'workspace-search__result-heading',
      'workspace-search__result-count',
      'Literal results',
      'Showing bounded results',
      'Ln 7, Col 3',
      'aria-label="Clear search query"',
    ]) expect(markup).toContain(expected)
    expect(markup).toMatch(/<button[^>]*aria-pressed="true"[^>]*>.*Literal/u)
    expect(markup).toMatch(/<button[^>]*aria-pressed="false"[^>]*>.*Semantic/u)
    expect(markup).toContain('src/hostile-preview.ts')
    expect(markup).toContain('&lt;script&gt;')
    expect(markup).not.toContain('<script>')
  })

  it('projects semantic mode selection without changing search result evidence', async () => {
    const semanticProvider: SemanticWorkspaceSearchProvider = {
      availability: available,
      searchSemantic: async (request) => ({
        protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
        requestId: request.requestId,
        results: [{
          path: 'src/search/semantic.ts',
          pathKind: 'regular-file',
          line: 12,
          column: 5,
          preview: 'Find behavior by intent',
          matches: [{ start: 5, end: 13 }],
        }],
        truncated: false,
      }),
    }
    const controller = new WorkspaceSearchController({ semanticProvider })
    controller.setMode('semantic')
    controller.setQuery('find behavior')
    await controller.search()

    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} />)
    expect(markup).toContain('workspace-search--ready')
    expect(markup).toMatch(/<button[^>]*aria-pressed="false"[^>]*>.*Literal/u)
    expect(markup).toMatch(/<button[^>]*aria-pressed="true"[^>]*>.*Semantic/u)
    expect(markup).toContain('Semantic results')
    expect(markup).toContain('src/search/semantic.ts')
    expect(markup).toContain('Ln 12, Col 5')
    expect(markup).toContain('<mark>behavior</mark>')
  })

  it('renders unavailable, loading, empty, and error states with their live-status semantics', async () => {
    const unavailable = new WorkspaceSearchController()
    expect(renderToStaticMarkup(<WorkspaceSearchPanel controller={unavailable} />)).toContain('workspace-search--unavailable')

    const loadingProvider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: (_request, execution) => new Promise((_resolve, reject) => {
        execution.signal.addEventListener('abort', () => reject(new Error('cancelled')), { once: true })
      }),
    }
    const loading = new WorkspaceSearchController({ literalProvider: loadingProvider })
    loading.setQuery('pending')
    const pending = loading.search()
    const loadingMarkup = renderToStaticMarkup(<WorkspaceSearchPanel controller={loading} heading="Code search" />)
    expect(loadingMarkup).toContain('Code search')
    expect(loadingMarkup).toContain('workspace-search--searching')
    expect(loadingMarkup).toContain('aria-busy="true"')
    expect(loadingMarkup).toContain('aria-label="Search in progress"')
    expect(loadingMarkup).toContain('aria-keyshortcuts="Control+F Meta+F"')
    expect(loadingMarkup).toContain('>Cancel</button>')
    loading.cancel()
    await pending

    const empty = new WorkspaceSearchController({ literalProvider: literalProvider([]) })
    empty.setQuery('nothing')
    await empty.search()
    const emptyMarkup = renderToStaticMarkup(<WorkspaceSearchPanel controller={empty} />)
    expect(emptyMarkup).toContain('workspace-search--empty')
    expect(emptyMarkup).toContain('workspace-search__state')
    expect(emptyMarkup).toContain('No results found.')
    expect(emptyMarkup).toContain('No matching workspace evidence was found')

    const failed = new WorkspaceSearchController({
      literalProvider: { availability: available, searchLiteral: async () => { throw new Error('offline') } },
    })
    failed.setQuery('failure')
    await failed.search()
    const errorMarkup = renderToStaticMarkup(<WorkspaceSearchPanel controller={failed} />)
    expect(errorMarkup).toContain('workspace-search--error')
    expect(errorMarkup).toContain('role="alert"')
    expect(errorMarkup).toContain('Search failed safely. Try again.')
    expect(errorMarkup).toContain('The search stopped safely.')
  })
})
