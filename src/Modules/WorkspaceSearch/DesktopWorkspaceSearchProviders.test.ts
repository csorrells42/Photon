import { describe, expect, it, vi } from 'vitest'
import { WORKSPACE_SEARCH_PROTOCOL_VERSION } from './contracts'
import { DesktopWorkspaceSearchClient, createDesktopWorkspaceSearchProviders, type WorkspaceSearchHostMessageBridge } from './DesktopWorkspaceSearchProviders'

class FakeBridge implements WorkspaceSearchHostMessageBridge {
  readonly sent: unknown[] = []
  private listener: ((event: MessageEvent) => void) | null = null
  postMessage(message: unknown) { this.sent.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listener = listener }
  removeEventListener(_type: 'message', listener: (event: MessageEvent) => void) { if (this.listener === listener) this.listener = null }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

const literalRequest = {
  protocolVersion: WORKSPACE_SEARCH_PROTOCOL_VERSION,
  requestId: 'workspace-search:literal:1',
  query: 'Photon',
  maxResults: 20,
  maxResultsPerFile: 5,
  maxPreviewCharacters: 256,
} as const

function execution(controller = new AbortController()) {
  return { controller, value: { signal: controller.signal, reportProgress: vi.fn() } }
}

describe('Desktop Workspace Search providers', () => {
  it('sends only the typed literal contract and accepts matching bounded host frames', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopWorkspaceSearchClient(() => bridge)
    const active = execution()
    const pending = client.searchLiteral(literalRequest, active.value)

    expect(bridge.sent).toEqual([{
      type: 'workspaceSearch.literal', version: 1, ...literalRequest,
    }])
    expect(JSON.stringify(bridge.sent[0])).not.toMatch(/endpoint|executable|environment|toolName|arguments/i)

    bridge.emit({ type: 'workspaceSearch.progress', version: 1, requestId: literalRequest.requestId, completedFiles: 4, totalFiles: 10 })
    expect(active.value.reportProgress).toHaveBeenCalledWith({ completedFiles: 4, totalFiles: 10 })

    bridge.emit({
      type: 'workspaceSearch.literal.result', version: 1, protocolVersion: 1,
      requestId: literalRequest.requestId, results: [], truncated: false,
    })
    await expect(pending).resolves.toEqual({ protocolVersion: 1, requestId: literalRequest.requestId, results: [], truncated: false })
  })

  it('ignores mismatched and malformed frames and cancels the exact active request', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopWorkspaceSearchClient(() => bridge)
    const active = execution()
    const pending = client.searchLiteral(literalRequest, active.value)
    bridge.emit({ type: 'workspaceSearch.literal.result', version: 1, protocolVersion: 1, requestId: 'foreign', results: [], truncated: false })
    bridge.emit({ type: 'workspaceSearch.progress', version: 1, requestId: literalRequest.requestId, completedFiles: -1 })
    expect(active.value.reportProgress).not.toHaveBeenCalled()

    active.controller.abort()
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
    expect(bridge.sent[1]).toMatchObject({ type: 'workspaceSearch.cancel', version: 1, targetRequestId: literalRequest.requestId })
  })

  it('keeps semantic authority native and sanitizes host errors', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopWorkspaceSearchClient(() => bridge)
    const active = execution()
    const pending = client.searchSemantic({
      protocolVersion: 1, requestId: 'workspace-search:semantic:1', intent: 'find the session controller',
      maxResults: 20, maxResultsPerFile: 5, maxPreviewCharacters: 256,
    }, active.value)
    expect(bridge.sent[0]).toEqual(expect.objectContaining({
      type: 'workspaceSearch.semantic', intent: 'find the session controller',
    }))
    expect(bridge.sent[0]).not.toEqual(expect.objectContaining({ tool: expect.anything() }))

    bridge.emit({
      type: 'workspaceSearch.error', version: 1, requestId: 'workspace-search:semantic:1',
      message: `safe\u0000message${'x'.repeat(400)}`,
    })
    await expect(pending).rejects.toThrow(/^safe messagex{1,}/)
  })

  it('rejects an oversized host result instead of retaining unbounded provider data', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopWorkspaceSearchClient(() => bridge)
    const pending = client.searchLiteral(literalRequest, execution().value)
    bridge.emit({
      type: 'workspaceSearch.literal.result', version: 1, protocolVersion: 1,
      requestId: literalRequest.requestId,
      results: Array.from({ length: 201 }, () => ({})),
      truncated: true,
    })
    await expect(pending).rejects.toThrow(/invalid result/)
  })

  it('reports unavailable without a native bridge and rejects duplicate identifiers', async () => {
    const missing = new DesktopWorkspaceSearchClient(() => null)
    expect(missing.availability).toEqual({ state: 'unavailable', reason: 'not-configured' })
    await expect(missing.searchLiteral(literalRequest, execution().value)).rejects.toThrow(/desktop app/)

    const bridge = new FakeBridge()
    const providers = createDesktopWorkspaceSearchProviders(new DesktopWorkspaceSearchClient(() => bridge))
    expect(providers.literalProvider.availability).toEqual({ state: 'available' })
    const first = providers.literalProvider.searchLiteral(literalRequest, execution().value)
    await expect(providers.literalProvider.searchLiteral(literalRequest, execution().value)).rejects.toThrow(/already active/)
    providers.close()
    await expect(first).rejects.toThrow(/disconnected/)
  })
})
