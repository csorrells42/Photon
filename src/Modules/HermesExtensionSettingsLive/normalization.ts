import type {
  AdvancedModelSettings,
  ExtensionProvenance,
  McpServerConfiguration,
  ModelOption,
  MoaPreset,
  MoaSettings,
  SkillDocument,
  ToolsetProvider,
  ToolsetSettings,
} from '../HermesExtensionSettings/contracts'
import { liveAdapterLimits, type WorkbenchReviewAttestation } from './contracts'

type JsonObject = Record<string, unknown>

const credentialText = /(?:\bAuthorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._~+\/-]{8,}|\bBearer\s+[A-Za-z0-9._~+\/-]{8,}|\bsk-[A-Za-z0-9_-]{8,}|\b(?:api[_-]?key|access[_-]?token|oauth[_-]?token|token|password|secret)\s*[:=]\s*\S+)/gi
const auxiliaryTaskMap = {
  vision: 'vision',
  compression: 'compression',
  title_generation: 'title-generation',
} as const

export function liveObject(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as JsonObject
    : {}
}

export function liveSafeText(value: unknown, maximum: number = liveAdapterLimits.text) {
  if (typeof value !== 'string') return ''
  return value
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '')
    .replace(credentialText, '[redacted]')
    .trim()
    .slice(0, maximum)
}

export function liveSafeId(value: unknown) {
  return liveSafeText(value, liveAdapterLimits.identifier)
    .replace(/[^A-Za-z0-9._:/-]/g, '-')
    .replace(/-+/g, '-')
}

function boundedArray(value: unknown, maximum: number = liveAdapterLimits.collection) {
  return Array.isArray(value) ? value.slice(0, maximum) : []
}

function stringList(value: unknown, maximum = 64, itemMaximum = 512) {
  return [...new Set(boundedArray(value, maximum)
    .map((item) => liveSafeText(item, itemMaximum))
    .filter(Boolean))]
}

function reviewSet(reviews: readonly WorkbenchReviewAttestation[], subject: WorkbenchReviewAttestation['subject']) {
  return new Set(reviews.filter((entry) => entry.subject === subject).map((entry) => liveSafeId(entry.id)))
}

function skillProvenance(raw: unknown, id: string, reviewed: Set<string>): ExtensionProvenance {
  if (raw === 'bundled') return 'nous-approved'
  if (raw === 'agent') return 'user-created'
  if (reviewed.has(id)) return 'workbench-reviewed'
  return 'external-unreviewed'
}

export function normalizeLiveSkillInventory(
  value: unknown,
  reviews: readonly WorkbenchReviewAttestation[] = [],
): SkillDocument[] {
  const reviewed = reviewSet(reviews, 'skill')
  return boundedArray(value).flatMap((entry) => {
    const row = liveObject(entry)
    const name = liveSafeText(row.name, liveAdapterLimits.label)
    const id = liveSafeId(name)
    if (!name || !id) return []
    const provenance = skillProvenance(row.provenance, id, reviewed)
    return [{
      id,
      name,
      description: liveSafeText(row.description, 2_048),
      content: '',
      provenance,
      editable: provenance === 'user-created',
    }]
  })
}

export function normalizeLiveSkillContent(value: unknown) {
  const row = liveObject(value)
  const name = liveSafeText(row.name, liveAdapterLimits.label)
  if (!name) return null
  return {
    id: liveSafeId(name),
    content: liveSafeText(row.content, liveAdapterLimits.content),
  }
}

export function mergeLiveSkillContents(skills: SkillDocument[], contents: readonly { id: string; content: string }[]) {
  const byId = new Map(contents.map((entry) => [entry.id, entry.content]))
  return skills.map((skill) => ({ ...skill, content: byId.get(skill.id) ?? '' }))
}

function providerState(value: unknown): 'ready' | 'partial' | 'unavailable' | 'error' {
  const normalized = liveSafeText(value, 64).toLowerCase().replace(/[^a-z]/g, '')
  if (normalized.includes('ready') || normalized.includes('configured')) return 'ready'
  if (normalized.includes('unavailable') || normalized.includes('unsupported')) return 'unavailable'
  if (normalized.includes('error') || normalized.includes('failed')) return 'error'
  return 'partial'
}

export interface NormalizedWebToolset {
  providers: ToolsetProvider[]
  selections: ToolsetSettings
}

