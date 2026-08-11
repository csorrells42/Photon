import { describe, expect, it, vi } from 'vitest'
import { HERMES_MCP_EDITOR_CONTRACT_VERSION, type HermesMcpEditorReviewRequest } from './HermesMcpEditorContract'
import { HermesMcpLiveEditorController } from './HermesMcpLiveEditorController'

const request: HermesMcpEditorReviewRequest = {
  contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
  serverId: 'search/server',
  revision: 'mcp-rev:opaque',
  edit: { transport: 'http', url: 'https://example.test/mcp', command: '', args: [], environmentVariableNames: [], auth: null, enabled: true },
}

describe('HermesMcpLiveEditorController', () => {
  it('sends only safe configuration and normalizes an opaque review', async () => {
    const fetcher = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
      contract_version: 3, status: 'ready', reason: 'ready',
      review_handle: `mcp-review:${'a'.repeat(43)}`, risk: 'source',
    }), { status: 200 }))
    const controller = new HermesMcpLiveEditorController('search/server', fetcher)
    const result = await controller.review(request)
    expect(result).toMatchObject({ status: 'ready', risk: 'source' })
    expect(fetcher.mock.calls[0]?.[0]).toBe('/api/mcp/servers/search%2Fserver/review')
    const body = String((fetcher.mock.calls[0]?.[1] as RequestInit).body)
    expect(body).toContain('environment_variable_names')
    expect(body).not.toMatch(/secret|token|password/iu)
  })

  it('maps backend details to fixed reasons and never returns arbitrary text', async () => {
    const fetcher = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({ detail: 'database leaked secret text' }), { status: 500 }))
    const result = await new HermesMcpLiveEditorController('search/server', fetcher).review(request)
    expect(result).toEqual({ contractVersion: 3, status: 'unavailable', reason: 'unavailable' })
    expect(JSON.stringify(result)).not.toContain('database')
  })

  it('commits and discards by opaque handle without configuration or secrets', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(
      String(input).endsWith('/commit')
        ? { contract_version: 3, status: 'success', reason: 'committed' }
        : { ok: true },
    ), { status: 200 }))
    const controller = new HermesMcpLiveEditorController('search/server', fetcher)
    const handle = `mcp-review:${'b'.repeat(43)}`
    expect(await controller.commit({ contractVersion: 3, reviewHandle: handle, riskConfirmed: true }))
      .toMatchObject({ status: 'success', reason: 'committed' })
    await controller.discard({ contractVersion: 3, reviewHandle: handle })
    expect(String((fetcher.mock.calls[0]?.[1] as RequestInit).body)).toBe(JSON.stringify({ review_handle: handle, risk_confirmed: true }))
    expect(fetcher.mock.calls[1]?.[1]).toMatchObject({ method: 'DELETE', credentials: 'include' })
  })
})
