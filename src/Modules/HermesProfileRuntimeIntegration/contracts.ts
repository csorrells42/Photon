export const HERMES_PROFILE_RUNTIME_INTEGRATION_LABEL = 'Live read-only beta' as const

export const HERMES_PROFILE_RUNTIME_READ_ONLY_OPERATIONS = [
  'select-active-profile',
  'preview-profile-mutation',
  'apply-profile-mutation',
  'save-document',
  'save-intent',
  'select-terminal-backend',
  'preview-permission-grant',
  'confirm-permission-grant',
  'save-configuration',
  'preview-import',
  'apply-import',
  'export-profile',
] as const

export type HermesProfileRuntimeReadOnlyOperation =
  (typeof HERMES_PROFILE_RUNTIME_READ_ONLY_OPERATIONS)[number]

export type HermesProfileRuntimeIntegrationCapabilities = {
  readonly label: typeof HERMES_PROFILE_RUNTIME_INTEGRATION_LABEL
  readonly mode: 'live-read-only'
  readonly canLoad: true
  readonly mutationsEnabled: false
  readonly mutationPresentation: 'hide-or-disable'
  readonly disabledOperations: readonly HermesProfileRuntimeReadOnlyOperation[]
  readonly detail: string
}

const CAPABILITIES: HermesProfileRuntimeIntegrationCapabilities = Object.freeze({
  label: HERMES_PROFILE_RUNTIME_INTEGRATION_LABEL,
  mode: 'live-read-only',
  canLoad: true,
  mutationsEnabled: false,
  mutationPresentation: 'hide-or-disable',
  disabledOperations: Object.freeze([...HERMES_PROFILE_RUNTIME_READ_ONLY_OPERATIONS]),
  detail: 'Live profile reads are available. Profile changes remain delegated and are not sent to Hermes.',
})

export function getHermesProfileRuntimeIntegrationCapabilities(): HermesProfileRuntimeIntegrationCapabilities {
  return CAPABILITIES
}

export class HermesProfileRuntimeReadOnlyError extends Error {
  public readonly code = 'live-read-only' as const

  public constructor(public readonly operation: HermesProfileRuntimeReadOnlyOperation) {
    super(`${HERMES_PROFILE_RUNTIME_INTEGRATION_LABEL}: ${operation} is unavailable; no write was sent.`)
    this.name = 'HermesProfileRuntimeReadOnlyError'
  }
}

export type HermesProfileRuntimeBridgeErrorCode =
  | 'invalid-request'
  | 'malformed-live-result'
  | 'cross-profile-response'
  | 'cross-correlation-response'
  | 'stale-response'
  | 'duplicate-correlation'
  | 'cancelled'
  | 'busy'
  | 'oversized-response'
  | 'live-read-error'
  | 'revision-capacity'

export class HermesProfileRuntimeBridgeError extends Error {
  public constructor(
    public readonly code: HermesProfileRuntimeBridgeErrorCode,
    message: string,
    public readonly profileId: string,
    public readonly correlationId: string,
  ) {
    super(message)
    this.name = 'HermesProfileRuntimeBridgeError'
  }
}
