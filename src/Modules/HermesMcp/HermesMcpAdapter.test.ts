import { describe, expect, it, vi } from 'vitest'
import {
  HermesMcpAdapter,
  normalizeHermesMcpCatalog,
  normalizeHermesMcpOAuthFlow,
  normalizeHermesMcpServers,
  normalizeHermesMcpTestResult,
} from './HermesMcpAdapter'

describe('Hermes MCP compatibility adapter', () => {
  it('normalizes bounded server metadata and never returns environment values', () => {
    const result = normalizeHermesMcpServers({ servers: [{
      name: 'serena',
      transport: 'http',
      url: 'http://host.docker.internal:9121/mcp',
      env: { SERENA_TOKEN: 'masked-but-still-not-for-react', INVALID_KEY: 'also-hidden' },
      auth: 'header',
      enabled: true,
      tools: ['find_symbol'],
      private_path: '/root/.hermes',
    }, { transport: 'http' }] })

    expect(result).toEqual([expect.objectContaining({
      name: 'serena',
      environmentVariableNames: [],
      tools: null,
      url: null,
      command: null,
      args: [],
      auth: 'header',
      editable: false,
    })])
    expect(JSON.stringify(result)).not.toContain('masked-but-still-not-for-react')
    expect(JSON.stringify(result)).not.toContain('/root/.hermes')
  })

  it('normalizes the Nous catalog, diagnostics, and transparent install details', () => {
    const result = normalizeHermesMcpCatalog({
      entries: [{
        name: 'n8n', description: 'Workflow tools', source: 'nous', transport: 'stdio', auth_type: 'api_key',
        required_env: [{ name: 'N8N_API_KEY', prompt: 'API key', required: true }, { prompt: 'broken' }],
        command: '${INSTALL_DIR}/server.py', args: ['--stdio'], install_url: 'https://example.com/repo.git', install_ref: 'abc123',
        bootstrap: ['python -m venv .venv'], post_install: 'Restart Hermes', needs_install: true, installed: false, enabled: false,
      }, { name: 'broken', transport: 'socket' }],
      diagnostics: [{ name: 'n8n', kind: 'warning', message: 'Pinned revision unavailable' }, { name: '', message: 'drop me' }],
    })

    expect(result.catalog).toEqual([expect.objectContaining({
      name: 'n8n', transport: 'stdio', authType: 'api_key', installRef: 'abc123', bootstrap: ['python -m venv .venv'],
      requiredEnvironment: [{ name: 'N8N_API_KEY', prompt: 'API key', required: true }],
    })])
    expect(result.diagnostics).toEqual([{ name: 'n8n', kind: 'warning', message: 'Pinned revision unavailable' }])
  })

  it('normalizes test and OAuth frames without retaining unknown fields', () => {
    expect(normalizeHermesMcpTestResult({ ok: true, tools: [{ name: 'search', description: 'Search' }], token: 'secret' }))
      .toEqual({ ok: true, error: '', tools: [{ name: 'search', description: 'Search' }] })
    expect(normalizeHermesMcpOAuthFlow({ flow_id: 'flow', server_name: 'figma', status: 'authorization_required', authorization_url: 'https://example.com/auth', tools: [] }))
      .toMatchObject({ flowId: 'flow', serverName: 'figma', status: 'authorization_required', authorizationUrl: 'https://example.com/auth' })
  })

  it('never accepts provider secrets in renderer requests and encodes server names', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/test')) return new Response(JSON.stringify({ ok: true, tools: [] }), { status: 200 })
      if (String(input) === '/api/mcp/servers') return new Response(JSON.stringify({ ok: true }), { status: 200 })
      return new Response(JSON.stringify({ ok: true }), { status: 200 })
    })
    const adapter = new HermesMcpAdapter(fetcher)

    await expect(adapter.create({
      name: 'reports', transport: 'http', url: 'https://example.com/mcp', auth: 'header', bearerToken: 'secret-token',
    } as never)).rejects.toThrow('native Connections broker')
    await adapter.create({ name: 'reports', transport: 'http', url: 'https://example.com/mcp', auth: 'oauth' })

    const createCall = fetcher.mock.calls[0]!
    expect(createCall[0]).toBe('/api/mcp/servers')
    expect(String(createCall[0])).not.toContain('secret-token')
    expect(JSON.parse(String(createCall[1]?.body))).toEqual({ name: 'reports', url: 'https://example.com/mcp', auth: 'oauth' })
    expect(String(createCall[1]?.body)).not.toContain('secret-token')
    expect(createCall[1]?.credentials).toBe('include')

    await adapter.test('team/reports')
    expect(fetcher.mock.calls[1]![0]).toBe('/api/mcp/servers/team%2Freports/test')
  })

  it('rejects unsafe URLs before a network request', async () => {
    const fetcher = vi.fn()
    const adapter = new HermesMcpAdapter(fetcher)

    await expect(adapter.create({ name: 'files', transport: 'http', url: 'file:///secret' })).rejects.toThrow('HTTP or HTTPS')
    expect(fetcher).not.toHaveBeenCalled()
  })
})
