import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, Bookmark, ChevronDown, ChevronUp, Download, Globe2, LoaderCircle, MapPinned, Plus, RotateCw, Route, Search, Star, Trash2, X } from 'lucide-react'
import { desktopBrowserClient, tryNormalizeBrowserAddress, type BrowserBookmarkImportItem, type BrowserSurfaceState } from './DesktopBrowserClient'
import './BrowserWorkspace.css'

export type BrowserTab = { id: string; url: string; title: string; state?: BrowserSurfaceState; pendingOpenRequestId?: string; requestKey?: string }
export type BrowserOpenRequest = { nonce: number; url: string; label?: string; key?: string; bookmark?: boolean }
export type BrowserBookmark = { id: string; title: string; url: string; folder?: string }
export type BrowserWorkspaceMode = 'browser' | 'maps'
export type GoogleMapsTravelMode = 'driving' | 'walking' | 'bicycling' | 'transit'
export const HERMES_HELP_BROWSER_REQUEST = Object.freeze({
  key: 'hermes-help',
  label: 'Hermes Help',
  url: 'https://hermes-agent.nousresearch.com/docs/',
  bookmark: true,
})
const googleMapsTravelModes = new Set<GoogleMapsTravelMode>(['driving', 'walking', 'bicycling', 'transit'])
export const browserBookmarksStorageKey = 'phos.browser.bookmarks.v1'
const maximumBrowserBookmarks = 256

export type BrowserBookmarkMerge = {
  bookmarks: BrowserBookmark[]
  importedCount: number
  duplicateCount: number
  capacitySkippedCount: number
}

