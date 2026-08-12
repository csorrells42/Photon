export const hermesConnectionsProtocolVersion = 2 as const

export type ConnectionAuthKind = 'api-key' | 'oauth' | 'device-code'
export type ConnectionSourceKind = 'native' | 'oauth' | 'device-code' | 'external-cli'
export type ConnectionReviewAction = 'change' | 'remove'

export type ConnectionCatalogEntry = {
  providerId: string
  displayName: string
  slotId: string
  authKind: ConnectionAuthKind
  sourceKind: ConnectionSourceKind
  purposes: string[]
  supportsNativeChange: boolean
  description?: string
}

export type ConnectionMetadata = {
  connectionRef: string
  profileId: string
  providerId: string
  slotId: string
  authKind: ConnectionAuthKind
  sourceKind: ConnectionSourceKind
  purposes: string[]
  revision: number
  updatedAt: string
  configured: true
}

export type ConnectionReview = {
  reviewHandle: string
  action: ConnectionReviewAction
  connectionRef?: string
  profileId: string
  providerId: string
  slotId: string
  expectedRevision: number
  expiresAt: string
}

export type ConnectionChangeIntent = {
  profileId: string
  providerId: string
  slotId: string
  authKind: ConnectionAuthKind
  sourceKind: ConnectionSourceKind
  purposes: string[]
  existingReference?: string
  expectedRevision: number
}

export type ConnectionClientResult<T> =
  | { kind: 'success'; value: T }
  | { kind: 'failure'; code: string; message: string; retryable: boolean }
  | { kind: 'unavailable'; message: string }

export type OpenRouterSessionConnection = {
  providerId: 'openrouter'
  revision: number
  sessionOnly: true
}

export type ConnectionsHostFrame = {
  type?: unknown
  version?: unknown
  requestId?: unknown
  entries?: unknown
  entry?: unknown
  review?: unknown
  code?: unknown
  message?: unknown
  retryable?: unknown
  providerId?: unknown
  revision?: unknown
  sessionOnly?: unknown
}

export function normalizeOpenRouterSessionConnection(value: ConnectionsHostFrame): OpenRouterSessionConnection | null {
  const revision = typeof value.revision === 'number' && Number.isSafeInteger(value.revision) && value.revision > 0
    ? value.revision
    : null
  return value.type === 'connections.openrouter.session.result'
    && value.providerId === 'openrouter'
    && value.sessionOnly === true
    && revision !== null
    ? { providerId: 'openrouter', revision, sessionOnly: true }
    : null
}

const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/
const connectionRefPattern = /^hcv2_[A-Za-z0-9_-]{43}$/
const reviewHandlePattern = /^hcr2_[A-Za-z0-9_-]{43}$/
const authKinds = new Set<ConnectionAuthKind>(['api-key', 'oauth', 'device-code'])
const sourceKinds = new Set<ConnectionSourceKind>(['native', 'oauth', 'device-code', 'external-cli'])
const reviewActions = new Set<ConnectionReviewAction>(['change', 'remove'])
const forbiddenHostField = /secret|token|password|protected|payload|path|userSid|machineId|installId/i

function containsForbiddenHostField(raw: Record<string, unknown>) {
  return Object.keys(raw).some((key) => forbiddenHostField.test(key))
}

export function normalizeIdentifier(value: unknown, maximum = 128): string | null {
  return typeof value === 'string' && value.length > 0 && value.length <= maximum && identifierPattern.test(value)
    ? value
    : null
}

export function normalizeConnectionRef(value: unknown): string | null {
  return typeof value === 'string' && connectionRefPattern.test(value) ? value : null
}

export function normalizeReviewHandle(value: unknown): string | null {
  return typeof value === 'string' && reviewHandlePattern.test(value) ? value : null
}

export function normalizePurposes(value: unknown): string[] | null {
  if (!Array.isArray(value) || value.length === 0 || value.length > 32) return null
  const normalized = value.map((purpose) => normalizeIdentifier(purpose, 160))
  if (normalized.some((purpose) => purpose === null)) return null
  const allowed = normalized as string[]
  if (allowed.some((purpose) => !purpose.startsWith('model:')
    && !purpose.startsWith('mcp:')
    && !purpose.startsWith('channel:')
    && purpose !== 'oauth-refresh'
    && purpose !== 'usage')) return null
  return [...new Set(allowed)].sort((left, right) => left.localeCompare(right))
}

