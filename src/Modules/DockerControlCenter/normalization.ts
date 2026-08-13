import {
  DOCKER_CONTROL_LIMITS,
  DOCKER_CONTROL_PROTOCOL_VERSION,
  DOCKER_PRODUCT_SERVICES,
  type DockerHealthState,
  type DockerImageIdentity,
  type DockerLogsResult,
  type DockerMutationIntent,
  type DockerMutationReviewResult,
  type DockerObservedState,
  type DockerProductService,
  type DockerResourceSnapshot,
  type DockerStackSnapshot,
  type DockerVerificationState,
} from './contracts'

type UnknownRecord = Record<string, unknown>

const CONTROL_AND_DIRECTIONAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/gu
const SHA256 = /^sha256:[a-f0-9]{64}$/u
const CONTAINER_ID = /^[a-f0-9]{12,64}$/u
const MODEL_REFERENCE = /^[A-Za-z0-9][A-Za-z0-9._/:@+-]{0,255}$/u
const OPAQUE_REVIEW_TOKEN = /^[A-Za-z0-9_-]{32,512}$/u
const UTC_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/u
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/gu
const TOKENISH = /\b(?:gh[opusr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b/gu
const AUTH_VALUE = /\b(Bearer|Basic)\s+[A-Za-z0-9+/._=-]{8,}/giu
const KEY_VALUE = /\b(api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|authorization|password|passwd|secret|cookie|set-cookie)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)/giu
const URI_USERINFO = /\b(https?:\/\/)[^\s/@:]+:[^\s/@]+@/giu

const observedStates = new Set<DockerObservedState>(['running', 'stopped', 'degraded', 'unavailable', 'unknown'])
const healthStates = new Set<DockerHealthState>(['healthy', 'unhealthy', 'starting', 'not-configured', 'unknown'])
const verificationStates = new Set<DockerVerificationState>(['verified', 'mismatch', 'unverified', 'unavailable'])
const serviceIds = new Set<string>(DOCKER_PRODUCT_SERVICES)

function record(value: unknown): UnknownRecord | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as UnknownRecord : null
}

function integer(value: unknown, minimum: number, maximum: number): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum ? value : null
}

function text(value: unknown, max: number = DOCKER_CONTROL_LIMITS.messageCharacters): string | undefined {
  if (typeof value !== 'string') return undefined
  const normalized = value.replace(CONTROL_AND_DIRECTIONAL, ' ').trim().slice(0, max)
  return normalized || undefined
}

function identityText(value: unknown): string | undefined {
  const normalized = text(value, DOCKER_CONTROL_LIMITS.versionCharacters)
  return normalized && /^[A-Za-z0-9][A-Za-z0-9._+:/-]*$/u.test(normalized) ? normalized : undefined
}

function displayText(value: unknown, maximum = 128): string | undefined {
  const normalized = text(value, maximum)
  return normalized && /^[A-Za-z0-9][A-Za-z0-9 ._+:/@()-]*$/u.test(normalized) ? normalized : undefined
}

function finiteNumber(value: unknown, minimum: number, maximum: number): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum ? value : undefined
}

function normalizeResources(value: unknown): DockerResourceSnapshot | null {
  const raw = record(value)
  if (!raw) return null
  const cpuPercent = raw.cpuPercent == null ? undefined : finiteNumber(raw.cpuPercent, 0, 1_000_000)
  const memoryPercent = raw.memoryPercent == null ? undefined : finiteNumber(raw.memoryPercent, 0, 1_000_000)
  const memoryUsage = raw.memoryUsage == null ? undefined : displayText(raw.memoryUsage, 64)
  const memoryLimit = raw.memoryLimit == null ? undefined : displayText(raw.memoryLimit, 64)
  const networkIo = raw.networkIo == null ? undefined : displayText(raw.networkIo, 64)
  const blockIo = raw.blockIo == null ? undefined : displayText(raw.blockIo, 64)
  const pids = raw.pids == null ? undefined : integer(raw.pids, 0, 10_000_000) ?? undefined
  if ((raw.cpuPercent != null && cpuPercent === undefined) || (raw.memoryPercent != null && memoryPercent === undefined)
    || (raw.memoryUsage != null && memoryUsage === undefined) || (raw.memoryLimit != null && memoryLimit === undefined)
    || (raw.networkIo != null && networkIo === undefined) || (raw.blockIo != null && blockIo === undefined)
    || (raw.pids != null && pids === undefined)) return null
  return { cpuPercent, memoryUsage, memoryLimit, memoryPercent, networkIo, blockIo, pids }
}

function timestamp(value: unknown): string | undefined {
  if (typeof value !== 'string' || value.length > 40 || !UTC_TIMESTAMP.test(value)) return undefined
  return Number.isFinite(Date.parse(value)) ? value : undefined
}

