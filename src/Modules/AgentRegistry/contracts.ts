import type { ReactNode } from 'react'

export const AGENT_PANEL_REGISTRY_FORMAT = 'agent-panel-registry/v1' as const
export const AGENT_PANEL_RENDER_CONTRACT = 'agent-panel-render/v1' as const

export type AgentPanelId = string
export type ProviderKind = string
export type AgentPanelLifecycle = 'available' | 'starting' | 'stopping' | 'unavailable' | 'retired'
export type AgentPanelConnectionState = 'not_configured' | 'disconnected' | 'connecting' | 'connected' | 'degraded' | 'error' | 'unavailable'
export type AgentPanelAuthenticationDisposition = 'none' | 'host_managed' | 'user_action_required' | 'delegated' | 'unsupported' | 'unavailable'
export type AgentPanelCapabilityAvailability = 'available' | 'unavailable' | 'unknown'
export type AgentPanelDockZone = 'top' | 'right' | 'bottom' | 'left' | 'center'
export type AgentPanelDockMode = 'vertical' | 'horizontal' | 'tabs'

export type AgentPanelDisplayMetadata = {
  label: string
  shortLabel: string
  description: string
  iconKey: string
  accent: string
}

export type AgentPanelCapability = {
  id: string
  label: string
  description: string
  availability: AgentPanelCapabilityAvailability
}

export type AgentPanelDockPlacementIntent = {
  preferredZone: AgentPanelDockZone
  preferredMode: AgentPanelDockMode
  minimumWidth: number
  minimumHeight: number
  placementPriority: number
}

export type AgentPanelRenderBoundary = {
  contract: typeof AGENT_PANEL_RENDER_CONTRACT
  factoryKey: string
}

export type AgentPanelDescriptor = {
  id: AgentPanelId
  providerKind: ProviderKind
  display: AgentPanelDisplayMetadata
  capabilities: readonly AgentPanelCapability[]
  lifecycle: AgentPanelLifecycle
  lifecycleDetail: string
  connectionState: AgentPanelConnectionState
  connectionDetail: string
  authenticationDisposition: AgentPanelAuthenticationDisposition
  authenticationDetail: string
  dock: AgentPanelDockPlacementIntent
  render: AgentPanelRenderBoundary
  enabled: boolean
}

export type AgentPanelRegistrySnapshot = {
  format: typeof AGENT_PANEL_REGISTRY_FORMAT
  order: readonly AgentPanelId[]
  panels: readonly AgentPanelDescriptor[]
}

export type PersistedAgentPanelRegistry = AgentPanelRegistrySnapshot

export type AgentPanelRuntimePatch = {
  lifecycle?: AgentPanelLifecycle
  lifecycleDetail?: string
  connectionState?: AgentPanelConnectionState
  connectionDetail?: string
  authenticationDisposition?: AgentPanelAuthenticationDisposition
  authenticationDetail?: string
}

export type AgentPanelFactoryContext = {
  panel: AgentPanelDescriptor
  dockControls: ReactNode
}

export type AgentPanelFactory = (context: AgentPanelFactoryContext) => ReactNode

export type AgentPanelDockRegistration = {
  id: AgentPanelId
  label: string
  render: (dockControls: ReactNode) => ReactNode
}