export function normalizeConnectionMetadata(value: unknown): ConnectionMetadata | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  if (containsForbiddenHostField(raw)) return null
  const connectionRef = normalizeConnectionRef(raw.connectionRef)
  const profileId = normalizeIdentifier(raw.profileId)
  const providerId = normalizeIdentifier(raw.providerId, 96)
  const slotId = normalizeIdentifier(raw.slotId)
  const purposes = normalizePurposes(raw.purposes)
  const authKind = typeof raw.authKind === 'string' && authKinds.has(raw.authKind as ConnectionAuthKind)
    ? raw.authKind as ConnectionAuthKind
    : null
  const sourceKind = typeof raw.sourceKind === 'string' && sourceKinds.has(raw.sourceKind as ConnectionSourceKind)
    ? raw.sourceKind as ConnectionSourceKind
    : null
  const revision = typeof raw.revision === 'number' && Number.isSafeInteger(raw.revision) && raw.revision > 0
    ? raw.revision
    : null
  const updatedAt = typeof raw.updatedAt === 'string' && raw.updatedAt.length <= 64 && !Number.isNaN(Date.parse(raw.updatedAt))
    ? raw.updatedAt
    : null
  if (!connectionRef || !profileId || !providerId || !slotId || !authKind || !sourceKind || !purposes || !revision || !updatedAt || raw.configured !== true) return null
  return { connectionRef, profileId, providerId, slotId, authKind, sourceKind, purposes, revision, updatedAt, configured: true }
}

export function normalizeConnectionReview(value: unknown): ConnectionReview | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  if (containsForbiddenHostField(raw)) return null
  const reviewHandle = normalizeReviewHandle(raw.reviewHandle)
  const action = typeof raw.action === 'string' && reviewActions.has(raw.action as ConnectionReviewAction)
    ? raw.action as ConnectionReviewAction
    : null
  const connectionRef = raw.connectionRef === undefined || raw.connectionRef === null
    ? undefined
    : normalizeConnectionRef(raw.connectionRef) ?? undefined
  const profileId = normalizeIdentifier(raw.profileId)
  const providerId = normalizeIdentifier(raw.providerId, 96)
  const slotId = normalizeIdentifier(raw.slotId)
  const expectedRevision = typeof raw.expectedRevision === 'number' && Number.isSafeInteger(raw.expectedRevision) && raw.expectedRevision >= 0
    ? raw.expectedRevision
    : null
  const expiresAt = typeof raw.expiresAt === 'string' && raw.expiresAt.length <= 64 && !Number.isNaN(Date.parse(raw.expiresAt))
    ? raw.expiresAt
    : null
  if (!reviewHandle || !action || !profileId || !providerId || !slotId || expectedRevision === null || !expiresAt) return null
  if (action === 'remove' && !connectionRef) return null
  return { reviewHandle, action, ...(connectionRef ? { connectionRef } : {}), profileId, providerId, slotId, expectedRevision, expiresAt }
}

export function normalizeCatalogEntry(value: unknown): ConnectionCatalogEntry | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  const providerId = normalizeIdentifier(raw.providerId, 96)
  const slotId = normalizeIdentifier(raw.slotId)
  const purposes = normalizePurposes(raw.purposes)
  const authKind = typeof raw.authKind === 'string' && authKinds.has(raw.authKind as ConnectionAuthKind)
    ? raw.authKind as ConnectionAuthKind
    : null
  const sourceKind = typeof raw.sourceKind === 'string' && sourceKinds.has(raw.sourceKind as ConnectionSourceKind)
    ? raw.sourceKind as ConnectionSourceKind
    : null
  const displayName = typeof raw.displayName === 'string' && raw.displayName.trim().length > 0 && raw.displayName.length <= 120
    ? raw.displayName.trim()
    : null
  const description = typeof raw.description === 'string' && raw.description.trim().length > 0 && raw.description.length <= 280
    ? raw.description.trim()
    : undefined
  if (!providerId || !slotId || !purposes || !authKind || !sourceKind || !displayName || typeof raw.supportsNativeChange !== 'boolean') return null
  return { providerId, displayName, slotId, authKind, sourceKind, purposes, supportsNativeChange: raw.supportsNativeChange, ...(description ? { description } : {}) }
}

export function assertSafeChangeIntent(intent: ConnectionChangeIntent): ConnectionChangeIntent {
  const profileId = normalizeIdentifier(intent.profileId)
  const providerId = normalizeIdentifier(intent.providerId, 96)
  const slotId = normalizeIdentifier(intent.slotId)
  const purposes = normalizePurposes(intent.purposes)
  const existingReference = intent.existingReference === undefined ? undefined : normalizeConnectionRef(intent.existingReference)
  if (!profileId || !providerId || !slotId || !purposes || !authKinds.has(intent.authKind) || !sourceKinds.has(intent.sourceKind)
    || (intent.existingReference !== undefined && !existingReference)
    || !Number.isSafeInteger(intent.expectedRevision) || intent.expectedRevision < 0) {
    throw new Error('The connection intent is invalid.')
  }
  return { profileId, providerId, slotId, authKind: intent.authKind, sourceKind: intent.sourceKind, purposes,
    ...(existingReference ? { existingReference } : {}), expectedRevision: intent.expectedRevision }
}
