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
  const stateHint = state.status === 'searching'
    ? 'Searching workspace files and preparing matching evidence.'
    : state.status === 'empty'
      ? 'No matching workspace evidence was found for this query.'
      : state.status === 'unavailable'
        ? 'This search mode needs a configured provider before it can run.'
        : state.status === 'error'
          ? 'The search stopped safely. Refine the query or try again.'
          : state.status === 'cancelled'
            ? 'The search was cancelled. Update the query when you are ready.'
            : 'Choose Literal for exact text or Semantic for intent-based search.'

  return (
    <section className={`workspace-search workspace-search--${state.status}`} aria-label="Workspace search" aria-busy={state.status === 'searching'}>
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

        <div className="workspace-search__modes" role="group" aria-labelledby={`${listId}-modes`}>
          <span id={`${listId}-modes`} className="workspace-search__modes-label">Search mode</span>
          <div className="workspace-search__mode-options">
            <button
              type="button"
              className="workspace-search__mode"
              aria-pressed={state.mode === 'literal'}
              disabled={literalAvailability.state !== 'available'}
              title={literalAvailability.state === 'available' ? 'Exact text search' : 'Literal search is unavailable'}
              onClick={() => controller.setMode('literal')}
            ><span>Literal</span><small>Exact text</small></button>
            <button
              type="button"
              className="workspace-search__mode"
              aria-pressed={state.mode === 'semantic'}
              disabled={semanticAvailability.state !== 'available'}
              title={semanticAvailability.state === 'available' ? 'Intent-based search' : 'Semantic search is unavailable'}
              onClick={() => controller.setMode('semantic')}
            ><span>Semantic</span><small>Intent-based</small></button>
          </div>
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
            <span className="workspace-search__result-count">{state.results.length} {state.results.length === 1 ? 'result' : 'results'}</span>
            {attribution && <span>{attribution}</span>}
            {state.truncated && <span className="workspace-search__bounded">Showing bounded results</span>}
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
                <span className="workspace-search__result-heading">
                  <span className="workspace-search__path">{result.path}</span>
                  <span className="workspace-search__position">Ln {result.line}, Col {result.column}</span>
                </span>
                <code className="workspace-search__preview">{highlightedPreview(result.preview, result.matches)}</code>
              </button>
            ))}
          </div>
        </div>
      )}

      {!state.results.length && (
        <div className={`workspace-search__state workspace-search__state--${state.status}`} aria-hidden="true">
          <span className="workspace-search__state-mark" />
          <p>{stateHint}</p>
        </div>
      )}
    </section>
  )
}
