export const HERMES_SESSION_ADMIN_CONTRACT_VERSION = 'hermes-session-admin/v1' as const

export const sessionAdminBounds = {
  maxSessions: 200,
  maxSelection: 50,
  maxDescendants: 128,
  maxDescendantDepth: 8,
  maxDelete: 100,
  maxExport: 50,
  maxImportBytes: 512_000,
  maxImportSessions: 100,
  maxMessagesPerImportedSession: 500,
  maxText: 16_384,
} as const

export type SessionAdminOperation =
  | 'list'
  | 'branch'
  | 'descendants'
  | 'delete-preview'
  | 'delete-commit'
  | 'prune-preview'
  | 'prune-commit'
  | 'import-validate'
  | 'import-commit'
  | 'export'
  | 'statistics'
  | 'model-lock-set'
  | 'model-lock-clear'

export type SessionAdminResultStatus = 'success' | 'partial' | 'unavailable' | 'error' | 'cancelled'
export type SessionLifecycle = 'active' | 'ended' | 'archived'
export type BranchMode = 'branch' | 'fork'

export interface ProfileScope {
  profileId: string
}

export interface CorrelatedProfileRequest extends ProfileScope {
  correlationId: string
}

export interface SessionAdminResult<T> extends CorrelatedProfileRequest {
  contractVersion: typeof HERMES_SESSION_ADMIN_CONTRACT_VERSION
  status: SessionAdminResultStatus
  data: T | null
  notices: string[]
}

export interface SessionAdminSession extends ProfileScope {
  sessionId: string
  title: string
  parentSessionId: string | null
  modelLock: string | null
  lifecycle: SessionLifecycle
  startedAt: number
  lastActiveAt: number
  messageCount: number | null
  toolCallCount: number | null
}

export interface SessionPage {
  sessions: SessionAdminSession[]
  total: number | null
  truncated: boolean
}

export interface DescendantNode extends ProfileScope {
  sessionId: string
  title: string
  depth: number
  parentSessionId: string | null
  children: DescendantNode[]
}

export interface DescendantTree {
  rootSessionId: string
  nodes: DescendantNode[]
  returned: number
  truncated: boolean
  maxDepth: number
}

export interface ListSessionsRequest extends CorrelatedProfileRequest {
  limit: number
}

export interface DescendantsRequest extends CorrelatedProfileRequest {
  rootSessionId: string
  maxDepth: number
  maxNodes: number
}

export interface CreateBranchRequest extends CorrelatedProfileRequest {
  sourceSessionId: string
  newSessionId: string
  title: string
  mode: BranchMode
}

export interface CreateBranchData {
  created: SessionAdminSession
}

export interface DeletePreviewRequest extends CorrelatedProfileRequest {
  sessionIds: string[]
  includeDescendants: boolean
}

export interface DeletePreviewData {
  previewToken: string
  requestedIds: string[]
  resolvedIds: string[]
  activeIds: string[]
  descendantCount: number
  truncated: boolean
}

export interface DeleteCommitRequest extends CorrelatedProfileRequest {
  previewToken: string
  confirmation: 'delete-reviewed'
}

export interface DestructiveCommitData {
  deletedIds: string[]
  failed: Array<{ sessionId: string; reason: string }>
}

export interface PruneCriteria {
  inactiveBefore: number
  includeArchived: boolean
  maxDelete: number
}

export interface PrunePreviewRequest extends CorrelatedProfileRequest {
  criteria: PruneCriteria
}

export interface PrunePreviewData {
  previewToken: string
  criteria: PruneCriteria
  candidateIds: string[]
  activeExcluded: number
  totalMatching: number
  truncated: boolean
}

export interface PruneCommitRequest extends CorrelatedProfileRequest {
  previewToken: string
  confirmation: 'prune-reviewed'
}

export interface ImportedMessageText {
  role: 'user' | 'assistant' | 'system' | 'tool'
  text: string
}

export interface ImportedSessionText {
  requestedSessionId: string
  title: string
  messages: ImportedMessageText[]
}

export interface ImportValidationRequest extends CorrelatedProfileRequest {
  untrustedJsonText: string
}

export interface ImportValidationData {
  validationToken: string | null
  valid: boolean
  sessions: ImportedSessionText[]
  errors: string[]
  byteCount: number
  truncated: boolean
}

export interface ImportCommitRequest extends CorrelatedProfileRequest {
  validationToken: string
}

export interface ImportCommitData {
  importedIds: string[]
  skipped: Array<{ requestedSessionId: string; reason: string }>
}

export interface ExportSessionsRequest extends CorrelatedProfileRequest {
  sessionIds: string[]
}

export interface ExportSessionText extends ProfileScope {
  sessionId: string
  title: string
  messages: ImportedMessageText[]
}

export interface ExportSessionsData {
  fileName: string
  mediaType: 'application/json'
  contentText: string
  sessions: ExportSessionText[]
}

export type StatisticQuality = 'reported' | 'partial' | 'unavailable'

export interface SessionStatistic {
  value: number | null
  quality: StatisticQuality
  detail: string
}

export interface SessionStatisticsData {
  total: SessionStatistic
  active: SessionStatistic
  ended: SessionStatistic
  archived: SessionStatistic
  messages: SessionStatistic
  storageBytes: SessionStatistic
}

export interface SetModelLockRequest extends CorrelatedProfileRequest {
  sessionId: string
  model: string
  expectedCurrentModel: string | null
  confirmReplace: boolean
}

export interface ClearModelLockRequest extends CorrelatedProfileRequest {
  sessionId: string
  expectedCurrentModel: string
  confirmation: 'clear-model-lock'
}

export interface ModelLockData {
  sessionId: string
  previousModel: string | null
  model: string | null
  replaced: boolean
}

export interface HermesSessionAdminAdapter {
  readonly contractVersion: typeof HERMES_SESSION_ADMIN_CONTRACT_VERSION
  readonly supportedOperations?: readonly SessionAdminOperation[]
  listSessions(request: ListSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionPage>>
  descendants(request: DescendantsRequest, signal?: AbortSignal): Promise<SessionAdminResult<DescendantTree>>
  createBranch(request: CreateBranchRequest, signal?: AbortSignal): Promise<SessionAdminResult<CreateBranchData>>
  previewDelete(request: DeletePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<DeletePreviewData>>
  commitDelete(request: DeleteCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>>
  previewPrune(request: PrunePreviewRequest, signal?: AbortSignal): Promise<SessionAdminResult<PrunePreviewData>>
  commitPrune(request: PruneCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<DestructiveCommitData>>
  validateImport(request: ImportValidationRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportValidationData>>
  commitImport(request: ImportCommitRequest, signal?: AbortSignal): Promise<SessionAdminResult<ImportCommitData>>
  exportSessions(request: ExportSessionsRequest, signal?: AbortSignal): Promise<SessionAdminResult<ExportSessionsData>>
  statistics(request: CorrelatedProfileRequest, signal?: AbortSignal): Promise<SessionAdminResult<SessionStatisticsData>>
  setModelLock(request: SetModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>>
  clearModelLock(request: ClearModelLockRequest, signal?: AbortSignal): Promise<SessionAdminResult<ModelLockData>>
}
