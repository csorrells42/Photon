export const WORKSPACE_SEARCH_PROTOCOL_VERSION = 1 as const

export const WORKSPACE_SEARCH_LIMITS = {
  queryCharacters: 512,
  results: 200,
  resultsPerFile: 25,
  previewCharacters: 1_024,
  matchRanges: 32,
  pathCharacters: 1_024,
  retainedHistory: 20,
  providerResults: 1_000,
  progressFiles: 1_000_000,
  statusMessageCharacters: 256,
} as const

export type WorkspaceSearchMode = 'literal' | 'semantic'
export type WorkspaceSearchStatus =
  | 'idle'
  | 'searching'
  | 'ready'
  | 'empty'
  | 'cancelled'
  | 'error'
  | 'unavailable'

export type WorkspaceSearchUnavailableReason =
  | 'not-configured'
  | 'disabled'
  | 'offline'
  | 'unsupported'

export type WorkspaceSearchAvailability =
  | { state: 'available' }
  | { state: 'unavailable'; reason: WorkspaceSearchUnavailableReason }

export type WorkspaceSearchMatchRange = { start: number; end: number }

/** This is an untrusted provider classification, not proof of root containment or reparse safety.
 * A trusted provider must enforce those filesystem properties before returning a result. */
export type WorkspaceSearchProviderPathKind = 'regular-file' | 'directory' | 'reparse-point' | 'other'

export type WorkspaceSearchProviderResult = {
  path: string
  pathKind: WorkspaceSearchProviderPathKind
  line: number
  column: number
  preview: string
  matches: readonly WorkspaceSearchMatchRange[]
}

export type WorkspaceSearchResult = Omit<WorkspaceSearchProviderResult, 'pathKind'> & {
  id: string
  pathKind: 'regular-file'
}
export type WorkspaceSearchActivation = { path: string; line: number; column: number }

export type LiteralWorkspaceSearchRequest = {
  protocolVersion: typeof WORKSPACE_SEARCH_PROTOCOL_VERSION
  requestId: string
  query: string
  maxResults: number
  maxResultsPerFile: number
  maxPreviewCharacters: number
}

/** Semantic providers receive intent only; command, executable, argument, environment,
 * credential, secret, endpoint, and transport fields are deliberately absent. */
export type SemanticWorkspaceSearchRequest = {
  protocolVersion: typeof WORKSPACE_SEARCH_PROTOCOL_VERSION
  requestId: string
  intent: string
  maxResults: number
  maxResultsPerFile: number
  maxPreviewCharacters: number
}

export type WorkspaceSearchProviderProgress = { completedFiles: number; totalFiles?: number }
export type WorkspaceSearchExecution = {
  signal: AbortSignal
  reportProgress(progress: WorkspaceSearchProviderProgress): void
}

export type WorkspaceSearchProviderOutput = {
  protocolVersion: typeof WORKSPACE_SEARCH_PROTOCOL_VERSION
  requestId: string
  results: readonly WorkspaceSearchProviderResult[]
  truncated: boolean
}

export type LiteralWorkspaceSearchProvider = {
  availability: WorkspaceSearchAvailability
  searchLiteral(request: LiteralWorkspaceSearchRequest, execution: WorkspaceSearchExecution): Promise<unknown>
}

export type SemanticWorkspaceSearchProvider = {
  availability: WorkspaceSearchAvailability
  searchSemantic(request: SemanticWorkspaceSearchRequest, execution: WorkspaceSearchExecution): Promise<unknown>
}

export type WorkspaceSearchHistoryEntry = { mode: WorkspaceSearchMode; query: string; resultCount: number }
export type WorkspaceSearchAttribution = 'serena' | 'semantic' | 'literal'
export type WorkspaceSearchState = {
  mode: WorkspaceSearchMode
  query: string
  status: WorkspaceSearchStatus
  results: readonly WorkspaceSearchResult[]
  selectedIndex: number
  progress: WorkspaceSearchProviderProgress | null
  truncated: boolean
  attribution: WorkspaceSearchAttribution | null
  message: string
  history: readonly WorkspaceSearchHistoryEntry[]
}
