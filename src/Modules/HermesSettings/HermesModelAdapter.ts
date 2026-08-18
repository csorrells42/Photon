import { hermesGateway } from '../HermesGateway/HermesGatewayClient'

export const HERMES_MODEL_ADAPTER_VERSION = 1
export const HERMES_DEFAULT_MODEL_CHANGED_EVENT = 'hermes-default-model-changed'

export type HermesApprovalMode = 'manual' | 'smart' | 'off'

export type HermesReasoningEffort = 'none' | 'enabled' | 'minimal' | 'low' | 'medium' | 'high' | 'xhigh' | 'max' | 'ultra'

export type HermesModelCapabilities = {
  fast: boolean
  reasoning: boolean
}

export type HermesModelProvider = {
  authenticated: boolean
  capabilities: Record<string, HermesModelCapabilities>
  featuredModels: string[]
  models: string[]
  name: string
  slug: string
  unavailableModels: string[]
  warning?: string
}

export type HermesModelCatalog = {
  currentModel: string
  currentProvider: string
  providers: HermesModelProvider[]
  reasoningControl: HermesReasoningControlOptions
}

export type HermesReasoningControlOptions = {
  defaultEffort?: HermesReasoningEffort
  defaultEnabled?: boolean
  effortAliases?: Partial<Record<HermesReasoningEffort, HermesReasoningEffort>>
  label: 'Effort' | 'Reasoning' | 'Thinking'
  mandatory?: boolean
  optionLabels: Partial<Record<HermesReasoningEffort, string>>
  options: HermesReasoningEffort[]
  source: 'server' | 'server-managed' | 'compatibility' | 'unverified' | 'unsupported'
  supportsEffort?: boolean
  supportsToggle?: boolean
  targetModel?: string
  targetProvider?: string
}

export type HermesModelSelection = {
  model: string
  provider: string
}

export type HermesModelSwitchResponse = {
  confirmMessage?: string
  confirmRequired: boolean
  deferred: boolean
  model: string
}

type RawModelProvider = {
  authenticated?: unknown
  capabilities?: unknown
  featured_models?: unknown
  models?: unknown
  name?: unknown
  slug?: unknown
  unavailable_models?: unknown
  warning?: unknown
}

type RawModelOptions = {
  model?: unknown
  provider?: unknown
  providers?: unknown
  reasoning_control?: unknown
}

