export const desktopCredentialProtocolVersion = 1

export type DesktopCredentialMetadata = {
  provider: string
  credentialId: string
  configured: true
  updatedAt?: string
}

export type DesktopCredentialListResult =
  | { kind: 'success'; entries: DesktopCredentialMetadata[] }
  | { kind: 'failure'; message: string }
  | { kind: 'unavailable' }

export type DesktopCredentialFrame = {
  type?: string
  version?: number
  entries?: unknown
  entry?: unknown
  provider?: unknown
  credentialId?: unknown
  removed?: unknown
  message?: unknown
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type DesktopWindow = Window & {
  __HERMES_DESKTOP_HOST__?: { capabilities?: { credentials?: boolean; credentialsVersion?: number } }
  chrome?: { webview?: WebViewBridge }
}

function validIdentifier(value: unknown, maximum: number): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= maximum && /^[a-z0-9._-]+$/i.test(value)
}

export function credentialIdFromLabel(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^[._-]+|[._-]+$/g, '')
    .slice(0, 128)
}

export function credentialDisplayName(credentialId: string) {
  return credentialId
    .split(/[._-]+/)
    .filter(Boolean)
    .map((part) => `${part[0]?.toUpperCase() ?? ''}${part.slice(1)}`)
    .join(' ') || credentialId
}

export function normalizeCredentialMetadata(value: unknown): DesktopCredentialMetadata | null {
  if (!value || typeof value !== 'object') return null
  const raw = value as Record<string, unknown>
  if (!validIdentifier(raw.provider, 64) || !validIdentifier(raw.credentialId, 128) || raw.configured !== true) return null
  const updatedAt = typeof raw.updatedAt === 'string' && raw.updatedAt.length <= 64 && !Number.isNaN(Date.parse(raw.updatedAt))
    ? raw.updatedAt
    : undefined
  return { provider: raw.provider, credentialId: raw.credentialId, configured: true, ...(updatedAt ? { updatedAt } : {}) }
}

export function desktopCredentialWebView(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  const desktop = window as DesktopWindow
  const capability = desktop.__HERMES_DESKTOP_HOST__?.capabilities
  if (capability?.credentials !== true || capability.credentialsVersion !== desktopCredentialProtocolVersion) return null
  return desktop.chrome?.webview ?? null
}

export function requestCredentialList() {
  const host = desktopCredentialWebView()
  if (!host) return false
  host.postMessage({ type: 'credentials.list', version: desktopCredentialProtocolVersion })
  return true
}

export function listDesktopCredentials(signal?: AbortSignal): Promise<DesktopCredentialListResult> {
  const host = desktopCredentialWebView()
  if (!host) return Promise.resolve({ kind: 'unavailable' })
  if (signal?.aborted) return Promise.resolve({ kind: 'failure', message: 'Credential metadata collection was cancelled.' })

  return new Promise((resolve) => {
    let settled = false
    const finish = (result: DesktopCredentialListResult) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      signal?.removeEventListener('abort', abort)
      host.removeEventListener('message', receive)
      resolve(result)
    }
    const abort = () => finish({ kind: 'failure', message: 'Credential metadata collection was cancelled.' })
    const receive = (event: MessageEvent) => {
      const frame = event.data as DesktopCredentialFrame
      if (frame?.version !== desktopCredentialProtocolVersion || typeof frame.type !== 'string') return
      if (frame.type === 'credentials.list.result') {
        const entries = Array.isArray(frame.entries)
          ? frame.entries.map(normalizeCredentialMetadata).filter((entry): entry is DesktopCredentialMetadata => entry !== null)
          : []
        finish({ kind: 'success', entries })
      } else if (frame.type === 'credentials.error') {
        finish({ kind: 'failure', message: typeof frame.message === 'string' ? frame.message : 'The native credential vault needs attention.' })
      }
    }
    const timeout = window.setTimeout(() => finish({ kind: 'failure', message: 'The native credential vault timed out.' }), 8_000)
    signal?.addEventListener('abort', abort, { once: true })
    host.addEventListener('message', receive)
    try {
      host.postMessage({ type: 'credentials.list', version: desktopCredentialProtocolVersion })
    } catch {
      finish({ kind: 'failure', message: 'The desktop credential bridge is unavailable.' })
    }
  })
}

export function openCredentialDialog(provider: string, credentialId = 'primary') {
  const host = desktopCredentialWebView()
  if (!host || !validIdentifier(provider, 64) || !validIdentifier(credentialId, 128)) return false
  host.postMessage({ type: 'credentials.open', version: desktopCredentialProtocolVersion, provider, credentialId })
  return true
}

export function deleteCredential(provider: string, credentialId = 'primary') {
  const host = desktopCredentialWebView()
  if (!host || !validIdentifier(provider, 64) || !validIdentifier(credentialId, 128)) return false
  host.postMessage({ type: 'credentials.delete', version: desktopCredentialProtocolVersion, provider, credentialId })
  return true
}
