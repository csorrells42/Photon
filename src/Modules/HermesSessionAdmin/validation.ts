import {
  HERMES_SESSION_ADMIN_CONTRACT_VERSION,
  type CorrelatedProfileRequest,
  type ProfileScope,
  type SessionAdminResult,
  sessionAdminBounds,
} from './contracts'

const ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/

export class SessionAdminValidationError extends Error {
  constructor(message: string, readonly code: string) {
    super(message)
    this.name = 'SessionAdminValidationError'
  }
}

export function boundedText(value: unknown, label: string, limit: number = sessionAdminBounds.maxText): string {
  if (typeof value !== 'string') throw new SessionAdminValidationError(`${label} must be text.`, 'invalid-text')
  const text = value.trim()
  if (!text) throw new SessionAdminValidationError(`${label} is required.`, 'missing-text')
  if (text.length > limit) throw new SessionAdminValidationError(`${label} exceeds ${limit} characters.`, 'text-too-long')
  return text
}

export function validateProfileId(value: unknown): string {
  const profileId = boundedText(value, 'Profile identity', 128)
  if (!ID_PATTERN.test(profileId)) throw new SessionAdminValidationError('Profile identity contains unsupported characters.', 'invalid-profile')
  return profileId
}

export function validateSessionId(value: unknown, label = 'Session identity'): string {
  const sessionId = boundedText(value, label, 256)
  if (!ID_PATTERN.test(sessionId)) throw new SessionAdminValidationError(`${label} contains unsupported characters.`, 'invalid-session')
  return sessionId
}

export function validateCorrelation(request: CorrelatedProfileRequest): void {
  validateProfileId(request.profileId)
  const correlationId = boundedText(request.correlationId, 'Correlation identity', 256)
  if (!ID_PATTERN.test(correlationId)) throw new SessionAdminValidationError('Correlation identity contains unsupported characters.', 'invalid-correlation')
}

export function assertProfileMatch(expected: ProfileScope, actual: ProfileScope): void {
  const expectedProfile = validateProfileId(expected.profileId)
  const actualProfile = validateProfileId(actual.profileId)
  if (expectedProfile !== actualProfile) {
    throw new SessionAdminValidationError(
      `Cross-profile response rejected: expected ${expectedProfile}, received ${actualProfile}.`,
      'cross-profile',
    )
  }
}

export function validateResultProfile<T>(request: CorrelatedProfileRequest, result: SessionAdminResult<T>): SessionAdminResult<T> {
  assertProfileMatch(request, result)
  if (result.correlationId !== request.correlationId) {
    throw new SessionAdminValidationError('Response correlation does not match the pending request.', 'correlation-mismatch')
  }
  if (result.contractVersion !== HERMES_SESSION_ADMIN_CONTRACT_VERSION) {
    throw new SessionAdminValidationError('Session administration contract version mismatch.', 'contract-mismatch')
  }
  return result
}

export function boundedUniqueIds(values: readonly string[], limit: number, label: string): string[] {
  if (!Array.isArray(values) || values.length === 0) throw new SessionAdminValidationError(`${label} requires at least one session.`, 'empty-selection')
  if (values.length > limit) throw new SessionAdminValidationError(`${label} is limited to ${limit} sessions.`, 'selection-too-large')
  const ids = values.map((value) => validateSessionId(value))
  if (new Set(ids).size !== ids.length) throw new SessionAdminValidationError(`${label} contains duplicate sessions.`, 'duplicate-session')
  return ids
}

export function statusNotice(status: SessionAdminResult<unknown>['status']): string {
  if (status === 'partial') return 'The operation completed only partially.'
  if (status === 'unavailable') return 'This operation is unavailable from the current upstream surface.'
  if (status === 'error') return 'The operation failed without a complete result.'
  if (status === 'cancelled') return 'The operation was cancelled.'
  return ''
}

