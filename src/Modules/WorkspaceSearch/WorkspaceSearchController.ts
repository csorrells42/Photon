import {
  WORKSPACE_SEARCH_LIMITS,
  WORKSPACE_SEARCH_PROTOCOL_VERSION,
  type LiteralWorkspaceSearchProvider,
  type SemanticWorkspaceSearchProvider,
  type WorkspaceSearchActivation,
  type WorkspaceSearchAttribution,
  type WorkspaceSearchAvailability,
  type WorkspaceSearchHistoryEntry,
  type WorkspaceSearchMode,
  type WorkspaceSearchState,
} from './contracts'
import {
  normalizeWorkspaceSearchMessage,
  normalizeWorkspaceSearchOutput,
  normalizeWorkspaceSearchProgress,
  normalizeWorkspaceSearchQuery,
} from './safety'

export type WorkspaceSearchControllerOptions = {
  literalProvider?: LiteralWorkspaceSearchProvider
  semanticProvider?: SemanticWorkspaceSearchProvider
  onActivate?(activation: WorkspaceSearchActivation): void
  createRequestId?(): string
  /** Set only by the trusted composition root when the injected semantic adapter is Serena-backed. */
  trustedSemanticProvider?: 'serena'
}

type ActiveSearch = {
  abort: AbortController
  generation: number
  mode: WorkspaceSearchMode
  query: string
}
type Listener = () => void

const unavailable: WorkspaceSearchAvailability = { state: 'unavailable', reason: 'not-configured' }

function unavailableMessage(availability: WorkspaceSearchAvailability) {
  if (availability.state === 'available') return ''
  const messages = {
    'not-configured': 'This search provider is not configured.',
    disabled: 'This search provider is disabled.',
    offline: 'This search provider is offline.',
    unsupported: 'This search mode is not supported.',
  } as const
  return messages[availability.reason]
}

export class WorkspaceSearchController {
  private readonly listeners = new Set<Listener>()
  private readonly literalProvider?: LiteralWorkspaceSearchProvider
  private readonly semanticProvider?: SemanticWorkspaceSearchProvider
  private readonly onActivate?: (activation: WorkspaceSearchActivation) => void
  private readonly createRequestId: () => string
  private readonly semanticAttribution: Extract<WorkspaceSearchAttribution, 'semantic' | 'serena'>
  private requestIdSequence = 0
  private generation = 0
  private active?: ActiveSearch
  private state: WorkspaceSearchState

  constructor(options: WorkspaceSearchControllerOptions = {}) {
    this.literalProvider = options.literalProvider
    this.semanticProvider = options.semanticProvider
    this.onActivate = options.onActivate
    this.createRequestId = options.createRequestId ?? (() => `workspace-search:${++this.requestIdSequence}`)
    this.semanticAttribution = options.trustedSemanticProvider === 'serena' ? 'serena' : 'semantic'
    const literalAvailable = this.availabilityFor('literal').state === 'available'
    const semanticAvailable = this.availabilityFor('semantic').state === 'available'
    const mode: WorkspaceSearchMode = literalAvailable || !semanticAvailable ? 'literal' : 'semantic'
    const availability = this.availabilityFor(mode)
    this.state = {
      mode,
      query: '',
      status: availability.state === 'available' ? 'idle' : 'unavailable',
      results: [],
      selectedIndex: -1,
      progress: null,
      truncated: false,
      attribution: null,
      message: unavailableMessage(availability),
      history: [],
    }
  }

  readonly subscribe = (listener: Listener) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  readonly getSnapshot = () => this.state

  availabilityFor(mode: WorkspaceSearchMode): WorkspaceSearchAvailability {
    return mode === 'literal'
      ? this.literalProvider?.availability ?? unavailable
      : this.semanticProvider?.availability ?? unavailable
  }

  private update(patch: Partial<WorkspaceSearchState>) {
    const boundedPatch = patch.message === undefined
      ? patch
      : { ...patch, message: normalizeWorkspaceSearchMessage(patch.message) }
    this.state = { ...this.state, ...boundedPatch }
    for (const listener of this.listeners) listener()
  }

  setQuery(query: string) {
    const normalized = normalizeWorkspaceSearchQuery(query)
    if (normalized === this.state.query) return
    this.invalidateActive()
    const availability = this.availabilityFor(this.state.mode)
    this.update({
      query: normalized,
      status: availability.state === 'available' ? 'idle' : 'unavailable',
      results: [],
      selectedIndex: -1,
      progress: null,
      truncated: false,
      attribution: null,
      message: availability.state === 'available'
        ? normalized.trim() ? '' : 'Enter a search query.'
        : unavailableMessage(availability),
    })
  }

  clearQuery() {
    if (this.state.query) {
      this.setQuery('')
      return
    }
    this.invalidateActive()
    const availability = this.availabilityFor(this.state.mode)
    this.update({
      status: availability.state === 'available' ? 'idle' : 'unavailable',
      results: [], selectedIndex: -1, progress: null, truncated: false, attribution: null,
      message: availability.state === 'available' ? 'Enter a search query.' : unavailableMessage(availability),
    })
  }

