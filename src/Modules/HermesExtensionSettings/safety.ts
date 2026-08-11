import {
  HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
  extensionLimits,
  type AdvancedModelSettings,
  type AuxiliaryTask,
  type ExtensionDataState,
  type ExtensionNotice,
  type ExtensionProvenance,
  type HermesExtensionSettingsSnapshot,
  type McpServerConfiguration,
  type ModelOption,
  type ModelSpecialty,
  type MoaPreset,
  type SkillDocument,
  type TaskOverrideKind,
  type ToolCapability,
  type ToolsetProvider,
  type ToolsetSelection,
} from './contracts'

type JsonObject = Record<string, unknown>

const provenanceValues: readonly ExtensionProvenance[] = ['nous-approved', 'workbench-reviewed', 'external-unreviewed', 'user-created']
const auxiliaryValues: readonly AuxiliaryTask[] = ['vision', 'compression', 'title-generation', 'summarization']
const overrideValues: readonly TaskOverrideKind[] = ['research', 'coding', 'browser', 'document', 'analysis']
const specialtyValues: readonly ModelSpecialty[] = ['general', 'search', 'extract', 'vision', 'coding', 'summarization', 'aggregation']
const forbiddenKey = /(?:secret|token|password|credential|api[_-]?key|authorization|cookie)/i
const credentialText = /(?:\bAuthorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._~+\/-]{8,}|\bBearer\s+[A-Za-z0-9._~+\/-]{8,}|\bsk-[A-Za-z0-9_-]{8,}|\b(?:api[_-]?key|token|password|authorization)\s*[:=]\s*\S+)/gi

function object(value: unknown): JsonObject {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {}
  return Object.fromEntries(Object.entries(value as JsonObject).filter(([key]) => !forbiddenKey.test(key)))
}

export function safeText(value: unknown, maximum: number = extensionLimits.text) {
  if (typeof value !== 'string') return ''
  return value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '').replace(credentialText, '[redacted]').trim().slice(0, maximum)
}

function id(value: unknown) {
  return safeText(value, extensionLimits.identifier).replace(/[^A-Za-z0-9._:-]/g, '-')
}

function textList(value: unknown, maximum: number = extensionLimits.smallCollection, itemMaximum: number = extensionLimits.label) {
  if (!Array.isArray(value)) return []
  return [...new Set(value.map((item) => safeText(item, itemMaximum)).filter(Boolean))].slice(0, maximum)
}

function state(value: unknown): ExtensionDataState {
  return value === 'ready' || value === 'partial' || value === 'unavailable' ? value : 'error'
}

function provenance(value: unknown): ExtensionProvenance {
  return provenanceValues.includes(value as ExtensionProvenance) ? value as ExtensionProvenance : 'external-unreviewed'
}

function integer(value: unknown, fallback: number, minimum: number, maximum: number) {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.min(maximum, Math.max(minimum, Math.round(value)))
    : fallback
}

function boolean(value: unknown) {
  return value === true
}

function normalizeSkill(value: unknown): SkillDocument | null {
  const raw = object(value)
  const skillId = id(raw.id)
  const name = safeText(raw.name, extensionLimits.label)
  if (!skillId || !name) return null
  const source = provenance(raw.provenance)
  return {
    id: skillId,
    name,
    description: safeText(raw.description, 2_048),
    content: safeText(raw.content, extensionLimits.content),
    provenance: source,
    editable: source === 'user-created' && raw.editable !== false,
  }
}

function normalizeProvider(value: unknown): ToolsetProvider | null {
  const raw = object(value)
  const providerId = id(raw.id)
  const label = safeText(raw.label, extensionLimits.label)
  if (!providerId || !label) return null
  const backendsRaw = Array.isArray(raw.backends) ? raw.backends : []
  const backends = backendsRaw.flatMap((entry) => {
    const row = object(entry)
    const backendId = id(row.id)
    return backendId ? [{
      id: backendId,
      label: safeText(row.label, extensionLimits.label) || backendId,
      description: safeText(row.description, 1_024),
    }] : []
  }).slice(0, extensionLimits.smallCollection)
  const backendIds = new Set(backends.map((backend) => backend.id))
  const capabilitiesRaw = Array.isArray(raw.capabilities) ? raw.capabilities : []
  const capabilities = capabilitiesRaw.flatMap((entry) => {
    const row = object(entry)
    if (row.capability !== 'search' && row.capability !== 'extract') return []
    const advertised = textList(row.backendIds, extensionLimits.smallCollection, extensionLimits.identifier).filter((backendId) => backendIds.has(backendId))
    return advertised.length ? [{ capability: row.capability as ToolCapability, backendIds: advertised }] : []
  }).slice(0, 2)
  return { id: providerId, label, provenance: provenance(raw.provenance), state: state(raw.state), capabilities, backends }
}

