import { afterEach, describe, expect, it, vi } from 'vitest'

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
})

describe('Hermes session compatibility adapter', () => {
  it('prefers the unified cross-profile session list', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      sessions: [{ id: 's1', title: 'First', profile: 'default' }],
      total: 1,
    }))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    const result = await hermesSessionApi.list()

    expect(result.sessions[0]?.id).toBe('s1')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/profiles/sessions?')
    expect(String(fetchMock.mock.calls[0][0])).toContain('profile=all')
  })

  it('falls back to the legacy list when the unified endpoint is missing', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ detail: 'missing' }, 404))
      .mockResolvedValueOnce(jsonResponse({ sessions: [{ id: 'legacy' }], total: 1 }))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    const result = await hermesSessionApi.list()

    expect(result.sessions[0]?.id).toBe('legacy')
    expect(String(fetchMock.mock.calls[1][0])).toContain('/api/sessions?')
  })

  it('preserves every profile name advertised by the unified list', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({
      sessions: [{ id: 's1', profile: 'default' }],
      profile_totals: { default: 1, scarlett: 12, ali: 0 },
    })))
    const { hermesSessionApi } = await import('./HermesSessionApi')

    const result = await hermesSessionApi.list()

    expect(result.profiles).toEqual(['default', 'scarlett', 'ali'])
  })

  it('runs bounded profile-aware full-text search and normalizes hydrated and minimal hits', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      results: [
        {
          session_id: 'tip-1',
          title: 'Docker recovery',
          snippet: 'restart the container safely',
          role: 'assistant',
          model: 'deepseek-v4',
          session_started: 100,
          last_active: 125,
          message_count: 8,
        },
        { session_id: 'minimal', snippet: 'another match', session_started: 50 },
        { snippet: 'missing identity' },
      ],
    }))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')
    const controller = new AbortController()

    const result = await hermesSessionApi.search('  docker restart  ', {
      limit: 500,
      profile: 'scarlett',
      signal: controller.signal,
    })

    const url = new URL(String(fetchMock.mock.calls[0][0]), 'http://localhost')
    expect(url.pathname).toBe('/api/sessions/search')
    expect(url.searchParams.get('q')).toBe('docker restart')
    expect(url.searchParams.get('limit')).toBe('100')
    expect(url.searchParams.get('profile')).toBe('scarlett')
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ credentials: 'include', signal: controller.signal })
    expect(result.sessions).toHaveLength(2)
    expect(result.sessions[0]).toMatchObject({
      id: 'tip-1',
      title: 'Docker recovery',
      profile: 'scarlett',
      preview: 'restart the container safely',
      searchSnippet: 'restart the container safely',
      searchRole: 'assistant',
      started_at: 100,
      last_active: 125,
      message_count: 8,
    })
    expect(result.sessions[1]).toMatchObject({
      id: 'minimal',
      profile: 'scarlett',
      preview: 'another match',
      started_at: 50,
      last_active: 50,
    })
  })

  it('does not call Hermes for an empty full-text query', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    await expect(hermesSessionApi.search('   ')).resolves.toEqual({ sessions: [] })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('sends profile in PATCH bodies for rename, archive, and pin', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(jsonResponse({ ok: true, title: 'Renamed' })))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    await hermesSessionApi.rename('abc/123', 'Renamed', 'scarlett')
    await hermesSessionApi.archive('abc/123', true, 'scarlett')
    await hermesSessionApi.pin('abc/123', true, 'scarlett')

    const renameRequest = fetchMock.mock.calls[0]
    const archiveRequest = fetchMock.mock.calls[1]
    const pinRequest = fetchMock.mock.calls[2]
    expect(String(renameRequest[0])).toContain('/api/sessions/abc%2F123')
    expect(renameRequest[1]).toMatchObject({ method: 'PATCH' })
    expect(JSON.parse(String(renameRequest[1]?.body))).toEqual({ title: 'Renamed', profile: 'scarlett' })
    expect(JSON.parse(String(archiveRequest[1]?.body))).toEqual({ archived: true, profile: 'scarlett' })
    expect(JSON.parse(String(pinRequest[1]?.body))).toEqual({ pinned: true, profile: 'scarlett' })
  })

  it('sends profile as a query parameter for delete', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    await hermesSessionApi.remove('session id', 'ali')

    expect(fetchMock.mock.calls[0][0]).toBe('/api/sessions/session%20id?profile=ali')
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'DELETE', credentials: 'include' })
  })

  it('loads the latest transcript page with the owning profile', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      session_id: 'resolved',
      messages: [{ role: 'assistant', content: 'hello' }],
    }))
    vi.stubGlobal('fetch', fetchMock)
    const { hermesSessionApi } = await import('./HermesSessionApi')

    const result = await hermesSessionApi.messages('stored', 'default')

    expect(result.sessionId).toBe('resolved')
    const url = String(fetchMock.mock.calls[0][0])
    expect(url).toContain('/api/sessions/stored/messages?')
    expect(url).toContain('limit=500')
    expect(url).toContain('order=latest')
    expect(url).toContain('profile=default')
  })

  it('turns an authentication envelope into a user-facing sign-in error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ error: 'unauthenticated' }, 401)))
    const { hermesSessionApi } = await import('./HermesSessionApi')

    await expect(hermesSessionApi.list()).rejects.toThrow(
      'Sign in to the Hermes dashboard to load conversation history.',
    )
  })
})