function serviceId(value: unknown): DockerProductService | null {
  return typeof value === 'string' && serviceIds.has(value) ? value as DockerProductService : null
}

function observedState(value: unknown): DockerObservedState | null {
  return typeof value === 'string' && observedStates.has(value as DockerObservedState) ? value as DockerObservedState : null
}

function normalizeImage(value: unknown): DockerImageIdentity | null {
  const raw = record(value)
  if (!raw || typeof raw.verification !== 'string' || !verificationStates.has(raw.verification as DockerVerificationState)) return null
  const imageId = raw.imageId == null ? undefined : typeof raw.imageId === 'string' && SHA256.test(raw.imageId) ? raw.imageId : null
  const approvedDigest = raw.approvedDigest == null ? undefined : typeof raw.approvedDigest === 'string' && SHA256.test(raw.approvedDigest) ? raw.approvedDigest : null
  const ociRevision = raw.ociRevision == null ? undefined : identityText(raw.ociRevision) ?? null
  if (imageId === null || approvedDigest === null || ociRevision === null) return null
  return { verification: raw.verification as DockerVerificationState, imageId, approvedDigest, ociRevision }
}

export function normalizeDockerMessage(value: unknown, fallback: string): string {
  const candidate = text(value) ?? fallback
  return redactDockerLogText(candidate).slice(0, DOCKER_CONTROL_LIMITS.messageCharacters) || fallback
}

export function redactDockerLogText(value: unknown): string {
  if (typeof value !== 'string') return ''
  return value
    .replace(CONTROL_AND_DIRECTIONAL, ' ')
    .replace(URI_USERINFO, '$1[REDACTED]@')
    .replace(AUTH_VALUE, '$1 [REDACTED]')
    .replace(KEY_VALUE, '$1=[REDACTED]')
    .replace(JWT, '[REDACTED]')
    .replace(TOKENISH, '[REDACTED]')
    .slice(0, DOCKER_CONTROL_LIMITS.logLineCharacters)
}

