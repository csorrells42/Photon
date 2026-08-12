import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, ChevronDown, ChevronUp, Globe2, LoaderCircle, MapPinned, Plus, RotateCw, Route, Search, X } from 'lucide-react'
import { desktopBrowserClient, tryNormalizeBrowserAddress, type BrowserSurfaceState } from './DesktopBrowserClient'
import './BrowserWorkspace.css'

export type BrowserTab = { id: string; url: string; title: string; state?: BrowserSurfaceState; pendingOpenRequestId?: string; requestKey?: string }
export type BrowserOpenRequest = { nonce: number; url: string; label?: string; key?: string }
export type GoogleMapsTravelMode = 'driving' | 'walking' | 'bicycling' | 'transit'
export const HERMES_HELP_BROWSER_REQUEST = Object.freeze({
  key: 'hermes-help',
  label: 'Hermes Help',
  url: 'https://hermes-agent.nousresearch.com/docs/',
})

const googleMapsTravelModes = new Set<GoogleMapsTravelMode>(['driving', 'walking', 'bicycling', 'transit'])

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

export function BrowserMapsPanel({ onNavigate, onError, initiallyExpanded = false }: { onNavigate: (url: string) => void; onError: (message: string) => void; initiallyExpanded?: boolean }) {
  const [expanded, setExpanded] = useState(initiallyExpanded)
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
    <button className="browser-maps-toggle" type="button" aria-expanded={expanded} onClick={() => setExpanded((value) => !value)}>
      <MapPinned size={14} /><span><strong>Google Maps</strong><small>Search places or plan a route in this browser</small></span>{expanded ? <ChevronUp size={13} /> : <ChevronDown size={13} />}
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
let retainedTabs: BrowserTab[] | null = null
let retainedActiveId: string | null = null

export function BrowserWorkspace({ openRequest, onOpenRequestHandled }: { openRequest?: BrowserOpenRequest | null; onOpenRequestHandled?: (nonce: number) => void }) {
  const [tabs, setTabs] = useState<BrowserTab[]>(() => retainedTabs ?? [newTab()])
  const [activeId, setActiveId] = useState(() => retainedActiveId && tabs.some((tab) => tab.id === retainedActiveId) ? retainedActiveId : tabs[0].id)
  const [address, setAddress] = useState('')
  const [error, setError] = useState('')
  const surfaceRef = useRef<HTMLDivElement>(null)
  const handledOpenRequest = useRef<number | null>(null)
  const active = tabs.find((tab) => tab.id === activeId) ?? tabs[0]

  function openTab(rawUrl = 'about:blank') {
    const url = tryNormalizeBrowserAddress(rawUrl)
    if (!url) { setError('That address is not a supported HTTP or HTTPS destination.'); return }
    const tab = newTab(url)
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
    retainedActiveId = activeId
  }, [activeId, tabs])

  useEffect(() => {
    const removeState = desktopBrowserClient.onState((state) => {
      setTabs((current) => current.map((tab) => tab.id === state.tabId ? { ...tab, url: state.url, title: state.title || tab.title, state } : tab))
      if (state.tabId === activeId && state.url !== 'about:blank') setAddress(state.url)
    })
    const removeOpen = desktopBrowserClient.onOpenRequested(openRequestedTab)
    const removeError = desktopBrowserClient.onError(setError)
    const openForPhoton = (event: Event) => openTab(String((event as CustomEvent<unknown>).detail ?? ''))
    window.addEventListener('photos-browser-open', openForPhoton)
    return () => { removeState(); removeOpen(); removeError(); window.removeEventListener('photos-browser-open', openForPhoton); desktopBrowserClient.hide() }
  }, [activeId])

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
      const existing = tabs.find((tab) => tab.id === existingId)!
      setActiveId(existingId)
      setAddress(existing.url === 'about:blank' ? '' : existing.url)
    } else {
      const tab = { ...newTab(url), title: openRequest.label?.trim().slice(0, 256) || 'New tab', requestKey: openRequest.key }
      setTabs((current) => [...current, tab])
      setActiveId(tab.id)
      setAddress(url === 'about:blank' ? '' : url)
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

  useEffect(() => {
    const surface = surfaceRef.current
    if (!surface || !active) return
    const place = () => desktopBrowserClient.show(surface.getBoundingClientRect(), active.id, active.url)
    place()
    const observer = new ResizeObserver(place)
    observer.observe(surface)
    window.addEventListener('resize', place)
    return () => { observer.disconnect(); window.removeEventListener('resize', place) }
  }, [activeId, active?.url])

  function navigate() {
    const url = tryNormalizeBrowserAddress(address)
    if (!url) { setError('That address is not a supported HTTP or HTTPS destination.'); return }
    navigateTo(url)
  }

  function navigateTo(url: string) {
    setError('')
    setAddress(url === 'about:blank' ? '' : url)
    setTabs((current) => current.map((tab) => tab.id === activeId ? { ...tab, url } : tab))
    desktopBrowserClient.navigate(activeId, url)
  }

  if (!desktopBrowserClient.available) return <main className="browser-workspace unavailable"><Globe2 size={34} /><strong>Browser tabs require the desktop app</strong><p>External sites stay isolated from Workbench native capabilities.</p></main>

  return <main className="browser-workspace">
    <div className="browser-tabs" role="tablist" aria-label="Browser tabs">
      {tabs.map((tab) => <button className={tab.id === activeId ? 'active' : ''} role="tab" aria-selected={tab.id === activeId} key={tab.id} onClick={() => setActiveId(tab.id)}><Globe2 size={12} /><span>{tab.title || 'New tab'}</span>{tab.state?.loading && <LoaderCircle className="spin" size={11} />}<i role="button" aria-label={`Close ${tab.title || 'tab'}`} onClick={(event) => { event.stopPropagation(); closeTab(tab.id) }}><X size={11} /></i></button>)}
      <button className="browser-new-tab" aria-label="New browser tab" onClick={() => openTab()}><Plus size={14} /></button>
    </div>
    <form className="browser-toolbar" onSubmit={(event) => { event.preventDefault(); navigate() }}>
      <button type="button" aria-label="Back" disabled={!active?.state?.canGoBack} onClick={() => desktopBrowserClient.back(activeId)}><ArrowLeft size={15} /></button>
      <button type="button" aria-label="Forward" disabled={!active?.state?.canGoForward} onClick={() => desktopBrowserClient.forward(activeId)}><ArrowRight size={15} /></button>
      <button type="button" aria-label={active?.state?.loading ? 'Stop' : 'Reload'} onClick={() => active?.state?.loading ? desktopBrowserClient.stop(activeId) : desktopBrowserClient.reload(activeId)}>{active?.state?.loading ? <X size={14} /> : <RotateCw size={14} />}</button>
      <label><Search size={14} /><input aria-label="Address or search" placeholder="Search or enter an address" value={address} onChange={(event) => setAddress(event.target.value)} /></label>
    </form>
    <BrowserMapsPanel onNavigate={navigateTo} onError={setError} />
    {error && <div className="browser-error" role="status">{error}<button onClick={() => setError('')}><X size={12} /></button></div>}
    <div className="browser-native-surface" ref={surfaceRef}>{active?.url === 'about:blank' && <div className="browser-blank"><Globe2 size={32} /><strong>Browse from the Workbench</strong><small>Sites are isolated from files, terminal, credentials, and agent bridges.</small></div>}</div>
  </main>
}
