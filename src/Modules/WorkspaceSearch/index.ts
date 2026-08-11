export { WorkspaceSearchController } from './WorkspaceSearchController'
export type { WorkspaceSearchControllerOptions } from './WorkspaceSearchController'
export { WorkspaceSearchPanel } from './WorkspaceSearchPanel'
export type { WorkspaceSearchPanelProps } from './WorkspaceSearchPanel'
export { DesktopWorkspaceSearchClient, createDesktopWorkspaceSearchProviders } from './DesktopWorkspaceSearchProviders'
export type { WorkspaceSearchHostMessageBridge } from './DesktopWorkspaceSearchProviders'
export {
  isSafeWorkspaceSearchPath,
  normalizeWorkspaceSearchMessage,
  normalizeWorkspaceSearchOutput,
  normalizeWorkspaceSearchProgress,
  normalizeWorkspaceSearchQuery,
} from './safety'
export type { NormalizedWorkspaceSearchOutput } from './safety'
export * from './contracts'
