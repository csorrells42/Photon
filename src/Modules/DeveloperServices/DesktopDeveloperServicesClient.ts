import {
  LANGUAGE_TOOLING_CONTRACT,
  LANGUAGE_TOOLING_PROVIDER_CATALOG,
  type HostCapabilityEvidence,
  type LanguageToolingProviderId,
  type TrustedHostProviderEvidence,
} from '../LanguageToolingProviders'

export const DEVELOPER_SERVICES_PROTOCOL_VERSION = 2
const legacyDeveloperServicesProtocolVersion = 1
const maximumDiagnostics = 2_000
const maximumOutputCharacters = 256 * 1024
const maximumRevision = 1_000_000_000
const maximumTargetPathLength = 2_048

export type DeveloperOperation = 'build' | 'analyze'
export type DeveloperConfiguration = 'Debug' | 'Release'
export type DeveloperAvailabilityState = 'available' | 'unavailable' | 'error' | 'checking' | 'unknown'
export type DeveloperDiagnosticSeverity = 'info' | 'warning' | 'error'

export type DeveloperDiagnostic = {
  filePath: string
  severity: DeveloperDiagnosticSeverity
  code: string
  message: string
  project?: string
  source: string
  range: {
    start: { line: number; column: number }
    end: { line: number; column: number }
  }
}

export type DeveloperBuildResult = {
  requestId: string
  revision: number
  operation: DeveloperOperation
  stale: boolean
  workspaceRoot: string
  succeeded: boolean
  exitCode?: number
  wasCancelled: boolean
  failureCode?: string
  failureMessage?: string
  startedAt?: string
  completedAt?: string
  diagnostics: DeveloperDiagnostic[]
  output: {
    standardOutput: string
    standardError: string
    truncated: boolean
    droppedCharacters: number
  }
}

export type DeveloperProviderDescription = {
  contractVersion?: number
  providerId: string
  displayName: string
  providerVersion: string
  languageIds: string[]
  projectKinds: string[]
  build: { supported: boolean; producesDiagnostics: boolean; supportsCancellation: boolean; targetKinds: string[] }
  lsp: { supported: boolean }
  dap: { supported: boolean }
  availability: { state: DeveloperAvailabilityState; code?: string; message?: string }
}

export type DeveloperServicesDescription = {
  protocolVersion: 1 | 2
  requestId: string
  workspaceRoot: string
  targets: string[]
  providers: DeveloperProviderDescription[]
  languageTooling: TrustedHostProviderEvidence[]
  availability: { state: DeveloperAvailabilityState; code?: string; message?: string }
}

export type DeveloperServicesFrame =
  | { type: 'describe'; value: DeveloperServicesDescription }
  | { type: 'operation-started'; requestId: string; revision: number; operation: DeveloperOperation }
  | { type: 'operation-result'; value: DeveloperBuildResult }
  | { type: 'cancel-result'; requestId: string; accepted: boolean }
  | { type: 'error'; requestId: string; code: string; message: string; retryable: boolean }

export type DeveloperOperationRequest = {
  type: `developerServices.${DeveloperOperation}`
  version: 2
  requestId: string
  revision: number
  targetPath: string
  configuration: 'debug' | 'release'
}

