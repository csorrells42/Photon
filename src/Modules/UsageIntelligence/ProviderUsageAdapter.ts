import type {
  ProviderConnectionState,
  ProviderId,
  UsageCollectionRequest,
  UsageDashboardSnapshot,
} from './contracts'

export interface UsageAdapterFailure {
  kind: 'failure'
  code: 'not-configured' | 'permission-denied' | 'unavailable' | 'unsupported' | 'unexpected'
  state: Extract<ProviderConnectionState, 'error' | 'unavailable' | 'not-configured'>
  message: string
  retryable: boolean
  provider?: ProviderId
}

export interface UsageAdapterSuccess {
  kind: 'success'
  snapshot: UsageDashboardSnapshot
}

export type UsageAdapterResult = UsageAdapterSuccess | UsageAdapterFailure

/**
 * Implementations are transport-neutral. Production adapters are expected to
 * call a native-host or backend boundary, never a provider from the renderer.
 */
export interface ProviderUsageAdapter {
  collect(request: UsageCollectionRequest, signal?: AbortSignal): Promise<UsageAdapterResult>
}

export function isUsageAdapterSuccess(result: UsageAdapterResult): result is UsageAdapterSuccess {
  return result.kind === 'success'
}
