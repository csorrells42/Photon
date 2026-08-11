import { describe, expect, it, vi } from 'vitest'
import { SameOriginHermesRuntimeConfigurationClient } from './HermesRuntimeConfigurationClient'

function response(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('Same-origin Hermes runtime configuration client', () => {
  it('uses authenticated same-origin routes and sends no secret-shaped endpoint field', async () => {
    const calls: Array<[RequestInfo | URL, RequestInit | undefined]> = []
    const fetch: typeof globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push([input, init])
      return response({ current: { provider: 'local', model: 'gpt-oss:20b', base_url: 'http://host.docker.internal:11434/v1' }, endpoints: [] })
    })
    const client = new SameOriginHermesRuntimeConfigurationClient(fetch)
    await client.save({
      id: 'local', name: 'Local', baseUrl: 'http://host.docker.internal:11434/v1', model: 'gpt-oss:20b',
      discoverModels: true, makeDefault: true,
    })
    const [path, init] = calls[0]
    expect(path).toBe('/api/providers/custom-endpoints')
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(String(init?.body)).not.toMatch(/api_key|password|token|secret/i)
  })

  it('allows model discovery before the user enters a model ID', async () => {
    const calls: Array<[RequestInfo | URL, RequestInit | undefined]> = []
    const fetch: typeof globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push([input, init])
      return response({ ok: true, reachable: true, message: '', models: ['gpt-oss:20b'] })
    })
    const client = new SameOriginHermesRuntimeConfigurationClient(fetch)
    const result = await client.validate({
      name: '', baseUrl: 'http://host.docker.internal:11434/v1', model: '', discoverModels: true, makeDefault: false,
    })
    expect(result.models).toEqual(['gpt-oss:20b'])
    expect(String(calls[0][1]?.body)).toContain('discovery-probe')
  })

  it('does not expose backend error bodies', async () => {
    const fetch: typeof globalThis.fetch = vi.fn(async () => response({ detail: 'secret internal path' }, 500))
    const client = new SameOriginHermesRuntimeConfigurationClient(fetch)
    await expect(client.list()).rejects.toThrow('HTTP 500')
    await expect(client.list()).rejects.not.toThrow('secret internal path')
  })

  it('uses exact endpoint and model scoped profile routes', async () => {
    const calls: Array<[RequestInfo | URL, RequestInit | undefined]> = []
    const fetch: typeof globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push([input, init])
      return response({ current: {}, endpoints: [] })
    })
    const client = new SameOriginHermesRuntimeConfigurationClient(fetch)
    await client.saveProfile('local-lm-studio', { id: 'balanced', name: 'Balanced', model: 'openai/gpt-oss-20b', makeActive: true, overrides: { temperature: .4 } })
    await client.activateProfile('local-lm-studio', 'openai/gpt-oss-20b', null)
    expect(calls[0][0]).toBe('/api/providers/custom-endpoints/local-lm-studio/profiles')
    expect(String(calls[0][1]?.body)).toContain('"temperature":0.4')
    expect(calls[1][0]).toBe('/api/providers/custom-endpoints/local-lm-studio/profiles/activate')
    expect(String(calls[1][1]?.body)).toContain('"profile_id":null')
  })
})