function bookmarkableUrl(rawUrl: string) {
  try {
    const url = new URL(rawUrl)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}

function loadBrowserBookmarks(): BrowserBookmark[] {
  try {
    const raw = window.localStorage.getItem(browserBookmarksStorageKey)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    const seen = new Set<string>()
    const bookmarks: BrowserBookmark[] = []
    for (const candidate of parsed) {
      if (!candidate || typeof candidate !== 'object') continue
      const value = candidate as Partial<BrowserBookmark>
      const url = typeof value.url === 'string' ? bookmarkableUrl(value.url) : null
      if (!url || seen.has(url)) continue
      seen.add(url)
      bookmarks.push({
        id: typeof value.id === 'string' && value.id.length <= 128 ? value.id : crypto.randomUUID(),
        title: typeof value.title === 'string' && value.title.trim() ? value.title.trim().slice(0, 256) : new URL(url).hostname,
        url,
        ...(typeof value.folder === 'string' && value.folder.trim() && value.folder.length <= 512 ? { folder: value.folder.trim() } : {}),
      })
      if (bookmarks.length >= maximumBrowserBookmarks) break
    }
    return bookmarks
  } catch {
    return []
  }
}

function upsertBrowserBookmark(current: BrowserBookmark[], rawUrl: string, rawTitle?: string) {
  const url = bookmarkableUrl(rawUrl)
  if (!url) return current
  const title = rawTitle?.trim().slice(0, 256) || new URL(url).hostname
  const existing = current.find((bookmark) => bookmark.url === url)
  if (existing) return current.map((bookmark) => bookmark.id === existing.id ? { ...bookmark, title } : bookmark)
  return [{ id: crypto.randomUUID(), title, url }, ...current].slice(0, maximumBrowserBookmarks)
}

export function applyBrowserBookmarkRequest(current: BrowserBookmark[], request: BrowserOpenRequest): BrowserBookmark[] {
  return request.bookmark === true ? upsertBrowserBookmark(current, request.url, request.label) : current
}

export function mergeImportedBrowserBookmarks(current: readonly BrowserBookmark[], imported: readonly BrowserBookmarkImportItem[]): BrowserBookmarkMerge {
  const bookmarks = current.slice(0, maximumBrowserBookmarks)
  const seen = new Set(bookmarks.map((bookmark) => bookmarkableUrl(bookmark.url)).filter((url): url is string => url !== null))
  let importedCount = 0
  let duplicateCount = 0
  let capacitySkippedCount = 0
  for (const candidate of imported) {
    const url = bookmarkableUrl(candidate.url)
    if (!url) continue
    if (seen.has(url)) { duplicateCount++; continue }
    if (bookmarks.length >= maximumBrowserBookmarks) { capacitySkippedCount++; continue }
    seen.add(url)
    bookmarks.push({
      id: crypto.randomUUID(),
      title: candidate.title.trim().slice(0, 256) || new URL(url).hostname,
      url,
      ...(candidate.folder.trim() ? { folder: candidate.folder.trim().slice(0, 512) } : {}),
    })
    importedCount++
  }
  return { bookmarks, importedCount, duplicateCount, capacitySkippedCount }
}

export function BrowserBookmarksBar({ bookmarks, importing = false, importStatus = '', onImport, onOpen, onRemove }: { bookmarks: readonly BrowserBookmark[]; importing?: boolean; importStatus?: string; onImport?: () => void; onOpen: (url: string) => void; onRemove: (id: string) => void }) {
  return <section className="browser-bookmarks" aria-label="Bookmarks bar" data-bookmark-storage-key={browserBookmarksStorageKey}>
    <header><Bookmark size={12} /><strong>Bookmarks</strong><span>{bookmarks.length}</span>{onImport && <button type="button" disabled={importing} onClick={onImport}><Download size={10} />{importing ? 'Importing…' : 'Import Chrome'}</button>}</header>
    {bookmarks.length === 0
      ? <p>Bookmark a page here, or ask Photon to bookmark it. The same bar remains after Photon restarts.</p>
      : <div>{bookmarks.map((bookmark) => <article key={bookmark.id}>
          <button type="button" title={bookmark.folder ? `${bookmark.folder} / ${bookmark.title}` : bookmark.title} onClick={() => onOpen(bookmark.url)}><Globe2 size={12} /><span><strong>{bookmark.title}</strong><small>{new URL(bookmark.url).hostname}</small></span></button>
          <button type="button" aria-label={`Remove ${bookmark.title}`} onClick={() => onRemove(bookmark.id)}><Trash2 size={12} /></button>
        </article>)}</div>}
    {importStatus && <output aria-live="polite">{importStatus}</output>}
  </section>
}

function boundedMapsText(value: string, required: boolean) {
  const text = value.normalize('NFKC').trim()
  if ((!text && required) || text.length > 512 || /[\u0000-\u001f\u007f]/u.test(text)) return null
  return text
}

export function buildGoogleMapsSearchUrl(query: string): string | null {
  const safeQuery = boundedMapsText(query, true)
  if (!safeQuery) return null
  const url = new URL('https://www.google.com/maps/search/')
  url.searchParams.set('api', '1')
  url.searchParams.set('query', safeQuery)
  return url.href
}

export function buildGoogleMapsDirectionsUrl(origin: string, destination: string, travelMode: GoogleMapsTravelMode): string | null {
  const safeOrigin = boundedMapsText(origin, false)
  const safeDestination = boundedMapsText(destination, true)
  if (safeOrigin === null || !safeDestination || !googleMapsTravelModes.has(travelMode)) return null
  const url = new URL('https://www.google.com/maps/dir/')
  url.searchParams.set('api', '1')
  if (safeOrigin) url.searchParams.set('origin', safeOrigin)
  url.searchParams.set('destination', safeDestination)
  url.searchParams.set('travelmode', travelMode)
  return url.href
}

export function browserRequestTabId(tabs: readonly BrowserTab[], request: BrowserOpenRequest): string | null {
  if (!request.key) return null
  return tabs.find((tab) => tab.requestKey === request.key)?.id ?? null
}

export function BrowserMapsPanel({ onNavigate, onError, alwaysExpanded = false, initiallyExpanded = false }: { onNavigate: (url: string) => void; onError: (message: string) => void; alwaysExpanded?: boolean; initiallyExpanded?: boolean }) {
  const [expanded, setExpanded] = useState(alwaysExpanded || initiallyExpanded)
  const [place, setPlace] = useState('')
  const [origin, setOrigin] = useState('')
  const [destination, setDestination] = useState('')
  const [travelMode, setTravelMode] = useState<GoogleMapsTravelMode>('driving')

  function searchMaps() {
    const url = buildGoogleMapsSearchUrl(place)
    if (!url) { onError('Enter a place or address of 512 characters or fewer.'); return }
    onError('')
    onNavigate(url)
  }

  function openDirections() {
    const url = buildGoogleMapsDirectionsUrl(origin, destination, travelMode)
    if (!url) { onError('Enter a destination. Origin is optional.'); return }
    onError('')
    onNavigate(url)
  }

  return <section className={`browser-maps ${expanded ? 'expanded' : ''}`} aria-label="Google Maps tools">
    <button className="browser-maps-toggle" type="button" aria-expanded={alwaysExpanded || expanded} disabled={alwaysExpanded} onClick={() => setExpanded((value) => !value)}>
      <MapPinned size={14} /><span><strong>Google Maps</strong><small>Search places or plan a route in this browser</small></span>{!alwaysExpanded && (expanded ? <ChevronUp size={13} /> : <ChevronDown size={13} />)}
    </button>
    {expanded && <div className="browser-maps-controls">
      <form onSubmit={(event) => { event.preventDefault(); searchMaps() }}>
        <label><span>Find a place</span><input aria-label="Google Maps place or address" value={place} maxLength={512} placeholder="Place, business, or address" onChange={(event) => setPlace(event.target.value)} /></label>
        <button type="submit"><Search size={13} /> Search map</button>
      </form>
      <form onSubmit={(event) => { event.preventDefault(); openDirections() }}>
        <label><span>From <small>optional</small></span><input aria-label="Directions origin" value={origin} maxLength={512} placeholder="Use Google Maps default" onChange={(event) => setOrigin(event.target.value)} /></label>
        <label><span>To</span><input aria-label="Directions destination" value={destination} maxLength={512} placeholder="Destination" onChange={(event) => setDestination(event.target.value)} /></label>
        <label className="browser-maps-mode"><span>Travel</span><select aria-label="Travel mode" value={travelMode} onChange={(event) => setTravelMode(event.target.value as GoogleMapsTravelMode)}><option value="driving">Driving</option><option value="walking">Walking</option><option value="bicycling">Bicycling</option><option value="transit">Transit</option></select></label>
        <button type="submit"><Route size={13} /> Directions</button>
      </form>
      <p><MapPinned size={11} /> Results and directions are supplied by Google Maps. Hermes does not verify routes or traffic.</p>
    </div>}
  </section>
}

const newTab = (url = 'about:blank'): BrowserTab => ({ id: crypto.randomUUID(), url, title: 'New tab' })
const googleMapsTabUrl = 'https://www.google.com/maps/'
const googleMapsTabKey = 'google-maps'
let retainedTabs: BrowserTab[] | null = null
const retainedActiveIds: Record<BrowserWorkspaceMode, string | null> = { browser: null, maps: null }

export function BrowserWorkspace({ mode = 'browser', openRequest, onOpenRequestHandled }: { mode?: BrowserWorkspaceMode; openRequest?: BrowserOpenRequest | null; onOpenRequestHandled?: (nonce: number) => void }) {
  const [tabs, setTabs] = useState<BrowserTab[]>(() => retainedTabs ?? [newTab()])
  const [activeId, setActiveId] = useState(() => {
    const retainedActiveId = retainedActiveIds[mode]
    return retainedActiveId && tabs.some((tab) => tab.id === retainedActiveId) ? retainedActiveId : tabs[0].id
  })
  const [address, setAddress] = useState('')
  const [error, setError] = useState('')
  const [externalAuthentication, setExternalAuthentication] = useState<{ tabId: string; message: string } | null>(null)
  const [bookmarks, setBookmarks] = useState<BrowserBookmark[]>(loadBrowserBookmarks)
  const [bookmarkImportStatus, setBookmarkImportStatus] = useState('')
  const surfaceRef = useRef<HTMLDivElement>(null)
  const tabStripRef = useRef<HTMLDivElement>(null)
  const handledOpenRequest = useRef<number | null>(null)
  const activeIdRef = useRef(activeId)
  const bookmarksRef = useRef(bookmarks)
  const bookmarkImportRequest = useRef<string | null>(null)
  const bookmarkImportTimeout = useRef<number | null>(null)
  const active = tabs.find((tab) => tab.id === activeId) ?? tabs[0]

  useEffect(() => {
    if (mode !== 'maps') return
    const existing = tabs.find((tab) => tab.requestKey === googleMapsTabKey)
    if (existing) {
      setActiveId(existing.id)
      setAddress(existing.url)
      return
    }
    const mapsTab = { ...newTab(googleMapsTabUrl), title: 'Google Maps', requestKey: googleMapsTabKey }
    setTabs((current) => current.some((tab) => tab.requestKey === googleMapsTabKey) ? current : [...current, mapsTab])
    setActiveId(mapsTab.id)
    setAddress(mapsTab.url)
  }, [mode])

  useEffect(() => { activeIdRef.current = activeId }, [activeId])

  function openTab(rawUrl = 'about:blank', requestKey?: string, label?: string) {
    const url = tryNormalizeBrowserAddress(rawUrl)
    if (!url) { setError('That address could not be opened.'); return }
    if (requestKey) {
      const existing = tabs.find((tab) => tab.requestKey === requestKey)
      if (existing) {
        setActiveId(existing.id)
        setAddress(existing.url === 'about:blank' ? '' : existing.url)
        return
      }
    }
    const tab = { ...newTab(url), ...(requestKey ? { requestKey } : {}), ...(label ? { title: label.slice(0, 256) } : {}) }
    setTabs((current) => [...current, tab])
    setActiveId(tab.id)
    setAddress(url === 'about:blank' ? '' : url)
  }

  function openRequestedTab(request: { requestId: string; url: string }) {
    const tab = { ...newTab(request.url), pendingOpenRequestId: request.requestId }
    setTabs((current) => [...current, tab])
    setActiveId(tab.id)
    setAddress(request.url)
  }

  function closeTab(id: string) {
    desktopBrowserClient.closeTab(id)
    setTabs((current) => {
      if (current.length === 1) return [newTab()]
      const index = current.findIndex((tab) => tab.id === id)
      const next = current.filter((tab) => tab.id !== id)
      if (id === activeId) setActiveId((next[Math.max(0, index - 1)] ?? next[0]).id)
      return next
    })
  }

  useEffect(() => {
    retainedTabs = tabs
    retainedActiveIds[mode] = activeId
  }, [activeId, mode, tabs])

  useEffect(() => {
    bookmarksRef.current = bookmarks
    try {
      window.localStorage.setItem(browserBookmarksStorageKey, JSON.stringify(bookmarks))
    } catch {
      // A full or disabled browser store must not make browsing unavailable.
    }
  }, [bookmarks])

  useEffect(() => {
    const removeState = desktopBrowserClient.onState((state) => {
      // `state.url` is intentionally only a display-safe projection from the
      // native host.  Keep the tab's requested URL so an existing Google Maps
      // route (or any deep link) survives a resize, panel remount, or tab switch.
      setTabs((current) => current.map((tab) => tab.id === state.tabId ? { ...tab, title: state.title || tab.title, state } : tab))
      if (state.tabId === activeIdRef.current && state.url !== 'about:blank') {
        const requested = retainedTabs?.find((tab) => tab.id === state.tabId)?.url
        setAddress(requested && requested !== 'about:blank' ? requested : state.url)
      }
    })
    const removeOpen = desktopBrowserClient.onOpenRequested(openRequestedTab)
    const removeExternalAuthentication = desktopBrowserClient.onExternalAuthentication((request) => {
      if (request.tabId === activeIdRef.current) setExternalAuthentication(request)
    })
    const removeBookmarkSnapshot = desktopBrowserClient.onBookmarkSnapshot((snapshot) => {
      if (snapshot.requestId !== bookmarkImportRequest.current) return
      bookmarkImportRequest.current = null
      if (bookmarkImportTimeout.current !== null) window.clearTimeout(bookmarkImportTimeout.current)
      bookmarkImportTimeout.current = null
      if (snapshot.status !== 'available') {
        setBookmarkImportStatus(snapshot.message || 'Chrome bookmarks are unavailable.')
        return
      }
      const merged = mergeImportedBrowserBookmarks(bookmarksRef.current, snapshot.bookmarks)
      bookmarksRef.current = merged.bookmarks
      setBookmarks(merged.bookmarks)
      const capacity = merged.capacitySkippedCount ? ` ${merged.capacitySkippedCount} exceeded the 256-item bar limit.` : ''
      const truncated = snapshot.truncated ? ' Chrome supplied more bookmarks than the bounded importer can return.' : ''
      setBookmarkImportStatus(`Imported ${merged.importedCount}; kept ${merged.duplicateCount} existing duplicate${merged.duplicateCount === 1 ? '' : 's'}.${capacity}${truncated}`)
    })
    const removeError = desktopBrowserClient.onError(setError)
    return () => {
      removeState(); removeOpen(); removeExternalAuthentication(); removeBookmarkSnapshot(); removeError()
      if (bookmarkImportTimeout.current !== null) window.clearTimeout(bookmarkImportTimeout.current)
    }
  }, [])

  useEffect(() => {
    if (!openRequest || handledOpenRequest.current === openRequest.nonce) return
    handledOpenRequest.current = openRequest.nonce
    const url = tryNormalizeBrowserAddress(openRequest.url)
    if (!url) {
      setError('The requested browser destination was rejected.')
      onOpenRequestHandled?.(openRequest.nonce)
      return
    }
    const existingId = browserRequestTabId(tabs, openRequest)
    if (existingId) {
      setTabs((current) => current.map((tab) => tab.id === existingId ? { ...tab, url, title: openRequest.label?.trim().slice(0, 256) || tab.title } : tab))
      setActiveId(existingId)
      setAddress(url === 'about:blank' ? '' : url)
    } else {
      const tab = { ...newTab(url), title: openRequest.label?.trim().slice(0, 256) || 'New tab', requestKey: openRequest.key }
      setTabs((current) => [...current, tab])
      setActiveId(tab.id)
      setAddress(url === 'about:blank' ? '' : url)
    }
    if (openRequest.bookmark) {
      const nextBookmarks = applyBrowserBookmarkRequest(bookmarksRef.current, { ...openRequest, url })
      bookmarksRef.current = nextBookmarks
      setBookmarks(nextBookmarks)
    }
    onOpenRequestHandled?.(openRequest.nonce)
  }, [onOpenRequestHandled, openRequest, tabs])

  useEffect(() => {
    if (!active) return
    setAddress(active.url === 'about:blank' ? '' : active.url)
    if (active.pendingOpenRequestId) {
      desktopBrowserClient.acceptOpenRequest(active.pendingOpenRequestId, active.id)
      setTabs((current) => current.map((tab) => tab.id === active.id ? { ...tab, pendingOpenRequestId: undefined } : tab))
    }
  }, [activeId])

  useLayoutEffect(() => {
    const surface = surfaceRef.current
    if (!surface || !active) return
    let frame = 0
    const place = () => {
      frame = 0
      if (!surface.isConnected) return
      desktopBrowserClient.show(surface.getBoundingClientRect(), active.id, active.url)
    }
    const schedulePlace = () => {
      if (frame) window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(place)
    }
    schedulePlace()
    const observer = new ResizeObserver(schedulePlace)
    observer.observe(surface)
    window.addEventListener('resize', schedulePlace)
    return () => {
      if (frame) window.cancelAnimationFrame(frame)
      observer.disconnect()
      window.removeEventListener('resize', schedulePlace)
    }
  }, [activeId, active?.url])

  useEffect(() => () => desktopBrowserClient.hide(), [])

  useEffect(() => {
    const selected = tabStripRef.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')
    selected?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [activeId, tabs.length])

  function navigate() {
    const url = tryNormalizeBrowserAddress(address)
    if (!url) { setError('That address could not be opened.'); return }
    navigateTo(url)
  }

  function navigateTo(url: string) {
    setError('')
    setExternalAuthentication(null)
    setAddress(url === 'about:blank' ? '' : url)
    setTabs((current) => current.map((tab) => tab.id === activeId ? { ...tab, url } : tab))
    desktopBrowserClient.navigate(activeId, url)
  }

  function bookmarkCurrentPage() {
    if (!active) return
    const nextBookmarks = upsertBrowserBookmark(bookmarksRef.current, active.url, active.title)
    bookmarksRef.current = nextBookmarks
    setBookmarks(nextBookmarks)
  }

  function removeBookmark(id: string) {
    const nextBookmarks = bookmarksRef.current.filter((candidate) => candidate.id !== id)
    bookmarksRef.current = nextBookmarks
    setBookmarks(nextBookmarks)
  }

  function importChromeBookmarks() {
    if (bookmarkImportRequest.current) return
    const requestId = crypto.randomUUID()
    bookmarkImportRequest.current = requestId
    setBookmarkImportStatus('Reading Chrome bookmarks…')
    bookmarkImportTimeout.current = window.setTimeout(() => {
      if (bookmarkImportRequest.current !== requestId) return
      bookmarkImportRequest.current = null
      bookmarkImportTimeout.current = null
      setBookmarkImportStatus('Chrome did not answer the bookmark import request.')
    }, 15_000)
    desktopBrowserClient.importChromeBookmarks(requestId)
  }

  function retryActivePage() {
    if (!active) return
    setError('')
    setExternalAuthentication(null)
    desktopBrowserClient.reload(active.id)
  }

  if (!desktopBrowserClient.available) return <main className="browser-workspace unavailable"><Globe2 size={34} /><strong>Browser tabs require the desktop app</strong><p>External sites stay isolated from Workbench native capabilities.</p></main>

  return <main className={`browser-workspace ${mode === 'maps' ? 'maps-workspace' : ''}`}>
    {mode === 'browser' && <div className="browser-tabs" ref={tabStripRef} role="tablist" aria-label="Browser tabs">
      {tabs.map((tab) => <button className={tab.id === activeId ? 'active' : ''} role="tab" aria-selected={tab.id === activeId} key={tab.id} onClick={() => setActiveId(tab.id)}><Globe2 size={12} /><span>{tab.title || 'New tab'}</span>{tab.state?.loading && <LoaderCircle className="spin" size={11} />}<i role="button" aria-label={`Close ${tab.title || 'tab'}`} onClick={(event) => { event.stopPropagation(); closeTab(tab.id) }}><X size={11} /></i></button>)}
      <button className="browser-new-tab" aria-label="New browser tab" onClick={() => openTab()}><Plus size={14} /></button>
    </div>}
    {mode === 'browser' && <div className="browser-toolbar-region">
      <form className="browser-toolbar" onSubmit={(event) => { event.preventDefault(); navigate() }}>
        <button type="button" aria-label="Back" disabled={!active?.state?.canGoBack} onClick={() => desktopBrowserClient.back(activeId)}><ArrowLeft size={15} /></button>
        <button type="button" aria-label="Forward" disabled={!active?.state?.canGoForward} onClick={() => desktopBrowserClient.forward(activeId)}><ArrowRight size={15} /></button>
        <button type="button" aria-label={active?.state?.loading ? 'Stop' : 'Reload'} onClick={() => active?.state?.loading ? desktopBrowserClient.stop(activeId) : desktopBrowserClient.reload(activeId)}>{active?.state?.loading ? <X size={14} /> : <RotateCw size={14} />}</button>
        <label><Search size={14} /><input aria-label="Address or search" placeholder="Search or enter an address" value={address} onChange={(event) => setAddress(event.target.value)} /></label>
        {mode === 'browser' && <>
          <button type="button" aria-label="Bookmark this page" disabled={!active || !bookmarkableUrl(active.url)} onClick={bookmarkCurrentPage}><Star size={14} fill={bookmarks.some((bookmark) => bookmark.url === bookmarkableUrl(active?.url ?? '')) ? 'currentColor' : 'none'} /></button>
        </>}
      </form>
      {mode === 'browser' && <BrowserBookmarksBar bookmarks={bookmarks} importing={bookmarkImportRequest.current !== null} importStatus={bookmarkImportStatus} onImport={importChromeBookmarks} onOpen={navigateTo} onRemove={removeBookmark} />}
    </div>}
    {mode === 'maps' && <BrowserMapsPanel onNavigate={navigateTo} onError={setError} alwaysExpanded />}
    {error && <div className="browser-error" role="status">{error}<button aria-label="Dismiss browser error" onClick={() => setError('')}><X size={12} /></button></div>}
    <div className="browser-native-surface" ref={surfaceRef}>
      {externalAuthentication
        ? <section className="browser-recovery" aria-live="polite">
            <Globe2 size={34} />
            <strong>Complete Google sign-in in your browser</strong>
            <p>{externalAuthentication.message} Photon does not receive your Google password or browser session.</p>
            <div>
              <button type="button" onClick={() => navigateTo(active?.url ?? 'about:blank')}><ArrowLeft size={13} /> Return to Claude</button>
              <button type="button" onClick={() => setExternalAuthentication(null)}><X size={13} /> I’ll finish later</button>
            </div>
          </section>
        : error
        ? <section className="browser-recovery" aria-live="polite">
            <Globe2 size={34} />
            <strong>This page is unavailable</strong>
            <p>{error}</p>
            <div>
              <button type="button" onClick={retryActivePage}><RotateCw size={13} /> Retry page</button>
              <button type="button" onClick={() => { setError(''); setAddress('') }}><Search size={13} /> Enter another address</button>
            </div>
          </section>
        : active?.url === 'about:blank' && <div className="browser-blank"><Globe2 size={32} /><strong>Browse from the Workbench</strong><small>Sites are isolated from files, terminal, credentials, and agent bridges.</small></div>}
    </div>
  </main>
}
