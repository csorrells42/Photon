export const HERMES_MCP_ADAPTER_VERSION = 1

export type HermesMcpTransport = 'http' | 'stdio' | 'unknown'
export type HermesMcpAuth = 'header' | 'oauth' | null
export type HermesMcpCatalogAuth = 'api_key' | 'oauth' | 'none'

export type HermesMcpServer = {
  name: string
  transport: HermesMcpTransport
  url: string | null
  command: string | null
  args: string[]
  environmentVariableNames: string[]
  auth: HermesMcpAuth
  enabled: boolean
  tools: string[] | null
  revision: string
  editable: boolean
  editorBlockReason: string
}

export type HermesMcpCatalogRequirement = {
  name: string
  prompt: string
  required: boolean
}

export type HermesMcpCatalogEntry = {
  name: string
  description: string
  source: string
  transport: Exclude<HermesMcpTransport, 'unknown'>
  authType: HermesMcpCatalogAuth
  requiredEnvironment: HermesMcpCatalogRequirement[]
  command: string | null
  args: string[]
  url: string | null
  installUrl: string | null
  installRef: string | null
  bootstrap: string[]
  defaultEnabledTools: string[] | null
  postInstall: string
  needsInstall: boolean
  installed: boolean
  enabled: boolean
}

export type HermesMcpCatalogDiagnostic = {
  name: string
  kind: string
  message: string
}

export type HermesMcpSnapshot = {
  servers: HermesMcpServer[]
  catalog: HermesMcpCatalogEntry[]
  diagnostics: HermesMcpCatalogDiagnostic[]
}

export type HermesMcpTestResult = {
  ok: boolean
  error: string
  tools: Array<{ name: string; description: string }>
}

export type HermesMcpCreateRequest = {
  name: string
  transport: 'http' | 'stdio'
  url?: string
  command?: string
  args?: string[]
  auth?: 'none' | 'oauth'
}

export type HermesMcpOAuthFlow = {
  flowId: string
  serverName: string
  status: 'starting' | 'authorization_required' | 'approved' | 'error'
  authorizationUrl: string | null
  error: string
  tools: Array<{ name: string; description: string }>
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
type JsonObject = Record<string, unknown>

const MAX_SERVERS = 128
const MAX_CATALOG = 256
const MAX_LIST = 64
const MAX_TEXT = 8_192

function object(value: unknown): JsonObject {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function text(value: unknown, maximum = 512) {
  return typeof value === 'string' ? value.trim().slice(0, maximum) : ''
}

function boolean(value: unknown) {
  return value === true
}

function stringList(value: unknown, maximum = MAX_LIST) {
  if (!Array.isArray(value)) return []
  return value
    .filter((item): item is string => typeof item === 'string')
    .map((item) => text(item))
    .filter(Boolean)
    .slice(0, maximum)
}

function transport(value: unknown): HermesMcpTransport {
  return value === 'http' || value === 'stdio' ? value : 'unknown'
}

function catalogAuth(value: unknown): HermesMcpCatalogAuth {
  return value === 'api_key' || value === 'oauth' ? value : 'none'
}

export function normalizeHermesMcpServers(value: unknown): HermesMcpServer[] {
  const rows = object(value).servers
  if (!Array.isArray(rows)) return []
  return rows.flatMap((item) => {
    const raw = object(item)
    const name = text(raw.name, 128)
    if (!name) return []
    return [{
      name,
      transport: transport(raw.transport),
      url: null,
      command: null,
      args: [],
      environmentVariableNames: [],
      auth: null,
      enabled: raw.enabled !== false,
      tools: null,
      revision: text(raw.revision, 256),
      editable: false,
      editorBlockReason: 'requires-trusted-reentry',
    }]
  }).slice(0, MAX_SERVERS)
}

function normalizeRequirements(value: unknown): HermesMcpCatalogRequirement[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const raw = object(item)
    const name = text(raw.name, 128)
    if (!name || !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name)) return []
    return [{ name, prompt: text(raw.prompt, 256) || name, required: boolean(raw.required) }]
  }).slice(0, 32)
}

export function normalizeHermesMcpCatalog(value: unknown): Pick<HermesMcpSnapshot, 'catalog' | 'diagnostics'> {
  const raw = object(value)
  const entries = Array.isArray(raw.entries) ? raw.entries : []
  const diagnostics = Array.isArray(raw.diagnostics) ? raw.diagnostics : []

  return {
    catalog: entries.flatMap((item) => {
      const row = object(item)
      const name = text(row.name, 128)
      const rowTransport = transport(row.transport)
      if (!name || rowTransport === 'unknown') return []
      return [{
        name,
        description: text(row.description, 2_048),
        source: text(row.source, 2_048),
        transport: rowTransport,
        authType: catalogAuth(row.auth_type),
        requiredEnvironment: normalizeRequirements(row.required_env),
        command: text(row.command, 1_024) || null,
        args: stringList(row.args),
        url: text(row.url, 2_048) || null,
        installUrl: text(row.install_url, 2_048) || null,
        installRef: text(row.install_ref, 256) || null,
        bootstrap: stringList(row.bootstrap, 32).map((command) => command.slice(0, 2_048)),
        defaultEnabledTools: Array.isArray(row.default_enabled) ? stringList(row.default_enabled) : null,
        postInstall: text(row.post_install, MAX_TEXT),
        needsInstall: boolean(row.needs_install),
        installed: boolean(row.installed),
        enabled: boolean(row.enabled),
      } satisfies HermesMcpCatalogEntry]
    }).slice(0, MAX_CATALOG),
    diagnostics: diagnostics.flatMap((item) => {
      const row = object(item)
      const name = text(row.name, 128)
      const message = text(row.message, 2_048)
      if (!name || !message) return []
      return [{ name, kind: text(row.kind, 64) || 'warning', message }]
    }).slice(0, MAX_CATALOG),
  }
}

