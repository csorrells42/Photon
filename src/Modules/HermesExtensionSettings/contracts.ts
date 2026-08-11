export const HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION = 'hermes-extension-settings/v1' as const

export const extensionLimits = {
  collection: 128,
  smallCollection: 32,
  identifier: 128,
  label: 256,
  text: 8_192,
  content: 256_000,
} as const

export type ExtensionDataState = 'ready' | 'partial' | 'unavailable' | 'error'
export type ExtensionProvenance = 'nous-approved' | 'workbench-reviewed' | 'external-unreviewed' | 'user-created'
export type ToolCapability = 'search' | 'extract'
export type ModelSpecialty = 'general' | 'search' | 'extract' | 'vision' | 'coding' | 'summarization' | 'aggregation'
export type ModelCostTier = 'standard' | 'expensive'
export type AuxiliaryTask = 'vision' | 'compression' | 'title-generation' | 'summarization'
export type TaskOverrideKind = 'research' | 'coding' | 'browser' | 'document' | 'analysis'
export type ProviderValidationState = 'valid' | 'invalid' | 'unavailable' | 'partial'

export interface ExtensionNotice {
  id: string
  level: 'info' | 'warning' | 'error'
  message: string
}

export interface SkillDocument {
  id: string
  name: string
  description: string
  content: string
  provenance: ExtensionProvenance
  editable: boolean
}

export interface CapabilityAdvertisement {
  capability: ToolCapability
  backendIds: string[]
}

export interface ToolBackend {
  id: string
  label: string
  description: string
}

export interface ToolsetProvider {
  id: string
  label: string
  provenance: ExtensionProvenance
  state: ExtensionDataState
  capabilities: CapabilityAdvertisement[]
  backends: ToolBackend[]
}

export interface ModelOption {
  id: string
  label: string
  providerId: string
  specialties: ModelSpecialty[]
  costTier: ModelCostTier
  available: boolean
}

export interface ToolsetSelection {
  providerId: string
  backendId: string
  specialtyModelId: string
}

export interface ToolsetSettings {
  search: ToolsetSelection
  extract: ToolsetSelection
}

export interface AuxiliaryModelSelection {
  task: AuxiliaryTask
  modelId: string
}

export interface TaskModelOverride {
  task: TaskOverrideKind
  modelId: string
}

export interface MoaPreset {
  id: string
  label: string
  description: string
  referenceCount: number
}

export interface MoaSettings {
  enabled: boolean
  presetId: string
  referenceModelIds: string[]
  aggregatorModelId: string
}

export interface CustomEndpointSettings {
  baseUrl: string
  apiPath: string
  authHeaderName: string
}

export interface ProviderModelSettings {
  providerId: string
  timeoutMs: number
  maxRetries: number
  customEndpoint: CustomEndpointSettings | null
}

export interface AdvancedModelSettings {
  defaultModelId: string
  auxiliaryModels: AuxiliaryModelSelection[]
  taskOverrides: TaskModelOverride[]
  moa: MoaSettings
  providers: ProviderModelSettings[]
}

export interface McpServerConfiguration {
  id: string
  name: string
  transport: 'http' | 'stdio'
  endpoint: string
  command: string
  args: string[]
  environmentVariableNames: string[]
  enabled: boolean
  provenance: ExtensionProvenance
}

export interface HermesExtensionSettingsSnapshot {
  contractVersion: typeof HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION
  state: ExtensionDataState
  skills: SkillDocument[]
  toolsetProviders: ToolsetProvider[]
  models: ModelOption[]
  toolsets: ToolsetSettings
  auxiliaryTasks: AuxiliaryTask[]
  taskOverrideKinds: TaskOverrideKind[]
  moaPresets: MoaPreset[]
  modelSettings: AdvancedModelSettings
  mcpServers: McpServerConfiguration[]
  notices: ExtensionNotice[]
}

export type SkillWriteIntent =
  | { kind: 'create'; name: string; description: string; content: string }
  | { kind: 'edit'; skillId: string; name: string; description: string; content: string }
  | { kind: 'clone-to-user'; sourceSkillId: string; name: string; description: string; content: string }

export interface ToolsetWriteIntent {
  kind: 'toolsets'
  search: ToolsetSelection
  extract: ToolsetSelection
}

export interface ModelSettingsWriteIntent {
  kind: 'models'
  settings: AdvancedModelSettings
}

export type McpWriteIntent =
  | { kind: 'mcp-update'; serverId: string; configuration: McpServerConfiguration }
  | { kind: 'mcp-enable'; serverId: string; enabled: boolean }
  | { kind: 'mcp-test'; serverId: string }

export type ExtensionWriteIntent = SkillWriteIntent | ToolsetWriteIntent | ModelSettingsWriteIntent | McpWriteIntent

export interface WriteReview {
  reviewId: string
  kind: ExtensionWriteIntent['kind']
  title: string
  before: string[]
  after: string[]
  warnings: string[]
  requiresExpensiveModelConfirmation: boolean
}

export interface CommitRequest {
  reviewId: string
  confirmed: true
  expensiveModelConfirmed?: boolean
}

export interface CommitResult {
  status: 'success' | 'unavailable' | 'error'
  message: string
  snapshot?: HermesExtensionSettingsSnapshot
}

/** The secret value is write-only input. Controllers must never retain or return it. */
export interface SecretWriteIntent {
  fieldName: string
  value: string
}

export interface ProviderValidationIntent {
  providerId: string
  endpoint: CustomEndpointSettings | null
  secrets: readonly SecretWriteIntent[]
}

export interface ProviderValidationResult {
  providerId: string
  state: ProviderValidationState
  message: string
}

export interface LoadResult {
  state: ExtensionDataState
  snapshot?: HermesExtensionSettingsSnapshot
  message?: string
}

export interface HermesExtensionSettingsController {
  load(): Promise<LoadResult>
  preview(intent: ExtensionWriteIntent): Promise<WriteReview>
  commit(request: CommitRequest): Promise<CommitResult>
  validateProvider(intent: ProviderValidationIntent): Promise<ProviderValidationResult>
}
