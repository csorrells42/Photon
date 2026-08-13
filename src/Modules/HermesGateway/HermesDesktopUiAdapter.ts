import type { HermesGatewayEvent } from './HermesGatewayClient'

export type HermesDesktopUiAction =
  | { kind: 'open-preview'; url: string; label: string; bookmark?: true }
  | { kind: 'open-workspace-file'; path: string; label: string }
  | { kind: 'reveal-pane'; pane: 'chat' | 'files' | 'terminal' | 'review' | 'sessions' }

type RevealPane = Extract<HermesDesktopUiAction, { kind: 'reveal-pane' }>['pane']
const panes = new Set<RevealPane>(['chat', 'files', 'terminal', 'review', 'sessions'])
const sensitiveQueryNames = new Set([
  'accesstoken', 'apikey', 'auth', 'authtoken', 'bearer', 'code', 'credential', 'jwt', 'key',
  'clientsecret', 'idtoken', 'nonce', 'password', 'privatekey', 'refreshtoken', 'secret', 'session',
  'sessionid', 'sig', 'signature', 'ticket', 'token',
])
const blockedWorkspaceLeaf = /^(?:\.env(?:\..*)?|auth(?:\.json)?|credentials(?:\.json)?|secrets(?:\.json)?|.*\.(?:key|pem|pfx|p12|ppk))$/iu

function boundedText(value: unknown, maximum: number) {
  if (typeof value !== 'string') return ''
  const text = value.normalize('NFKC').replace(/[\u0000-\u001f\u007f]/gu, '').trim()
  return text.length <= maximum ? text : ''
}

export function normalizeHermesPreviewUrl(value: unknown): string | null {
  const text = boundedText(value, 4_096)
  if (!text) return null
  let parsed: URL
  try { parsed = new URL(text) } catch { return null }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') return null
  if (parsed.username || parsed.password) return null
  for (const key of parsed.searchParams.keys()) {
    const canonical = key.normalize('NFKC').toLocaleLowerCase().replace(/[^a-z0-9]/gu, '')
    if (sensitiveQueryNames.has(canonical)) return null
  }
  parsed.hash = ''
  return parsed.href
}

export function normalizeHermesWorkspacePreviewPath(value: unknown): string | null {
  let text = boundedText(value, 2_048)
  if (!text) return null
  if (text.startsWith('file:')) {
    try {
      const parsed = new URL(text)
      if (parsed.protocol !== 'file:' || parsed.host) return null
      text = decodeURIComponent(parsed.pathname)
    } catch { return null }
  }
  text = text.replace(/\\/gu, '/')
  if (text === '/workspace') return null
  if (text.startsWith('/workspace/')) text = text.slice('/workspace/'.length)
  else if (text.startsWith('./')) text = text.slice(2)
  else if (text.startsWith('/')) return null
  const segments = text.split('/')
  if (!segments.length || segments.some((segment) => !segment || segment === '.' || segment === '..' || segment.includes(':'))) return null
  if (segments.some((segment) => blockedWorkspaceLeaf.test(segment))) return null
  return segments.join('/')
}

export function shouldAcceptHermesDesktopUiAction(sessionOpening: boolean, activeSessionId: string | null, eventSessionId?: string) {
  return !sessionOpening && Boolean(activeSessionId) && eventSessionId === activeSessionId
}

export function normalizeHermesDesktopUiAction(event: HermesGatewayEvent): HermesDesktopUiAction | null {
  if (event.type === 'preview.open') {
    const url = normalizeHermesPreviewUrl(event.payload?.url)
    const label = boundedText(event.payload?.label, 256)
    if (url) return { kind: 'open-preview', url, label, ...(event.payload?.bookmark === true ? { bookmark: true as const } : {}) }
    const path = normalizeHermesWorkspacePreviewPath(event.payload?.url)
    return path ? { kind: 'open-workspace-file', path, label } : null
  }
  if (event.type === 'pane.reveal') {
    const pane = boundedText(event.payload?.pane, 32) as RevealPane
    return panes.has(pane) ? { kind: 'reveal-pane', pane } : null
  }
  return null
}
