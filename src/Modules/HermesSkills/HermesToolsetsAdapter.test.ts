import { describe, expect, it, vi } from 'vitest'
import { HermesToolsetsAdapter, normalizeHermesToolsetConfig, normalizeHermesToolsets } from './HermesToolsetsAdapter'

describe('HermesToolsetsAdapter v1', () => {
  it('normalizes toolsets without retaining configuration extras', () => {
    const rows = normalizeHermesToolsets([{ name: 'web', label: 'Web', platform: 'cli', enabled: true, available: true, configured: false, tools: ['web_search'], config: { token: 'discard' } }])
    expect(rows).toEqual([{ name: 'web', label: 'Web', description: '', platform: 'cli', platformLabel: 'cli', enabled: true, available: true, configured: false, tools: ['web_search'] }])
    expect(JSON.stringify(rows)).not.toContain('discard')
  })

  it('returns provider key status and prompts but never values or private fields', () => {
    const config = normalizeHermesToolsetConfig({
      name: 'web', has_category: true, active_provider: 'Brave', active_search_backend: 'brave',
      providers: [{ name: 'Brave', badge: 'fast', tag: 'Search', status: 'ready', is_active: true, web_backend: 'brave', capabilities: ['search'], env_vars: [{ key: 'BRAVE_API_KEY', prompt: 'Brave key', url: 'https://example.invalid/key', is_set: true, value: 'discard-secret' }], internal_path: '/private' }],
    })
    expect(config).toMatchObject({ name: 'web', activeProvider: 'Brave', providers: [{ name: 'Brave', status: 'ready', environment: [{ key: 'BRAVE_API_KEY', isSet: true }] }] })
    expect(JSON.stringify(config)).not.toContain('discard-secret')
    expect(JSON.stringify(config)).not.toContain('/private')
  })

  it('has no renderer API that posts provider secret values', async () => {
    const secret = 'fixture-secret-value'
    const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    const adapter = new HermesToolsetsAdapter(request)
    await adapter.selectProvider('web', 'Brave')
    const body = JSON.parse(String(request.mock.calls[0]?.[1]?.body))
    expect(body).toEqual({ provider: 'Brave' })
    expect(String(request.mock.calls[0]?.[1]?.body)).not.toContain(secret)
  })

  it('rejects unadvertised post-setup hooks before network access', async () => {
    const request = vi.fn()
    const adapter = new HermesToolsetsAdapter(request)
    await expect(adapter.runPostSetup('browser', 'arbitrary-command', ['camofox'])).rejects.toThrow('not present')
    expect(request).not.toHaveBeenCalled()
  })

  it('encodes toolset names in all configuration routes', async () => {
    const request = vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).includes('/config') ? { name: 'web/team', providers: [] } : { ok: true }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    const adapter = new HermesToolsetsAdapter(request)
    await adapter.config('web/team')
    await adapter.toggle('web/team', true)
    expect(String(request.mock.calls[0]?.[0])).toContain('/web%2Fteam/config')
    expect(String(request.mock.calls[1]?.[0])).toContain('/web%2Fteam')
  })
})