export function normalizeLiveWebToolset(
  value: unknown,
  reviews: readonly WorkbenchReviewAttestation[] = [],
): NormalizedWebToolset {
  const raw = liveObject(value)
  const reviewed = reviewSet(reviews, 'toolset-provider')
  const providers = boundedArray(raw.providers).flatMap((entry) => {
    const row = liveObject(entry)
    const name = liveSafeText(row.name, liveAdapterLimits.label)
    const backendId = liveSafeId(row.web_backend)
    const id = liveSafeId(name || backendId)
    const capabilities = stringList(row.capabilities, 2, 16)
      .filter((capability): capability is 'search' | 'extract' => capability === 'search' || capability === 'extract')
    if (!id || !name || !backendId || capabilities.length === 0) return []
    const provenance: ExtensionProvenance = reviewed.has(id) ? 'workbench-reviewed' : 'external-unreviewed'
    return [{
      id,
      label: name,
      provenance,
      state: providerState(row.status),
      capabilities: capabilities.map((capability) => ({ capability, backendIds: [backendId] })),
      backends: [{ id: backendId, label: name, description: liveSafeText(row.tag, 1_024) }],
    } satisfies ToolsetProvider]
  })
  const selected = (capability: 'search' | 'extract', backendValue: unknown) => {
    const backendId = liveSafeId(backendValue)
    const provider = providers.find((entry) =>
      entry.capabilities.some((advertisement) => advertisement.capability === capability && advertisement.backendIds.includes(backendId)))
    return { providerId: provider?.id ?? '', backendId: provider ? backendId : '', specialtyModelId: '' }
  }
  return {
    providers,
    selections: {
      search: selected('search', raw.active_search_backend),
      extract: selected('extract', raw.active_extract_backend),
    },
  }
}

export function normalizeLiveToolsetInventory(value: unknown) {
  const rows = boundedArray(value)
  const web = rows.map(liveObject).find((row) => row.name === 'web')
  return {
    itemCount: rows.length,
    webAdvertised: Boolean(web),
    webAvailable: web ? web.available !== false : false,
  }
}

export function normalizeLiveMcpCatalogNames(value: unknown) {
  const raw = liveObject(value)
  return new Set(boundedArray(raw.entries).map((entry) => liveSafeText(liveObject(entry).name, liveAdapterLimits.label)).filter(Boolean))
}

function safeHttpOrigin(value: unknown) {
  const text = liveSafeText(value, 2_048)
  try {
    const parsed = new URL(text)
    if ((parsed.protocol !== 'http:' && parsed.protocol !== 'https:') || parsed.username || parsed.password) return ''
    return parsed.origin + '/'
  } catch {
    return ''
  }
}

export function normalizeLiveMcpServers(
  value: unknown,
  nousCatalogNames: ReadonlySet<string>,
  reviews: readonly WorkbenchReviewAttestation[] = [],
): McpServerConfiguration[] {
  const raw = liveObject(value)
  const reviewed = reviewSet(reviews, 'mcp-server')
  return boundedArray(raw.servers).flatMap((entry) => {
    const row = liveObject(entry)
    const name = liveSafeText(row.name, liveAdapterLimits.label)
    const id = liveSafeId(name)
    const transport = row.transport === 'http' || row.transport === 'stdio' ? row.transport : null
    if (!id || !name || !transport) return []
    const provenance: ExtensionProvenance = nousCatalogNames.has(name)
      ? 'nous-approved'
      : reviewed.has(id)
        ? 'workbench-reviewed'
        : 'user-created'
    return [{
      id,
      name,
      transport,
      endpoint: transport === 'http' ? safeHttpOrigin(row.url) : '',
      command: '',
      args: [],
      environmentVariableNames: Object.keys(liveObject(row.env))
        .filter((key) => /^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(key))
        .sort((left, right) => left.localeCompare(right))
        .slice(0, 64),
      enabled: row.enabled !== false,
      provenance,
    }]
  })
}

export function liveModelId(provider: unknown, model: unknown) {
  const providerId = liveSafeId(provider)
  const modelId = liveSafeId(model)
  return providerId && modelId ? liveSafeId(`${providerId}::${modelId}`) : ''
}

export interface NormalizedModelOptions {
  models: ModelOption[]
  currentProvider: string
  currentModel: string
}

function modelPrice(value: unknown) {
  if (typeof value === 'number') return Number.isFinite(value) && value >= 0 ? value : null
  if (typeof value !== 'string') return null
  const match = value.trim().match(/^\$?\s*(\d+(?:\.\d+)?)/)
  if (!match) return null
  const parsed = Number(match[1])
  return Number.isFinite(parsed) ? parsed : null
}

