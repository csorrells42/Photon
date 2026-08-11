import {
  HERMES_SESSION_ADMIN_CONTRACT_VERSION,
  sessionAdminBounds,
  type ClearModelLockRequest,
  type CorrelatedProfileRequest,
  type CreateBranchData,
  type CreateBranchRequest,
  type DeleteCommitRequest,
  type DeletePreviewData,
  type DeletePreviewRequest,
  type DescendantNode,
  type DescendantsRequest,
  type DescendantTree,
  type DestructiveCommitData,
  type ExportSessionsData,
  type ExportSessionsRequest,
  type HermesSessionAdminAdapter,
  type ImportCommitData,
  type ImportCommitRequest,
  type ImportValidationData,
  type ImportValidationRequest,
  type ListSessionsRequest,
  type ModelLockData,
  type PruneCommitRequest,
  type PrunePreviewData,
  type PrunePreviewRequest,
  type SessionAdminOperation,
  type SessionAdminResult,
  type SessionAdminSession,
  type SessionPage,
  type SessionStatisticsData,
  type SetModelLockRequest,
} from './contracts'
import { boundedUniqueIds, validateCorrelation, validateProfileId, validateSessionId } from './validation'

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
type JsonObject = Record<string, unknown>
type LiveAction<T> = { data: T; status?: 'success' | 'partial'; notices?: string[] }

const MAX_JSON_BYTES = 2_000_000
const MAX_ERROR_BYTES = 2_048
const MAX_CORRELATIONS = 512

export const LIVE_SESSION_ADMIN_OPERATIONS = [
  'list',
  'descendants',
  'export',
  'statistics',
] as const satisfies readonly SessionAdminOperation[]

function object(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function nonNegativeInteger(value: unknown): number | null {
  const number = finiteNumber(value)
  return number === null ? null : Math.max(0, Math.trunc(number))
}

function boundedPositiveInteger(value: unknown, fallback: number, maximum: number): number {
  const number = finiteNumber(value)
  if (number === null) return fallback
  return Math.max(1, Math.min(maximum, Math.trunc(number)))
}

function epochMilliseconds(value: unknown): number {
  const number = finiteNumber(value) ?? 0
  return number > 0 && number < 10_000_000_000 ? Math.trunc(number * 1_000) : Math.trunc(number)
}

function safeNotice(reason: unknown): string {
  const message = reason instanceof Error ? reason.message : 'Hermes session administration failed.'
  return message.replace(/[\r\n\t]+/g, ' ').slice(0, 512) || 'Hermes session administration failed.'
}

async function boundedResponseText(response: Response, maxBytes: number): Promise<string> {
  const declared = Number(response.headers.get('content-length'))
  if (Number.isFinite(declared) && declared > maxBytes) throw new Error(`Hermes response exceeds the ${maxBytes.toLocaleString()} byte limit.`)
  if (!response.body) {
    const body = await response.text()
    if (new TextEncoder().encode(body).byteLength > maxBytes) throw new Error(`Hermes response exceeds the ${maxBytes.toLocaleString()} byte limit.`)
    return body
  }

  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  try {
    while (true) {
      const next = await reader.read()
      if (next.done) break
      total += next.value.byteLength
      if (total > maxBytes) {
        await reader.cancel()
        throw new Error(`Hermes response exceeds the ${maxBytes.toLocaleString()} byte limit.`)
      }
      chunks.push(next.value)
    }
  } finally {
    reader.releaseLock()
  }
  const merged = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) { merged.set(chunk, offset); offset += chunk.byteLength }
  return new TextDecoder().decode(merged)
}

function makeResult<T>(
  request: CorrelatedProfileRequest,
  status: SessionAdminResult<T>['status'],
  data: T | null,
  notices: string[] = [],
): SessionAdminResult<T> {
  return {
    contractVersion: HERMES_SESSION_ADMIN_CONTRACT_VERSION,
    profileId: request.profileId,
    correlationId: request.correlationId,
    status,
    data,
    notices,
  }
}

