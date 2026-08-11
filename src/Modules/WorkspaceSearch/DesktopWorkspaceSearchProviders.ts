import {
  WORKSPACE_SEARCH_LIMITS,
  WORKSPACE_SEARCH_PROTOCOL_VERSION,
  type LiteralWorkspaceSearchProvider,
  type LiteralWorkspaceSearchRequest,
  type SemanticWorkspaceSearchProvider,
  type SemanticWorkspaceSearchRequest,
  type WorkspaceSearchAvailability,
  type WorkspaceSearchExecution,
  type WorkspaceSearchProviderOutput,
  type WorkspaceSearchProviderProgress,
} from './contracts'

const requestIdPattern = /^[A-Za-z0-9_:-]{1,128}$/u
const maximumErrorCharacters = WORKSPACE_SEARCH_LIMITS.statusMessageCharacters

export type WorkspaceSearchHostMessageBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

type SearchKind = 'literal' | 'semantic'
type PendingSearch = {
  kind: SearchKind
  execution: WorkspaceSearchExecution
  resolve(value: unknown): void
  reject(reason: Error): void
  removeAbort(): void
}

function currentBridge(): WorkspaceSearchHostMessageBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WorkspaceSearchHostMessageBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function boundedInteger(value: unknown, minimum: number, maximum: number) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum ? value : null
}

