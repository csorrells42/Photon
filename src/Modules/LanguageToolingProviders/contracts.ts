export const LANGUAGE_TOOLING_CONTRACT = 'language-tooling-providers/v1' as const

export type LanguageToolingProviderId =
  | 'dotnet'
  | 'java-jdt'
  | 'arduino'
  | 'python'
  | 'gcc'
  | 'raspberry-pi'
export type LanguageToolingCapabilityKind =
  | 'language-server'
  | 'compiler'
  | 'test-runner'
  | 'debug-adapter'
  | 'project-inspection'
  | 'remote-host'
  | 'deployment'

export type LanguageToolingCapabilityDeclaration = {
  id: string
  kind: LanguageToolingCapabilityKind
  label: string
  features: readonly string[]
  protocol?: 'lsp' | 'dap'
  integration: 'existing-host-reference' | 'host-adapter-required'
  adapterKey: string
}

/**
 * Static declarations describe what an adapter is intended to provide. They are deliberately not
 * runtime availability claims; only trusted host evidence can make a capability available.
 */
export type LanguageToolingProviderDescriptor = {
  contract: typeof LANGUAGE_TOOLING_CONTRACT
  id: LanguageToolingProviderId
  label: string
  languages: readonly string[]
  projectKinds: readonly string[]
  capabilities: readonly LanguageToolingCapabilityDeclaration[]
}

export type HostCapabilityAvailability = 'available' | 'unavailable' | 'error'

export type HostCapabilityEvidence = {
  capabilityId: string
  availability: HostCapabilityAvailability
  code: string
  detail: string
  version?: string
}

export type TrustedHostProviderEvidence = {
  contract: typeof LANGUAGE_TOOLING_CONTRACT
  source: 'trusted-host'
  evidenceId: string
  providerId: LanguageToolingProviderId
  checkedAt: string
  capabilities: readonly HostCapabilityEvidence[]
}

export type ReportedCapabilityAvailability = HostCapabilityAvailability | 'unknown'

export type LanguageToolingCapabilityReport = LanguageToolingCapabilityDeclaration & {
  availability: ReportedCapabilityAvailability
  code: string
  detail: string
  version?: string
  checkedAt?: string
  evidenceId?: string
}

export type LanguageToolingProviderReport = Omit<LanguageToolingProviderDescriptor, 'capabilities'> & {
  capabilities: readonly LanguageToolingCapabilityReport[]
}

export type LanguageToolingRequestEnvelope = {
  contract: typeof LANGUAGE_TOOLING_CONTRACT
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
}

export type InspectProviderIntent = {
  operation: 'inspect-provider'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
}

export type StartLanguageSessionIntent = {
  operation: 'start-language-session'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  documentPath: string
}

export type StopLanguageSessionIntent = {
  operation: 'stop-language-session'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  sessionId: string
}

export type InspectProjectIntent = {
  operation: 'inspect-project'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  projectPath: string
}

export type CompileIntent = {
  operation: 'compile'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  targetPath: string
  mode?: 'debug' | 'release' | 'check'
  boardFqbn?: string
}

export type RunTestsIntent = {
  operation: 'run-tests'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  targetPath: string
  selection?: string
}

export type StartDebugIntent = {
  operation: 'start-debug'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  programPath: string
  stopAtEntry?: boolean
}

export type InspectRemoteTargetIntent = {
  operation: 'inspect-remote-target'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  targetId: string
}

export type DeployFileIntent = {
  operation: 'deploy-file'
  requestId: string
  workspaceId: string
  providerId: LanguageToolingProviderId
  targetId: string
  sourcePath: string
  destinationId: string
}

export type LanguageToolingHostIntent =
  | InspectProviderIntent
  | StartLanguageSessionIntent
  | StopLanguageSessionIntent
  | InspectProjectIntent
  | CompileIntent
  | RunTestsIntent
  | StartDebugIntent
  | InspectRemoteTargetIntent
  | DeployFileIntent

/**
 * These messages contain structured intent only. There is no executable, argument vector,
 * environment, or command-text field for renderer code to populate.
 */
export type LanguageToolingHostRequest =
  | (LanguageToolingRequestEnvelope & { operation: 'inspect-provider' })
  | (LanguageToolingRequestEnvelope & {
      operation: 'start-language-session'
      documentPath: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'stop-language-session'
      sessionId: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'inspect-project'
      projectPath: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'compile'
      targetPath: string
      mode: 'debug' | 'release' | 'check'
      boardFqbn?: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'run-tests'
      targetPath: string
      selection?: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'start-debug'
      programPath: string
      stopAtEntry: boolean
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'inspect-remote-target'
      targetId: string
    })
  | (LanguageToolingRequestEnvelope & {
      operation: 'deploy-file'
      targetId: string
      sourcePath: string
      destinationId: string
    })

export interface LanguageToolingHostAdapter {
  request(request: LanguageToolingHostRequest, signal: AbortSignal): Promise<unknown>
}
