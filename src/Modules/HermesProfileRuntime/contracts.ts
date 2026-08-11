export const HERMES_PROFILE_RUNTIME_CONTRACT = 'hermes-profile-runtime/v1' as const

export type ProfileId = string
export type CorrelationId = string
export type Availability = 'ready' | 'partial' | 'error' | 'unavailable'
export type CapabilityDisposition = 'manageable' | 'delegated' | 'unsupported'

export type RequestContext = {
  contract: typeof HERMES_PROFILE_RUNTIME_CONTRACT
  profileId: ProfileId
  correlationId: CorrelationId
  expectedRevision?: number
}

export type AdapterResponse<T> = RequestContext & {
  revision: number
  value: T
}

export type ProfileSummary = {
  id: ProfileId
  name: string
  description: string
  isActive: boolean
  isDeleteProtected: boolean
}

export type EditableProfileDocument = {
  kind: 'persona' | 'soul' | 'context'
  text: string
  maximumCharacters: number
}

export type ProfileIntent = {
  modelId: string | null
  projectPath: string | null
  worktreePath: string | null
  note: string
}

export type HermesTerminalBackend = {
  id: string
  label: string
  description: string
  selected: boolean
  availability: Availability
  detail: string
}

export type ComputerUsePermission = {
  id: string
  label: string
  description: string
  state: 'not_requested' | 'requested' | 'granted' | 'denied' | 'unavailable'
  disposition: CapabilityDisposition
  detail: string
}

export type ComputerUseStatus = {
  availability: Availability
  detail: string
  permissions: ComputerUsePermission[]
}

export type ProviderRuntimeStatus = {
  id: string
  label: string
  oauth: 'connected' | 'disconnected' | 'expired' | 'unsupported' | 'unavailable'
  credentialPool: 'configured' | 'empty' | 'delegated' | 'unsupported' | 'unavailable'
  configuredCredentialSlots: number
  customEndpoint: {
    state: 'configured' | 'not_configured' | 'delegated' | 'unsupported' | 'unavailable'
    origin: string | null
  }
  disposition: CapabilityDisposition
  detail: string
}

export type ConfigurationFieldSchema = {
  key: string
  label: string
  description: string
  kind: 'boolean' | 'integer' | 'string' | 'enum'
  required: boolean
  minimum?: number
  maximum?: number
  maximumLength?: number
  options?: string[]
  disposition: CapabilityDisposition
}

export type ConfigurationValue = boolean | number | string | null

export type ProfileRuntimeSnapshot = {
  contract: typeof HERMES_PROFILE_RUNTIME_CONTRACT
  profileId: ProfileId
  revision: number
  availability: Availability
  detail: string
  profiles: ProfileSummary[]
  documents: EditableProfileDocument[]
  intent: ProfileIntent
  terminalBackends: HermesTerminalBackend[]
  computerUse: ComputerUseStatus
  providers: ProviderRuntimeStatus[]
  configurationSchema: ConfigurationFieldSchema[]
  configuration: Record<string, ConfigurationValue>
}

export type ProfileMutation =
  | { kind: 'create'; name: string }
  | { kind: 'clone'; sourceProfileId: ProfileId; name: string }
  | { kind: 'rename'; name: string }
  | { kind: 'delete' }

export type OperationPreview = {
  previewId: string
  operation: ProfileMutation['kind'] | 'import' | 'permission_grant'
  profileId: ProfileId
  title: string
  summary: string
  consequences: string[]
  destructive: boolean
  confirmationPhrase: string | null
  expiresAt: string
}

export type PreviewProfileMutationRequest = RequestContext & { mutation: ProfileMutation }
export type ApplyProfileMutationRequest = RequestContext & {
  mutation: ProfileMutation
  previewId: string
  destructiveConfirmation?: { phrase: string }
}

export type SelectActiveProfileRequest = RequestContext & { targetProfileId: ProfileId }
export type ProfileSelectionResult = { activeProfileId: ProfileId }
export type ProfileMutationResult = { affectedProfileId: ProfileId; activeProfileId: ProfileId }
export type SaveDocumentRequest = RequestContext & {
  document: EditableProfileDocument['kind']
  text: string
}
export type SaveIntentRequest = RequestContext & { intent: ProfileIntent }
export type SelectTerminalBackendRequest = RequestContext & { backendId: string }

export type PermissionGrantIntent = {
  permissionId: string
  duration: 'once' | 'session' | 'persistent'
  rationale: string
}
export type PreviewPermissionGrantRequest = RequestContext & { intent: PermissionGrantIntent }
export type ConfirmPermissionGrantRequest = RequestContext & {
  intent: PermissionGrantIntent
  previewId: string
  confirmationPhrase: string
}

export type SaveConfigurationRequest = RequestContext & {
  values: Record<string, ConfigurationValue>
}

export type ProfileImportData = {
  format: typeof HERMES_PROFILE_RUNTIME_CONTRACT
  name: string
  documents: Partial<Record<EditableProfileDocument['kind'], string>>
  intent: ProfileIntent
  configuration: Record<string, ConfigurationValue>
}

export type ProfileImportInspection = {
  data: ProfileImportData
  warnings: string[]
  characterCount: number
}

export type PreviewImportRequest = RequestContext & {
  inspection: ProfileImportInspection
}
export type ApplyImportRequest = RequestContext & {
  inspection: ProfileImportInspection
  previewId: string
  confirmationPhrase: string
}
export type ExportProfileRequest = RequestContext
export type ProfileExport = { fileName: string; text: string; byteCount: number }

export interface HermesProfileRuntimeAdapter {
  load(context: RequestContext, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  selectActiveProfile(request: SelectActiveProfileRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileSelectionResult>>
  previewProfileMutation(request: PreviewProfileMutationRequest, signal: AbortSignal): Promise<AdapterResponse<OperationPreview>>
  applyProfileMutation(request: ApplyProfileMutationRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileMutationResult>>
  saveDocument(request: SaveDocumentRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  saveIntent(request: SaveIntentRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  selectTerminalBackend(request: SelectTerminalBackendRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  previewPermissionGrant(request: PreviewPermissionGrantRequest, signal: AbortSignal): Promise<AdapterResponse<OperationPreview>>
  confirmPermissionGrant(request: ConfirmPermissionGrantRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  saveConfiguration(request: SaveConfigurationRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  previewImport(request: PreviewImportRequest, signal: AbortSignal): Promise<AdapterResponse<OperationPreview>>
  applyImport(request: ApplyImportRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>>
  exportProfile(request: ExportProfileRequest, signal: AbortSignal): Promise<AdapterResponse<ProfileExport>>
}