export type DeveloperOperationHandle = {
  requestId: string
  revision: number
  operation: DeveloperOperation
  promise: Promise<DeveloperBuildResult>
  cancel: () => void
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type PendingRequest = {
  kind: DeveloperOperation | 'describe'
  revision: number | null
  resolve: (value: DeveloperBuildResult | DeveloperServicesDescription) => void
  reject: (reason: Error) => void
}

function getBridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function text(value: unknown, maximum = 4_096) {
  return typeof value === 'string' ? value.slice(0, maximum) : ''
}

function optionalText(value: unknown, maximum = 4_096) {
  const normalized = text(value, maximum)
  return normalized || undefined
}

function requestIdentifier(value: unknown): string | null {
  if (typeof value !== 'string' || value.length === 0 || value.length > 128 || !/^[a-zA-Z0-9_:-]+$/.test(value)) return null
  return value
}

function finiteNumber(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function positiveInteger(value: unknown, fallback = 1) {
  return Math.max(1, Math.trunc(finiteNumber(value, fallback)))
}

function boundedRevision(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= maximumRevision ? value : null
}

function stringArray(value: unknown, maximum = 32) {
  return Array.isArray(value) ? value.slice(0, maximum).map((item) => text(item, 128)).filter(Boolean) : []
}

function availabilityState(value: unknown): DeveloperAvailabilityState {
  return value === 'available' || value === 'unavailable' || value === 'error' || value === 'checking'
    ? value
    : 'unknown'
}

const languageToolingProviderIds = new Set<LanguageToolingProviderId>(
  LANGUAGE_TOOLING_PROVIDER_CATALOG.map((provider) => provider.id),
)

function languageToolingProviderId(value: unknown): LanguageToolingProviderId | null {
  return typeof value === 'string' && languageToolingProviderIds.has(value as LanguageToolingProviderId)
    ? value as LanguageToolingProviderId
    : null
}

function normalizeLanguageToolingCapability(
  value: unknown,
  providerId: LanguageToolingProviderId,
): HostCapabilityEvidence | null {
  const raw = record(value)
  if (!raw) return null
  const capabilityId = text(raw.capabilityId, 128)
  const declared = LANGUAGE_TOOLING_PROVIDER_CATALOG
    .find((provider) => provider.id === providerId)?.capabilities
    .some((capability) => capability.id === capabilityId)
  const availability = raw.availability
  const code = text(raw.code, 96)
  const detail = text(raw.detail, 512)
  if (!declared
    || (availability !== 'available' && availability !== 'unavailable' && availability !== 'error')
    || !code
    || !detail) return null
  return {
    capabilityId,
    availability,
    code,
    detail,
    version: optionalText(raw.version, 64),
  }
}

export function normalizeLanguageToolingEvidence(value: unknown): TrustedHostProviderEvidence[] | null {
  if (value === undefined) return []
  if (!Array.isArray(value) || value.length > 16) return null
  const normalized: TrustedHostProviderEvidence[] = []
  for (const candidate of value) {
    const raw = record(candidate)
    const providerId = languageToolingProviderId(raw?.providerId)
    const evidenceId = text(raw?.evidenceId, 128)
    const checkedAt = text(raw?.checkedAt, 64)
    if (!raw
      || raw.contract !== LANGUAGE_TOOLING_CONTRACT
      || raw.source !== 'trusted-host'
      || !providerId
      || !/^[a-zA-Z0-9_.:-]{1,128}$/.test(evidenceId)
      || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(checkedAt)
      || !Number.isFinite(Date.parse(checkedAt))
      || !Array.isArray(raw.capabilities)
      || raw.capabilities.length > 64) return null
    const capabilities = raw.capabilities.map((capability) =>
      normalizeLanguageToolingCapability(capability, providerId))
    if (capabilities.some((capability) => capability === null)) return null
    normalized.push({
      contract: LANGUAGE_TOOLING_CONTRACT,
      source: 'trusted-host',
      evidenceId,
      providerId,
      checkedAt,
      capabilities: capabilities as HostCapabilityEvidence[],
    })
  }
  return normalized
}

export function normalizeWorkspaceRelativePath(value: string): string | null {
  const candidate = value.trim().replaceAll('\\', '/')
  if (!candidate || candidate.length > maximumTargetPathLength || candidate.includes('\0')
    || candidate.startsWith('/') || /^[a-zA-Z]:/.test(candidate)) return null
  const components = candidate.split('/').filter(Boolean)
  if (!components.length || components.some((component) => component === '.' || component === '..')) return null
  return components.join('/')
}

function normalizeDeveloperTargetPath(value: unknown): string | null {
  const target = normalizeWorkspaceRelativePath(text(value, maximumTargetPathLength))
  return target && /\.(?:sln|slnx|csproj)$/i.test(target) ? target : null
}

function normalizeDiagnostic(value: unknown, requireRelativePath: boolean): DeveloperDiagnostic | null {
  const raw = record(value)
  const rawRange = record(raw?.range)
  const rawStart = record(rawRange?.start)
  const rawEnd = record(rawRange?.end)
  const rawFilePath = text(raw?.filePath, maximumTargetPathLength)
  const filePath = requireRelativePath ? normalizeWorkspaceRelativePath(rawFilePath) : rawFilePath
  const message = text(raw?.message, 16_384)
  if (!raw || !rawRange || !rawStart || !rawEnd || !filePath || !message) return null
  const severity = raw.severity === 'error' || raw.severity === 'warning' ? raw.severity : 'info'
  const start = { line: positiveInteger(rawStart.line), column: positiveInteger(rawStart.column) }
  const end = {
    line: Math.max(start.line, positiveInteger(rawEnd.line, start.line)),
    column: positiveInteger(rawEnd.column, start.column),
  }
  return {
    filePath,
    severity,
    code: text(raw.code, 128),
    message,
    project: optionalText(raw.project),
    source: text(raw.source, 128) || 'msbuild',
    range: { start, end },
  }
}

function normalizeDescription(raw: Record<string, unknown>, requestId: string, version: 1 | 2): DeveloperServicesDescription | null {
  const availability = record(raw.availability)
  if (version === 2 && (typeof raw.workspaceRoot !== 'string' || !Array.isArray(raw.targets) || !Array.isArray(raw.providers) || !availability)) return null
  const providers = Array.isArray(raw.providers) ? raw.providers.slice(0, 32).flatMap((value) => {
    const provider = record(value)
    const build = record(provider?.build)
    const lsp = record(provider?.lsp)
    const dap = record(provider?.dap)
    const providerAvailability = record(provider?.availability)
    const providerId = text(provider?.providerId, 128)
    if (!provider || !build || !providerId) return []
    const contractVersion = boundedRevision(provider.contractVersion)
    return [{
      contractVersion: contractVersion ?? undefined,
      providerId,
      displayName: text(provider.displayName, 256) || providerId,
      providerVersion: text(provider.providerVersion, 64),
      languageIds: stringArray(provider.languageIds),
      projectKinds: stringArray(provider.projectKinds),
      build: {
        supported: build.supported === true,
        producesDiagnostics: build.producesDiagnostics === true,
        supportsCancellation: build.supportsCancellation === true,
        targetKinds: stringArray(build.targetKinds),
      },
      lsp: { supported: lsp?.supported === true },
      dap: { supported: dap?.supported === true },
      availability: {
        state: availabilityState(providerAvailability?.state),
        code: optionalText(providerAvailability?.code, 128),
        message: optionalText(providerAvailability?.message, 1_024),
      },
    }]
  }) : []
  const targets = Array.isArray(raw.targets)
    ? raw.targets.slice(0, 128).map(normalizeDeveloperTargetPath).filter((target): target is string => target !== null)
    : []
  const languageTooling = normalizeLanguageToolingEvidence(raw.languageTooling)
  if (languageTooling === null) return null
  return {
    protocolVersion: version,
    requestId,
    workspaceRoot: text(raw.workspaceRoot),
    targets,
    providers,
    languageTooling,
    availability: {
      state: availabilityState(availability?.state),
      code: optionalText(availability?.code, 128),
      message: optionalText(availability?.message, 1_024),
    },
  }
}

function operationForFrame(frameType: unknown, rawOperation: unknown, version: 1 | 2): DeveloperOperation | null {
  if (version === legacyDeveloperServicesProtocolVersion) return frameType === 'developerServices.build.started' || frameType === 'developerServices.build.result' ? 'build' : null
  if (frameType === 'developerServices.build.started' || frameType === 'developerServices.build.result') return rawOperation === 'build' ? 'build' : null
  if (frameType === 'developerServices.analyze.started' || frameType === 'developerServices.analyze.result') return rawOperation === 'analyze' ? 'analyze' : null
  return null
}

function normalizeOperationResult(
  raw: Record<string, unknown>,
  requestId: string,
  version: 1 | 2,
  operation: DeveloperOperation,
): DeveloperBuildResult | null {
  const output = record(raw.output)
  const revision = version === legacyDeveloperServicesProtocolVersion ? 0 : boundedRevision(raw.revision)
  if (revision === null || (version === 2 && (
    typeof raw.workspaceRoot !== 'string'
    || typeof raw.succeeded !== 'boolean'
    || typeof raw.wasCancelled !== 'boolean'
    || typeof raw.stale !== 'boolean'
    || !Array.isArray(raw.diagnostics)
    || !output
  ))) return null
  return {
    requestId,
    revision,
    operation,
    stale: version === 2 ? raw.stale === true : false,
    workspaceRoot: text(raw.workspaceRoot),
    succeeded: raw.succeeded === true,
    exitCode: typeof raw.exitCode === 'number' ? finiteNumber(raw.exitCode) : undefined,
    wasCancelled: raw.wasCancelled === true,
    failureCode: optionalText(raw.failureCode, 128),
    failureMessage: optionalText(raw.failureMessage, 1_024),
    startedAt: optionalText(raw.startedAt, 128),
    completedAt: optionalText(raw.completedAt, 128),
    diagnostics: Array.isArray(raw.diagnostics)
      ? raw.diagnostics.slice(0, maximumDiagnostics).map((item) => normalizeDiagnostic(item, version === 2)).filter((item): item is DeveloperDiagnostic => item !== null)
      : [],
    output: {
      standardOutput: text(output?.standardOutput, maximumOutputCharacters),
      standardError: text(output?.standardError, maximumOutputCharacters),
      truncated: output?.truncated === true,
      droppedCharacters: Math.max(0, Math.trunc(finiteNumber(output?.droppedCharacters))),
    },
  }
}

export function normalizeDeveloperServicesFrame(value: unknown): DeveloperServicesFrame | null {
  const raw = record(value)
  const rawVersion = finiteNumber(raw?.version, -1)
  if (!raw || (rawVersion !== legacyDeveloperServicesProtocolVersion && rawVersion !== DEVELOPER_SERVICES_PROTOCOL_VERSION)) return null
  const version = rawVersion as 1 | 2
  const requestId = requestIdentifier(raw.requestId)
  if (!requestId) return null
  switch (raw.type) {
    case 'developerServices.describe.result': {
      const description = normalizeDescription(raw, requestId, version)
      return description ? { type: 'describe', value: description } : null
    }
    case 'developerServices.build.started':
    case 'developerServices.analyze.started': {
      const operation = operationForFrame(raw.type, raw.operation, version)
      const revision = version === 1 ? 0 : boundedRevision(raw.revision)
      return operation && revision !== null ? { type: 'operation-started', requestId, revision, operation } : null
    }
    case 'developerServices.build.result':
    case 'developerServices.analyze.result': {
      const operation = operationForFrame(raw.type, raw.operation, version)
      if (!operation) return null
      const result = normalizeOperationResult(raw, requestId, version, operation)
      return result ? { type: 'operation-result', value: result } : null
    }
    case 'developerServices.cancel.result': return typeof raw.accepted === 'boolean' ? { type: 'cancel-result', requestId, accepted: raw.accepted } : null
    case 'developerServices.error':
      if (version === 2 && (typeof raw.code !== 'string' || !raw.code || typeof raw.message !== 'string' || !raw.message || typeof raw.retryable !== 'boolean')) return null
      return {
        type: 'error',
        requestId,
        code: text(raw.code, 128) || 'unexpected',
        message: text(raw.message, 1_024) || 'Developer services returned an unexpected error.',
        retryable: raw.retryable === true,
      }
    default: return null
  }
}

export function workspaceRelativePath(filePath: string, workspaceRoot: string) {
  const relative = normalizeWorkspaceRelativePath(filePath)
  if (relative) return relative
  const file = filePath.replaceAll('\\', '/').replace(/\/+$/, '')
  const root = workspaceRoot.replaceAll('\\', '/').replace(/\/+$/, '')
  if (!root || file.toLowerCase() === root.toLowerCase()) return ''
  const prefix = `${root}/`
  return file.toLowerCase().startsWith(prefix.toLowerCase()) ? normalizeWorkspaceRelativePath(file.slice(prefix.length)) : null
}

function validateRequestId(requestId: string) {
  if (!requestId || requestId.length > 128 || !/^[a-zA-Z0-9_:-]+$/.test(requestId)) throw new Error('The developer-services request identifier is invalid.')
}

export function createDeveloperOperationRequest(
  operation: DeveloperOperation,
  requestId: string,
  revision: number,
  targetPath: string,
  configuration: DeveloperConfiguration,
): DeveloperOperationRequest {
  validateRequestId(requestId)
  const validatedTarget = normalizeDeveloperTargetPath(targetPath)
  if (!validatedTarget) throw new Error('Select a workspace-relative .sln, .slnx, or .csproj target.')
  if (boundedRevision(revision) === null) throw new Error('The developer-services revision is invalid.')
  return {
    type: `developerServices.${operation}`,
    version: DEVELOPER_SERVICES_PROTOCOL_VERSION,
    requestId,
    revision,
    targetPath: validatedTarget,
    configuration: configuration === 'Release' ? 'release' : 'debug',
  }
}

export function createDeveloperCancelRequest(requestId: string) {
  validateRequestId(requestId)
  return { type: 'developerServices.cancel' as const, version: DEVELOPER_SERVICES_PROTOCOL_VERSION, requestId }
}

export class DesktopDeveloperServicesClient {
  private bridge: WebViewBridge | null = null
  private sequence = 0
  private revision = 0
  private readonly pending = new Map<string, PendingRequest>()
  private readonly listeners = new Set<(frame: DeveloperServicesFrame) => void>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeDeveloperServicesFrame(event.data)
    if (!frame) return
    this.listeners.forEach((listener) => listener(frame))
    if (frame.type === 'operation-result') this.completeOperation(frame.value)
    else if (frame.type === 'describe') this.completeDescription(frame.value)
    else if (frame.type === 'error') this.fail(frame.requestId, new Error(frame.message))
  }

  get available() { return getBridge() !== null }

  onFrame(listener: (frame: DeveloperServicesFrame) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  describe(): Promise<DeveloperServicesDescription> {
    const requestId = this.nextRequestId('describe')
    return this.request<DeveloperServicesDescription>('describe', null, requestId, {
      type: 'developerServices.describe', version: DEVELOPER_SERVICES_PROTOCOL_VERSION, requestId,
    })
  }

  build(targetPath: string, configuration: DeveloperConfiguration = 'Debug', revision = this.nextRevision()) {
    return this.run('build', targetPath, configuration, revision)
  }

  analyze(targetPath: string, configuration: DeveloperConfiguration = 'Debug', revision = this.nextRevision()) {
    return this.run('analyze', targetPath, configuration, revision)
  }

  cancel(requestId: string) {
    this.connect()?.postMessage(createDeveloperCancelRequest(requestId))
  }

  close() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    this.pending.forEach((pending) => pending.reject(new Error('Developer services disconnected.')))
    this.pending.clear()
  }

  private run(operation: DeveloperOperation, targetPath: string, configuration: DeveloperConfiguration, revision: number): DeveloperOperationHandle {
    const requestId = this.nextRequestId(operation)
    const promise = this.request<DeveloperBuildResult>(operation, revision, requestId, createDeveloperOperationRequest(operation, requestId, revision, targetPath, configuration))
    return { requestId, revision, operation, promise, cancel: () => this.cancel(requestId) }
  }

  private request<T extends DeveloperBuildResult | DeveloperServicesDescription>(
    kind: PendingRequest['kind'],
    revision: number | null,
    requestId: string,
    message: unknown,
  ) {
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Developer services are available in the Hermes desktop app.'))
    return new Promise<T>((resolve, reject) => {
      if (this.pending.has(requestId)) {
        reject(new Error('The developer-services request identifier is already pending.'))
        return
      }
      this.pending.set(requestId, { kind, revision, resolve: resolve as PendingRequest['resolve'], reject })
      try {
        bridge.postMessage(message)
      } catch (reason) {
        this.pending.delete(requestId)
        reject(reason instanceof Error ? reason : new Error('Developer services could not receive the request.'))
      }
    })
  }

  private connect() {
    const bridge = getBridge()
    if (bridge && bridge !== this.bridge) {
      this.bridge?.removeEventListener('message', this.receive)
      this.bridge = bridge
      bridge.addEventListener('message', this.receive)
    }
    return bridge
  }

  private completeDescription(value: DeveloperServicesDescription) {
    const pending = this.pending.get(value.requestId)
    if (!pending || pending.kind !== 'describe' || pending.revision !== null) return
    this.pending.delete(value.requestId)
    pending.resolve(value)
  }

  private completeOperation(value: DeveloperBuildResult) {
    const pending = this.pending.get(value.requestId)
    if (!pending || pending.kind !== value.operation || pending.revision !== value.revision) return
    this.pending.delete(value.requestId)
    pending.resolve(value)
  }

  private fail(requestId: string, error: Error) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.pending.delete(requestId)
    pending.reject(error)
  }

  private nextRequestId(kind: string) {
    this.sequence = (this.sequence + 1) % Number.MAX_SAFE_INTEGER
    return `${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}`
  }

  private nextRevision() {
    this.revision = (this.revision + 1) % maximumRevision
    return this.revision
  }
}

export const desktopDeveloperServicesClient = new DesktopDeveloperServicesClient()
