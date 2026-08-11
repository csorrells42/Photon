export type HermesSession = {
  id: string
  title: string | null
  preview: string | null
  model: string | null
  cwd: string | null
  profile?: string
  pinned?: boolean
  archived?: boolean
  is_active: boolean
  started_at: number
  last_active: number
  message_count: number
  tool_call_count: number
  searchSnippet?: string
  searchRole?: string | null
}

export type HermesStoredMessage = {
  id?: number
  row_id?: number
  role: 'assistant' | 'system' | 'tool' | 'user'
  content?: unknown
  text?: unknown
  timestamp?: number
}

type PaginatedSessions = {
  sessions?: HermesSession[]
  total?: number
  limit?: number
  offset?: number
  profile_totals?: Record<string, number>
}

type SessionSearchResult = Partial<HermesSession> & {
  session_id?: string
  lineage_root?: string
  snippet?: string
  role?: string | null
  session_started?: number
}

type SessionSearchResponse = {
  results?: SessionSearchResult[]
}

type HermesSessionListResult = {
  sessions: HermesSession[]
  total?: number
  profiles?: string[]
}

type SessionMessagesResponse = {
  session_id?: string
  messages?: HermesStoredMessage[]
}

let unifiedSessionListAvailable: boolean | undefined

export class HermesApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
    this.name = 'HermesApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: 'include',
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  if (!response.ok) {
    const detail = await response.text().catch(() => '')
    const message = response.status === 401
      ? 'Sign in to the Hermes dashboard to load conversation history.'
      : detail || `Hermes returned HTTP ${response.status}`
    throw new HermesApiError(message, response.status)
  }

  return response.json() as Promise<T>
}

function normalizedNumber(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

export function normalizeSessionSearchResult(
  result: SessionSearchResult,
  profile?: string,
): HermesSession | null {
  const id = typeof result.id === 'string' && result.id.trim()
    ? result.id.trim()
    : typeof result.session_id === 'string' && result.session_id.trim()
      ? result.session_id.trim()
      : ''
  if (!id) return null

  const startedAt = normalizedNumber(result.started_at, normalizedNumber(result.session_started))
  const snippet = typeof result.snippet === 'string' ? result.snippet.trim() : ''
  return {
    id,
    title: typeof result.title === 'string' ? result.title : null,
    preview: typeof result.preview === 'string' ? result.preview : snippet || null,
    model: typeof result.model === 'string' ? result.model : null,
    cwd: typeof result.cwd === 'string' ? result.cwd : null,
    profile: typeof result.profile === 'string' ? result.profile : profile,
    pinned: result.pinned === true,
    archived: result.archived === true,
    is_active: result.is_active === true,
    started_at: startedAt,
    last_active: normalizedNumber(result.last_active, startedAt),
    message_count: normalizedNumber(result.message_count),
    tool_call_count: normalizedNumber(result.tool_call_count),
    searchSnippet: snippet || undefined,
    searchRole: typeof result.role === 'string' ? result.role : null,
  }
}

async function searchSessions(
  search: string,
  options: { limit?: number; profile?: string; signal?: AbortSignal } = {},
): Promise<HermesSessionListResult> {
  const query = search.trim()
  if (!query) return { sessions: [] as HermesSession[] }
  const limit = Math.max(1, Math.min(100, Math.trunc(options.limit ?? 40)))
  const parameters = new URLSearchParams({ q: query, limit: String(limit) })
  if (options.profile) parameters.set('profile', options.profile)
  const result = await request<SessionSearchResponse>(`/api/sessions/search?${parameters.toString()}`, {
    signal: options.signal,
  })
  return {
    sessions: (result.results ?? [])
      .map((entry) => normalizeSessionSearchResult(entry, options.profile))
      .filter((entry): entry is HermesSession => entry !== null),
  }
}

export const hermesSessionApi = {
  async list(options: { archived?: 'exclude' | 'include' | 'only'; limit?: number; search?: string } = {}): Promise<HermesSessionListResult> {
    const query = new URLSearchParams({
      limit: String(options.limit ?? 80),
      offset: '0',
      min_messages: '1',
      archived: options.archived ?? 'exclude',
      order: 'recent',
    })
    const search = options.search?.trim()
    let result: PaginatedSessions
    if (search) {
      return searchSessions(search, { limit: options.limit })
    } else if (unifiedSessionListAvailable !== false) {
      try {
        result = await request<PaginatedSessions>(`/api/profiles/sessions?${query.toString()}&profile=all`)
        unifiedSessionListAvailable = true
      } catch (reason) {
        if (!(reason instanceof HermesApiError) || reason.status !== 404) throw reason
        unifiedSessionListAvailable = false
        result = await request<PaginatedSessions>(`/api/sessions?${query.toString()}`)
      }
    } else {
      result = await request<PaginatedSessions>(`/api/sessions?${query.toString()}`)
    }

    return {
      sessions: result.sessions ?? [],
      total: typeof result.total === 'number' ? result.total : undefined,
      profiles: result.profile_totals
        ? Object.keys(result.profile_totals)
        : [...new Set((result.sessions ?? []).map((session) => session.profile).filter((profile): profile is string => Boolean(profile)))],
    }
  },

  search: searchSessions,

  async messages(id: string, profile?: string) {
    const query = new URLSearchParams({ limit: '500', order: 'latest' })
    if (profile) query.set('profile', profile)
    const result = await request<SessionMessagesResponse>(
      `/api/sessions/${encodeURIComponent(id)}/messages?${query.toString()}`,
    )
    return { sessionId: result.session_id ?? id, messages: result.messages ?? [] }
  },

  rename(id: string, title: string, profile?: string) {
    return request<{ ok: boolean; title: string }>(`/api/sessions/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ title, ...(profile ? { profile } : {}) }),
    })
  },

  archive(id: string, archived: boolean, profile?: string) {
    return request<{ ok: boolean }>(`/api/sessions/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ archived, ...(profile ? { profile } : {}) }),
    })
  },

  pin(id: string, pinned: boolean, profile?: string) {
    return request<{ ok: boolean; pinned: boolean }>(`/api/sessions/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ pinned, ...(profile ? { profile } : {}) }),
    })
  },

  remove(id: string, profile?: string) {
    const suffix = profile ? `?profile=${encodeURIComponent(profile)}` : ''
    return request<{ ok: boolean }>(`/api/sessions/${encodeURIComponent(id)}${suffix}`, { method: 'DELETE' })
  },
}

export function storedMessageText(message: HermesStoredMessage): string {
  const value = message.text ?? message.content
  if (typeof value === 'string') return value
  if (Array.isArray(value)) {
    return value.map((part) => {
      if (typeof part === 'string') return part
      if (part && typeof part === 'object' && 'text' in part && typeof part.text === 'string') return part.text
      return ''
    }).join('')
  }
  if (value && typeof value === 'object' && 'text' in value && typeof value.text === 'string') return value.text
  return ''
}
