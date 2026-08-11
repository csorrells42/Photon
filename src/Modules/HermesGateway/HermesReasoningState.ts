import { normalizeHermesReasoningEffort } from '../HermesSettings/HermesModelAdapter'
import type { HermesReasoningEffort } from '../HermesSettings/HermesModelAdapter'
import type { HermesModelSelection } from '../HermesSettings/HermesModelAdapter'

export function reasoningEffortFromSessionInfo(
  payload: Record<string, unknown> | null | undefined,
  current: HermesReasoningEffort | null,
): HermesReasoningEffort | null {
  if (!payload) return current
  if (payload.reasoning_configured === false) return null
  if (!Object.prototype.hasOwnProperty.call(payload, 'reasoning_effort')) return current
  return normalizeHermesReasoningEffort(payload.reasoning_effort)
}

export function reasoningCatalogSelection(
  current: HermesModelSelection | null,
  catalog: HermesModelSelection | null,
  requested: HermesModelSelection | undefined,
  hasActiveSession: boolean,
) {
  if (requested?.model.trim() && requested.provider.trim()) return requested
  return hasActiveSession ? catalog ?? current : current ?? catalog
}