export function normalizeDockerSnapshot(value: unknown): DockerStackSnapshot | null {
  const raw = record(value)
  const engine = record(raw?.engine)
  const compose = record(raw?.compose)
  const revision = integer(raw?.revision, 0, Number.MAX_SAFE_INTEGER)
  const observedAtUtc = timestamp(raw?.observedAtUtc)
  const engineState = observedState(engine?.state)
  const composeState = observedState(compose?.state)
  if (!raw || raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || revision === null || !observedAtUtc
    || !engine || !compose || !engineState || !composeState || !Array.isArray(raw.services)
    || raw.services.length > DOCKER_PRODUCT_SERVICES.length || !Array.isArray(raw.volumes) || raw.volumes.length > 2) return null

  const services = raw.services.map((candidate) => {
    const item = record(candidate)
    const id = serviceId(item?.id)
    const state = observedState(item?.state)
    const health = typeof item?.health === 'string' && healthStates.has(item.health as DockerHealthState) ? item.health as DockerHealthState : null
    if (!item || !id || !state || !health || !Array.isArray(item.ports) || item.ports.length > 16) return null
    const ports = item.ports.map((candidatePort) => {
      const port = record(candidatePort)
      const hostPort = integer(port?.hostPort, 1, 65_535)
      const containerPort = integer(port?.containerPort, 1, 65_535)
      if (!port || (port.address !== '127.0.0.1' && port.address !== '::1') || (port.protocol !== 'tcp' && port.protocol !== 'udp')
        || hostPort === null || containerPort === null) return null
      return {
        address: port.address as '127.0.0.1' | '::1',
        hostPort,
        containerPort,
        protocol: port.protocol as 'tcp' | 'udp',
      }
    })
    const version = item.version == null ? undefined : identityText(item.version) ?? null
    const manageable = item.manageable == null ? undefined : typeof item.manageable === 'boolean' ? item.manageable : null
    const containerId = item.containerId == null ? undefined : typeof item.containerId === 'string' && CONTAINER_ID.test(item.containerId) ? item.containerId : null
    const image = item.image == null ? undefined : normalizeImage(item.image)
    const resources = item.resources == null ? undefined : normalizeResources(item.resources)
    return ports.some((port) => port === null) || version === null || manageable === null || containerId === null || image === null || resources === null
      ? null
      : { id, state, health, manageable, containerId, version, image, ports: ports as NonNullable<typeof ports[number]>[], resources }
  })
  if (services.some((service) => service === null)) return null
  const normalizedServices = services as NonNullable<typeof services[number]>[]
  if (new Set(normalizedServices.map((service) => service.id)).size !== normalizedServices.length) return null

  const volumes = raw.volumes.map((candidate) => {
    const volume = record(candidate)
    if (!volume || (volume.role !== 'data' && volume.role !== 'workspace')
      || !['mounted', 'unmounted', 'unavailable', 'unknown'].includes(String(volume.state))
      || typeof volume.persistent !== 'boolean') return null
    return {
      role: volume.role as 'data' | 'workspace',
      state: volume.state as 'mounted' | 'unmounted' | 'unavailable' | 'unknown',
      persistent: volume.persistent,
    }
  })
  if (volumes.some((volume) => volume === null)) return null
  const normalizedVolumes = volumes as NonNullable<typeof volumes[number]>[]
  if (new Set(normalizedVolumes.map((volume) => volume.role)).size !== normalizedVolumes.length) return null

  const engineVersion = engine.version == null ? undefined : identityText(engine.version) ?? null
  const definitionFingerprint = compose.definitionFingerprint == null
    ? undefined
    : typeof compose.definitionFingerprint === 'string' && SHA256.test(compose.definitionFingerprint) ? compose.definitionFingerprint : null
  const upstreamRevision = compose.upstreamRevision == null ? undefined : identityText(compose.upstreamRevision) ?? null
  const runtimeProtocol = compose.runtimeProtocol == null ? undefined : identityText(compose.runtimeProtocol) ?? null
  if (engineVersion === null || definitionFingerprint === null || upstreamRevision === null || runtimeProtocol === null) return null

  const workflowRaw = raw.lastWorkflow == null ? undefined : record(raw.lastWorkflow)
  let lastWorkflow: DockerStackSnapshot['lastWorkflow']
  if (raw.lastWorkflow != null) {
    const summary = normalizeDockerMessage(workflowRaw?.summary, '') || undefined
    const completedAtUtc = workflowRaw?.completedAtUtc === undefined ? undefined : timestamp(workflowRaw.completedAtUtc)
    if (!workflowRaw || (workflowRaw.kind !== 'update' && workflowRaw.kind !== 'rollback')
      || !['succeeded', 'failed', 'cancelled', 'unknown'].includes(String(workflowRaw.state))
      || !summary || (workflowRaw.completedAtUtc !== undefined && !completedAtUtc)) return null
    lastWorkflow = { kind: workflowRaw.kind, state: workflowRaw.state as 'succeeded' | 'failed' | 'cancelled' | 'unknown', summary, completedAtUtc }
  }


  let modelRunner: DockerStackSnapshot['modelRunner']
  if (raw.modelRunner != null) {
    const modelRunnerRaw = record(raw.modelRunner)
    const runnerState = observedState(modelRunnerRaw?.state)
    if (!modelRunnerRaw || !runnerState || modelRunnerRaw.loadAvailable !== false || typeof modelRunnerRaw.unloadAvailable !== 'boolean'
      || !Array.isArray(modelRunnerRaw.models) || modelRunnerRaw.models.length > DOCKER_CONTROL_LIMITS.models) return null
    const runnerVersion = modelRunnerRaw.version == null ? undefined : identityText(modelRunnerRaw.version) ?? null
    const endpoint = modelRunnerRaw.endpoint == null ? undefined : displayText(modelRunnerRaw.endpoint, 256) ?? null
    const kind = modelRunnerRaw.kind == null ? undefined : displayText(modelRunnerRaw.kind, 64) ?? null
    const diskUsage = modelRunnerRaw.diskUsage == null ? undefined : displayText(modelRunnerRaw.diskUsage, 64) ?? null
    const message = modelRunnerRaw.message == null ? undefined : normalizeDockerMessage(modelRunnerRaw.message, '') || null
    if (runnerVersion === null || endpoint === null || kind === null || diskUsage === null || message === null) return null
    const models = modelRunnerRaw.models.map((candidate) => {
      const model = record(candidate)
      const reference = typeof model?.reference === 'string' && MODEL_REFERENCE.test(model.reference) ? model.reference : null
      const modelId = model?.modelId == null ? undefined : typeof model.modelId === 'string' && SHA256.test(model.modelId) ? model.modelId : null
      const size = model?.size == null ? undefined : displayText(model.size, 64) ?? null
      const format = model?.format == null ? undefined : identityText(model.format) ?? null
      const parameters = model?.parameters == null ? undefined : displayText(model.parameters, 64) ?? null
      const backend = model?.backend == null ? undefined : identityText(model.backend) ?? null
      const mode = model?.mode == null ? undefined : identityText(model.mode) ?? null
      if (!model || !reference || modelId === null || size === null || format === null || parameters === null || backend === null || mode === null
        || typeof model.loaded !== 'boolean') return null
      return { reference, modelId, size, format, parameters, loaded: model.loaded, backend, mode }
    })
    if (models.some((model) => model === null)) return null
    modelRunner = {
      state: runnerState,
      version: runnerVersion,
      endpoint,
      kind,
      diskUsage,
      loadAvailable: false,
      unloadAvailable: modelRunnerRaw.unloadAvailable,
      models: models as NonNullable<typeof models[number]>[],
      message: message ?? undefined,
    }
  }

  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    revision,
    observedAtUtc,
    engine: { state: engineState, version: engineVersion },
    compose: { state: composeState, definitionFingerprint, upstreamRevision, runtimeProtocol },
    services: normalizedServices,
    volumes: normalizedVolumes,
    modelRunner,
    lastWorkflow,
  }
}