function normalizeSession(value: unknown, expectedProfile: string): SessionAdminSession {
  const raw = object(value)
  const sessionId = validateSessionId(text(raw.id) || text(raw.session_id))
  const reportedProfile = text(raw.profile) || expectedProfile
  if (validateProfileId(reportedProfile) !== expectedProfile) throw new Error('Hermes returned a session owned by another profile.')
  const rawParent = text(raw.parent_session_id) || text(raw.parentSessionId)
  const rawTitle = text(raw.title).trim()
  return {
    profileId: expectedProfile,
    sessionId,
    title: (rawTitle || sessionId).slice(0, 512),
    parentSessionId: rawParent ? validateSessionId(rawParent, 'Parent session identity') : null,
    modelLock: null,
    lifecycle: raw.archived === true ? 'archived' : raw.is_active === true ? 'active' : 'ended',
    startedAt: epochMilliseconds(raw.started_at),
    lastActiveAt: epochMilliseconds(raw.last_active ?? raw.started_at),
    messageCount: nonNegativeInteger(raw.message_count),
    toolCallCount: nonNegativeInteger(raw.tool_call_count),
  }
}

function messageText(value: unknown): string {
  if (typeof value === 'string') return value
  if (Array.isArray(value)) return value.map((part) => {
    if (typeof part === 'string') return part
    return text(object(part).text)
  }).join('')
  return text(object(value).text)
}

function normalizeExportMessage(value: unknown): { role: 'user' | 'assistant' | 'system' | 'tool'; text: string; truncated: boolean } | null {
  const raw = object(value)
  const role = raw.role
  if (role !== 'user' && role !== 'assistant' && role !== 'system' && role !== 'tool') return null
  const fullText = messageText(raw.text ?? raw.content)
  return { role, text: fullText.slice(0, sessionAdminBounds.maxText), truncated: fullText.length > sessionAdminBounds.maxText }
}

export class LiveHermesSessionAdminAdapter implements HermesSessionAdminAdapter {
  readonly contractVersion = HERMES_SESSION_ADMIN_CONTRACT_VERSION
  readonly supportedOperations: readonly SessionAdminOperation[] = LIVE_SESSION_ADMIN_OPERATIONS
  private readonly seenCorrelations = new Set<string>()
  private readonly correlationOrder: string[] = []

  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  private rememberCorrelation(request: CorrelatedProfileRequest): boolean {
    const key = `${request.profileId}\u0000${request.correlationId}`
    if (this.seenCorrelations.has(key)) return false
    this.seenCorrelations.add(key)
    this.correlationOrder.push(key)
    if (this.correlationOrder.length > MAX_CORRELATIONS) {
      const expired = this.correlationOrder.shift()
      if (expired) this.seenCorrelations.delete(expired)
    }
    return true
  }

  private async json(path: string, signal: AbortSignal | undefined, maxBytes = MAX_JSON_BYTES): Promise<unknown> {
    const response = await this.fetcher(path, { credentials: 'include', signal })
    if (!response.ok) {
      const detail = (await boundedResponseText(response, MAX_ERROR_BYTES).catch(() => '')).trim()
      throw new Error(response.status === 401 ? 'Sign in to Hermes to use live session administration.' : detail || `Hermes returned HTTP ${response.status}.`)
    }
    const body = await boundedResponseText(response, maxBytes)
    try { return JSON.parse(body) as unknown }
    catch { throw new Error('Hermes returned malformed session data.') }
  }

  private async execute<T>(
    operation: SessionAdminOperation,
    request: CorrelatedProfileRequest,
    signal?: AbortSignal,
    action?: () => Promise<LiveAction<T>>,
  ): Promise<SessionAdminResult<T>> {
    validateCorrelation(request)
    if (!this.rememberCorrelation(request)) return makeResult<T>(request, 'error', null, ['Duplicate correlation was rejected.'])
    if (signal?.aborted) return makeResult<T>(request, 'cancelled', null, ['Cancelled before dispatch.'])
    if (!this.supportedOperations.includes(operation)) {
      return makeResult<T>(request, 'unavailable', null, [`${operation} is not exposed by the verified read-only Hermes adapter.`])
    }
    try {
      const result = await action!()
      if (signal?.aborted) return makeResult<T>(request, 'cancelled', null, ['Cancelled before accepting the response.'])
      return makeResult(request, result.status ?? 'success', result.data, result.notices ?? [])
    } catch (reason) {
      if (signal?.aborted || (reason instanceof DOMException && reason.name === 'AbortError')) {
        return makeResult<T>(request, 'cancelled', null, ['The operation was cancelled.'])
      }
      return makeResult<T>(request, 'error', null, [safeNotice(reason)])
    }
  }