function normalizeModel(value: unknown): ModelOption | null {
  const raw = object(value)
  const modelId = id(raw.id)
  const label = safeText(raw.label, extensionLimits.label)
  const providerId = id(raw.providerId)
  if (!modelId || !label || !providerId) return null
  const specialties = textList(raw.specialties, specialtyValues.length, 32)
    .filter((item): item is ModelSpecialty => specialtyValues.includes(item as ModelSpecialty))
  return {
    id: modelId,
    label,
    providerId,
    specialties: specialties.length ? specialties : ['general'],
    costTier: raw.costTier === 'expensive' ? 'expensive' : 'standard',
    available: raw.available !== false,
  }
}

function normalizeEndpoint(value: unknown) {
  const raw = object(value)
  const baseUrl = safeText(raw.baseUrl, 2_048)
  let normalizedUrl = ''
  try {
    const parsed = new URL(baseUrl)
    if ((parsed.protocol === 'http:' || parsed.protocol === 'https:') && !parsed.username && !parsed.password && !parsed.search) {
      parsed.hash = ''
      normalizedUrl = parsed.toString()
    }
  } catch {
    normalizedUrl = ''
  }
  if (!normalizedUrl) return null
  const authHeaderName = safeText(raw.authHeaderName, 128)
  return {
    baseUrl: normalizedUrl,
    apiPath: safeText(raw.apiPath, 512).replace(/[^A-Za-z0-9._~!$&'()*+,;=:@\/-]/g, ''),
    authHeaderName: /^[A-Za-z][A-Za-z0-9-]{0,127}$/.test(authHeaderName) ? authHeaderName : '',
  }
}

function normalizeMcp(value: unknown): McpServerConfiguration | null {
  const raw = object(value)
  const serverId = id(raw.id)
  const name = safeText(raw.name, extensionLimits.label)
  if (!serverId || !name) return null
  const transport = raw.transport === 'stdio' ? 'stdio' : 'http'
  let endpoint = safeText(raw.endpoint, 2_048)
  if (transport === 'http') {
    try {
      const parsed = new URL(endpoint)
      endpoint = parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.toString() : ''
    } catch {
      endpoint = ''
    }
  } else {
    endpoint = ''
  }
  return {
    id: serverId,
    name,
    transport,
    endpoint,
    command: transport === 'stdio' ? safeText(raw.command, 1_024) : '',
    args: textList(raw.args, 64, 1_024),
    environmentVariableNames: textList(raw.environmentVariableNames, 64, 128).filter((name) => /^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name)),
    enabled: boolean(raw.enabled),
    provenance: provenance(raw.provenance),
  }
}

function selection(value: unknown): ToolsetSelection {
  const raw = object(value)
  return { providerId: id(raw.providerId), backendId: id(raw.backendId), specialtyModelId: id(raw.specialtyModelId) }
}

function modelSettings(value: unknown): AdvancedModelSettings {
  const raw = object(value)
  const auxiliary = Array.isArray(raw.auxiliaryModels) ? raw.auxiliaryModels : []
  const overrides = Array.isArray(raw.taskOverrides) ? raw.taskOverrides : []
  const providers = Array.isArray(raw.providers) ? raw.providers : []
  const moa = object(raw.moa)
  return {
    defaultModelId: id(raw.defaultModelId),
    auxiliaryModels: auxiliary.flatMap((entry) => {
      const row = object(entry)
      return auxiliaryValues.includes(row.task as AuxiliaryTask)
        ? [{ task: row.task as AuxiliaryTask, modelId: id(row.modelId) }]
        : []
    }).slice(0, auxiliaryValues.length),
    taskOverrides: overrides.flatMap((entry) => {
      const row = object(entry)
      return overrideValues.includes(row.task as TaskOverrideKind)
        ? [{ task: row.task as TaskOverrideKind, modelId: id(row.modelId) }]
        : []
    }).slice(0, overrideValues.length),
    moa: {
      enabled: boolean(moa.enabled),
      presetId: id(moa.presetId),
      referenceModelIds: textList(moa.referenceModelIds, 8, extensionLimits.identifier).map(id).filter(Boolean),
      aggregatorModelId: id(moa.aggregatorModelId),
    },
    providers: providers.flatMap((entry) => {
      const row = object(entry)
      const providerId = id(row.providerId)
      return providerId ? [{
        providerId,
        timeoutMs: integer(row.timeoutMs, 30_000, 1_000, 120_000),
        maxRetries: integer(row.maxRetries, 2, 0, 10),
        customEndpoint: normalizeEndpoint(row.customEndpoint),
      }] : []
    }).slice(0, extensionLimits.smallCollection),
  }
}

function list<T>(value: unknown, normalizer: (entry: unknown) => T | null): T[] {
  return (Array.isArray(value) ? value : []).flatMap((entry) => {
    const normalized = normalizer(entry)
    return normalized ? [normalized] : []
  }).slice(0, extensionLimits.collection)
}

export function providersForCapability(snapshot: HermesExtensionSettingsSnapshot, capability: ToolCapability) {
  return snapshot.toolsetProviders.filter((provider) =>
    provider.state !== 'unavailable' && provider.state !== 'error'
      && provider.capabilities.some((advertisement) => advertisement.capability === capability && advertisement.backendIds.length > 0))
}

export function backendsForProviderCapability(provider: ToolsetProvider, capability: ToolCapability) {
  const advertised = new Set(provider.capabilities.find((entry) => entry.capability === capability)?.backendIds ?? [])
  return provider.backends.filter((backend) => advertised.has(backend.id))
}

export function modelsForSpecialty(snapshot: HermesExtensionSettingsSnapshot, specialty: ModelSpecialty) {
  return snapshot.models.filter((model) => model.available && model.specialties.includes(specialty))
}

export function isAdvertisedToolsetSelection(snapshot: HermesExtensionSettingsSnapshot, capability: ToolCapability, candidate: ToolsetSelection) {
  const provider = providersForCapability(snapshot, capability).find((entry) => entry.id === candidate.providerId)
  if (!provider || !backendsForProviderCapability(provider, capability).some((backend) => backend.id === candidate.backendId)) return false
  return modelsForSpecialty(snapshot, capability).some((model) => model.id === candidate.specialtyModelId)
}

export function nextWorkspaceTab(current: number, key: string, count: number) {
  if (count <= 0) return -1
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  if (key === 'ArrowRight' || key === 'ArrowDown') return (current + 1) % count
  if (key === 'ArrowLeft' || key === 'ArrowUp') return (current - 1 + count) % count
  return current
}

export function normalizeExtensionSettingsSnapshot(value: unknown): HermesExtensionSettingsSnapshot {
  const raw = object(value)
  const toolsets = object(raw.toolsets)
  const notices = list(raw.notices, (entry): ExtensionNotice | null => {
    const row = object(entry)
    const noticeId = id(row.id)
    const message = safeText(row.message, 2_048)
    return noticeId && message ? {
      id: noticeId,
      level: row.level === 'error' || row.level === 'warning' ? row.level : 'info',
      message,
    } : null
  })
  const presets = list(raw.moaPresets, (entry): MoaPreset | null => {
    const row = object(entry)
    const presetId = id(row.id)
    const label = safeText(row.label, extensionLimits.label)
    return presetId && label ? {
      id: presetId,
      label,
      description: safeText(row.description, 1_024),
      referenceCount: integer(row.referenceCount, 1, 1, 8),
    } : null
  })
  return {
    contractVersion: HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
    state: state(raw.state),
    skills: list(raw.skills, normalizeSkill),
    toolsetProviders: list(raw.toolsetProviders, normalizeProvider),
    models: list(raw.models, normalizeModel),
    toolsets: { search: selection(toolsets.search), extract: selection(toolsets.extract) },
    auxiliaryTasks: textList(raw.auxiliaryTasks, auxiliaryValues.length, 64).filter((item): item is AuxiliaryTask => auxiliaryValues.includes(item as AuxiliaryTask)),
    taskOverrideKinds: textList(raw.taskOverrideKinds, overrideValues.length, 64).filter((item): item is TaskOverrideKind => overrideValues.includes(item as TaskOverrideKind)),
    moaPresets: presets,
    modelSettings: modelSettings(raw.modelSettings),
    mcpServers: list(raw.mcpServers, normalizeMcp),
    notices,
  }
}
