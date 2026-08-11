export const HERMES_TOOLSETS_ADAPTER_VERSION = 1

const MAX_TOOLSETS = 128
const MAX_PROVIDERS = 64
const MAX_ENVIRONMENT = 32
const MAX_TEXT = 8_192

export type HermesToolset = {
  name: string
  label: string
  description: string
  platform: string
  platformLabel: string
  enabled: boolean
  available: boolean
  configured: boolean
  tools: string[]
}

export type HermesToolsetEnvironment = {
  key: string
  prompt: string
  helpUrl: string | null
  defaultHint: string | null
  isSet: boolean
}

export type HermesToolsetProvider = {
  name: string
  badge: string
  tag: string
  environment: HermesToolsetEnvironment[]
  postSetup: string | null
  requiresNousAuth: boolean
  active: boolean
  status: string
  webBackend: string | null
  capabilities: string[]
  ttsProvider: string | null
}

export type HermesToolsetConfig = {
  name: string
  hasCategory: boolean
  providers: HermesToolsetProvider[]
  activeProvider: string | null
  activeSearchBackend: string | null
  activeExtractBackend: string | null
}

export type HermesToolsetAction = {
  ok: boolean
  name: string
  processId: number | null
  key: string | null
}

export type HermesToolsetActionStatus = {
  name: string
  processId: number | null
  running: boolean
  exitCode: number | null
  lines: string[]
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function text(value: unknown, limit = MAX_TEXT) {
  return typeof value === 'string' ? value.trim().slice(0, limit) : ''
}

function finite(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function list(value: unknown, limit = 64, itemLimit = 512) {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const normalized = text(item, itemLimit)
    return normalized ? [normalized] : []
  }).slice(0, limit)
}

function safeHelpUrl(value: unknown) {
  const raw = text(value, 2_048)
  if (!raw) return null
  try {
    const url = new URL(raw)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.toString() : null
  } catch { return null }
}

function profileQuery(profile?: string) {
  const value = text(profile, 128)
  return value ? `?profile=${encodeURIComponent(value)}` : ''
}

function profileValue(profile?: string) {
  return text(profile, 128) || undefined
}

export function normalizeHermesToolsets(value: unknown): HermesToolset[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const row = object(item)
    const name = text(row.name, 128)
    if (!name) return []
    return [{
      name,
      label: text(row.label, 256) || name,
      description: text(row.description, 2_048),
      platform: text(row.platform, 128) || 'unknown',
      platformLabel: text(row.platform_label, 256) || text(row.platform, 128) || 'Unknown',
      enabled: row.enabled === true,
      available: row.available !== false,
      configured: row.configured === true,
      tools: list(row.tools, 256),
    }]
  }).slice(0, MAX_TOOLSETS)
}

function normalizeEnvironment(value: unknown): HermesToolsetEnvironment[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const row = object(item)
    const key = text(row.key, 128)
    if (!/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(key)) return []
    return [{
      key,
      prompt: text(row.prompt, 512) || key,
      helpUrl: safeHelpUrl(row.url),
      defaultHint: text(row.default, 256) || null,
      isSet: row.is_set === true,
    }]
  }).slice(0, MAX_ENVIRONMENT)
}

export function normalizeHermesToolsetConfig(value: unknown): HermesToolsetConfig | null {
  const raw = object(value)
  const name = text(raw.name, 128)
  if (!name) return null
  const providers = Array.isArray(raw.providers) ? raw.providers : []
  return {
    name,
    hasCategory: raw.has_category === true,
    providers: providers.flatMap((item) => {
      const row = object(item)
      const providerName = text(row.name, 128)
      if (!providerName) return []
      return [{
        name: providerName,
        badge: text(row.badge, 128),
        tag: text(row.tag, 512),
        environment: normalizeEnvironment(row.env_vars),
        postSetup: text(row.post_setup, 128) || null,
        requiresNousAuth: row.requires_nous_auth === true,
        active: row.is_active === true,
        status: text(row.status, 128) || 'unknown',
        webBackend: text(row.web_backend, 128) || null,
        capabilities: list(row.capabilities, 16, 64),
        ttsProvider: text(row.tts_provider, 128) || null,
      }]
    }).slice(0, MAX_PROVIDERS),
    activeProvider: text(raw.active_provider, 128) || null,
    activeSearchBackend: text(raw.active_search_backend, 128) || null,
    activeExtractBackend: text(raw.active_extract_backend, 128) || null,
  }
}

export function normalizeHermesToolsetAction(value: unknown): HermesToolsetAction {
  const raw = object(value)
  return {
    ok: raw.ok === true,
    name: text(raw.name, 128),
    processId: finite(raw.pid),
    key: text(raw.key, 128) || null,
  }
}

export function normalizeHermesToolsetActionStatus(value: unknown): HermesToolsetActionStatus {
  const raw = object(value)
  return {
    name: text(raw.name, 128),
    processId: finite(raw.pid),
    running: raw.running === true,
    exitCode: finite(raw.exit_code),
    lines: list(raw.lines, 300, 2_048),
  }
}

async function readJson(response: Response) {
  const value = await response.json().catch(() => ({}))
  if (response.ok) return value
  const raw = object(value)
  let message = text(raw.detail, 2_048) || text(raw.error, 2_048) || `Hermes request failed (${response.status}).`
  throw new Error(message)
}

export class HermesToolsetsAdapter {
  constructor(private readonly request: FetchLike = (input, init) => fetch(input, init)) {}

  async list(profile?: string) {
    return normalizeHermesToolsets(await readJson(await this.request(`/api/tools/toolsets${profileQuery(profile)}`, { credentials: 'include' })))
  }

  async config(name: string, profile?: string) {
    const value = await readJson(await this.request(`/api/tools/toolsets/${encodeURIComponent(text(name, 128))}/config${profileQuery(profile)}`, { credentials: 'include' }))
    const normalized = normalizeHermesToolsetConfig(value)
    if (!normalized) throw new Error('Hermes returned an invalid toolset configuration.')
    return normalized
  }

  async toggle(name: string, enabled: boolean, profile?: string) {
    return readJson(await this.request(`/api/tools/toolsets/${encodeURIComponent(text(name, 128))}`, {
      method: 'PUT', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled, profile: profileValue(profile) }),
    }))
  }

  async selectProvider(name: string, provider: string, capability?: 'search' | 'extract', profile?: string) {
    return readJson(await this.request(`/api/tools/toolsets/${encodeURIComponent(text(name, 128))}/provider`, {
      method: 'PUT', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ provider: text(provider, 128), capability, profile: profileValue(profile) }),
    }))
  }

  async runPostSetup(name: string, key: string, allowedKeys: string[], profile?: string) {
    const normalizedKey = text(key, 128)
    if (!allowedKeys.includes(normalizedKey)) throw new Error('The requested setup hook is not present in the current provider contract.')
    return normalizeHermesToolsetAction(await readJson(await this.request(`/api/tools/toolsets/${encodeURIComponent(text(name, 128))}/post-setup`, {
      method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key: normalizedKey, profile: profileValue(profile) }),
    })))
  }

  async postSetupStatus() {
    return normalizeHermesToolsetActionStatus(await readJson(await this.request('/api/actions/tools-post-setup/status?lines=300', { credentials: 'include' })))
  }
}

export const hermesToolsetsAdapter = new HermesToolsetsAdapter()