  listSessions(request: ListSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionPage>> {
    return this.execute('list', request, signal, async () => {
      const profileId = validateProfileId(request.profileId)
      const limit = boundedPositiveInteger(request.limit, 100, sessionAdminBounds.maxSessions)
      const sessions = new Map<string, SessionAdminSession>()
      const notices: string[] = []
      let total: number | null = null
      let offset = 0
      while (sessions.size < limit) {
        const pageSize = Math.min(100, limit - sessions.size)
        const query = new URLSearchParams({
          limit: String(pageSize), offset: String(offset), min_messages: '0', archived: 'include', order: 'recent', profile: profileId,
        })
        const raw = object(await this.json(`/api/sessions?${query.toString()}`, signal))
        const rows = Array.isArray(raw.sessions) ? raw.sessions : []
        const reportedTotal = nonNegativeInteger(raw.total)
        if (reportedTotal !== null) total = Math.max(total ?? 0, reportedTotal)
        for (const row of rows) {
          const normalized = normalizeSession(row, profileId)
          if (sessions.has(normalized.sessionId)) notices.push(`Hermes repeated session ${normalized.sessionId}; the duplicate was ignored.`)
          sessions.set(normalized.sessionId, normalized)
        }
        offset += rows.length
        if (rows.length < pageSize || rows.length === 0 || (total !== null && offset >= total)) break
      }
      const values = [...sessions.values()].slice(0, limit)
      const truncated = (total !== null && total > values.length) || values.length >= limit
      return { data: { sessions: values, total: total === null ? null : Math.max(total, values.length), truncated }, notices }
    })
  }

  statistics(request: CorrelatedProfileRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionStatisticsData>> {
    return this.execute('statistics', request, signal, async () => {
      const profileId = validateProfileId(request.profileId)
      const query = new URLSearchParams({ profile: profileId })
      const raw = object(await this.json(`/api/sessions/stats?${query.toString()}`, signal))
      const statistic = (value: unknown, detail: string) => ({ value: nonNegativeInteger(value), quality: nonNegativeInteger(value) === null ? 'unavailable' as const : 'reported' as const, detail })
      return {
        status: 'partial',
        notices: ['Hermes does not report active-runtime, ended, or storage-byte totals from this route.'],
        data: {
          total: statistic(raw.total, 'Reported by Hermes session storage.'),
          active: { value: null, quality: 'unavailable', detail: 'The route reports non-archived storage, not currently active runtime sessions.' },
          ended: { value: null, quality: 'unavailable', detail: 'No ended-session total is reported.' },
          archived: statistic(raw.archived, 'Reported by Hermes session storage.'),
          messages: statistic(raw.messages, 'Reported by Hermes session storage.'),
          storageBytes: { value: null, quality: 'unavailable', detail: 'Storage bytes are not reported.' },
        },
      }
    })
  }

  descendants(request: DescendantsRequest, signal?: AbortSignal): Promise<SessionAdminResult<DescendantTree>> {
    return this.execute('descendants', request, signal, async () => {
      const profileId = validateProfileId(request.profileId)
      const rootSessionId = validateSessionId(request.rootSessionId)
      const maxDepth = boundedPositiveInteger(request.maxDepth, sessionAdminBounds.maxDescendantDepth, sessionAdminBounds.maxDescendantDepth)
      const maxNodes = boundedPositiveInteger(request.maxNodes, sessionAdminBounds.maxDescendants, sessionAdminBounds.maxDescendants)
      const query = new URLSearchParams({ profile: profileId })
      const raw = object(await this.json(`/api/sessions/${encodeURIComponent(rootSessionId)}/latest-descendant?${query.toString()}`, signal))
      const rawPath = Array.isArray(raw.path) ? raw.path : []
      const path = rawPath.map((value) => validateSessionId(value)).filter((value, index, values) => index === 0 || value !== values[index - 1])
      if (!path.length) path.push(rootSessionId)
      if (path[0] !== rootSessionId) throw new Error('Hermes returned a descendant path for another root session.')
      const available = path.slice(1, 1 + Math.min(maxDepth, maxNodes))
      let children: DescendantNode[] = []
      for (let index = available.length - 1; index >= 0; index -= 1) {
        const sessionId = available[index]
        children = [{ profileId, sessionId, title: sessionId, depth: index + 1, parentSessionId: path[index], children }]
      }
      const truncated = available.length < path.length - 1
      return {
        status: available.length ? 'partial' : 'success',
        notices: available.length ? ['Hermes reports the latest descendant path only; side branches are not represented as a complete tree.'] : [],
        data: { rootSessionId, nodes: children, returned: available.length, truncated, maxDepth },
      }
    })
  }

  exportSessions(request: ExportSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<ExportSessionsData>> {
    return this.execute('export', request, signal, async () => {
      const profileId = validateProfileId(request.profileId)
      const sessionIds = boundedUniqueIds(request.sessionIds, sessionAdminBounds.maxExport, 'Export')
      const sessions: ExportSessionsData['sessions'] = []
      const notices: string[] = []
      for (const requestedId of sessionIds) {
        try {
          const query = new URLSearchParams({ profile: profileId })
          const raw = object(await this.json(`/api/sessions/${encodeURIComponent(requestedId)}/export?${query.toString()}`, signal, sessionAdminBounds.maxImportBytes))
          const reportedId = validateSessionId(text(raw.id) || text(raw.session_id) || requestedId)
          if (reportedId !== requestedId) throw new Error(`Hermes exported ${reportedId} instead of ${requestedId}.`)
          const reportedProfile = text(raw.profile)
          if (reportedProfile && validateProfileId(reportedProfile) !== profileId) throw new Error('Hermes returned an export owned by another profile.')
          const rawMessages = Array.isArray(raw.messages) ? raw.messages : []
          const messages = rawMessages.slice(0, sessionAdminBounds.maxMessagesPerImportedSession).flatMap((message) => {
            const normalized = normalizeExportMessage(message)
            if (!normalized) { notices.push(`Session ${requestedId} contained an unsupported message row that was omitted.`); return [] }
            if (normalized.truncated) notices.push(`Session ${requestedId} contained message text longer than ${sessionAdminBounds.maxText.toLocaleString()} characters; retained text was truncated.`)
            return [{ role: normalized.role, text: normalized.text }]
          })
          if (rawMessages.length > sessionAdminBounds.maxMessagesPerImportedSession) notices.push(`Session ${requestedId} export was bounded to ${sessionAdminBounds.maxMessagesPerImportedSession} messages.`)
          const exported = { profileId, sessionId: requestedId, title: (text(raw.title).trim() || requestedId).slice(0, 512), messages }
          const candidate = JSON.stringify({ profileId, sessions: [...sessions, exported] }, null, 2)
          if (new TextEncoder().encode(candidate).byteLength > sessionAdminBounds.maxImportBytes) {
            notices.push(`Session ${requestedId} was omitted because the combined export would exceed ${sessionAdminBounds.maxImportBytes.toLocaleString()} bytes.`)
            continue
          }
          sessions.push(exported)
        } catch (reason) {
          if (signal?.aborted) throw reason
          notices.push(`Session ${requestedId} could not be exported: ${safeNotice(reason)}`)
        }
      }
      if (!sessions.length) throw new Error('No selected sessions could be exported within the verified bounds.')
      const contentText = JSON.stringify({ profileId, sessions }, null, 2)
      return {
        status: notices.length ? 'partial' : 'success',
        notices,
        data: { fileName: `hermes-sessions-${profileId}.json`, mediaType: 'application/json', contentText, sessions },
      }
    })
  }

  createBranch(request: CreateBranchRequest, signal?: AbortSignal): Promise<SessionAdminResult<CreateBranchData>> { return this.execute('branch', request, signal) }
  previewDelete(request: DeletePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<DeletePreviewData>> { return this.execute('delete-preview', request, signal) }
  commitDelete(request: DeleteCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>> { return this.execute('delete-commit', request, signal) }
  previewPrune(request: PrunePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<PrunePreviewData>> { return this.execute('prune-preview', request, signal) }
  commitPrune(request: PruneCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>> { return this.execute('prune-commit', request, signal) }
  validateImport(request: ImportValidationRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportValidationData>> { return this.execute('import-validate', request, signal) }
  commitImport(request: ImportCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportCommitData>> { return this.execute('import-commit', request, signal) }
  setModelLock(request: SetModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>> { return this.execute('model-lock-set', request, signal) }
  clearModelLock(request: ClearModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>> { return this.execute('model-lock-clear', request, signal) }
}

export const liveHermesSessionAdminAdapter = new LiveHermesSessionAdminAdapter()
