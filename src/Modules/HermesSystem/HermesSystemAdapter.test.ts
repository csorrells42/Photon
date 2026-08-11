import { describe, expect, it, vi } from 'vitest'
import {
  HermesSystemAdapter,
  normalizeHermesIdentity,
  normalizeHermesProviders,
  normalizeHermesStatus,
  normalizeHermesSystemStats,
} from './HermesSystemAdapter'

describe('Hermes system compatibility adapter', () => {
  it('normalizes public health without leaking upstream path fields', () => {
    const result = normalizeHermesStatus({
      version: '0.9.0', overall: 'ok', active_sessions: 4,
      gateway_running: true, gateway_state: 'running', gateway_busy: true,
      active_agents: 2, auth_required: true, auth_providers: ['password'],
      auth_flows: ['cookie'], hermes_home: 'C:/secret/path',
      components: { gateway: { status: 'ok', state: 'running' }, storage: { status: 'degraded' } },
    })

    expect(result).toMatchObject({
      version: '0.9.0', overall: 'ok', activeSessions: 4,
      gateway: { running: true, state: 'running', busy: true, activeAgents: 2 },
      auth: { required: true, providers: ['password'], flows: ['cookie'] },
    })
    expect(JSON.stringify(result)).not.toContain('secret/path')
  })

  it('drops malformed providers and maps identity and stats fields', () => {
    expect(normalizeHermesProviders({ providers: [
      { name: 'local', display_name: 'Local account', supports_password: true },
      { display_name: 'broken' },
    ] })).toEqual([{ name: 'local', displayName: 'Local account', supportsPassword: true }])

    expect(normalizeHermesIdentity({ user_id: 'owner', provider: 'local', expires_at: 42 })).toMatchObject({ userId: 'owner', provider: 'local', expiresAt: 42 })
    expect(normalizeHermesSystemStats({ os: 'Linux', os_release: '6', memory: { total: 10, used: 4, available: 6, percent: 40 } })).toMatchObject({ operatingSystem: 'Linux 6', memory: { percent: 40 } })
  })

  it('keeps password credentials in the request body and never in the URL', async () => {
    const fetcher = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({ ok: true, next: '/' }), { status: 200 }))
    const adapter = new HermesSystemAdapter(fetcher)

    await adapter.passwordLogin('local', 'engineer', 'correct horse')

    expect(fetcher).toHaveBeenCalledOnce()
    const call = fetcher.mock.calls[0]
    expect(call).toBeDefined()
    const [url, init] = call!
    expect(url).toBe('/auth/password-login')
    expect(String(url)).not.toContain('correct horse')
    expect(JSON.parse(String(init?.body))).toMatchObject({ provider: 'local', username: 'engineer', password: 'correct horse' })
    expect(init?.credentials).toBe('include')
  })

  it('treats an identity 401 as signed out but surfaces other failures', async () => {
    const unauthorized = new HermesSystemAdapter(async () => new Response(JSON.stringify({ detail: 'Unauthorized' }), { status: 401 }))
    await expect(unauthorized.identity()).resolves.toBeNull()

    const unavailable = new HermesSystemAdapter(async () => new Response('offline', { status: 503 }))
    await expect(unavailable.identity()).rejects.toThrow('offline')
  })
})
