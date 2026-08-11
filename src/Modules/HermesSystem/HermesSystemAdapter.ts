export const HERMES_SYSTEM_ADAPTER_VERSION = 1

export type HermesAuthProvider = {
  name: string
  displayName: string
  supportsPassword: boolean
}

export type HermesIdentity = {
  userId: string
  email: string
  displayName: string
  organizationId: string
  provider: string
  expiresAt: number
}

export type HermesComponentStatus = {
  name: string
  status: 'ok' | 'degraded' | 'unknown'
  state?: string
}

export type HermesStatusSnapshot = {
  version: string
  releaseDate: string
  overall: 'ok' | 'degraded' | 'unknown'
  activeSessions: number
  gateway: {
    running: boolean
    state: string
    busy: boolean
    drainable: boolean
    activeAgents: number
  }
  auth: {
    required: boolean
    providers: string[]
    flows: string[]
  }
  components: HermesComponentStatus[]
}

export type HermesSystemStats = {
  operatingSystem: string
  platform: string
  architecture: string
  hostname: string
  pythonVersion: string
  hermesVersion: string
  cpuCount?: number
  cpuPercent?: number
  uptimeSeconds?: number
  memory?: { total: number; used: number; available: number; percent: number }
  disk?: { total: number; used: number; free: number; percent: number }
  process?: { pid: number; rss: number; threads: number }
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
type JsonObject = Record<string, unknown>

export class HermesSystemHttpError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'HermesSystemHttpError'
  }
}

function object(value: unknown): JsonObject {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function text(value: unknown, fallback = '') {
  return typeof value === 'string' ? value : fallback
}

function number(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function boolean(value: unknown, fallback = false) {
  return typeof value === 'boolean' ? value : fallback
}

function stringList(value: unknown) {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0) : []
}

function health(value: unknown): 'ok' | 'degraded' | 'unknown' {
  return value === 'ok' ? 'ok' : value === 'degraded' ? 'degraded' : 'unknown'
}

export function normalizeHermesStatus(value: unknown): HermesStatusSnapshot {
  const raw = object(value)
  const rawGatewayState = text(raw.gateway_state, boolean(raw.gateway_running) ? 'running' : 'stopped')
  const components = Object.entries(object(raw.components)).map(([name, component]) => {
    const detail = object(component)
    return { name, status: health(detail.status), state: text(detail.state) || undefined }
  })

  return {
    version: text(raw.version, 'unknown'),
    releaseDate: text(raw.release_date),
    overall: health(raw.overall),
    activeSessions: Math.max(0, number(raw.active_sessions)),
    gateway: {
      running: boolean(raw.gateway_running),
      state: rawGatewayState,
      busy: boolean(raw.gateway_busy),
      drainable: boolean(raw.gateway_drainable),
      activeAgents: Math.max(0, number(raw.active_agents)),
    },
    auth: {
      required: boolean(raw.auth_required),
      providers: stringList(raw.auth_providers),
      flows: stringList(raw.auth_flows),
    },
    components,
  }
}

export function normalizeHermesProviders(value: unknown): HermesAuthProvider[] {
  const providers = object(value).providers
  if (!Array.isArray(providers)) return []
  return providers.flatMap((provider) => {
    const raw = object(provider)
    const name = text(raw.name).trim()
    if (!name) return []
    return [{
      name,
      displayName: text(raw.display_name, name).trim() || name,
      supportsPassword: boolean(raw.supports_password),
    }]
  })
}

export function normalizeHermesIdentity(value: unknown): HermesIdentity {
  const raw = object(value)
  return {
    userId: text(raw.user_id),
    email: text(raw.email),
    displayName: text(raw.display_name),
    organizationId: text(raw.org_id),
    provider: text(raw.provider),
    expiresAt: number(raw.expires_at),
  }
}

export function normalizeHermesSystemStats(value: unknown): HermesSystemStats {
  const raw = object(value)
  const memory = object(raw.memory)
  const disk = object(raw.disk)
  const process = object(raw.process)
  return {
    operatingSystem: [text(raw.os), text(raw.os_release)].filter(Boolean).join(' ') || 'Unknown OS',
    platform: text(raw.platform),
    architecture: text(raw.arch),
    hostname: text(raw.hostname),
    pythonVersion: text(raw.python_version),
    hermesVersion: text(raw.hermes_version),
    cpuCount: typeof raw.cpu_count === 'number' ? number(raw.cpu_count) : undefined,
    cpuPercent: typeof raw.cpu_percent === 'number' ? number(raw.cpu_percent) : undefined,
    uptimeSeconds: typeof raw.uptime_seconds === 'number' ? number(raw.uptime_seconds) : undefined,
    memory: Object.keys(memory).length ? {
      total: number(memory.total), used: number(memory.used), available: number(memory.available), percent: number(memory.percent),
    } : undefined,
    disk: Object.keys(disk).length ? {
      total: number(disk.total), used: number(disk.used), free: number(disk.free), percent: number(disk.percent),
    } : undefined,
    process: Object.keys(process).length ? {
      pid: number(process.pid), rss: number(process.rss), threads: number(process.num_threads),
    } : undefined,
  }
}

async function errorMessage(response: Response) {
  try {
    const body = object(await response.clone().json())
    return text(body.detail) || text(body.error) || `Hermes returned HTTP ${response.status}.`
  } catch {
    const body = await response.text().catch(() => '')
    return body.trim() || `Hermes returned HTTP ${response.status}.`
  }
}

export class HermesSystemAdapter {
  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  private async json(path: string, init?: RequestInit) {
    const response = await this.fetcher(path, { credentials: 'include', ...init })
    if (!response.ok) throw new HermesSystemHttpError(response.status, await errorMessage(response))
    return response.json() as Promise<unknown>
  }

  async status() {
    return normalizeHermesStatus(await this.json('/api/status'))
  }

  async providers() {
    return normalizeHermesProviders(await this.json('/api/auth/providers'))
  }

  async identity(): Promise<HermesIdentity | null> {
    try {
      return normalizeHermesIdentity(await this.json('/api/auth/me'))
    } catch (reason) {
      if (reason instanceof HermesSystemHttpError && reason.status === 401) return null
      throw reason
    }
  }

  async stats() {
    return normalizeHermesSystemStats(await this.json('/api/system/stats'))
  }

  async passwordLogin(provider: string, username: string, password: string) {
    if (!provider.trim() || !username.trim() || !password) throw new Error('Provider, username, and password are required.')
    const result = object(await this.json('/auth/password-login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ provider: provider.trim(), username: username.trim(), password, next: '/' }),
    }))
    if (result.ok !== true) throw new Error('Hermes did not confirm the sign-in.')
  }

  async logout() {
    const response = await this.fetcher('/auth/logout', { method: 'POST', credentials: 'include', redirect: 'manual' })
    if (response.status >= 400) throw new HermesSystemHttpError(response.status, await errorMessage(response))
  }
}

export const hermesSystemAdapter = new HermesSystemAdapter()
