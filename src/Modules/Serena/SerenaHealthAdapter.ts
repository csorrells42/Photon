export const SERENA_HEALTH_ADAPTER_VERSION = 1

export type SerenaHealthState =
  | 'ready'
  | 'disabled'
  | 'missing'
  | 'authentication-required'
  | 'error'

export type SerenaToolSummary = {
  name: string
  description: string
}

export type SerenaHealthSnapshot = {
  protocolVersion: 1
  observedAtUtc: string
  state: SerenaHealthState
  configured: boolean
  enabled: boolean
  reachable: boolean
  transport: 'http' | 'stdio' | 'unknown'
  endpoint: string
  tools: SerenaToolSummary[]
  toolCount: number
  prompts: number
  resources: number
  detail: string
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
type JsonObject = Record<string, unknown>

class SerenaHealthHttpError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'SerenaHealthHttpError'
  }
}

function object(value: unknown): JsonObject {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function text(value: unknown, maximum = 500) {
  return typeof value === 'string' ? value.trim().slice(0, maximum) : ''
}

function count(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0
}

function transport(value: unknown): SerenaHealthSnapshot['transport'] {
  return value === 'http' || value === 'stdio' ? value : 'unknown'
}

function safeEndpoint(value: unknown) {
  const candidate = text(value, 1_024)
  if (!candidate) return ''
  try {
    const parsed = new URL(candidate)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return ''
    return `${parsed.protocol}//${parsed.host}${parsed.pathname}`
  } catch {
    return ''
  }
}

function baseSnapshot(state: SerenaHealthState, detail: string): SerenaHealthSnapshot {
  return {
    protocolVersion: SERENA_HEALTH_ADAPTER_VERSION,
    observedAtUtc: new Date().toISOString(),
    state,
    configured: false,
    enabled: false,
    reachable: false,
    transport: 'unknown',
    endpoint: '',
    tools: [],
    toolCount: 0,
    prompts: 0,
    resources: 0,
    detail,
  }
}

async function responseMessage(response: Response) {
  try {
    const body = object(await response.clone().json())
    return text(body.detail) || text(body.error) || `Hermes returned HTTP ${response.status}.`
  } catch {
    return text(await response.text().catch(() => '')) || `Hermes returned HTTP ${response.status}.`
  }
}

export class SerenaHealthAdapter {
  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  private async json(path: string, init?: RequestInit) {
    const response = await this.fetcher(path, {
      credentials: 'include',
      signal: AbortSignal.timeout(20_000),
      ...init,
    })
    if (!response.ok) throw new SerenaHealthHttpError(response.status, await responseMessage(response))
    return response.json() as Promise<unknown>
  }

  async inspect(): Promise<SerenaHealthSnapshot> {
    try {
      const list = object(await this.json('/api/mcp/servers'))
      const servers = Array.isArray(list.servers) ? list.servers : []
      const rawServer = servers.map(object).find((server) => text(server.name, 128).toLowerCase() === 'serena')
      if (!rawServer) return baseSnapshot('missing', 'Serena is not configured in the Hermes MCP server list.')

      const name = text(rawServer.name, 128) || 'serena'
      const enabled = rawServer.enabled !== false
      const endpoint = safeEndpoint(rawServer.url)
      const configured: SerenaHealthSnapshot = {
        ...baseSnapshot(enabled ? 'error' : 'disabled', enabled ? 'Serena has not been probed yet.' : 'Serena is configured but disabled in Hermes.'),
        configured: true,
        enabled,
        transport: transport(rawServer.transport),
        endpoint,
      }
      if (!enabled) return configured

      const probe = object(await this.json(`/api/mcp/servers/${encodeURIComponent(name)}/test`, { method: 'POST' }))
      if (probe.ok !== true) {
        return { ...configured, detail: text(probe.error) || 'Hermes could not connect to Serena.' }
      }

      const rawTools = Array.isArray(probe.tools) ? probe.tools.slice(0, 256) : []
      const tools = rawTools.flatMap((value): SerenaToolSummary[] => {
        const raw = object(value)
        const toolName = text(raw.name, 128)
        return toolName ? [{ name: toolName, description: text(raw.description) }] : []
      })
      return {
        ...configured,
        state: 'ready',
        reachable: true,
        tools,
        toolCount: tools.length,
        prompts: count(probe.prompts),
        resources: count(probe.resources),
        detail: `Hermes reached Serena and discovered ${tools.length} tool${tools.length === 1 ? '' : 's'}.`,
      }
    } catch (reason) {
      if (reason instanceof SerenaHealthHttpError && reason.status === 401) {
        return baseSnapshot('authentication-required', 'Sign in to Hermes to verify the Serena tool connection.')
      }
      const detail = reason instanceof Error && reason.name === 'TimeoutError'
        ? 'The Serena probe timed out after 20 seconds.'
        : reason instanceof Error ? reason.message : 'Serena health could not be checked.'
      return baseSnapshot('error', detail)
    }
  }
}

export const serenaHealthAdapter = new SerenaHealthAdapter()