function liveModelCostTier(provider: JsonObject, model: string): ModelOption['costTier'] {
  const pricing = liveObject(liveObject(provider.pricing)[model])
  const input = modelPrice(pricing.input)
  const output = modelPrice(pricing.output)
  return (input !== null && input > 20) || (output !== null && output > 100)
    ? 'expensive'
    : 'standard'
}

export function normalizeLiveModelOptions(value: unknown, modelInfo: unknown): NormalizedModelOptions {
  const raw = liveObject(value)
  const info = liveObject(modelInfo)
  const currentProvider = liveSafeId(raw.provider)
  const currentModel = liveSafeId(raw.model)
  const infoCaps = liveObject(info.capabilities)
  const currentComposite = liveModelId(currentProvider, currentModel)
  const seen = new Set<string>()
  const models = boundedArray(raw.providers).flatMap((entry) => {
    const provider = liveObject(entry)
    const providerId = liveSafeId(provider.slug)
    if (!providerId || providerId === 'moa') return []
    const unavailable = new Set(stringList(provider.unavailable_models, liveAdapterLimits.collection, liveAdapterLimits.identifier).map(liveSafeId))
    return stringList(provider.models, liveAdapterLimits.collection, liveAdapterLimits.identifier).flatMap((model) => {
      const id = liveModelId(providerId, model)
      if (!id || seen.has(id)) return []
      seen.add(id)
      const specialties: ModelOption['specialties'] = ['general']
      if (id === currentComposite && infoCaps.supports_vision === true) specialties.push('vision')
      return [{
        id,
        label: model,
        providerId,
        specialties,
        costTier: liveModelCostTier(provider, model),
        available: provider.authenticated !== false && !unavailable.has(liveSafeId(model)),
      }]
    })
  }).slice(0, liveAdapterLimits.collection)
  return { models, currentProvider, currentModel }
}

export function normalizeLiveAuxiliarySettings(value: unknown) {
  const raw = liveObject(value)
  const main = liveObject(raw.main)
  const auxiliaryModels = boundedArray(raw.tasks, 32).flatMap((entry) => {
    const row = liveObject(entry)
    const task = liveSafeText(row.task, 64)
    const mapped = auxiliaryTaskMap[task as keyof typeof auxiliaryTaskMap]
    const modelId = liveModelId(row.provider, row.model)
    return mapped && modelId ? [{ task: mapped, modelId }] : []
  })
  return {
    defaultModelId: liveModelId(main.provider, main.model),
    auxiliaryModels,
  }
}

function modelSlotId(value: unknown) {
  const row = liveObject(value)
  return liveModelId(row.provider, row.model)
}

export interface NormalizedMoa {
  presets: MoaPreset[]
  settings: MoaSettings
}

export function normalizeLiveMoa(value: unknown): NormalizedMoa {
  const raw = liveObject(value)
  const presetRows = Object.entries(liveObject(raw.presets)).slice(0, 32)
  const presets = presetRows.flatMap(([name, value]) => {
    const id = liveSafeId(name)
    if (!id) return []
    const row = liveObject(value)
    return [{
      id,
      label: liveSafeText(name, liveAdapterLimits.label),
      description: 'Source-confirmed upstream MoA preset.',
      referenceCount: Math.min(8, Math.max(1, boundedArray(row.reference_models, 8).length || 1)),
    }]
  })
  const requested = liveSafeId(raw.active_preset) || liveSafeId(raw.default_preset)
  const presetId = presets.some((preset) => preset.id === requested) ? requested : presets[0]?.id ?? ''
  const selectedRaw = liveObject(liveObject(raw.presets)[presetId])
  const referenceModelIds = boundedArray(selectedRaw.reference_models, 8).map(modelSlotId).filter(Boolean)
  const aggregatorModelId = modelSlotId(selectedRaw.aggregator)
  return {
    presets,
    settings: {
      enabled: selectedRaw.enabled === true && Boolean(presetId),
      presetId,
      referenceModelIds,
      aggregatorModelId,
    },
  }
}

export function buildLiveAdvancedModelSettings(
  options: NormalizedModelOptions,
  auxiliaryValue: unknown,
  moaValue: unknown,
): { settings: AdvancedModelSettings; presets: MoaPreset[] } {
  const auxiliary = normalizeLiveAuxiliarySettings(auxiliaryValue)
  const moa = normalizeLiveMoa(moaValue)
  const defaultModelId = auxiliary.defaultModelId || liveModelId(options.currentProvider, options.currentModel)
  return {
    settings: {
      defaultModelId,
      auxiliaryModels: auxiliary.auxiliaryModels,
      taskOverrides: [],
      moa: moa.settings,
      providers: [],
    },
    presets: moa.presets,
  }
}
