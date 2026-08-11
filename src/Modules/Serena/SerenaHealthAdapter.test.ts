import { describe, expect, it, vi } from 'vitest'
import { SerenaHealthAdapter } from './SerenaHealthAdapter'

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('Serena health adapter', () => {
  it('proves the Hermes-to-Serena path and exposes bounded tool metadata only', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => String(input).endsWith('/test')
      ? json({ ok: true, tools: [{ name: 'find_symbol', description: 'Find code symbols.' }], prompts: 2, resources: 1 })
      : json({ servers: [{
        name: 'serena', transport: 'http', enabled: true,
        url: 'http://host.docker.internal:9121/mcp?token=must-not-leak',
        env: { API_KEY: 'must-not-leak' }, headers: { Authorization: 'must-not-leak' },
      }] }))

    const result = await new SerenaHealthAdapter(fetcher).inspect()

    expect(result).toMatchObject({
      state: 'ready', configured: true, enabled: true, reachable: true,
      endpoint: 'http://host.docker.internal:9121/mcp', toolCount: 1, prompts: 2, resources: 1,
      tools: [{ name: 'find_symbol', description: 'Find code symbols.' }],
    })
    expect(JSON.stringify(result)).not.toContain('must-not-leak')
    expect(fetcher).toHaveBeenNthCalledWith(2, '/api/mcp/servers/serena/test', expect.objectContaining({ method: 'POST', credentials: 'include' }))
  })

  it('reports missing and disabled configurations without probing', async () => {
    const missingFetch = vi.fn(async () => json({ servers: [] }))
    await expect(new SerenaHealthAdapter(missingFetch).inspect()).resolves.toMatchObject({ state: 'missing', configured: false })
    expect(missingFetch).toHaveBeenCalledTimes(1)

    const disabledFetch = vi.fn(async () => json({ servers: [{ name: 'serena', enabled: false, transport: 'http' }] }))
    await expect(new SerenaHealthAdapter(disabledFetch).inspect()).resolves.toMatchObject({ state: 'disabled', configured: true, reachable: false })
    expect(disabledFetch).toHaveBeenCalledTimes(1)
  })

  it('turns authentication and probe failures into explicit non-secret states', async () => {
    const unauthorized = new SerenaHealthAdapter(async () => json({ detail: 'Unauthorized' }, 401))
    await expect(unauthorized.inspect()).resolves.toMatchObject({ state: 'authentication-required', reachable: false })

    const fetcher = vi.fn(async (input: RequestInfo | URL) => String(input).endsWith('/test')
      ? json({ ok: false, error: 'connection refused' })
      : json({ servers: [{ name: 'serena', enabled: true, transport: 'http' }] }))
    await expect(new SerenaHealthAdapter(fetcher).inspect()).resolves.toMatchObject({ state: 'error', detail: 'connection refused' })
  })
})