  setMode(mode: WorkspaceSearchMode) {
    if (mode === this.state.mode) return
    this.cancel(false)
    const availability = this.availabilityFor(mode)
    this.update({
      mode,
      status: availability.state === 'available' ? 'idle' : 'unavailable',
      results: [],
      selectedIndex: -1,
      progress: null,
      truncated: false,
      attribution: null,
      message: unavailableMessage(availability),
    })
  }

  async search() {
    const query = normalizeWorkspaceSearchQuery(this.state.query).trim()
    this.setQuery(query)
    this.cancel(false)
    if (!query) {
      this.update({ status: 'idle', results: [], selectedIndex: -1, message: 'Enter a search query.' })
      return
    }

    const mode = this.state.mode
    const availability = this.availabilityFor(mode)
    if (availability.state !== 'available') {
      this.update({ status: 'unavailable', results: [], selectedIndex: -1, message: unavailableMessage(availability) })
      return
    }

    const requestId = this.createRequestId()
    const operation: ActiveSearch = {
      abort: new AbortController(),
      generation: ++this.generation,
      mode,
      query,
    }
    this.active = operation
    this.update({
      status: 'searching', results: [], selectedIndex: -1, progress: { completedFiles: 0 },
      truncated: false, attribution: null, message: 'Searching workspace…',
    })

    const execution = {
      signal: operation.abort.signal,
      reportProgress: (candidate: unknown) => {
        if (!this.isCurrent(operation)) return
        const progress = normalizeWorkspaceSearchProgress(candidate)
        if (progress) this.update({ progress })
      },
    }

    try {
      const raw = mode === 'literal'
        ? await this.literalProvider!.searchLiteral({
            protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
            requestId,
            query,
            maxResults: WORKSPACE_SEARCH_LIMITS.results,
            maxResultsPerFile: WORKSPACE_SEARCH_LIMITS.resultsPerFile,
            maxPreviewCharacters: WORKSPACE_SEARCH_LIMITS.previewCharacters,
          }, execution)
        : await this.semanticProvider!.searchSemantic({
            protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
            requestId,
            intent: query,
            maxResults: WORKSPACE_SEARCH_LIMITS.results,
            maxResultsPerFile: WORKSPACE_SEARCH_LIMITS.resultsPerFile,
            maxPreviewCharacters: WORKSPACE_SEARCH_LIMITS.previewCharacters,
          }, execution)
      if (!this.isCurrent(operation)) return
      const normalized = normalizeWorkspaceSearchOutput(raw, requestId, mode)
      if (!normalized) {
        this.update({ status: 'error', progress: null, message: 'The search provider returned invalid results.' })
        return
      }
      const historyEntry: WorkspaceSearchHistoryEntry = { mode, query, resultCount: normalized.results.length }
      const history = [historyEntry, ...this.state.history.filter((entry) => entry.mode !== mode || entry.query !== query)]
        .slice(0, WORKSPACE_SEARCH_LIMITS.retainedHistory)
      this.update({
        status: normalized.results.length ? 'ready' : 'empty',
        results: normalized.results,
        selectedIndex: normalized.results.length ? 0 : -1,
        progress: null,
        truncated: normalized.truncated,
        attribution: mode === 'literal' ? 'literal' : this.semanticAttribution,
        message: normalized.results.length ? `${normalized.results.length} result${normalized.results.length === 1 ? '' : 's'}.` : 'No results found.',
        history,
      })
    } catch {
      if (!this.isCurrent(operation)) return
      this.update({ status: 'error', results: [], selectedIndex: -1, progress: null, attribution: null, message: 'Search failed safely. Try again.' })
    } finally {
      if (this.active === operation) this.active = undefined
    }
  }

  cancel(showState = true) {
    const active = this.active
    this.invalidateActive()
    if (active && showState) this.update({ status: 'cancelled', progress: null, message: 'Search cancelled.' })
  }

  private invalidateActive() {
    this.generation += 1
    const active = this.active
    this.active = undefined
    active?.abort.abort()
  }

  private isCurrent(operation: ActiveSearch) {
    return this.active === operation
      && operation.generation === this.generation
      && !operation.abort.signal.aborted
      && this.state.mode === operation.mode
      && this.state.query === operation.query
  }

  select(index: number) {
    if (!Number.isSafeInteger(index) || index < 0 || index >= this.state.results.length) return
    this.update({ selectedIndex: index })
  }

  moveSelection(delta: -1 | 1) {
    const count = this.state.results.length
    if (!count) return
    const current = this.state.selectedIndex
    const next = current < 0 ? (delta > 0 ? 0 : count - 1) : (current + delta + count) % count
    this.update({ selectedIndex: next })
  }

  activateSelected() {
    const result = this.state.results[this.state.selectedIndex]
    if (!result) return null
    const activation: WorkspaceSearchActivation = { path: result.path, line: result.line, column: result.column }
    this.onActivate?.(activation)
    return activation
  }

  handleKey(key: 'ArrowDown' | 'ArrowUp' | 'Enter' | 'Escape') {
    if (key === 'Escape') {
      if (this.state.status === 'searching') this.cancel()
      else this.update({ selectedIndex: -1 })
      return true
    }
    if (key === 'ArrowDown' || key === 'ArrowUp') {
      if (!this.state.results.length) return false
      this.moveSelection(key === 'ArrowDown' ? 1 : -1)
      return true
    }
    return this.activateSelected() !== null
  }

  dispose() {
    this.cancel(false)
    this.listeners.clear()
  }
}