function safeMessage(value: unknown, fallback: string) {
  if (typeof value !== 'string') return fallback
  const normalized = value.replace(/[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/gu, ' ')
    .slice(0, maximumErrorCharacters)
  return normalized || fallback
}

function validRequestId(value: unknown): value is string {
  return typeof value === 'string' && requestIdPattern.test(value)
}

function available(bridge: WorkspaceSearchHostMessageBridge | null): WorkspaceSearchAvailability {
  return bridge ? { state: 'available' } : { state: 'unavailable', reason: 'not-configured' }
}

function abortError() {
  return new DOMException('Workspace search was cancelled.', 'AbortError')
}

/**
 * Typed renderer-to-host transport for Workspace Search. The public methods do not
 * accept a Serena endpoint, executable, MCP tool name, environment, or arbitrary
 * argument object. Those security decisions remain native-host owned.
 */
export class DesktopWorkspaceSearchClient {
  private bridge: WorkspaceSearchHostMessageBridge | null = null
  private cancelSequence = 0
  private readonly pending = new Map<string, PendingSearch>()
  private readonly receive = (event: MessageEvent) => {
    const raw = record(event.data)
    if (!raw || raw.version !== WORKSPACE_SEARCH_PROTOCOL_VERSION || !validRequestId(raw.requestId)) return
    const pending = this.pending.get(raw.requestId)
    if (!pending) return

    if (raw.type === 'workspaceSearch.progress') {
      const completedFiles = boundedInteger(raw.completedFiles, 0, WORKSPACE_SEARCH_LIMITS.progressFiles)
      const totalFiles = raw.totalFiles === undefined
        ? undefined
        : boundedInteger(raw.totalFiles, completedFiles ?? 0, WORKSPACE_SEARCH_LIMITS.progressFiles)
      if (completedFiles !== null && (raw.totalFiles === undefined || totalFiles !== null)) {
        const progress: WorkspaceSearchProviderProgress = { completedFiles, totalFiles: totalFiles ?? undefined }
        pending.execution.reportProgress(progress)
      }
      return
    }

    if (raw.type === `workspaceSearch.${pending.kind}.result`) {
      if (raw.protocolVersion !== WORKSPACE_SEARCH_PROTOCOL_VERSION
        || !Array.isArray(raw.results) || raw.results.length > WORKSPACE_SEARCH_LIMITS.results
        || typeof raw.truncated !== 'boolean') {
        this.complete(raw.requestId)
        pending.reject(new Error('The workspace-search host returned an invalid result.'))
        return
      }
      this.complete(raw.requestId)
      pending.resolve({
        protocolVersion: raw.protocolVersion,
        requestId: raw.requestId,
        results: raw.results,
        truncated: raw.truncated,
      } satisfies WorkspaceSearchProviderOutput)
      return
    }

    if (raw.type === 'workspaceSearch.error') {
      this.complete(raw.requestId)
      pending.reject(new Error(safeMessage(raw.message, 'Workspace search failed safely.')))
    }
  }

  constructor(private readonly bridgeFactory: () => WorkspaceSearchHostMessageBridge | null = currentBridge) {}

  get availability(): WorkspaceSearchAvailability { return available(this.bridgeFactory()) }

  searchLiteral(request: LiteralWorkspaceSearchRequest, execution: WorkspaceSearchExecution): Promise<unknown> {
    return this.search('literal', request, execution)
  }

  searchSemantic(request: SemanticWorkspaceSearchRequest, execution: WorkspaceSearchExecution): Promise<unknown> {
    return this.search('semantic', request, execution)
  }

  close() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    for (const [requestId, pending] of this.pending) {
      pending.removeAbort()
      pending.reject(new Error('Workspace search disconnected.'))
      this.pending.delete(requestId)
    }
  }

  private search(
    kind: SearchKind,
    request: LiteralWorkspaceSearchRequest | SemanticWorkspaceSearchRequest,
    execution: WorkspaceSearchExecution,
  ) {
    if (request.protocolVersion !== WORKSPACE_SEARCH_PROTOCOL_VERSION || !validRequestId(request.requestId)) {
      return Promise.reject(new Error('The workspace-search request is invalid.'))
    }
    if (execution.signal.aborted) return Promise.reject(abortError())
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Workspace search is available in the Phos Agape Aphthartos desktop app.'))
    if (this.pending.has(request.requestId)) return Promise.reject(new Error('That workspace-search request is already active.'))

    return new Promise<unknown>((resolve, reject) => {
      const cancel = () => {
        const active = this.pending.get(request.requestId)
        if (!active) return
        this.complete(request.requestId)
        try {
          bridge.postMessage({
            type: 'workspaceSearch.cancel',
            version: WORKSPACE_SEARCH_PROTOCOL_VERSION,
            requestId: `cancel:${Date.now().toString(36)}:${(++this.cancelSequence).toString(36)}`,
            targetRequestId: request.requestId,
          })
        } catch { /* Cancellation remains local even if the host has gone away. */ }
        reject(abortError())
      }
      execution.signal.addEventListener('abort', cancel, { once: true })
      this.pending.set(request.requestId, {
        kind,
        execution,
        resolve,
        reject,
        removeAbort: () => execution.signal.removeEventListener('abort', cancel),
      })
      try {
        bridge.postMessage({ type: `workspaceSearch.${kind}`, version: WORKSPACE_SEARCH_PROTOCOL_VERSION, ...request })
      } catch (reason) {
        this.complete(request.requestId)
        reject(reason instanceof Error ? reason : new Error('Workspace search could not receive the request.'))
      }
    })
  }

  private connect() {
    const bridge = this.bridgeFactory()
    if (bridge && bridge !== this.bridge) {
      this.bridge?.removeEventListener('message', this.receive)
      this.bridge = bridge
      bridge.addEventListener('message', this.receive)
    }
    return bridge
  }

  private complete(requestId: string) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    pending.removeAbort()
    this.pending.delete(requestId)
  }
}

export function createDesktopWorkspaceSearchProviders(client = new DesktopWorkspaceSearchClient()): {
  literalProvider: LiteralWorkspaceSearchProvider
  semanticProvider: SemanticWorkspaceSearchProvider
  close(): void
} {
  return {
    literalProvider: {
      get availability() { return client.availability },
      searchLiteral: (request, execution) => client.searchLiteral(request, execution),
    },
    semanticProvider: {
      get availability() { return client.availability },
      searchSemantic: (request, execution) => client.searchSemantic(request, execution),
    },
    close: () => client.close(),
  }
}
