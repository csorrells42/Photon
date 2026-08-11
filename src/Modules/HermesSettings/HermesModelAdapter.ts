import { hermesGateway } from '../HermesGateway/HermesGatewayClient'

export const HERMES_MODEL_ADAPTER_VERSION = 1

export type HermesApprovalMode = 'manual' | 'smart' | 'off'

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
  return {
    currentModel: cleanString(raw.model),
    currentProvider: cleanString(raw.provider),
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

export function modelSwitchValue(selection: HermesModelSelection) {
  const model = selection.model.trim()
  const provider = selection.provider.trim()
  if (!model || !provider) throw new Error('A model and provider are required.')
  return `${model} --provider ${provider} --session`
}

function normalizeApprovalMode(value: unknown): HermesApprovalMode {
  return value === 'manual' || value === 'smart' || value === 'off' ? value : 'manual'
}

export const hermesModelAdapter = {
  async options(sessionId?: string, refresh = false): Promise<HermesModelCatalog> {
    const raw = await hermesGateway.request<RawModelOptions>('model.options', {
      explicit_only: true,
      ...(sessionId ? { session_id: sessionId } : {}),
      ...(refresh ? { refresh: true } : {}),
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
}
