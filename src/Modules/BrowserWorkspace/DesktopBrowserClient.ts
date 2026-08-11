export const BROWSER_SURFACE_PROTOCOL_VERSION = 1

export type BrowserSurfaceState = {
  tabId: string
  url: string
  title: string
  canGoBack: boolean
  canGoForward: boolean
  loading: boolean
}

type Bridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

function bridge() { return (window as Window & { chrome?: { webview?: Bridge } }).chrome?.webview ?? null }
function bounded(value: unknown, maximum: number) { return typeof value === 'string' ? value.slice(0, maximum) : '' }

export function normalizeBrowserAddress(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) return 'about:blank'
  try {
    const explicit = new URL(trimmed)
    if (explicit.protocol === 'http:' || explicit.protocol === 'https:') return explicit.href
  } catch { /* Treat it as a hostname or search below. */ }
  if (/^[\w.-]+\.[a-z]{2,}(?:[/:?#].*)?$/i.test(trimmed)) return new URL(`https://${trimmed}`).href
  return `https://www.google.com/search?q=${encodeURIComponent(trimmed)}`
}

export class DesktopBrowserClient {
  private connected: Bridge | null = null
  private readonly listeners = new Set<(state: BrowserSurfaceState) => void>()
  private readonly openListeners = new Set<(request: { requestId: string; url: string }) => void>()
  private readonly errorListeners = new Set<(message: string) => void>()
  private readonly receive = (event: MessageEvent) => {
    const raw = event.data as Record<string, unknown> | null
    if (!raw || raw.version !== BROWSER_SURFACE_PROTOCOL_VERSION) return
    if (raw.type === 'browser.state') {
      const state: BrowserSurfaceState = {
        tabId: bounded(raw.tabId, 128), url: bounded(raw.url, 4_096), title: bounded(raw.title, 256),
        canGoBack: raw.canGoBack === true, canGoForward: raw.canGoForward === true, loading: raw.loading === true,
      }
      this.listeners.forEach((listener) => listener(state))
    } else if (raw.type === 'browser.openRequested') {
      const url = bounded(raw.url, 4_096)
      const requestId = bounded(raw.requestId, 128)
      if (url && requestId) this.openListeners.forEach((listener) => listener({ requestId, url }))
    } else if (raw.type === 'browser.error') {
      const message = bounded(raw.message, 1_024) || 'The embedded browser reported an error.'
      this.errorListeners.forEach((listener) => listener(message))
    }
  }

  get available() { return bridge() !== null }
  onState(listener: (state: BrowserSurfaceState) => void) { this.ensure(); this.listeners.add(listener); return () => this.listeners.delete(listener) }
  onOpenRequested(listener: (request: { requestId: string; url: string }) => void) { this.ensure(); this.openListeners.add(listener); return () => this.openListeners.delete(listener) }
  onError(listener: (message: string) => void) { this.ensure(); this.errorListeners.add(listener); return () => this.errorListeners.delete(listener) }
  show(rect: DOMRect, tabId: string, initialUrl: string) { this.post('browser.surface.show', { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height), tabId, url: normalizeBrowserAddress(initialUrl) }) }
  hide() { this.post('browser.surface.hide') }
  closeTab(tabId: string) { this.post('browser.tab.close', { tabId }) }
  navigate(tabId: string, url: string) { this.post('browser.navigate', { tabId, url: normalizeBrowserAddress(url) }) }
  acceptOpenRequest(requestId: string, tabId: string) { this.post('browser.openRequested.accept', { requestId, tabId }) }
  back(tabId: string) { this.post('browser.back', { tabId }) }
  forward(tabId: string) { this.post('browser.forward', { tabId }) }
  reload(tabId: string) { this.post('browser.reload', { tabId }) }
  stop(tabId: string) { this.post('browser.stop', { tabId }) }

  private ensure() {
    const host = bridge()
    if (host === this.connected) return
    this.connected?.removeEventListener('message', this.receive)
    this.connected = host
    host?.addEventListener('message', this.receive)
  }
  private post(type: string, fields: Record<string, unknown> = {}) { this.ensure(); this.connected?.postMessage({ type, version: BROWSER_SURFACE_PROTOCOL_VERSION, ...fields }) }
}

export const desktopBrowserClient = new DesktopBrowserClient()
