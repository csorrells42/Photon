import { describe, expect, it } from 'vitest'
import { HermesExtensionSettingsLiveAdapter, type HermesExtensionSettingsLiveFetch } from './HermesExtensionSettingsLiveAdapter'
import { liveAdapterLimits } from './contracts'

type RouteValue = unknown | ((url: URL) => unknown | Promise<unknown>)

function baseRoutes(): Record<string, RouteValue> {
  return {
    '/api/skills': [
      { name: 'bundled-skill', description: 'Bundled', provenance: 'bundled' },
      { name: 'agent-skill', description: 'Agent', provenance: 'agent' },
      { name: 'reviewed-skill', description: 'Hub reviewed', provenance: 'hub' },
      { name: 'external-skill', description: 'Hub external', provenance: 'hub' },
    ],
    '/api/skills/content': (url: URL) => ({ name: url.searchParams.get('name'), content: `# ${url.searchParams.get('name')}` }),
    '/api/tools/toolsets': [{ name: 'web', available: true }],
    '/api/tools/toolsets/web/config': {
      providers: [
        { name: 'reviewed-search', web_backend: 'search-backend', capabilities: ['search'], status: 'ready', tag: 'Search only.' },
        { name: 'external-extract', web_backend: 'extract-backend', capabilities: ['extract'], status: 'ready', tag: 'Extract only.' },
      ],
      active_search_backend: 'search-backend',
      active_extract_backend: 'extract-backend',
    },
    '/api/mcp/servers': {
      servers: [
        { name: 'Catalog MCP', transport: 'http', url: 'https://catalog.example.test/mcp', env: {}, enabled: true },
        { name: 'Reviewed MCP', transport: 'stdio', command: 'reviewed-command', args: ['--run'], env: {}, enabled: true },
        { name: 'Local MCP', transport: 'stdio', command: 'local-command', args: ['--run'], env: {}, enabled: false },
      ],
    },
    '/api/mcp/catalog': { entries: [{ name: 'Catalog MCP', source: 'nous', bootstrap: ['ignored setup command'] }], diagnostics: [] },
    '/api/model/options': {
      provider: 'openrouter',
      model: 'model-one',
      providers: [{
        slug: 'openrouter',
        name: 'OpenRouter',
        authenticated: true,
        models: ['model-one', 'model-two'],
        unavailable_models: [],
        pricing: {
          'model-one': { input: '$20.00', output: '$100.00' },
          'model-two': { input: '$20.01', output: '$101.00' },
        },
        capabilities: { 'model-one': { fast: true, reasoning: true } },
      }],
    },
    '/api/model/info': {
      model: 'model-one',
      provider: 'openrouter',
      capabilities: { supports_vision: true, supports_reasoning: true },
    },
    '/api/model/auxiliary': {
      main: { provider: 'openrouter', model: 'model-one' },
      tasks: [
        { task: 'vision', provider: 'openrouter', model: 'model-two', base_url: '' },
        { task: 'compression', provider: 'openrouter', model: 'model-one', base_url: '' },
        { task: 'title_generation', provider: 'openrouter', model: 'model-one', base_url: '' },
      ],
    },
    '/api/model/moa': {
      default_preset: 'balanced',
      active_preset: 'balanced',
      presets: {
        balanced: {
          enabled: true,
          reference_models: [{ provider: 'openrouter', model: 'model-one' }],
          aggregator: { provider: 'openrouter', model: 'model-two' },
        },
      },
    },
  }
}

function jsonResponse(value: unknown, status = 200, declaredLength?: number) {
  const body = JSON.stringify(value)
  return new Response(body, {
    status,
    headers: {
      'Content-Type': 'application/json',
      'Content-Length': String(declaredLength ?? new TextEncoder().encode(body).byteLength),
    },
  })
}

function createFetch(
  overrides: Record<string, RouteValue | { status: number; body?: unknown }> = {},
  delay?: Promise<void>,
) {
  const routes = { ...baseRoutes(), ...overrides }
  const requests: Array<{ url: URL; init?: RequestInit }> = []
  const fetcher: HermesExtensionSettingsLiveFetch = async (input, init) => {
    const url = new URL(String(input), 'http://hermes.local')
    requests.push({ url, init })
    if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    if (delay) await delay
    const route = routes[url.pathname]
    if (route && typeof route === 'object' && !Array.isArray(route) && 'status' in route) {
      return jsonResponse((route as { status: number; body?: unknown }).body ?? {}, Number(route.status))
    }
    const value = typeof route === 'function' ? await route(url) : route
    return jsonResponse(value ?? {})
  }
  return { fetcher, requests }
}

