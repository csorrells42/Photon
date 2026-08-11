import {
  HERMES_MCP_EDITOR_CONTRACT_VERSION,
  type HermesMcpEditorCommitRequest,
  type HermesMcpEditorCommitResult,
  type HermesMcpEditorController,
  type HermesMcpEditorDiscardRequest,
  type HermesMcpEditorReason,
  type HermesMcpEditorReviewRequest,
  type HermesMcpEditorReviewResult,
  type HermesMcpEditorRisk,
} from './HermesMcpEditorContract'

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
type JsonObject = Record<string, unknown>

const reviewReasons = new Set(['validation-error', 'unavailable', 'stale-revision', 'secret-rebind-required'])
const commitReasons = new Set([
  'unavailable', 'stale-revision', 'stale-review', 'expired-review', 'unknown-review',
  'risk-confirmation-required', 'secret-rebind-required', 'commit-failed', 'committed',
])

function object(value: unknown): JsonObject {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function risk(value: unknown): HermesMcpEditorRisk {
  return value === 'transport' || value === 'source' || value === 'command' ? value : 'none'
}

async function detail(response: Response) {
  try {
    const value = object(await response.json()).detail
    return typeof value === 'string' ? value : 'unavailable'
  } catch { return 'unavailable' }
}

export class HermesMcpLiveEditorController implements HermesMcpEditorController {
  constructor(
    private readonly serverId: string,
    private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_),
  ) {}

  async review(request: HermesMcpEditorReviewRequest): Promise<HermesMcpEditorReviewResult> {
    if (request.contractVersion !== HERMES_MCP_EDITOR_CONTRACT_VERSION || request.serverId !== this.serverId) {
      return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'rejected', reason: 'validation-error' }
    }
    try {
      const response = await this.fetcher(`/api/mcp/servers/${encodeURIComponent(this.serverId)}/review`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revision: request.revision, edit: {
          transport: request.edit.transport, url: request.edit.url, command: request.edit.command,
          args: request.edit.args, environment_variable_names: request.edit.environmentVariableNames,
          auth: request.edit.auth, enabled: request.edit.enabled,
        } }),
      })
      if (!response.ok) {
        const reason = await detail(response)
        const safeReason = reviewReasons.has(reason) ? reason as Extract<HermesMcpEditorReason, 'validation-error' | 'unavailable' | 'stale-revision' | 'secret-rebind-required'> : 'unavailable'
        return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: safeReason === 'unavailable' ? 'unavailable' : 'rejected', reason: safeReason }
      }
      const raw = object(await response.json())
      if (raw.contract_version !== HERMES_MCP_EDITOR_CONTRACT_VERSION || raw.status !== 'ready' || raw.reason !== 'ready' || typeof raw.review_handle !== 'string') {
        return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'unavailable', reason: 'unavailable' }
      }
      return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'ready', reason: 'ready', reviewHandle: raw.review_handle, risk: risk(raw.risk) }
    } catch {
      return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'unavailable', reason: 'unavailable' }
    }
  }

  async commit(request: HermesMcpEditorCommitRequest): Promise<HermesMcpEditorCommitResult> {
    try {
      const response = await this.fetcher(`/api/mcp/servers/${encodeURIComponent(this.serverId)}/commit`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ review_handle: request.reviewHandle, risk_confirmed: request.riskConfirmed }),
      })
      if (!response.ok) {
        const reason = await detail(response)
        const safeReason = commitReasons.has(reason) ? reason as Exclude<HermesMcpEditorReason, 'ready' | 'validation-error'> : 'commit-failed'
        return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: safeReason === 'unavailable' ? 'unavailable' : 'failure', reason: safeReason }
      }
      const raw = object(await response.json())
      return raw.contract_version === HERMES_MCP_EDITOR_CONTRACT_VERSION && raw.status === 'success' && raw.reason === 'committed'
        ? { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'success', reason: 'committed' }
        : { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'failure', reason: 'commit-failed' }
    } catch {
      return { contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION, status: 'unavailable', reason: 'unavailable' }
    }
  }

  async discard(request: HermesMcpEditorDiscardRequest) {
    try {
      await this.fetcher(`/api/mcp/servers/${encodeURIComponent(this.serverId)}/review/${encodeURIComponent(request.reviewHandle)}`, { method: 'DELETE', credentials: 'include' })
    } catch { /* The server's bounded TTL remains authoritative. */ }
  }
}
