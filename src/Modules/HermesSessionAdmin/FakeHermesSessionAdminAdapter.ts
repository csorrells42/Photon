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
  type ImportedMessageText,
  type ImportedSessionText,
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
import {
  SessionAdminValidationError,
  boundedText,
  boundedUniqueIds,
  validateCorrelation,
  validateProfileId,
  validateSessionId,
} from './validation'

type StoredSession = SessionAdminSession & { messages: ImportedMessageText[] }

type StoredDeletePreview = DeletePreviewData & { profileId: string }
type StoredPrunePreview = PrunePreviewData & { profileId: string }
type StoredImportValidation = ImportValidationData & { profileId: string }

export interface FakeSessionAdminOptions {
  latencyMs?: number
  unavailableOperations?: SessionAdminOperation[]
  errorOperations?: SessionAdminOperation[]
  partialOperations?: SessionAdminOperation[]
  partialDeleteIds?: string[]
  seeds?: Array<SessionAdminSession & { messages?: ImportedMessageText[] }>
}

const defaultSeeds: Array<SessionAdminSession & { messages: ImportedMessageText[] }> = [
  {
    profileId: 'default', sessionId: 'root-default', title: 'Default root', parentSessionId: null,
    modelLock: null, lifecycle: 'ended', startedAt: 100, lastActiveAt: 180,
    messageCount: 2, toolCallCount: 1,
    messages: [{ role: 'user', text: 'Plan a safe change.' }, { role: 'assistant', text: 'Draft ready.' }],
  },
  {
    profileId: 'default', sessionId: 'child-default', title: 'Default child', parentSessionId: 'root-default',
    modelLock: 'provider/model-a', lifecycle: 'active', startedAt: 200, lastActiveAt: 250,
    messageCount: 1, toolCallCount: 0, messages: [{ role: 'user', text: 'Continue.' }],
  },
  {
    profileId: 'scarlett', sessionId: 'root-scarlett', title: 'Scarlett root', parentSessionId: null,
    modelLock: null, lifecycle: 'archived', startedAt: 50, lastActiveAt: 90,
    messageCount: 0, toolCallCount: 0, messages: [],
  },
]

