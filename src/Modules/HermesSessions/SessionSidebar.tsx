import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Archive,
  Check,
  ChevronDown,
  Clock3,
  LoaderCircle,
  MessageSquareText,
  Pencil,
  Pin,
  PinOff,
  RefreshCw,
  Search,
  Trash2,
  X,
} from 'lucide-react'
import { hermesSessionApi } from './HermesSessionApi'
import type { HermesSession } from './HermesSessionApi'
import { subscribeHermesAuthChanged } from '../HermesSystem/HermesAuthEvents'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'

type Props = {
  activeSession?: Pick<HermesSession, 'id' | 'profile'> | null
  onOpen: (session: HermesSession) => void
  onSignIn?: () => void
}

export function sessionIdentity(session: Pick<HermesSession, 'id' | 'profile'>) {
  return JSON.stringify([session.profile?.trim() ?? '', session.id])
}

export function updateSessionByIdentity(
  sessions: HermesSession[],
  target: Pick<HermesSession, 'id' | 'profile'>,
  update: (session: HermesSession) => HermesSession,
) {
  const key = sessionIdentity(target)
  return sessions.map((session) => sessionIdentity(session) === key ? update(session) : session)
}

export function removeSessionByIdentity(
  sessions: HermesSession[],
  target: Pick<HermesSession, 'id' | 'profile'>,
) {
  const key = sessionIdentity(target)
  return sessions.filter((session) => sessionIdentity(session) !== key)
}

function sessionTitle(session: HermesSession) {
  return session.title?.trim() || session.preview?.trim() || 'Untitled conversation'
}