export function normalizeDockerLogs(value: unknown, requestId: string, expectedService: DockerProductService): DockerLogsResult | null {
  const raw = record(value)
  if (!raw || raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || raw.requestId !== requestId
    || raw.service !== expectedService || typeof raw.truncated !== 'boolean' || !Array.isArray(raw.entries)) return null
  let total = 0
  let truncated = raw.truncated || raw.entries.length > DOCKER_CONTROL_LIMITS.logEntries
  const entries: DockerLogsResult['entries'][number][] = []
  for (const candidate of raw.entries.slice(0, DOCKER_CONTROL_LIMITS.logEntries)) {
    const item = record(candidate)
    if (!item || (item.stream !== 'stdout' && item.stream !== 'stderr' && item.stream !== 'system')) { truncated = true; continue }
    const timestampUtc = item.timestampUtc === undefined ? undefined : timestamp(item.timestampUtc)
    if (item.timestampUtc !== undefined && !timestampUtc) { truncated = true; continue }
    const redacted = redactDockerLogText(item.text)
    if (!redacted) continue
    if (total + redacted.length > DOCKER_CONTROL_LIMITS.logTotalCharacters) { truncated = true; break }
    total += redacted.length
    entries.push({ timestampUtc, stream: item.stream, text: redacted })
  }
  return { protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION, requestId, service: expectedService, entries, truncated }
}

export function normalizeDockerReview(
  value: unknown,
  requestId: string,
  snapshotRevision: number,
  intent: DockerMutationIntent,
): DockerMutationReviewResult | null {
  const raw = record(value)
  if (!raw || raw.protocolVersion !== DOCKER_CONTROL_PROTOCOL_VERSION || raw.requestId !== requestId) return null
  if (raw.status === 'rejected') {
    return { protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION, requestId, status: 'rejected', message: normalizeDockerMessage(raw.message, 'Docker rejected this request.') }
  }
  if (raw.status !== 'ready' || raw.snapshotRevision !== snapshotRevision || typeof raw.reviewToken !== 'string'
    || !OPAQUE_REVIEW_TOKEN.test(raw.reviewToken) || typeof raw.fingerprint !== 'string' || !SHA256.test(raw.fingerprint)
    || !timestamp(raw.expiresAtUtc) || !Array.isArray(raw.affectedServices) || raw.affectedServices.length === 0
    || raw.affectedServices.length > DOCKER_PRODUCT_SERVICES.length || !Array.isArray(raw.warnings)
    || raw.warnings.length > DOCKER_CONTROL_LIMITS.warnings) return null
  const affectedServices = raw.affectedServices.map(serviceId)
  if (affectedServices.some((id) => id === null) || new Set(affectedServices).size !== affectedServices.length) return null
  const affected = affectedServices as DockerProductService[]
  if ((intent.kind === 'start-service' || intent.kind === 'stop-service' || intent.kind === 'restart-service' || intent.kind === 'repair-service')
    && (affected.length !== 1 || affected[0] !== intent.service)) return null
  if (intent.kind === 'unload-model' && (affected.length !== 1 || affected[0] !== 'model-runner')) return null
  if ((intent.kind === 'start-stack' || intent.kind === 'stop-stack') && affected.length === 0) return null
  if (intent.kind === 'request-update' && !affected.includes('hermes')) return null
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    requestId,
    status: 'ready',
    snapshotRevision,
    reviewToken: raw.reviewToken,
    fingerprint: raw.fingerprint,
    expiresAtUtc: raw.expiresAtUtc as string,
    affectedServices: affected,
    summary: normalizeDockerMessage(raw.summary, 'Review Docker operation.'),
    warnings: raw.warnings.map((warning) => normalizeDockerMessage(warning, 'Docker reported a warning.')),
  }
}

export function isOpaqueDockerReviewToken(value: unknown): value is string {
  return typeof value === 'string' && OPAQUE_REVIEW_TOKEN.test(value)
}

export function isDockerFingerprint(value: unknown): value is string {
  return typeof value === 'string' && SHA256.test(value)
}