export function normalizeHermesMcpTestResult(value: unknown): HermesMcpTestResult {
  const raw = object(value)
  const tools = Array.isArray(raw.tools) ? raw.tools : []
  return {
    ok: boolean(raw.ok),
    error: text(raw.error, 2_048),
    tools: tools.flatMap((item) => {
      const row = object(item)
      const name = text(row.name, 256)
      return name ? [{ name, description: text(row.description, 1_024) }] : []
    }).slice(0, 256),
  }
}

export function normalizeHermesMcpOAuthFlow(value: unknown): HermesMcpOAuthFlow {
  const raw = object(value)
  const status = raw.status === 'authorization_required' || raw.status === 'approved' || raw.status === 'error'
    ? raw.status
    : 'starting'
  return {
    flowId: text(raw.flow_id, 256),
    serverName: text(raw.server_name, 128),
    status,
    authorizationUrl: text(raw.authorization_url, 2_048) || null,
    error: text(raw.error, 2_048),
    tools: normalizeHermesMcpTestResult({ ok: true, tools: raw.tools }).tools,
  }
}

function cleanName(value: string) {
  const result = value.trim()
  if (!result || result.length > 128 || /[\u0000-\u001f\u007f]/.test(result)) throw new Error('A valid MCP server name is required.')
  return result
}

function safeHttpUrl(value: string, label: string) {
  let parsed: URL
  try { parsed = new URL(value.trim()) }
  catch { throw new Error(`${label} must be a valid HTTP or HTTPS URL.`) }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') throw new Error(`${label} must use HTTP or HTTPS.`)
  return parsed.toString()
}

async function responseMessage(response: Response) {
  try {
    const body = object(await response.clone().json())
    return text(body.detail, 2_048) || text(body.error, 2_048) || `Hermes returned HTTP ${response.status}.`
  } catch {
    const body = await response.text().catch(() => '')
    return text(body, 2_048) || `Hermes returned HTTP ${response.status}.`
  }
}

export class HermesMcpAdapter {
  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  private async json(path: string, init?: RequestInit) {
    const response = await this.fetcher(path, { credentials: 'include', ...init })
    if (!response.ok) throw new Error(await responseMessage(response))
    return response.json() as Promise<unknown>
  }

  async snapshot(): Promise<HermesMcpSnapshot> {
    const [serversRaw, catalogRaw] = await Promise.all([
      this.json('/api/mcp/servers'),
      this.json('/api/mcp/catalog'),
    ])
    const catalog = normalizeHermesMcpCatalog(catalogRaw)
    return { servers: normalizeHermesMcpServers(serversRaw), ...catalog }
  }

  async create(request: HermesMcpCreateRequest) {
    const name = cleanName(request.name)
    const body: Record<string, unknown> = { name }
    if (request.transport === 'http') {
      body.url = safeHttpUrl(request.url ?? '', 'MCP URL')
      const selectedAuth = request.auth ?? 'none'
      if (selectedAuth !== 'none' && selectedAuth !== 'oauth') throw new Error('Secrets must be connected through the native Connections broker.')
      if (selectedAuth !== 'none') body.auth = selectedAuth
    } else {
      const command = request.command?.trim() ?? ''
      if (!command) throw new Error('A stdio command is required.')
      body.command = command.slice(0, 1_024)
      const args = (request.args ?? []).map((item) => item.trim()).filter(Boolean).slice(0, MAX_LIST)
      if (args.length) body.args = args
    }
    return this.json('/api/mcp/servers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
  }

  async test(name: string) {
    return normalizeHermesMcpTestResult(await this.json(`/api/mcp/servers/${encodeURIComponent(cleanName(name))}/test`, { method: 'POST' }))
  }

  async setEnabled(name: string, enabled: boolean) {
    await this.json(`/api/mcp/servers/${encodeURIComponent(cleanName(name))}/enabled`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
  }

  async remove(name: string) {
    await this.json(`/api/mcp/servers/${encodeURIComponent(cleanName(name))}`, { method: 'DELETE' })
  }

  async install(name: string) {
    const raw = object(await this.json('/api/mcp/catalog/install', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: cleanName(name), env: {}, enable: true }),
    }))
    return {
      ok: boolean(raw.ok),
      background: boolean(raw.background),
      action: text(raw.action, 256),
    }
  }

  async startOAuth(name: string) {
    return normalizeHermesMcpOAuthFlow(await this.json(`/api/mcp/servers/${encodeURIComponent(cleanName(name))}/auth`, { method: 'POST' }))
  }

  async oauthStatus(flowId: string) {
    const cleanFlowId = flowId.trim()
    if (!cleanFlowId || cleanFlowId.length > 256) throw new Error('The OAuth flow identifier is invalid.')
    return normalizeHermesMcpOAuthFlow(await this.json(`/api/mcp/oauth/flows/${encodeURIComponent(cleanFlowId)}`))
  }
}

export const hermesMcpAdapter = new HermesMcpAdapter()