function relativeTime(epoch: number) {
  const milliseconds = epoch > 10_000_000_000 ? epoch : epoch * 1000
  const seconds = Math.max(0, Math.round((Date.now() - milliseconds) / 1000))
  if (seconds < 60) return 'now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h`
  if (seconds < 604_800) return `${Math.floor(seconds / 86_400)}d`
  return new Date(milliseconds).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

export function SessionSidebar({ activeSession, onOpen, onSignIn }: Props) {
  const [assistantName] = useAssistantDisplayName()
  const [sessions, setSessions] = useState<HermesSession[]>([])
  const [profiles, setProfiles] = useState<string[]>([])
  const [serverMatches, setServerMatches] = useState<HermesSession[]>([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [searchPending, setSearchPending] = useState(false)
  const [searchError, setSearchError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editingKey, setEditingKey] = useState<string | null>(null)
  const [titleDraft, setTitleDraft] = useState('')
  const [confirmDeleteKey, setConfirmDeleteKey] = useState<string | null>(null)
  const [workingKey, setWorkingKey] = useState<string | null>(null)
  const activeSessionKey = activeSession ? sessionIdentity(activeSession) : null

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await hermesSessionApi.list({ limit: 100 })
      setSessions(result.sessions)
      setProfiles(result.profiles ?? [])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not load Hermes sessions.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])
  useEffect(() => subscribeHermesAuthChanged((state) => {
    if (state === 'signed-in') void load()
    else {
      setSessions([])
      setProfiles([])
      setServerMatches([])
      setError('Sign in to Hermes from Workbench Account to view conversations.')
      setLoading(false)
    }
  }), [load])

  const trimmedQuery = query.trim()

  useEffect(() => {
    if (!trimmedQuery) {
      setServerMatches([])
      setSearchPending(false)
      setSearchError(null)
      return
    }

    const controller = new AbortController()
    setSearchPending(true)
    setSearchError(null)
    const timer = window.setTimeout(() => {
      const targets: Array<string | undefined> = profiles.length ? profiles : [undefined]
      void Promise.allSettled(targets.map((profile) => hermesSessionApi.search(trimmedQuery, {
        limit: 50,
        profile,
        signal: controller.signal,
      }))).then((results) => {
        if (controller.signal.aborted) return
        const matches = results.flatMap((result) => result.status === 'fulfilled' ? result.value.sessions : [])
        setServerMatches(matches)
        const failures = results.filter((result) => result.status === 'rejected')
        if (failures.length === results.length) setSearchError('Hermes full-text search is temporarily unavailable.')
        else if (failures.length) setSearchError(`Search completed, but ${failures.length} profile${failures.length === 1 ? '' : 's'} could not be reached.`)
      }).finally(() => {
        if (!controller.signal.aborted) setSearchPending(false)
      })
    }, 200)

    return () => {
      controller.abort()
      window.clearTimeout(timer)
    }
  }, [profiles, trimmedQuery])

  const visible = useMemo(() => {
    if (!trimmedQuery) return sessions
    const term = trimmedQuery.toLowerCase()
    const merged = new Map<string, HermesSession>()
    for (const session of sessions) {
      if (`${sessionTitle(session)} ${session.preview ?? ''} ${session.cwd ?? ''} ${session.model ?? ''}`.toLowerCase().includes(term)) {
        merged.set(`${session.profile ?? ''}:${session.id}`, session)
      }
    }
    for (const session of serverMatches) {
      const key = `${session.profile ?? ''}:${session.id}`
      if (!merged.has(key)) merged.set(key, session)
    }
    return [...merged.values()]
  }, [serverMatches, sessions, trimmedQuery])

  async function commitRename(session: HermesSession) {
    const title = titleDraft.trim()
    if (!title) return
    const key = sessionIdentity(session)
    setWorkingKey(key)
    try {
      await hermesSessionApi.rename(session.id, title, session.profile)
      setSessions((current) => updateSessionByIdentity(current, session, (item) => ({ ...item, title })))
      setServerMatches((current) => updateSessionByIdentity(current, session, (item) => ({ ...item, title })))
      setEditingKey(null)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not rename the session.')
    } finally { setWorkingKey(null) }
  }

  async function archive(session: HermesSession) {
    setWorkingKey(sessionIdentity(session))
    try {
      await hermesSessionApi.archive(session.id, true, session.profile)
      setSessions((current) => removeSessionByIdentity(current, session))
      setServerMatches((current) => removeSessionByIdentity(current, session))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not archive the session.')
    } finally { setWorkingKey(null) }
  }

  async function togglePin(session: HermesSession) {
    const pinned = !session.pinned
    setWorkingKey(sessionIdentity(session))
    try {
      await hermesSessionApi.pin(session.id, pinned, session.profile)
      setSessions((current) => updateSessionByIdentity(current, session, (item) => ({ ...item, pinned })))
      setServerMatches((current) => updateSessionByIdentity(current, session, (item) => ({ ...item, pinned })))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : `Could not ${pinned ? 'pin' : 'unpin'} the session.`)
    } finally { setWorkingKey(null) }
  }

  async function remove(session: HermesSession) {
    const key = sessionIdentity(session)
    if (confirmDeleteKey !== key) { setConfirmDeleteKey(key); return }
    setWorkingKey(key)
    try {
      await hermesSessionApi.remove(session.id, session.profile)
      setSessions((current) => removeSessionByIdentity(current, session))
      setServerMatches((current) => removeSessionByIdentity(current, session))
      setConfirmDeleteKey(null)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not delete the session.')
    } finally { setWorkingKey(null) }
  }

  return (
    <aside className="session-sidebar">
      <div className="session-panel-title">
        <span>SESSIONS</span>
        <button aria-label="Refresh sessions" onClick={() => void load()}><RefreshCw size={14} /></button>
      </div>
      <label className="session-search">
        <Search size={13} />
        <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search conversations" />
        {query && <button aria-label="Clear search" onClick={() => setQuery('')}><X size={12} /></button>}
      </label>
      <div className="session-section-label"><ChevronDown size={13} /> {trimmedQuery ? 'SEARCH RESULTS' : 'RECENT'} <span>{searchPending && <LoaderCircle className="spin" size={10} />}{visible.length}</span></div>
      <div className="session-list">
        {loading && <div className="session-empty"><LoaderCircle className="spin" size={17} /> Loading sessions…</div>}
        {!loading && error && <div className="session-error">
          <span>{error}</span>
          {error.startsWith('Sign in') && <button type="button" onClick={onSignIn}>Sign in inside Workbench</button>}
          <button onClick={() => void load()}>Retry</button>
        </div>}
        {!loading && !error && visible.length === 0 && <div className="session-empty"><MessageSquareText size={18} /> No conversations found</div>}
        {!loading && !error && searchError && <div className="session-search-note">{searchError}</div>}
        {!loading && visible.map((session) => {
          const key = sessionIdentity(session)
          const editing = editingKey === key
          const deleting = confirmDeleteKey === key
          const working = workingKey === key
          return (
            <div className={`session-row ${activeSessionKey === key ? 'active' : ''}`} key={key} data-session-key={key}>
              {editing ? (
                <div className="session-edit">
                  <input autoFocus value={titleDraft} onChange={(event) => setTitleDraft(event.target.value)} onKeyDown={(event) => {
                    if (event.key === 'Enter') void commitRename(session)
                    if (event.key === 'Escape') setEditingKey(null)
                  }} />
                  <button aria-label="Save title" onClick={() => void commitRename(session)}><Check size={13} /></button>
                  <button aria-label="Cancel rename" onClick={() => setEditingKey(null)}><X size={13} /></button>
                </div>
              ) : (
                <>
                  <button className="session-open" onClick={() => onOpen(session)}>
                    <span className="session-title">{session.pinned && <Pin aria-label="Pinned" size={10} />}{sessionTitle(session)}</span>
                    {trimmedQuery && session.searchSnippet
                      ? <span className="session-meta session-match"><Search size={10} />{session.searchRole ? `${session.searchRole}: ` : ''}{session.searchSnippet}</span>
                      : <span className="session-meta"><Clock3 size={10} /> {relativeTime(session.last_active || session.started_at)}{session.message_count ? ` · ${session.message_count} messages` : ''}</span>}
                  </button>
                  <div className="session-actions">
                    {working ? <LoaderCircle className="spin" size={13} /> : <>
                      <button className={session.pinned ? 'pinned' : ''} aria-label={session.pinned ? 'Unpin session' : 'Pin session'} onClick={() => void togglePin(session)}>{session.pinned ? <PinOff size={12} /> : <Pin size={12} />}</button>
                      <button aria-label="Rename session" onClick={() => { setEditingKey(key); setTitleDraft(sessionTitle(session)); setConfirmDeleteKey(null) }}><Pencil size={12} /></button>
                      <button aria-label="Archive session" onClick={() => void archive(session)}><Archive size={12} /></button>
                      <button className={deleting ? 'confirm-delete' : ''} aria-label={deleting ? 'Confirm delete session' : 'Delete session'} onClick={() => void remove(session)}>{deleting ? <Check size={12} /> : <Trash2 size={12} />}</button>
                    </>}
                  </div>
                </>
              )}
            </div>
          )
        })}
      </div>
      <div className="session-sidebar-footer">{assistantName} history · update-safe Hermes REST adapter</div>
    </aside>
  )
}
