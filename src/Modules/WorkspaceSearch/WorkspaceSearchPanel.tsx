import { useEffect, useId, useRef, useSyncExternalStore, type KeyboardEvent, type ReactNode } from 'react'
import { WORKSPACE_SEARCH_LIMITS, type WorkspaceSearchMatchRange, type WorkspaceSearchResult } from './contracts'
import { WorkspaceSearchController } from './WorkspaceSearchController'
import './WorkspaceSearchPanel.css'

export type WorkspaceSearchPanelProps = {
  controller: WorkspaceSearchController
  heading?: string
}

function highlightedPreview(preview: string, ranges: readonly WorkspaceSearchMatchRange[]): ReactNode {
  if (!ranges.length) return preview
  const nodes: ReactNode[] = []
  let cursor = 0
  for (const [index, range] of ranges.entries()) {
    if (range.start > cursor) nodes.push(<span key={`plain-${index}`}>{preview.slice(cursor, range.start)}</span>)
    nodes.push(<mark key={`match-${index}`}>{preview.slice(range.start, range.end)}</mark>)
    cursor = range.end
  }
  if (cursor < preview.length) nodes.push(<span key="plain-tail">{preview.slice(cursor)}</span>)
  return nodes
}

function resultLabel(result: WorkspaceSearchResult) {
  return `${result.path}, line ${result.line}, column ${result.column}`
}

export function WorkspaceSearchPanel({ controller, heading = 'Search' }: WorkspaceSearchPanelProps) {
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot)
  const input = useRef<HTMLInputElement>(null)
  const listId = useId()
  const statusId = `${listId}-status`

  useEffect(() => {
    const focusSearch = (event: globalThis.KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f') {
        event.preventDefault()
        input.current?.focus()
        input.current?.select()
      }
    }
    document.addEventListener('keydown', focusSearch)
    return () => document.removeEventListener('keydown', focusSearch)
  }, [])

  const literalAvailability = controller.availabilityFor('literal')
  const semanticAvailability = controller.availabilityFor('semantic')
  const selected = state.results[state.selectedIndex]

  function onInputKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.nativeEvent.isComposing || !['ArrowDown', 'ArrowUp', 'Enter', 'Escape'].includes(event.key)) return
    const key = event.key as 'ArrowDown' | 'ArrowUp' | 'Enter' | 'Escape'
    const handled = controller.handleKey(key)
    if (handled) event.preventDefault()
    if (key === 'Enter' && !handled) {
      event.preventDefault()
      void controller.search()
    }
  }

  const attribution = state.attribution === 'serena'
    ? 'Serena semantic results'
    : state.attribution === 'semantic'
      ? 'Semantic results'
      : state.attribution === 'literal'
        ? 'Literal results'
        : ''

  return (
    <section className="workspace-search" aria-label="Workspace search" aria-busy={state.status === 'searching'}>
      <header className="workspace-search__header">
        <div>
          <p className="workspace-search__eyebrow">Workspace</p>
          <h2>{heading}</h2>
        </div>
        <kbd aria-label="Keyboard shortcut Control F">Ctrl F</kbd>
      </header>

      <form className="workspace-search__form" role="search" onSubmit={(event) => { event.preventDefault(); void controller.search() }}>
        <label htmlFor={`${listId}-input`}>Search query</label>
        <div className="workspace-search__input-row">
          <input
            ref={input}
            id={`${listId}-input`}
            type="search"
            value={state.query}
            maxLength={WORKSPACE_SEARCH_LIMITS.queryCharacters}
            placeholder={state.mode === 'literal' ? 'Find text in workspace files' : 'Describe the code or behavior to find'}
            onChange={(event) => controller.setQuery(event.currentTarget.value)}
            onKeyDown={onInputKeyDown}
            role="combobox"
            aria-expanded={state.results.length > 0}
            aria-controls={state.results.length ? listId : undefined}
            aria-describedby={statusId}
            aria-activedescendant={selected ? `${listId}-${selected.id}` : undefined}
            aria-autocomplete="none"
            aria-keyshortcuts="Control+F Meta+F"
            autoComplete="off"
            spellCheck={false}
          />
          {state.query && (
            <button
              type="button"
              className="workspace-search__clear"
              aria-label="Clear search query"
              title="Clear search query"
              onClick={() => { controller.clearQuery(); input.current?.focus() }}
            >Clear</button>
          )}
          {state.status === 'searching'
            ? <button type="button" className="workspace-search__cancel" onClick={() => controller.cancel()}>Cancel</button>
            : <button type="submit" disabled={!state.query.trim() || controller.availabilityFor(state.mode).state !== 'available'}>Search</button>}
        </div>

        <div className="workspace-search__modes" role="group" aria-label="Search mode">
          <button
            type="button"
            aria-pressed={state.mode === 'literal'}
            disabled={literalAvailability.state !== 'available'}
            title={literalAvailability.state === 'available' ? 'Exact text search' : 'Literal search is unavailable'}
            onClick={() => controller.setMode('literal')}
          >Literal</button>
          <button
            type="button"
            aria-pressed={state.mode === 'semantic'}
            disabled={semanticAvailability.state !== 'available'}
            title={semanticAvailability.state === 'available' ? 'Intent-based search' : 'Semantic search is unavailable'}
            onClick={() => controller.setMode('semantic')}
          >Semantic</button>
        </div>
      </form>

      <div id={statusId} className={`workspace-search__status workspace-search__status--${state.status}`} role={state.status === 'error' ? 'alert' : 'status'} aria-live="polite">
        <span>{state.message || 'Enter a query to search the workspace.'}</span>
        {state.status === 'searching' && state.progress && (
          state.progress.totalFiles !== undefined
            ? <progress aria-label="Search progress" max={state.progress.totalFiles || 1} value={state.progress.completedFiles} />
            : <span className="workspace-search__indeterminate" aria-label="Search in progress" />
        )}
      </div>

      {state.results.length > 0 && (
        <div className="workspace-search__results-wrap">
          <div className="workspace-search__summary">
            <span>{attribution}</span>
            {state.truncated && <span>Showing bounded results</span>}
          </div>
          <div id={listId} className="workspace-search__results" role="listbox" aria-label="Search results">
            {state.results.map((result, index) => (
              <button
                key={result.id}
                id={`${listId}-${result.id}`}
                type="button"
                role="option"
                aria-selected={index === state.selectedIndex}
                aria-label={resultLabel(result)}
                className="workspace-search__result"
                tabIndex={index === state.selectedIndex ? 0 : -1}
                onMouseEnter={() => controller.select(index)}
                onFocus={() => controller.select(index)}
                onClick={() => { controller.select(index); controller.activateSelected() }}
                onKeyDown={(event) => {
                  if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Enter' || event.key === 'Escape') {
                    event.preventDefault()
                    controller.handleKey(event.key)
                    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                      const next = controller.getSnapshot().results[controller.getSnapshot().selectedIndex]
                      if (next) document.getElementById(`${listId}-${next.id}`)?.focus()
                    } else if (event.key === 'Escape') input.current?.focus()
                  }
                }}
              >
                <span className="workspace-search__path">{result.path}</span>
                <span className="workspace-search__position">Ln {result.line}, Col {result.column}</span>
                <code className="workspace-search__preview">{highlightedPreview(result.preview, result.matches)}</code>
              </button>
            ))}
          </div>
        </div>
      )}

      {state.status === 'idle' && !state.results.length && (
        <div className="workspace-search__empty" aria-hidden="true">
          <span>⌕</span><p>Literal search finds exact text. Semantic search accepts intent when a trusted provider is available.</p>
        </div>
      )}
    </section>
  )
}
