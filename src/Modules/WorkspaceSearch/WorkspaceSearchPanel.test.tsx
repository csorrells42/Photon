import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { WORKSPACE_SEARCH_PROTOCOL_VERSION, type LiteralWorkspaceSearchProvider } from './contracts'
import { WorkspaceSearchController } from './WorkspaceSearchController'
import { WorkspaceSearchPanel } from './WorkspaceSearchPanel'

const available = { state: 'available' } as const

describe('WorkspaceSearchPanel', () => {
  it('renders an accessible search form, modes, status, bounded results, and hostile text safely', async () => {
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: async (request) => ({
        protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
        requestId: request.requestId,
        results: [{
          path: 'src/hostile-preview.ts',
          pathKind: 'regular-file',
          line: 7,
          column: 3,
          preview: '<script>alert(1)</script>',
          matches: [{ start: 0, end: 8 }],
        }],
        truncated: true,
      }),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider })
    controller.setQuery('script')
    await controller.search()

    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} />)
    for (const expected of [
      'aria-label="Workspace search"',
      'aria-busy="false"',
      'role="search"',
      'role="combobox"',
      'aria-describedby=',
      'aria-controls=',
      'aria-activedescendant=',
      'role="status"',
      'aria-live="polite"',
      'role="listbox"',
      'role="option"',
      'aria-selected="true"',
      'tabindex="0"',
      'Literal results',
      'Showing bounded results',
      'aria-label="Clear search query"',
    ]) expect(markup).toContain(expected)
    expect(markup).toContain('src/hostile-preview.ts')
    expect(markup).toContain('&lt;script&gt;')
    expect(markup).not.toContain('<script>')
  })

  it('renders honest provider availability and disabled actions', () => {
    const controller = new WorkspaceSearchController()
    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} />)

    expect(markup).toContain('This search provider is not configured.')
    expect(markup).toContain('Literal search is unavailable')
    expect(markup).toContain('Semantic search is unavailable')
    expect(markup.match(/disabled=""/g)?.length).toBeGreaterThanOrEqual(3)
    expect(markup).not.toContain('aria-controls=')
  })

  it('shows a clean searching state, progress semantics, keyboard shortcut, and no mojibake', async () => {
    const provider: LiteralWorkspaceSearchProvider = {
      availability: available,
      searchLiteral: (_request, execution) => new Promise((_resolve, reject) => {
        execution.signal.addEventListener('abort', () => reject(new Error('cancelled')), { once: true })
      }),
    }
    const controller = new WorkspaceSearchController({ literalProvider: provider })
    controller.setQuery('pending')
    const pending = controller.search()
    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} heading="Code search" />)

    expect(markup).toContain('Code search')
    expect(markup).toContain('Searching workspace…')
    expect(markup).toContain('aria-busy="true"')
    expect(markup).toContain('aria-label="Search in progress"')
    expect(markup).toContain('aria-keyshortcuts="Control+F Meta+F"')
    expect(markup).toContain('>Cancel</button>')
    expect(markup).not.toMatch(/â|�/u)

    controller.cancel()
    await pending
  })

  it('renders the idle guidance without corrupted glyphs', () => {
    const controller = new WorkspaceSearchController({
      literalProvider: {
        availability: available,
        searchLiteral: async (request) => ({
          protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
          requestId: request.requestId,
          results: [],
          truncated: false,
        }),
      },
    })
    const markup = renderToStaticMarkup(<WorkspaceSearchPanel controller={controller} />)
    expect(markup).toContain('⌕')
    expect(markup).toContain('Literal search finds exact text.')
    expect(markup).not.toMatch(/â|�/u)
  })
})
