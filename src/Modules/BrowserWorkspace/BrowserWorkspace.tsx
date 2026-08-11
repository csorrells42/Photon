import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, Globe2, LoaderCircle, Plus, RotateCw, Search, X } from 'lucide-react'
import { desktopBrowserClient, normalizeBrowserAddress, type BrowserSurfaceState } from './DesktopBrowserClient'
import './BrowserWorkspace.css'

type BrowserTab = { id: string; url: string; title: string; state?: BrowserSurfaceState; pendingOpenRequestId?: string }
export type BrowserOpenRequest = { nonce: number; url: string; label?: string }
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
    const url = normalizeBrowserAddress(rawUrl)
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
    const url = normalizeBrowserAddress(openRequest.url)
    const tab = { ...newTab(url), title: openRequest.label?.trim().slice(0, 256) || 'New tab' }
    setTabs((current) => [...current, tab])
    setActiveId(tab.id)
    setAddress(url === 'about:blank' ? '' : url)
    onOpenRequestHandled?.(openRequest.nonce)
  }, [onOpenRequestHandled, openRequest])

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
    const url = normalizeBrowserAddress(address)
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
    {error && <div className="browser-error" role="status">{error}<button onClick={() => setError('')}><X size={12} /></button></div>}
    <div className="browser-native-surface" ref={surfaceRef}>{active?.url === 'about:blank' && <div className="browser-blank"><Globe2 size={32} /><strong>Browse from the Workbench</strong><small>Sites are isolated from files, terminal, credentials, and agent bridges.</small></div>}</div>
  </main>
}