function cleanString(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function cleanStrings(value: unknown) {
  if (!Array.isArray(value)) return []
  return [...new Set(value.map(cleanString).filter(Boolean))]
}

function normalizeCapabilities(value: unknown): Record<string, HermesModelCapabilities> {
  if (!value || typeof value !== 'object') return {}
  const result: Record<string, HermesModelCapabilities> = {}
  for (const [model, raw] of Object.entries(value as Record<string, unknown>)) {
    if (!raw || typeof raw !== 'object') continue
    const capabilities = raw as Record<string, unknown>
    result[model] = { fast: capabilities.fast === true, reasoning: capabilities.reasoning === true }
  }
  return result
}

export function normalizeHermesModelCatalog(raw: RawModelOptions): HermesModelCatalog {
  const providers = Array.isArray(raw.providers) ? raw.providers : []
  const reasoningRaw = raw.reasoning_control && typeof raw.reasoning_control === 'object'
    ? raw.reasoning_control as Record<string, unknown>
    : {}
  const reasoningOptions = cleanStrings(reasoningRaw.options)
    .map(normalizeHermesReasoningEffort)
    .filter((value): value is HermesReasoningEffort => value !== null)
  const reasoningOptionLabels = normalizeReasoningOptionLabels(reasoningRaw.option_labels)
  const reasoningEffortAliases = normalizeReasoningEffortAliases(reasoningRaw.effort_aliases, reasoningOptions)
  const reasoningLabel = reasoningRaw.label === 'Thinking' || reasoningRaw.label === 'Effort'
    ? reasoningRaw.label
    : 'Reasoning'
  const reasoningSource = reasoningRaw.source === 'server' || reasoningRaw.source === 'server-managed'
    || reasoningRaw.source === 'compatibility' || reasoningRaw.source === 'unsupported'
    ? reasoningRaw.source
    : 'unverified'
  const defaultEffort = normalizeHermesReasoningEffort(reasoningRaw.default_effort)
  const activeReasoningOptions = reasoningSource === 'server' || reasoningSource === 'compatibility'
    ? reasoningOptions
    : []
  const supportsToggle = typeof reasoningRaw.supports_toggle === 'boolean'
    ? reasoningRaw.supports_toggle
    : activeReasoningOptions.includes('none') || activeReasoningOptions.includes('enabled')
  const effortOptions = activeReasoningOptions.filter((option) => option !== 'none' && option !== 'enabled')
  const supportsEffort = typeof reasoningRaw.supports_effort === 'boolean'
    ? reasoningRaw.supports_effort
    : effortOptions.length > 1 || (effortOptions.length === 1 && !activeReasoningOptions.includes('enabled'))
  return {
    currentModel: cleanString(raw.model),
    currentProvider: cleanString(raw.provider),
    reasoningControl: {
      ...(defaultEffort && defaultEffort !== 'none' && defaultEffort !== 'enabled' ? { defaultEffort } : {}),
      ...(typeof reasoningRaw.default_enabled === 'boolean' ? { defaultEnabled: reasoningRaw.default_enabled } : {}),
      effortAliases: reasoningEffortAliases,
      label: reasoningLabel,
      mandatory: reasoningRaw.mandatory === true,
      optionLabels: reasoningOptionLabels,
      options: activeReasoningOptions,
      source: reasoningSource === 'server' || reasoningSource === 'compatibility'
        ? reasoningOptions.length ? reasoningSource : 'unverified'
        : reasoningSource,
      supportsEffort,
      supportsToggle,
      targetModel: cleanString(reasoningRaw.model) || cleanString(raw.model),
      targetProvider: cleanString(reasoningRaw.provider) || cleanString(raw.provider),
    },
    providers: providers.flatMap((item): HermesModelProvider[] => {
      if (!item || typeof item !== 'object') return []
      const provider = item as RawModelProvider
      const slug = cleanString(provider.slug)
      const name = cleanString(provider.name) || slug
      if (!slug || !name) return []
      return [{
        authenticated: provider.authenticated !== false,
        capabilities: normalizeCapabilities(provider.capabilities),
        featuredModels: cleanStrings(provider.featured_models),
        models: cleanStrings(provider.models),
        name,
        slug,
        unavailableModels: cleanStrings(provider.unavailable_models),
        ...(cleanString(provider.warning) ? { warning: cleanString(provider.warning) } : {}),
      }]
    }),
  }
}

function normalizeReasoningEffortAliases(
  value: unknown,
  options: HermesReasoningEffort[],
): Partial<Record<HermesReasoningEffort, HermesReasoningEffort>> {
  if (!value || typeof value !== 'object') return {}
  const result: Partial<Record<HermesReasoningEffort, HermesReasoningEffort>> = {}
  for (const [rawSource, rawTarget] of Object.entries(value as Record<string, unknown>)) {
    const source = normalizeHermesReasoningEffort(rawSource)
    const target = normalizeHermesReasoningEffort(rawTarget)
    if (source && target && options.includes(target)) result[source] = target
  }
  return result
}

function normalizeReasoningOptionLabels(value: unknown): Partial<Record<HermesReasoningEffort, string>> {
  if (!value || typeof value !== 'object') return {}
  const result: Partial<Record<HermesReasoningEffort, string>> = {}
  for (const [rawEffort, rawLabel] of Object.entries(value as Record<string, unknown>)) {
    const effort = normalizeHermesReasoningEffort(rawEffort)
    const label = cleanString(rawLabel)
    if (effort && label && label.length <= 32) result[effort] = label
  }
  return result
}

export function modelSwitchValue(selection: HermesModelSelection) {
  const model = selection.model.trim()
  const provider = selection.provider.trim()
  if (!model || !provider) throw new Error('A model and provider are required.')
  return `${model} --provider ${provider} --session`
}

export function defaultModelSwitchValue(selection: HermesModelSelection) {
  const model = selection.model.trim()
  const provider = selection.provider.trim()
  if (!model || !provider) throw new Error('A model and provider are required.')
  return `${model} --provider ${provider} --global`
}

function normalizeApprovalMode(value: unknown): HermesApprovalMode {
  return value === 'manual' || value === 'smart' || value === 'off' ? value : 'manual'
}

export function normalizeHermesReasoningEffort(value: unknown): HermesReasoningEffort | null {
  return value === 'none' || value === 'enabled' || value === 'minimal' || value === 'low' || value === 'medium'
    || value === 'high' || value === 'xhigh' || value === 'max' || value === 'ultra'
    ? value
    : null
}

export function hermesReasoningControlForSelection(
  catalog: HermesModelCatalog | null | undefined,
  selection: HermesModelSelection | null,
): HermesReasoningControlOptions | null {
  if (!catalog || !selection) return null
  const control = catalog.reasoningControl
  return control.targetModel === selection.model.trim() && control.targetProvider === selection.provider.trim()
    ? control
    : null
}

export function effectiveHermesReasoningEffort(
  effort: HermesReasoningEffort | null,
  control: HermesReasoningControlOptions | null | undefined,
): HermesReasoningEffort | null {
  if (!effort || !control || control.source === 'unverified' || control.source === 'unsupported') return null
  if (control.options.includes(effort)) return effort
  const aliased = control.effortAliases?.[effort]
  return aliased && control.options.includes(aliased) ? aliased : null
}

export const hermesModelAdapter = {
  async options(
    sessionId?: string,
    refresh = false,
    reasoningSelection?: HermesModelSelection,
  ): Promise<HermesModelCatalog> {
    const raw = await hermesGateway.request<RawModelOptions>('model.options', {
      explicit_only: true,
      ...(sessionId ? { session_id: sessionId } : {}),
      ...(refresh ? { refresh: true } : {}),
      ...(reasoningSelection?.model.trim() ? { reasoning_model: reasoningSelection.model.trim() } : {}),
      ...(reasoningSelection?.provider.trim() ? { reasoning_provider: reasoningSelection.provider.trim() } : {}),
    })
    return normalizeHermesModelCatalog(raw)
  },

  async select(
    selection: HermesModelSelection,
    sessionId: string,
    confirmExpensiveModel = false,
  ): Promise<HermesModelSwitchResponse> {
    const result = await hermesGateway.request<Record<string, unknown>>('config.set', {
      session_id: sessionId,
      key: 'model',
      value: modelSwitchValue(selection),
      ...(confirmExpensiveModel ? { confirm_expensive_model: true } : {}),
    })
    return {
      confirmMessage: cleanString(result.confirm_message) || undefined,
      confirmRequired: result.confirm_required === true,
      deferred: result.deferred === true,
      model: cleanString(result.value) || selection.model,
    }
  },

  async selectDefault(selection: HermesModelSelection, confirmExpensiveModel = false): Promise<HermesModelSwitchResponse> {
    const result = await hermesGateway.request<Record<string, unknown>>('config.set', {
      key: 'model',
      value: defaultModelSwitchValue(selection),
      ...(confirmExpensiveModel ? { confirm_expensive_model: true } : {}),
    })
    return {
      confirmMessage: cleanString(result.confirm_message) || undefined,
      confirmRequired: result.confirm_required === true,
      deferred: result.deferred === true,
      model: cleanString(result.value) || selection.model,
    }
  },

  async getApprovalMode(): Promise<HermesApprovalMode> {
    const result = await hermesGateway.request<{ value?: unknown }>('config.get', { key: 'approvals.mode' })
    return normalizeApprovalMode(result.value)
  },

  async setApprovalMode(mode: HermesApprovalMode): Promise<HermesApprovalMode> {
    const result = await hermesGateway.request<{ value?: unknown }>('config.set', {
      key: 'approvals.mode',
      value: mode,
    })
    return normalizeApprovalMode(result.value)
  },

  async getReasoningEffort(sessionId?: string): Promise<HermesReasoningEffort | null> {
    const result = await hermesGateway.request<{ configured?: unknown; value?: unknown }>('config.get', {
      key: 'reasoning',
      ...(sessionId ? { session_id: sessionId } : {}),
    })
    return result.configured === false ? null : normalizeHermesReasoningEffort(result.value)
  },

  async setReasoningEffort(effort: HermesReasoningEffort, sessionId: string): Promise<HermesReasoningEffort> {
    const targetSessionId = sessionId.trim()
    if (!targetSessionId) throw new Error('An active conversation is required to change reasoning effort.')
    const result = await hermesGateway.request<{ value?: unknown }>('config.set', {
      session_id: targetSessionId,
      key: 'reasoning',
      value: effort,
      validate_model_control: true,
    })
    const normalized = normalizeHermesReasoningEffort(result.value)
    if (!normalized) throw new Error('Hermes returned an invalid reasoning effort.')
    return normalized
  },
}
