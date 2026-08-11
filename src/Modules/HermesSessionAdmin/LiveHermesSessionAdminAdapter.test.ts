import { describe, expect, it, vi } from 'vitest'
import { LiveHermesSessionAdminAdapter } from './LiveHermesSessionAdminAdapter'
import { sessionAdminBounds } from './contracts'

let nextCorrelation = 0
function scope(profileId = 'default') {
  nextCorrelation += 1
  return { profileId, correlationId: `live-test:${nextCorrelation}` }
}

function response(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  })
}

describe('LiveHermesSessionAdminAdapter', () => {
  it('pages the verified list route, normalizes rows, and keeps the profile scope on every request', async () => {
    const rows = Array.from({ length: 101 }, (_, index) => ({
      id: `session-${index}`,
      profile: 'default',
      title: index === 0 ? 'Primary session' : '',
      started_at: 1_700_000_000 + index,
      last_active: 1_700_000_100 + index,
      message_count: index,
      tool_call_count: index + 1,
      archived: index === 100,
      is_active: index === 0,
    }))
    const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://workbench.local')
      expect(init?.credentials).toBe('include')
      expect(url.searchParams.get('profile')).toBe('default')
      expect(url.searchParams.get('archived')).toBe('include')
      expect(url.searchParams.get('order')).toBe('recent')
      const offset = Number(url.searchParams.get('offset'))
      const limit = Number(url.searchParams.get('limit'))
      return response({ sessions: rows.slice(offset, offset + limit), total: rows.length })
    })
    const adapter = new LiveHermesSessionAdminAdapter(fetcher)

    const result = await adapter.listSessions({ ...scope(), limit: 101 })

    expect(result).toMatchObject({ status: 'success', profileId: 'default' })
    expect(result.data).toMatchObject({ total: 101, truncated: true })
    expect(result.data?.sessions).toHaveLength(101)
    expect(result.data?.sessions[0]).toMatchObject({
      title: 'Primary session', lifecycle: 'active', startedAt: 1_700_000_000_000,
      lastActiveAt: 1_700_000_100_000, messageCount: 0, toolCallCount: 1,
    })
    expect(result.data?.sessions[100]).toMatchObject({ lifecycle: 'archived', title: 'session-100' })
    expect(fetcher).toHaveBeenCalledTimes(2)
  })

  it('uses a safe bounded fallback when a caller supplies a non-finite list limit', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://workbench.local')
      expect(url.searchParams.get('limit')).toBe('100')
      return response({ sessions: [], total: 0 })
    })
    const adapter = new LiveHermesSessionAdminAdapter(fetcher)

    await expect(adapter.listSessions({ ...scope(), limit: Number.NaN })).resolves.toMatchObject({ status: 'success' })
  })

  it('rejects a cross-profile list row without exposing it to the workspace', async () => {
    const adapter = new LiveHermesSessionAdminAdapter(async () => response({
      sessions: [{ id: 'foreign', profile: 'scarlett', started_at: 1 }], total: 1,
    }))

    const result = await adapter.listSessions({ ...scope('default'), limit: 10 })

    expect(result).toMatchObject({ status: 'error', data: null })
    expect(result.notices.join(' ')).toContain('another profile')
  })

  it('reports only statistics the verified route actually supplies', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
      expect(String(input)).toContain('/api/sessions/stats?profile=default')
      return response({ total: 9, active_store: 7, archived: 2, messages: 88 })
    })
    const adapter = new LiveHermesSessionAdminAdapter(fetcher)

    const result = await adapter.statistics(scope())

    expect(result.status).toBe('partial')
    expect(result.data?.total).toMatchObject({ value: 9, quality: 'reported' })
    expect(result.data?.archived).toMatchObject({ value: 2, quality: 'reported' })
    expect(result.data?.messages).toMatchObject({ value: 88, quality: 'reported' })
    expect(result.data?.active).toMatchObject({ value: null, quality: 'unavailable' })
    expect(result.data?.ended).toMatchObject({ value: null, quality: 'unavailable' })
    expect(result.data?.storageBytes).toMatchObject({ value: null, quality: 'unavailable' })
  })

  it('maps the latest-descendant route to an honestly partial bounded path', async () => {
    const adapter = new LiveHermesSessionAdminAdapter(async (input) => {
      expect(String(input)).toContain('/api/sessions/root/latest-descendant?profile=default')
      return response({ requested_session_id: 'root', session_id: 'grandchild', path: ['root', 'child', 'grandchild'], changed: true })
    })

    const result = await adapter.descendants({ ...scope(), rootSessionId: 'root', maxDepth: 1, maxNodes: 20 })

    expect(result.status).toBe('partial')
    expect(result.notices.join(' ')).toContain('latest descendant path only')
    expect(result.data).toMatchObject({ rootSessionId: 'root', returned: 1, truncated: true, maxDepth: 1 })
    expect(result.data?.nodes[0]).toMatchObject({ sessionId: 'child', parentSessionId: 'root', depth: 1, children: [] })
  })

  it('exports bounded text-only messages and omits unsupported rows without overstating truncation', async () => {
    const adapter = new LiveHermesSessionAdminAdapter(async (input) => {
      expect(String(input)).toContain('/api/sessions/export-me/export?profile=default')
      return response({
        id: 'export-me', profile: 'default', title: 'Export me',
        messages: [
          { role: 'user', content: 'hello' },
          { role: 'assistant', content: [{ type: 'text', text: 'world' }] },
          { role: 'unknown', content: 'omit me' },
        ],
      })
    })

    const result = await adapter.exportSessions({ ...scope(), sessionIds: ['export-me'] })

    expect(result.status).toBe('partial')
    expect(result.data).toMatchObject({ fileName: 'hermes-sessions-default.json', mediaType: 'application/json' })
    expect(result.data?.sessions[0]?.messages).toEqual([
      { role: 'user', text: 'hello' },
      { role: 'assistant', text: 'world' },
    ])
    expect(result.notices.join(' ')).toContain('unsupported message row')
    expect(result.notices.join(' ')).not.toContain(`bounded to ${sessionAdminBounds.maxMessagesPerImportedSession}`)
    expect(JSON.parse(result.data!.contentText)).toMatchObject({ profileId: 'default' })
  })

  it('rejects mismatched export identity and cross-profile export data', async () => {
    const mismatched = new LiveHermesSessionAdminAdapter(async () => response({ id: 'different', profile: 'default', messages: [] }))
    const wrongProfile = new LiveHermesSessionAdminAdapter(async () => response({ id: 'expected', profile: 'scarlett', messages: [] }))

    const mismatchResult = await mismatched.exportSessions({ ...scope(), sessionIds: ['expected'] })
    const profileResult = await wrongProfile.exportSessions({ ...scope(), sessionIds: ['expected'] })

    expect(mismatchResult).toMatchObject({ status: 'error', data: null })
    expect(mismatchResult.notices.join(' ')).toContain('No selected sessions')
    expect(profileResult).toMatchObject({ status: 'error', data: null })
    expect(profileResult.notices.join(' ')).toContain('No selected sessions')
  })

  it('keeps every unverified mutation unavailable and performs no request', async () => {
    const fetcher = vi.fn(async () => response({}))
    const adapter = new LiveHermesSessionAdminAdapter(fetcher)

    const result = await adapter.previewDelete({ ...scope(), sessionIds: ['one'], includeDescendants: true })

    expect(result).toMatchObject({ status: 'unavailable', data: null })
    expect(result.notices.join(' ')).toContain('not exposed by the verified read-only Hermes adapter')
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('returns correlated cancellation before dispatch and rejects duplicate correlations', async () => {
    const fetcher = vi.fn(async () => response({ sessions: [], total: 0 }))
    const adapter = new LiveHermesSessionAdminAdapter(fetcher)
    const controller = new AbortController()
    controller.abort()

    const cancelled = await adapter.listSessions({ profileId: 'default', correlationId: 'cancel:one', limit: 10 }, controller.signal)
    const first = await adapter.listSessions({ profileId: 'default', correlationId: 'duplicate:one', limit: 10 })
    const duplicate = await adapter.listSessions({ profileId: 'default', correlationId: 'duplicate:one', limit: 10 })

    expect(cancelled).toMatchObject({ status: 'cancelled', correlationId: 'cancel:one' })
    expect(first.status).toBe('success')
    expect(duplicate).toMatchObject({ status: 'error', data: null })
    expect(duplicate.notices.join(' ')).toContain('Duplicate correlation')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('rejects oversized and malformed responses with sanitized bounded notices', async () => {
    const oversized = new LiveHermesSessionAdminAdapter(async () => response({}, 200, { 'content-length': '2000001' }))
    const malformed = new LiveHermesSessionAdminAdapter(async () => new Response('{bad json', { status: 200 }))

    const oversizedResult = await oversized.listSessions({ ...scope(), limit: 10 })
    const malformedResult = await malformed.listSessions({ ...scope(), limit: 10 })

    expect(oversizedResult).toMatchObject({ status: 'error', data: null })
    expect(oversizedResult.notices[0]).toContain('exceeds')
    expect(malformedResult).toMatchObject({ status: 'error', data: null })
    expect(malformedResult.notices).toEqual(['Hermes returned malformed session data.'])
  })
})
