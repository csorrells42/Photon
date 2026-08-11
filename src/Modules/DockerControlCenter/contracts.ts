export const DOCKER_CONTROL_PROTOCOL_VERSION = 1 as const

export const DOCKER_CONTROL_LIMITS = {
  messageCharacters: 256,
  versionCharacters: 128,
  revisionCharacters: 128,
  warnings: 8,
  logEntries: 200,
  logLineCharacters: 512,
  logTotalCharacters: 32_768,
  requestedLogLines: 200,
} as const

export const DOCKER_PRODUCT_SERVICES = ['hermes', 'serena', 'model-runner'] as const
export type DockerProductService = typeof DOCKER_PRODUCT_SERVICES[number]
export type DockerCoreService = Exclude<DockerProductService, 'model-runner'>

export type DockerAvailability =
  | { state: 'available' }
  | { state: 'unavailable'; reason: 'not-registered' | 'engine-unavailable' | 'unsupported' | 'disabled' }

export type DockerObservedState = 'running' | 'stopped' | 'degraded' | 'unavailable' | 'unknown'
export type DockerHealthState = 'healthy' | 'unhealthy' | 'starting' | 'not-configured' | 'unknown'
export type DockerVerificationState = 'verified' | 'mismatch' | 'unverified' | 'unavailable'

export type DockerImageIdentity = {
  imageId?: string
  approvedDigest?: string
  ociRevision?: string
  verification: DockerVerificationState
}

export type DockerLoopbackPort = {
  address: '127.0.0.1' | '::1'
  hostPort: number
  containerPort: number
  protocol: 'tcp' | 'udp'
}

export type DockerProductServiceSnapshot = {
  id: DockerProductService
  state: DockerObservedState
  health: DockerHealthState
  version?: string
  image?: DockerImageIdentity
  ports: readonly DockerLoopbackPort[]
}

export type DockerVolumeSnapshot = {
  role: 'data' | 'workspace'
  state: 'mounted' | 'unmounted' | 'unavailable' | 'unknown'
  persistent: boolean
}

export type DockerWorkflowSummary = {
  kind: 'update' | 'rollback'
  state: 'succeeded' | 'failed' | 'cancelled' | 'unknown'
  completedAtUtc?: string
  summary: string
}

export type DockerStackSnapshot = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  revision: number
  observedAtUtc: string
  engine: { state: DockerObservedState; version?: string }
  compose: {
    state: DockerObservedState
    definitionFingerprint?: string
    upstreamRevision?: string
    runtimeProtocol?: string
  }
  services: readonly DockerProductServiceSnapshot[]
  volumes: readonly DockerVolumeSnapshot[]
  lastWorkflow?: DockerWorkflowSummary
}

export type DockerLogEntry = {
  timestampUtc?: string
  stream: 'stdout' | 'stderr' | 'system'
  text: string
}

export type DockerLogsRequest = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  requestId: string
  service: DockerProductService
  maxLines: number
}

export type DockerLogsResult = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  requestId: string
  service: DockerProductService
  entries: readonly DockerLogEntry[]
  truncated: boolean
}

export type DockerMutationIntent =
  | { kind: 'start-stack' }
  | { kind: 'stop-stack' }
  | { kind: 'restart-service'; service: DockerProductService }
  | { kind: 'request-update' }

export type DockerMutationReviewRequest = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  requestId: string
  snapshotRevision: number
  intent: DockerMutationIntent
}

export type DockerMutationReviewResult =
  | {
      protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
      requestId: string
      status: 'ready'
      snapshotRevision: number
      reviewToken: string
      fingerprint: string
      expiresAtUtc: string
      affectedServices: readonly DockerProductService[]
      summary: string
      warnings: readonly string[]
    }
  | {
      protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
      requestId: string
      status: 'rejected'
      message: string
    }

export type DockerMutationCommitRequest = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  requestId: string
  reviewToken: string
}

export type DockerMutationCommitResult = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  requestId: string
  status: 'succeeded' | 'failed' | 'stale' | 'expired'
  message: string
  snapshot?: unknown
}

export type DockerControlExecution = { signal: AbortSignal }

export type DockerControlOperations = {
  startStack: boolean
  stopStack: boolean
  restartService: boolean
  update: false
}

export type DockerControlDescription = {
  protocolVersion: typeof DOCKER_CONTROL_PROTOCOL_VERSION
  availability: { state: 'available' } | { state: 'unavailable'; reason: 'engine-unavailable' | 'unsupported' | 'disabled' }
  services: readonly DockerProductService[]
  operations: DockerControlOperations
  updateReason: 'derived-runtime-updater-not-integrated'
}

/**
 * Trusted composition injects this adapter. The deliberately narrow contract has no command,
 * executable, argument, environment, path, Compose-file, registry-auth, or generic service field.
 */
export type DockerControlAdapter = {
  availability: DockerAvailability
  describe(execution: DockerControlExecution): Promise<DockerControlDescription>
  refresh(execution: DockerControlExecution): Promise<unknown>
  readLogs(request: DockerLogsRequest, execution: DockerControlExecution): Promise<unknown>
  reviewMutation(request: DockerMutationReviewRequest, execution: DockerControlExecution): Promise<unknown>
  commitMutation(request: DockerMutationCommitRequest, execution: DockerControlExecution): Promise<unknown>
  discardReview?(reviewToken: string): Promise<void> | void
}

export type DockerControllerStatus = 'idle' | 'refreshing' | 'ready' | 'unavailable' | 'error'
export type DockerMutationStatus = 'idle' | 'reviewing' | 'awaiting-confirmation' | 'committing'
export type DockerLogsStatus = 'idle' | 'loading' | 'ready' | 'error'

export type DockerBoundReview = Extract<DockerMutationReviewResult, { status: 'ready' }> & {
  intent: DockerMutationIntent
  controllerGeneration: number
}

export type DockerControlState = {
  status: DockerControllerStatus
  mutationStatus: DockerMutationStatus
  logsStatus: DockerLogsStatus
  snapshot: DockerStackSnapshot | null
  selectedService: DockerProductService
  logs: readonly DockerLogEntry[]
  logsTruncated: boolean
  review: DockerBoundReview | null
  operations: DockerControlOperations
  updateReason: DockerControlDescription['updateReason'] | null
  message: string
}