describe('HermesExtensionSettingsLiveAdapter', () => {
  it('preserves trust labels independently and carries profile/correlation identity', async () => {
    const { fetcher, requests } = createFetch()
    const adapter = new HermesExtensionSettingsLiveAdapter(fetcher, {
      workbenchReviews: [
        { subject: 'skill', id: 'reviewed-skill' },
        { subject: 'toolset-provider', id: 'reviewed-search' },
        { subject: 'mcp-server', id: 'Reviewed-MCP' },
      ],
      now: () => new Date('2026-08-09T12:00:00.000Z'),
    })

    const result = await adapter.read({ profileId: 'labs', correlationId: 'corr-001' })

    expect(result.state).toBe('ready')
    expect(result.profileId).toBe('labs')
    expect(result.correlationId).toBe('corr-001')
    expect(result.snapshot.skills.map((skill) => [skill.id, skill.provenance])).toEqual([
      ['bundled-skill', 'nous-approved'],
      ['agent-skill', 'user-created'],
      ['reviewed-skill', 'workbench-reviewed'],
      ['external-skill', 'external-unreviewed'],
    ])
    expect(result.snapshot.toolsetProviders.map((provider) => provider.provenance)).toEqual([
      'workbench-reviewed',
      'external-unreviewed',
    ])
    expect(result.snapshot.mcpServers.map((server) => [server.name, server.provenance])).toEqual([
      ['Catalog MCP', 'nous-approved'],
      ['Reviewed MCP', 'workbench-reviewed'],
      ['Local MCP', 'user-created'],
    ])
    expect(result.snapshot.toolsets.search).toMatchObject({ providerId: 'reviewed-search', backendId: 'search-backend' })
    expect(result.snapshot.toolsets.extract).toMatchObject({ providerId: 'external-extract', backendId: 'extract-backend' })
    expect(requests.every((request) => request.url.searchParams.get('profile') === 'labs')).toBe(true)
    expect(requests.every((request) => new Headers(request.init?.headers).get('X-Hermes-Correlation-Id') === 'corr-001')).toBe(true)
    expect(JSON.stringify(result)).not.toMatch(/Chris|Codex approval/i)
  })

  it('redacts credential text and never exposes values, headers, OAuth tokens, or commands', async () => {
    const secret = 'ultra-private-credential-9f7c'
    const { fetcher } = createFetch({
      '/api/skills': [{ name: 'unsafe', description: `Authorization: Bearer ${secret}`, provenance: 'hub', api_key: secret }],
      '/api/skills/content': (url: URL) => ({ name: url.searchParams.get('name'), content: `api_key=${secret}`, path: 'C:/secret/path' }),
      '/api/mcp/servers': {
        servers: [{
          name: 'Unsafe MCP',
          transport: 'http',
          url: `https://user:${secret}@mcp.example.test/private?access_token=${secret}`,
          command: `setup --token=${secret}`,
          args: [secret],
          env: { SAFE_NAME: secret },
          headers: { Authorization: `Bearer ${secret}` },
          oauth_token: secret,
        }],
      },
      '/api/mcp/catalog': {
        entries: [{
          name: 'Unsafe MCP',
          source: 'nous',
          bootstrap: [`install --token=${secret}`],
          post_install: secret,
        }],
      },
      '/api/model/options': {
        provider: 'openrouter',
        model: 'safe-model',
        providers: [{ slug: 'openrouter', models: ['safe-model'], api_key: secret, headers: { Authorization: secret } }],
      },
    })
    const result = await new HermesExtensionSettingsLiveAdapter(fetcher).read({ correlationId: 'redaction-001' })
    const serialized = JSON.stringify(result)

    expect(serialized).not.toContain(secret)
    expect(serialized).not.toContain('setup --token')
    expect(serialized).not.toContain('install --token')
    expect(serialized).not.toContain('C:/secret/path')
    expect(result.snapshot.skills[0].description).toContain('[redacted]')
    expect(result.snapshot.skills[0].content).toContain('[redacted]')
    expect(result.snapshot.mcpServers[0]).toMatchObject({
      endpoint: '',
      command: '',
      args: [],
      environmentVariableNames: ['SAFE_NAME'],
    })
  })

  it('reports one unavailable provider source without discarding ready sources', async () => {
    const { fetcher } = createFetch({
      '/api/tools/toolsets/web/config': { status: 503, body: { detail: 'provider unavailable' } },
    })
    const result = await new HermesExtensionSettingsLiveAdapter(fetcher).read({ correlationId: 'partial-001' })

    expect(result.state).toBe('partial')
    expect(result.snapshot.skills.length).toBeGreaterThan(0)
    expect(result.snapshot.models.length).toBeGreaterThan(0)
    expect(result.snapshot.models.find((model) => model.id === 'openrouter::model-one')?.costTier).toBe('standard')
    expect(result.snapshot.models.find((model) => model.id === 'openrouter::model-two')?.costTier).toBe('expensive')
    expect(result.sources.find((source) => source.source === 'web-toolset-config')).toMatchObject({
      state: 'unavailable',
      itemCount: 0,
    })
    expect(JSON.stringify(result)).not.toContain('provider unavailable')
  })

  it('preserves a source-confirmed partial provider state', async () => {
    const { fetcher } = createFetch({
      '/api/tools/toolsets/web/config': {
        providers: [{
          name: 'needs-setup',
          web_backend: 'partial-search',
          capabilities: ['search'],
          status: 'needs setup',
        }],
        active_search_backend: 'partial-search',
        active_extract_backend: null,
      },
    })
    const result = await new HermesExtensionSettingsLiveAdapter(fetcher).read({ correlationId: 'provider-partial-001' })

    expect(result.snapshot.toolsetProviders[0]).toMatchObject({ id: 'needs-setup', state: 'partial' })
  })

  it('ignores malformed response fields and reports their sources as partial', async () => {
    const { fetcher } = createFetch({
      '/api/skills': { skills: 'wrong shape' },
      '/api/tools/toolsets/web/config': { providers: 'wrong shape', active_search_backend: { nested: true } },
      '/api/mcp/servers': { servers: 'wrong shape', environment: { SECRET: 'discard' } },
      '/api/model/auxiliary': { tasks: 'wrong shape', main: ['wrong'] },
      '/api/model/moa': { presets: 'wrong shape' },
    })
    const result = await new HermesExtensionSettingsLiveAdapter(fetcher).read({ correlationId: 'malformed-001' })

    expect(result.state).toBe('partial')
    expect(result.snapshot.skills).toEqual([])
    expect(result.snapshot.toolsetProviders).toEqual([])
    expect(result.snapshot.mcpServers).toEqual([])
    expect(result.sources.find((source) => source.source === 'skills')?.state).toBe('partial')
    expect(result.sources.find((source) => source.source === 'web-toolset-config')?.state).toBe('partial')
    expect(result.sources.find((source) => source.source === 'model-auxiliary')?.state).toBe('partial')
    expect(JSON.stringify(result)).not.toContain('discard')
  })
  it('honors cancellation and stops with an honest cancelled result', async () => {
    const controller = new AbortController()
    let calls = 0
    const fetcher: HermesExtensionSettingsLiveFetch = (_input, init) => new Promise((_resolve, reject) => {
      calls += 1
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
    })
    const pending = new HermesExtensionSettingsLiveAdapter(fetcher).read({
      profileId: 'labs',
      correlationId: 'cancel-001',
      signal: controller.signal,
    })
    controller.abort()
    const result = await pending

    expect(calls).toBeGreaterThan(0)
    expect(result.state).toBe('cancelled')
    expect(result.sources.some((source) => source.state === 'cancelled')).toBe(true)
  })

  it('rejects concurrent and replayed duplicate correlation identities', async () => {
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    const { fetcher } = createFetch({}, gate)
    const adapter = new HermesExtensionSettingsLiveAdapter(fetcher)

    const first = adapter.read({ profileId: 'labs', correlationId: 'duplicate-001' })
    const concurrent = await adapter.read({ profileId: 'labs', correlationId: 'duplicate-001' })
    expect(concurrent.state).toBe('duplicate')

    release()
    expect((await first).state).toBe('ready')
    const replay = await adapter.read({ profileId: 'labs', correlationId: 'duplicate-001' })
    expect(replay.state).toBe('duplicate')
  })

  it('returns unavailable for every unsupported mutation surface', async () => {
    const adapter = new HermesExtensionSettingsLiveAdapter(createFetch().fetcher)
    const results = await Promise.all([
      adapter.createSkill(),
      adapter.editSkill(),
      adapter.installSkill(),
      adapter.configureToolset(),
      adapter.configureModels(),
      adapter.configureMcp(),
    ])

    expect(results.every((result) => result.status === 'unavailable')).toBe(true)
    expect(results.map((result) => result.kind)).toEqual([
      'skill-create',
      'skill-edit',
      'skill-install',
      'toolset-configure',
      'model-configure',
      'mcp-configure',
    ])
  })

  it('bounds oversized sources and collections without losing other source data', async () => {
    const oversized = 'x'.repeat(liveAdapterLimits.responseBytes + 1)
    const manyModels = Array.from({ length: liveAdapterLimits.collection + 80 }, (_, index) => `model-${index}`)
    const { fetcher } = createFetch({
      '/api/model/info': () => oversized,
      '/api/model/options': {
        provider: 'openrouter',
        model: 'model-0',
        providers: [{ slug: 'openrouter', authenticated: true, models: manyModels }],
      },
    })
    const result = await new HermesExtensionSettingsLiveAdapter(fetcher).read({ correlationId: 'bounds-001' })

    expect(result.state).toBe('partial')
    expect(result.snapshot.models).toHaveLength(liveAdapterLimits.collection)
    expect(result.sources.find((source) => source.source === 'model-info')).toMatchObject({
      state: 'error',
      message: 'Source response exceeded the byte limit.',
    })
  })
})
