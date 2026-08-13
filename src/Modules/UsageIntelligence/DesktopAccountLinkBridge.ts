export const desktopAccountLinkProtocolVersion = 1

export type LocalAccountLinkProvider = 'claude' | 'antigravity' | 'google-cloud'

export type LocalAccountLinkStatus = {
  provider: LocalAccountLinkProvider
  installed: boolean
  linked: boolean
  trackingAvailable: boolean
  status: string
}

export type LocalAccountLinkOpenResult = {
  opened: boolean
  provider: LocalAccountLinkProvider
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

function host() {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function requestId(prefix: string) {
  return `${prefix}:${Date.now().toString(36)}:${Math.random().toString(36).slice(2, 9)}`
}

function normalizeStatus(value: unknown): LocalAccountLinkStatus | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  if (raw.provider !== 'claude' && raw.provider !== 'antigravity' && raw.provider !== 'google-cloud') return null
  return {
    provider: raw.provider,
    installed: raw.installed === true,
    linked: raw.linked === true,
    trackingAvailable: raw.trackingAvailable === true,
    status: typeof raw.status === 'string' ? raw.status.slice(0, 1_024) : 'Account status unavailable.',
  }
}

export function requestLocalAccountLinkStatus(provider?: LocalAccountLinkProvider): Promise<LocalAccountLinkStatus[]> {
  const bridge = host()
  if (!bridge) return Promise.resolve([])
  const id = requestId('account-status')
  return new Promise((resolve) => {
    let finished = false
    const finish = (value: LocalAccountLinkStatus[]) => {
      if (finished) return
      finished = true
      window.clearTimeout(timeout)
      bridge.removeEventListener('message', receive)
      resolve(value)
    }
    const receive = (event: MessageEvent) => {
      const raw = event.data as Record<string, unknown> | null
      if (!raw || raw.version !== desktopAccountLinkProtocolVersion || raw.requestId !== id) return
      if (raw.type === 'accountLink.status.result') {
        finish(Array.isArray(raw.accounts) ? raw.accounts.map(normalizeStatus).filter((entry): entry is LocalAccountLinkStatus => entry !== null) : [])
      } else if (raw.type === 'accountLink.error') finish([])
    }
    const timeout = window.setTimeout(() => finish([]), 8_000)
    bridge.addEventListener('message', receive)
    bridge.postMessage({ type: 'accountLink.status', version: desktopAccountLinkProtocolVersion, requestId: id, provider })
  })
}

export function openLocalAccountLink(provider: LocalAccountLinkStatus['provider']): Promise<LocalAccountLinkOpenResult> {
  const bridge = host()
  if (!bridge) return Promise.resolve({ provider, opened: false })
  const id = requestId('account-open')
  return new Promise((resolve) => {
    let finished = false
    const finish = (opened: boolean) => {
      if (finished) return
      finished = true
      window.clearTimeout(timeout)
      bridge.removeEventListener('message', receive)
      resolve({ provider, opened })
    }
    const receive = (event: MessageEvent) => {
      const raw = event.data as Record<string, unknown> | null
      if (!raw || raw.version !== desktopAccountLinkProtocolVersion || raw.requestId !== id) return
      if (raw.type === 'accountLink.open.result') finish(raw.opened === true)
      else if (raw.type === 'accountLink.error') finish(false)
    }
    const timeout = window.setTimeout(() => finish(false), 8_000)
    bridge.addEventListener('message', receive)
    bridge.postMessage({
      type: 'accountLink.open',
      version: desktopAccountLinkProtocolVersion,
      requestId: id,
      provider,
    })
  })
}

export function accountLinkHostAvailable() { return host() !== null }
