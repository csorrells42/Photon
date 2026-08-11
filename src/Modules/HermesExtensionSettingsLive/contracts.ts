import type { HermesExtensionSettingsSnapshot } from '../HermesExtensionSettings/contracts'

export const HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION = 'hermes-extension-settings-live/v1' as const

export const liveAdapterLimits = {
  responseBytes: 1_048_576,
  collection: 128,
  skillContents: 32,
  identifier: 128,
  label: 256,
  text: 8_192,
  content: 256_000,
  recentCorrelations: 128,
} as const

export type LiveSourceId =
  | 'skills'
  | 'skill-contents'
  | 'toolsets'
  | 'web-toolset-config'
  | 'mcp-servers'
  | 'mcp-catalog'
  | 'model-options'
  | 'model-info'
  | 'model-auxiliary'
  | 'model-moa'

export type LiveSourceState = 'ready' | 'partial' | 'unavailable' | 'error' | 'cancelled'
export type LiveReadState = 'ready' | 'partial' | 'unavailable' | 'error' | 'cancelled' | 'duplicate'

export interface LiveSourceReport {
  source: LiveSourceId
  route: string
  state: LiveSourceState
  itemCount: number
  message: string
}

export interface HermesExtensionSettingsLiveReadRequest {
  profileId?: string
  correlationId: string
  signal?: AbortSignal
}

export interface HermesExtensionSettingsLiveReadResult {
  contractVersion: typeof HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION
  correlationId: string
  profileId: string | null
  state: LiveReadState
  snapshot: HermesExtensionSettingsSnapshot
  sources: LiveSourceReport[]
  completedAt: string
}

export type TrustSubjectKind = 'skill' | 'toolset-provider' | 'mcp-server' | 'model-provider'

export interface WorkbenchReviewAttestation {
  subject: TrustSubjectKind
  id: string
}

export interface HermesExtensionSettingsLiveAdapterOptions {
  workbenchReviews?: readonly WorkbenchReviewAttestation[]
  now?: () => Date
}

export type UnsupportedMutationKind =
  | 'skill-create'
  | 'skill-edit'
  | 'skill-install'
  | 'toolset-configure'
  | 'model-configure'
  | 'mcp-configure'

export interface UnsupportedMutationResult {
  status: 'unavailable'
  kind: UnsupportedMutationKind
  message: string
}

export type LiveRouteCapability =
  | 'skill-inventory'
  | 'skill-content'
  | 'toolset-inventory'
  | 'search-extract-provider-matrix'
  | 'mcp-server-metadata'
  | 'mcp-nous-catalog-provenance'
  | 'model-catalog'
  | 'current-model-metadata'
  | 'auxiliary-model-assignments'
  | 'moa-presets'

export interface LiveRouteDefinition {
  source: LiveSourceId
  method: 'GET'
  path: string
  capability: LiveRouteCapability
  profileScoped: boolean
}

export const liveExtensionSettingsRoutes: readonly LiveRouteDefinition[] = [
  { source: 'skills', method: 'GET', path: '/api/skills', capability: 'skill-inventory', profileScoped: true },
  { source: 'skill-contents', method: 'GET', path: '/api/skills/content', capability: 'skill-content', profileScoped: true },
  { source: 'toolsets', method: 'GET', path: '/api/tools/toolsets', capability: 'toolset-inventory', profileScoped: true },
  { source: 'web-toolset-config', method: 'GET', path: '/api/tools/toolsets/web/config', capability: 'search-extract-provider-matrix', profileScoped: true },
  { source: 'mcp-servers', method: 'GET', path: '/api/mcp/servers', capability: 'mcp-server-metadata', profileScoped: true },
  { source: 'mcp-catalog', method: 'GET', path: '/api/mcp/catalog', capability: 'mcp-nous-catalog-provenance', profileScoped: true },
  { source: 'model-options', method: 'GET', path: '/api/model/options', capability: 'model-catalog', profileScoped: true },
  { source: 'model-info', method: 'GET', path: '/api/model/info', capability: 'current-model-metadata', profileScoped: true },
  { source: 'model-auxiliary', method: 'GET', path: '/api/model/auxiliary', capability: 'auxiliary-model-assignments', profileScoped: true },
  { source: 'model-moa', method: 'GET', path: '/api/model/moa', capability: 'moa-presets', profileScoped: true },
]