function safeMessage(value: unknown): ImportedMessageText | null {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null
  const row = value as Record<string, unknown>
  const role = row.role
  if (role !== 'user' && role !== 'assistant' && role !== 'system' && role !== 'tool') return null
  if (typeof row.text !== 'string' || row.text.length > sessionAdminBounds.maxText) return null
  return { role, text: row.text }
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

export class DeterministicFakeHermesSessionAdminAdapter implements HermesSessionAdminAdapter {
  readonly contractVersion = HERMES_SESSION_ADMIN_CONTRACT_VERSION
  private readonly sessions = new Map<string, StoredSession>()
  private readonly seenCorrelations = new Set<string>()
  private readonly deletePreviews = new Map<string, StoredDeletePreview>()
  private readonly prunePreviews = new Map<string, StoredPrunePreview>()
  private readonly importValidations = new Map<string, StoredImportValidation>()
  private readonly unavailable: Set<SessionAdminOperation>
  private readonly errors: Set<SessionAdminOperation>
  private readonly partial: Set<SessionAdminOperation>
  private readonly partialDeleteIds: Set<string>
  private readonly latencyMs: number
  private tokenCounter = 0

  constructor(options: FakeSessionAdminOptions = {}) {
    this.latencyMs = Math.max(0, Math.min(1_000, Math.trunc(options.latencyMs ?? 0)))
    this.unavailable = new Set(options.unavailableOperations ?? [])
    this.errors = new Set(options.errorOperations ?? [])
    this.partial = new Set(options.partialOperations ?? [])
    this.partialDeleteIds = new Set(options.partialDeleteIds ?? [])
    for (const seed of options.seeds ?? defaultSeeds) {
      const stored: StoredSession = {
        ...seed,
        profileId: validateProfileId(seed.profileId),
        sessionId: validateSessionId(seed.sessionId),
        parentSessionId: seed.parentSessionId ? validateSessionId(seed.parentSessionId) : null,
        title: boundedText(seed.title, 'Session title', 512),
        messages: (seed.messages ?? []).map((message) => ({ ...message })),
      }
      this.sessions.set(this.key(stored.profileId, stored.sessionId), stored)
    }
  }

  private key(profileId: string, sessionId: string): string {
    return `${profileId}\u0000${sessionId}`
  }

  private nextToken(kind: string, profileId: string): string {
    this.tokenCounter += 1
    return `${kind}:${profileId}:${this.tokenCounter}`
  }

  private publicSession(session: StoredSession): SessionAdminSession {
    const { messages: _messages, ...value } = session
    return { ...value }
  }

  private owned(profileId: string, sessionId: string): StoredSession {
    const session = this.sessions.get(this.key(profileId, sessionId))
    if (session) return session
    const belongsElsewhere = [...this.sessions.values()].some((candidate) => candidate.sessionId === sessionId)
    if (belongsElsewhere) throw new SessionAdminValidationError('Cross-profile session access rejected.', 'cross-profile')
    throw new SessionAdminValidationError(`Session ${sessionId} was not found.`, 'not-found')
  }

  private async run<T>(
    operation: SessionAdminOperation,
    request: CorrelatedProfileRequest,
    signal: AbortSignal | undefined,
    action: () => T,
  ): Promise<SessionAdminResult<T>> {
    validateCorrelation(request)
    const correlationKey = this.key(request.profileId, request.correlationId)
    if (this.seenCorrelations.has(correlationKey)) {
      return makeResult<T>(request, 'error', null, ['Duplicate correlation was rejected.'])
    }
    this.seenCorrelations.add(correlationKey)
    if (signal?.aborted) return makeResult<T>(request, 'cancelled', null, ['Cancelled before dispatch.'])
    if (this.latencyMs > 0) await new Promise((resolve) => setTimeout(resolve, this.latencyMs))
    else await Promise.resolve()
    if (signal?.aborted) return makeResult<T>(request, 'cancelled', null, ['Cancelled before mutation.'])
    if (this.unavailable.has(operation)) return makeResult<T>(request, 'unavailable', null, [`${operation} is unavailable in the deterministic fake configuration.`])
    if (this.errors.has(operation)) return makeResult<T>(request, 'error', null, [`${operation} failed in the deterministic fake configuration.`])
    const data = action()
    return makeResult(request, this.partial.has(operation) ? 'partial' : 'success', data,
      this.partial.has(operation) ? [`${operation} returned a configured partial result.`] : [])
  }

  listSessions(request: ListSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionPage>> {
    return this.run('list', request, signal, () => {
      const limit = Math.max(1, Math.min(sessionAdminBounds.maxSessions, Math.trunc(request.limit)))
      const all = [...this.sessions.values()].filter((session) => session.profileId === request.profileId)
        .sort((a, b) => b.lastActiveAt - a.lastActiveAt || a.sessionId.localeCompare(b.sessionId))
      return { sessions: all.slice(0, limit).map((session) => this.publicSession(session)), total: all.length, truncated: all.length > limit }
    })
  }

  descendants(request: DescendantsRequest, signal?: AbortSignal): Promise<SessionAdminResult<DescendantTree>> {
    return this.run('descendants', request, signal, () => {
      const rootId = validateSessionId(request.rootSessionId)
      this.owned(request.profileId, rootId)
      const maxDepth = Math.max(1, Math.min(sessionAdminBounds.maxDescendantDepth, Math.trunc(request.maxDepth)))
      const maxNodes = Math.max(1, Math.min(sessionAdminBounds.maxDescendants, Math.trunc(request.maxNodes)))
      const children = new Map<string, StoredSession[]>()
      for (const session of this.sessions.values()) {
        if (session.profileId !== request.profileId || !session.parentSessionId) continue
        const rows = children.get(session.parentSessionId) ?? []
        rows.push(session); children.set(session.parentSessionId, rows)
      }
      for (const rows of children.values()) rows.sort((a, b) => a.sessionId.localeCompare(b.sessionId))
      let returned = 0
      let truncated = false
      const visit = (parentId: string, depth: number): DescendantNode[] => {
        if (depth > maxDepth) { if ((children.get(parentId)?.length ?? 0) > 0) truncated = true; return [] }
        const nodes: DescendantNode[] = []
        for (const child of children.get(parentId) ?? []) {
          if (returned >= maxNodes) { truncated = true; break }
          returned += 1
          nodes.push({
            profileId: request.profileId, sessionId: child.sessionId, title: child.title,
            parentSessionId: child.parentSessionId, depth, children: visit(child.sessionId, depth + 1),
          })
        }
        return nodes
      }
      const nodes = visit(rootId, 1)
      return { rootSessionId: rootId, nodes, returned, truncated, maxDepth }
    })
  }

  createBranch(request: CreateBranchRequest, signal?: AbortSignal): Promise<SessionAdminResult<CreateBranchData>> {
    return this.run('branch', request, signal, () => {
      const source = this.owned(request.profileId, validateSessionId(request.sourceSessionId))
      const newId = validateSessionId(request.newSessionId, 'New session identity')
      if ([...this.sessions.values()].some((session) => session.sessionId === newId)) {
        throw new SessionAdminValidationError(`Session ${newId} already exists.`, 'duplicate-session')
      }
      if (request.mode !== 'branch' && request.mode !== 'fork') throw new SessionAdminValidationError('Unsupported branch mode.', 'invalid-mode')
      const created: StoredSession = {
        profileId: request.profileId, sessionId: newId, title: boundedText(request.title, 'Branch title', 512),
        parentSessionId: source.sessionId, modelLock: source.modelLock, lifecycle: 'active',
        startedAt: source.lastActiveAt + 1, lastActiveAt: source.lastActiveAt + 1,
        messageCount: source.messageCount, toolCallCount: source.toolCallCount,
        messages: source.messages.map((message) => ({ ...message })),
      }
      if (request.mode === 'branch') source.lifecycle = 'ended'
      this.sessions.set(this.key(request.profileId, newId), created)
      return { created: this.publicSession(created) }
    })
  }

  previewDelete(request: DeletePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<DeletePreviewData>> {
    return this.run('delete-preview', request, signal, () => {
      const requestedIds = boundedUniqueIds(request.sessionIds, sessionAdminBounds.maxSelection, 'Delete preview')
      requestedIds.forEach((id) => this.owned(request.profileId, id))
      const resolved = new Set(requestedIds)
      let descendantCount = 0
      let truncated = false
      if (request.includeDescendants) {
        const queue = [...requestedIds]
        while (queue.length) {
          const parent = queue.shift()!
          for (const session of this.sessions.values()) {
            if (session.profileId !== request.profileId || session.parentSessionId !== parent || resolved.has(session.sessionId)) continue
            if (resolved.size >= sessionAdminBounds.maxDelete) { truncated = true; continue }
            resolved.add(session.sessionId); queue.push(session.sessionId); descendantCount += 1
          }
        }
      }
      const resolvedIds = [...resolved].sort()
      const data: DeletePreviewData = {
        previewToken: this.nextToken('delete', request.profileId), requestedIds, resolvedIds,
        activeIds: resolvedIds.filter((id) => this.owned(request.profileId, id).lifecycle === 'active'),
        descendantCount, truncated,
      }
      this.deletePreviews.set(data.previewToken, { ...data, profileId: request.profileId })
      return data
    })
  }

  commitDelete(request: DeleteCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>> {
    return this.run('delete-commit', request, signal, () => {
      if (request.confirmation !== 'delete-reviewed') throw new SessionAdminValidationError('Delete confirmation is required.', 'confirmation-required')
      const preview = this.deletePreviews.get(request.previewToken)
      if (!preview) throw new SessionAdminValidationError('Delete preview expired or is unknown.', 'preview-missing')
      if (preview.profileId !== request.profileId) throw new SessionAdminValidationError('Cross-profile delete preview rejected.', 'cross-profile')
      const deletedIds: string[] = []
      const failed: DestructiveCommitData['failed'] = []
      for (const id of preview.resolvedIds) {
        if (this.partialDeleteIds.has(id)) { failed.push({ sessionId: id, reason: 'Configured partial failure.' }); continue }
        if (this.sessions.delete(this.key(request.profileId, id))) deletedIds.push(id)
        else failed.push({ sessionId: id, reason: 'Session no longer exists.' })
      }
      this.deletePreviews.delete(request.previewToken)
      return { deletedIds, failed }
    }).then((result) => result.data?.failed.length ? { ...result, status: 'partial', notices: [...result.notices, 'Some sessions could not be deleted.'] } : result)
  }

  previewPrune(request: PrunePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<PrunePreviewData>> {
    return this.run('prune-preview', request, signal, () => {
      const maxDelete = Math.max(1, Math.min(sessionAdminBounds.maxDelete, Math.trunc(request.criteria.maxDelete)))
      if (!Number.isFinite(request.criteria.inactiveBefore)) throw new SessionAdminValidationError('Prune cutoff must be finite.', 'invalid-cutoff')
      const matches = [...this.sessions.values()].filter((session) => session.profileId === request.profileId
        && session.lifecycle !== 'active' && session.lastActiveAt < request.criteria.inactiveBefore
        && (request.criteria.includeArchived || session.lifecycle !== 'archived'))
        .sort((a, b) => a.lastActiveAt - b.lastActiveAt || a.sessionId.localeCompare(b.sessionId))
      const data: PrunePreviewData = {
        previewToken: this.nextToken('prune', request.profileId),
        criteria: { ...request.criteria, maxDelete }, candidateIds: matches.slice(0, maxDelete).map((session) => session.sessionId),
        activeExcluded: [...this.sessions.values()].filter((session) => session.profileId === request.profileId && session.lifecycle === 'active' && session.lastActiveAt < request.criteria.inactiveBefore).length,
        totalMatching: matches.length, truncated: matches.length > maxDelete,
      }
      this.prunePreviews.set(data.previewToken, { ...data, profileId: request.profileId })
      return data
    })
  }

  commitPrune(request: PruneCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>> {
    return this.run('prune-commit', request, signal, () => {
      if (request.confirmation !== 'prune-reviewed') throw new SessionAdminValidationError('Prune confirmation is required.', 'confirmation-required')
      const preview = this.prunePreviews.get(request.previewToken)
      if (!preview) throw new SessionAdminValidationError('Prune preview expired or is unknown.', 'preview-missing')
      if (preview.profileId !== request.profileId) throw new SessionAdminValidationError('Cross-profile prune preview rejected.', 'cross-profile')
      const deletedIds: string[] = []; const failed: DestructiveCommitData['failed'] = []
      for (const id of preview.candidateIds) {
        if (this.partialDeleteIds.has(id)) { failed.push({ sessionId: id, reason: 'Configured partial failure.' }); continue }
        if (this.sessions.delete(this.key(request.profileId, id))) deletedIds.push(id)
        else failed.push({ sessionId: id, reason: 'Session no longer exists.' })
      }
      this.prunePreviews.delete(request.previewToken)
      return { deletedIds, failed }
    }).then((result) => result.data?.failed.length ? { ...result, status: 'partial', notices: [...result.notices, 'Some prune candidates could not be deleted.'] } : result)
  }

  validateImport(request: ImportValidationRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportValidationData>> {
    return this.run('import-validate', request, signal, () => {
      const byteCount = new TextEncoder().encode(request.untrustedJsonText).byteLength
      const errors: string[] = []
      if (byteCount > sessionAdminBounds.maxImportBytes) {
        return { validationToken: null, valid: false, sessions: [], errors: [`Import exceeds ${sessionAdminBounds.maxImportBytes} bytes.`], byteCount, truncated: true }
      }
      let parsed: unknown
      try { parsed = JSON.parse(request.untrustedJsonText) }
      catch { return { validationToken: null, valid: false, sessions: [], errors: ['Import is not valid JSON.'], byteCount, truncated: false } }
      const root = parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null
      if (root && typeof root.profileId === 'string' && root.profileId !== request.profileId) errors.push('Import profile does not match the active profile.')
      const rawSessions = Array.isArray(parsed) ? parsed : root && Array.isArray(root.sessions) ? root.sessions : []
      const truncated = rawSessions.length > sessionAdminBounds.maxImportSessions
      if (!rawSessions.length) errors.push('Import contains no sessions.')
      if (truncated) errors.push(`Import contains more than ${sessionAdminBounds.maxImportSessions} sessions.`)
      const sessions: ImportedSessionText[] = []
      const ids = new Set<string>()
      rawSessions.slice(0, sessionAdminBounds.maxImportSessions).forEach((value, index) => {
        if (value === null || typeof value !== 'object' || Array.isArray(value)) { errors.push(`Session ${index + 1} is not an object.`); return }
        const row = value as Record<string, unknown>
        try {
          const requestedSessionId = validateSessionId(row.sessionId ?? row.id, `Session ${index + 1} identity`)
          if (ids.has(requestedSessionId)) { errors.push(`Duplicate imported session identity: ${requestedSessionId}.`); return }
          ids.add(requestedSessionId)
          const title = boundedText(row.title, `Session ${index + 1} title`, 512)
          const rawMessages = Array.isArray(row.messages) ? row.messages : []
          if (rawMessages.length > sessionAdminBounds.maxMessagesPerImportedSession) errors.push(`Session ${requestedSessionId} exceeds the message limit.`)
          const messages = rawMessages.slice(0, sessionAdminBounds.maxMessagesPerImportedSession).flatMap((message, messageIndex) => {
            const normalized = safeMessage(message)
            if (!normalized) { errors.push(`Session ${requestedSessionId} message ${messageIndex + 1} is invalid.`); return [] }
            return [normalized]
          })
          sessions.push({ requestedSessionId, title, messages })
        } catch (reason) { errors.push(reason instanceof Error ? reason.message : `Session ${index + 1} is invalid.`) }
      })
      const valid = errors.length === 0
      const validationToken = valid ? this.nextToken('import', request.profileId) : null
      const data: ImportValidationData = { validationToken, valid, sessions, errors, byteCount, truncated }
      if (validationToken) this.importValidations.set(validationToken, { ...data, profileId: request.profileId })
      return data
    })
  }

  commitImport(request: ImportCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportCommitData>> {
    return this.run('import-commit', request, signal, () => {
      const validation = this.importValidations.get(request.validationToken)
      if (!validation || !validation.valid) throw new SessionAdminValidationError('A successful import validation is required.', 'validation-required')
      if (validation.profileId !== request.profileId) throw new SessionAdminValidationError('Cross-profile import validation rejected.', 'cross-profile')
      const importedIds: string[] = []; const skipped: ImportCommitData['skipped'] = []
      for (const imported of validation.sessions) {
        if ([...this.sessions.values()].some((session) => session.sessionId === imported.requestedSessionId)) {
          skipped.push({ requestedSessionId: imported.requestedSessionId, reason: 'Session identity already exists.' }); continue
        }
        const session: StoredSession = {
          profileId: request.profileId, sessionId: imported.requestedSessionId, title: imported.title,
          parentSessionId: null, modelLock: null, lifecycle: 'ended', startedAt: 0, lastActiveAt: 0,
          messageCount: imported.messages.length, toolCallCount: null,
          messages: imported.messages.map((message) => ({ ...message })),
        }
        this.sessions.set(this.key(request.profileId, session.sessionId), session); importedIds.push(session.sessionId)
      }
      this.importValidations.delete(request.validationToken)
      return { importedIds, skipped }
    }).then((result) => result.data?.skipped.length ? { ...result, status: 'partial', notices: [...result.notices, 'Duplicate imports were skipped.'] } : result)
  }

  exportSessions(request: ExportSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<ExportSessionsData>> {
    return this.run('export', request, signal, () => {
      const ids = boundedUniqueIds(request.sessionIds, sessionAdminBounds.maxExport, 'Export')
      const sessions = ids.map((id) => {
        const session = this.owned(request.profileId, id)
        return { profileId: request.profileId, sessionId: id, title: session.title, messages: session.messages.map((message) => ({ ...message })) }
      })
      const contentText = JSON.stringify({ contractVersion: this.contractVersion, profileId: request.profileId, sessions }, null, 2)
      return { fileName: `hermes-sessions-${request.profileId}.json`, mediaType: 'application/json', contentText, sessions }
    })
  }

  statistics(request: CorrelatedProfileRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionStatisticsData>> {
    return this.run('statistics', request, signal, () => {
      const sessions = [...this.sessions.values()].filter((session) => session.profileId === request.profileId)
      const knownMessages = sessions.filter((session) => session.messageCount !== null)
      const metric = (value: number, detail: string) => ({ value, quality: 'reported' as const, detail })
      return {
        total: metric(sessions.length, 'Counted by the adapter.'),
        active: metric(sessions.filter((session) => session.lifecycle === 'active').length, 'Counted by lifecycle state.'),
        ended: metric(sessions.filter((session) => session.lifecycle === 'ended').length, 'Counted by lifecycle state.'),
        archived: metric(sessions.filter((session) => session.lifecycle === 'archived').length, 'Counted by lifecycle state.'),
        messages: {
          value: knownMessages.reduce((sum, session) => sum + (session.messageCount ?? 0), 0),
          quality: knownMessages.length === sessions.length ? 'reported' : 'partial',
          detail: knownMessages.length === sessions.length ? 'All session message counts were reported.' : 'Some sessions did not report message counts.',
        },
        storageBytes: { value: null, quality: 'unavailable', detail: 'The adapter has no supported storage-size source.' },
      }
    })
  }

  setModelLock(request: SetModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>> {
    return this.run('model-lock-set', request, signal, () => {
      const session = this.owned(request.profileId, validateSessionId(request.sessionId))
      const model = boundedText(request.model, 'Model lock', 256)
      if (session.modelLock !== request.expectedCurrentModel) throw new SessionAdminValidationError('Model lock changed since it was reviewed.', 'stale-model-lock')
      const replacing = session.modelLock !== null && session.modelLock !== model
      if (replacing && !request.confirmReplace) throw new SessionAdminValidationError('Replacing a model lock requires explicit confirmation.', 'replacement-confirmation-required')
      const previousModel = session.modelLock; session.modelLock = model
      return { sessionId: session.sessionId, previousModel, model, replaced: replacing }
    })
  }

  clearModelLock(request: ClearModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>> {
    return this.run('model-lock-clear', request, signal, () => {
      if (request.confirmation !== 'clear-model-lock') throw new SessionAdminValidationError('Clearing a model lock requires confirmation.', 'confirmation-required')
      const session = this.owned(request.profileId, validateSessionId(request.sessionId))
      if (session.modelLock !== request.expectedCurrentModel) throw new SessionAdminValidationError('Model lock changed since it was reviewed.', 'stale-model-lock')
      const previousModel = session.modelLock; session.modelLock = null
      return { sessionId: session.sessionId, previousModel, model: null, replaced: false }
    })
  }
}

export function createDeterministicFakeSessionAdminAdapter(options: FakeSessionAdminOptions = {}): DeterministicFakeHermesSessionAdminAdapter {
  return new DeterministicFakeHermesSessionAdminAdapter(options)
}


