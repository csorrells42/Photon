export const PHOTON_CAD_CONTRACT_VERSION = 1 as const
export const PHOTON_CAD_INTERCHANGE_VERSION = 1 as const

export const PHOTON_CAD_LIMITS = {
  catalogCapabilities: 2_000,
  catalogParameters: 128,
  entities: 10_000,
  issues: 2_000,
  operations: 5_000,
  text: 4_096,
  identifier: 128,
  relativePath: 1_024,
  packageFiles: 20_000,
  bomRows: 20_000,
  occurrences: 50_000,
  absoluteMagnitude: 1_000_000_000,
} as const

export type PhotonCadBackend = 'geometry' | 'assembly'
export type PhotonCadUnit = 'millimeter' | 'inch'
export type PhotonCadOperationKind = 'create' | 'modify' | 'measure' | 'validate' | 'assemble' | 'drawing'
export type PhotonCadParameterKind = 'number' | 'integer' | 'boolean' | 'text' | 'choice' | 'vector3' | 'entity' | 'entity-list'
export type PhotonCadVector3 = { x: number; y: number; z: number }
export type PhotonCadInputValue = string | number | boolean | PhotonCadVector3 | string[] | null

export type PhotonCadSourceIdentity = { package: string; version: string; digest: string; license: string }
export type PhotonCadChoice = { value: string; label: string }
export type PhotonCadParameterDefinition = {
  id: string
  label: string
  description: string
  kind: PhotonCadParameterKind
  required: boolean
  unit?: 'length' | 'angle' | 'ratio' | 'count'
  minimum?: number
  maximum?: number
  step?: number
  defaultValue?: PhotonCadInputValue
  choices?: PhotonCadChoice[]
}
export type PhotonCadCapability = {
  id: string
  backend: PhotonCadBackend
  category: string
  title: string
  description: string
  operation: PhotonCadOperationKind
  parameters: PhotonCadParameterDefinition[]
  source: PhotonCadSourceIdentity
  previewSupported: boolean
  experimental: boolean
}
export type PhotonCadCatalog = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  catalogRevision: string
  generatedAtUtc: string
  capabilities: PhotonCadCapability[]
  coverage: { discovered: number; available: number; unavailable: number; unavailableReasons: string[] }
}

export type PhotonCadEntityKind = 'body' | 'part' | 'assembly' | 'occurrence' | 'drawing' | 'datum'
export type PhotonCadEntity = {
  id: string
  parentId: string | null
  kind: PhotonCadEntityKind
  name: string
  visible: boolean
  suppressed: boolean
  sourceCapabilityId?: string
}
export type PhotonCadIssueSeverity = 'info' | 'warning' | 'error'
export type PhotonCadIssue = { code: string; severity: PhotonCadIssueSeverity; message: string; entityIds: string[] }
export type PhotonCadOperationRecord = {
  id: string
  capabilityId: string
  label: string
  createdAtUtc: string
  state: 'proposed' | 'applied' | 'rejected'
}
export type PhotonCadProjectSnapshot = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  sessionId: string
  projectId: string
  revision: number
  title: string
  units: PhotonCadUnit
  mode: 'canonical' | 'scratch'
  entities: PhotonCadEntity[]
  /** Present on authoritative desktop frames; optional only for source-compatible in-memory fixtures. */
  occurrences?: PhotonCadAssemblyOccurrence[]
  operations: PhotonCadOperationRecord[]
  issues: PhotonCadIssue[]
  dirty: boolean
}
export type PhotonCadPreviewReceipt = {
  previewId: string
  projectId: string
  revision: number
  contentDigest: string
  units: PhotonCadUnit
  bounds: { minimum: PhotonCadVector3; maximum: PhotonCadVector3 }
  entityCount: number
}

export type PhotonCadOperationRequest = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  sessionId: string
  projectId: string
  baseRevision: number
  mode: 'suggest' | 'scratch'
  capabilityId: string
  inputs: Record<string, PhotonCadInputValue>
  targetEntityIds: string[]
}
export type PhotonCadOperationResult = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  projectId: string
  baseRevision: number
  resultingRevision: number
  status: 'accepted' | 'rejected' | 'unavailable'
  stale: boolean
  reason: string
  snapshot?: PhotonCadProjectSnapshot
  preview?: PhotonCadPreviewReceipt
  issues: PhotonCadIssue[]
}
export type PhotonCadVerificationRequest = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  sessionId: string
  projectId: string
  revision: number
  checks: Array<'valid-solids' | 'interference' | 'dimensions' | 'assembly-structure' | 'export-readiness'>
}
export type PhotonCadVerificationResult = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  projectId: string
  revision: number
  status: 'passed' | 'failed' | 'unavailable'
  stale: boolean
  issues: PhotonCadIssue[]
  measuredAtUtc: string
}

export type PhotonCadReleaseFormat = 'step-ap214' | 'step-ap242' | 'stl' | 'dxf' | 'svg'
export type PhotonCadPackageFileRole = 'manifest' | 'part-step' | 'assembly-step' | 'bom' | 'drawing' | 'preview' | 'validation-report'
export type PhotonCadReleaseReviewRequest = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  sessionId: string
  projectId: string
  revision: number
  formats: PhotonCadReleaseFormat[]
  destinationHandle: string
}
export type PhotonCadReleaseReviewResult = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  projectId: string
  revision: number
  status: 'ready' | 'rejected' | 'unavailable'
  reason: string
  reviewHandle?: string
  packageFingerprint?: string
  expiresAtUtc?: string
  files: Array<{ role: PhotonCadPackageFileRole; relativePath: string }>
  issues: PhotonCadIssue[]
}
export type PhotonCadReleaseCommitRequest = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  reviewHandle: string
  packageFingerprint: string
}
export type PhotonCadReleaseCommitResult = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  status: 'committed' | 'rejected' | 'unavailable'
  reason: string
  packageId?: string
  packageFingerprint?: string
}
export type PhotonCadStepExportRequest = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  sessionId: string
  projectId: string
  revision: number
  contentDigest: string
  entityId: string
}
export type PhotonCadStepExportResult = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  requestId: string
  projectId: string
  revision: number
  status: 'committed' | 'cancelled' | 'rejected' | 'unavailable'
  reason: string
  entityId?: string
  contentDigest?: string
  byteLength?: number
  destinationLabel?: string
}
export type PhotonCadRuntimeDescription = {
  contractVersion: typeof PHOTON_CAD_CONTRACT_VERSION
  status: 'available' | 'unavailable' | 'checking' | 'error'
  reason: string
  geometryBundleId?: string
  assemblyBundleId?: string
  catalog?: PhotonCadCatalog
}
export type PhotonCadController = {
  describe(): Promise<PhotonCadRuntimeDescription>
  execute(request: PhotonCadOperationRequest): Promise<PhotonCadOperationResult>
  verify(request: PhotonCadVerificationRequest): Promise<PhotonCadVerificationResult>
  reviewRelease(request: PhotonCadReleaseReviewRequest): Promise<PhotonCadReleaseReviewResult>
  commitRelease(request: PhotonCadReleaseCommitRequest): Promise<PhotonCadReleaseCommitResult>
  discardRelease(reviewHandle: string): Promise<void> | void
  exportStep?(request: PhotonCadStepExportRequest): Promise<PhotonCadStepExportResult>
}

export type PhotonCadPackageFile = {
  role: PhotonCadPackageFileRole
  relativePath: string
  sha256: string
  byteLength: number
  mediaType: string
}
export type PhotonCadBomRow = {
  partNumber: string
  description: string
  quantity: number
  unit: 'each' | 'length'
  sourceEntityId: string
}
export type PhotonCadAssemblyOccurrence = {
  occurrenceId: string
  parentOccurrenceId: string | null
  partNumber: string
  sourceEntityId: string
  transform: readonly [number, number, number, number, number, number, number, number, number, number, number, number, number, number, number, number]
}
export type PhotonCadInterchangeManifest = {
  schemaVersion: typeof PHOTON_CAD_INTERCHANGE_VERSION
  packageId: string
  packageFingerprint: string
  project: { projectId: string; title: string; revision: number; units: PhotonCadUnit }
  profile: {
    id: string
    stepApplicationProtocol: 'AP214' | 'AP242'
    geometry: 'exact-brep'
    coordinateSystem: {
      handedness: 'right'
      upAxis: 'z'
      matrixOrder: 'row-major'
      vectorConvention: 'column'
      transformMeaning: 'local-to-parent'
    }
  }
  createdAtUtc: string
  provenance: {
    photonVersion: string
    geometryRuntime: PhotonCadSourceIdentity
    assemblyRuntime?: PhotonCadSourceIdentity
    operationDigest: string
  }
  files: PhotonCadPackageFile[]
  bom: PhotonCadBomRow[]
  occurrences: PhotonCadAssemblyOccurrence[]
  validation: { status: 'passed' | 'failed'; checks: string[]; issues: PhotonCadIssue[] }
  acceptance: Array<{
    system: string
    profile: string
    status: 'passed' | 'failed' | 'not-run'
    observedAtUtc?: string
    reportDigest?: string
  }>
}

const IDENTIFIER = /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/u
const SHA256 = /^(?:sha256:)?[a-f0-9]{64}$/iu
const CONTROL_OR_DIRECTIONAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/u
const CONTROL_OR_DIRECTIONAL_GLOBAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/gu
const ISO_UTC = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?Z$/u
const WINDOWS_DEVICE = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/iu
const WINDOWS_PATH_INVALID = /[<>"|?*]/u

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export function photonCadDisplayText(value: unknown, maximum: number = PHOTON_CAD_LIMITS.text) {
  return typeof value === 'string'
    ? value.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').replace(/\s+/gu, ' ').trim().slice(0, maximum)
    : ''
}

export function isPhotonCadIdentifier(value: unknown): value is string {
  return typeof value === 'string' && IDENTIFIER.test(value)
}

export function isPhotonCadDigest(value: unknown): value is string {
  return typeof value === 'string' && SHA256.test(value)
}

export function isPhotonCadSafeText(value: unknown, maximum: number = PHOTON_CAD_LIMITS.text, required = false): value is string {
  return typeof value === 'string'
    && value.length <= maximum
    && !CONTROL_OR_DIRECTIONAL.test(value)
    && (!required || value.trim().length > 0)
}

export function isPhotonCadUtcTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = ISO_UTC.exec(value)
  if (!match) return false
  const [, yearText, monthText, dayText, hourText, minuteText, secondText] = match
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  const hour = Number(hourText)
  const minute = Number(minuteText)
  const second = Number(secondText)
  if (month < 1 || month > 12 || day < 1 || hour > 23 || minute > 59 || second > 59) return false
  const date = new Date(0)
  date.setUTCFullYear(year, month - 1, day)
  date.setUTCHours(hour, minute, second, 0)
  return date.getUTCFullYear() === year
    && date.getUTCMonth() === month - 1
    && date.getUTCDate() === day
    && date.getUTCHours() === hour
    && date.getUTCMinutes() === minute
    && date.getUTCSeconds() === second
}

export function normalizePhotonCadRelativePath(value: unknown): string | null {
  if (typeof value !== 'string' || !value || value.length > PHOTON_CAD_LIMITS.relativePath || value.includes('\0')) return null
  const candidate = value.replaceAll('\\', '/').normalize('NFC')
  if (candidate.startsWith('/') || candidate.startsWith('//') || /^[A-Za-z]:/u.test(candidate) || candidate.includes(':')) return null
  const segments = candidate.split('/')
  if (!segments.length || segments.some((segment) => !segment
    || segment === '.' || segment === '..' || segment.length > 255
    || segment.endsWith('.') || segment.endsWith(' ')
    || WINDOWS_DEVICE.test(segment) || WINDOWS_PATH_INVALID.test(segment)
    || CONTROL_OR_DIRECTIONAL.test(segment))) return null
  return segments.join('/')
}

function boundedNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= PHOTON_CAD_LIMITS.absoluteMagnitude ? value : null
}

function boundedRevision(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null
}

function normalizeVector(value: unknown): PhotonCadVector3 | null {
  const raw = record(value)
  const x = boundedNumber(raw?.x)
  const y = boundedNumber(raw?.y)
  const z = boundedNumber(raw?.z)
  return x === null || y === null || z === null ? null : { x, y, z }
}

export function validatePhotonCadInput(value: unknown): value is PhotonCadInputValue {
  if (value === null || typeof value === 'boolean') return true
  if (typeof value === 'number') return boundedNumber(value) !== null
  if (typeof value === 'string') return value.length <= PHOTON_CAD_LIMITS.text && !CONTROL_OR_DIRECTIONAL.test(value)
  if (Array.isArray(value)) return value.length <= 1_000 && value.every(isPhotonCadIdentifier)
  return normalizeVector(value) !== null
}

export function validatePhotonCadOperationRequest(request: PhotonCadOperationRequest): string[] {
  const issues: string[] = []
  if (request.contractVersion !== PHOTON_CAD_CONTRACT_VERSION) issues.push('unsupported-contract')
  if (!isPhotonCadIdentifier(request.requestId)) issues.push('invalid-request-id')
  if (!isPhotonCadIdentifier(request.sessionId)) issues.push('invalid-session-id')
  if (!isPhotonCadIdentifier(request.projectId)) issues.push('invalid-project-id')
  if (boundedRevision(request.baseRevision) === null) issues.push('invalid-revision')
  if (request.mode !== 'suggest' && request.mode !== 'scratch') issues.push('invalid-mode')
  if (!isPhotonCadIdentifier(request.capabilityId)) issues.push('invalid-capability')
  const entries = Object.entries(request.inputs)
  if (entries.length > PHOTON_CAD_LIMITS.catalogParameters) issues.push('too-many-inputs')
  if (entries.some(([key, value]) => !isPhotonCadIdentifier(key) || !validatePhotonCadInput(value))) issues.push('invalid-input')
  if (request.targetEntityIds.length > PHOTON_CAD_LIMITS.entities || request.targetEntityIds.some((id) => !isPhotonCadIdentifier(id))) issues.push('invalid-target')
  return [...new Set(issues)]
}

function sourceIdentity(value: unknown): PhotonCadSourceIdentity | null {
  const raw = record(value)
  const packageName = photonCadDisplayText(raw?.package, 128)
  const version = photonCadDisplayText(raw?.version, 64)
  const digest = typeof raw?.digest === 'string' && isPhotonCadDigest(raw.digest) ? raw.digest.toLowerCase() : ''
  const license = photonCadDisplayText(raw?.license, 64)
  return packageName && version && digest && license ? { package: packageName, version, digest, license } : null
}

function parameterKind(value: unknown): PhotonCadParameterKind | null {
  return ['number', 'integer', 'boolean', 'text', 'choice', 'vector3', 'entity', 'entity-list'].includes(String(value))
    ? value as PhotonCadParameterKind
    : null
}

function normalizeParameter(value: unknown): PhotonCadParameterDefinition | null {
  const raw = record(value)
  const id = raw?.id
  const kind = parameterKind(raw?.kind)
  const label = photonCadDisplayText(raw?.label, 256)
  if (!raw || !isPhotonCadIdentifier(id) || !kind || !label || typeof raw.required !== 'boolean') return null
  const minimum = boundedNumber(raw.minimum)
  const maximum = boundedNumber(raw.maximum)
  const step = boundedNumber(raw.step)
  const unit = ['length', 'angle', 'ratio', 'count'].includes(String(raw.unit)) ? raw.unit as PhotonCadParameterDefinition['unit'] : undefined
  if (raw.choices !== undefined && (!Array.isArray(raw.choices) || raw.choices.length > 1_000)) return null
  const choices = Array.isArray(raw.choices)
    ? raw.choices.map((item) => {
      const choice = record(item)
      const value = choice?.value
      const choiceLabel = photonCadDisplayText(choice?.label, 256)
      return isPhotonCadIdentifier(value) && choiceLabel ? { value, label: choiceLabel } : null
    })
    : undefined
  if (choices?.some((choice) => choice === null)) return null
  const validChoices = choices as PhotonCadChoice[] | undefined
  if (validChoices && new Set(validChoices.map((choice) => choice.value.toLowerCase())).size !== validChoices.length) return null
  if (kind === 'choice' && (!validChoices || validChoices.length === 0)) return null
  if (minimum !== null && maximum !== null && minimum > maximum) return null
  return {
    id,
    label,
    description: photonCadDisplayText(raw.description),
    kind,
    required: raw.required,
    unit,
    minimum: minimum ?? undefined,
    maximum: maximum ?? undefined,
    step: step && step > 0 ? step : undefined,
    defaultValue: validatePhotonCadInput(raw.defaultValue) ? raw.defaultValue : undefined,
    choices: validChoices,
  }
}

function normalizeCapability(value: unknown): PhotonCadCapability | null {
  const raw = record(value)
  const source = sourceIdentity(raw?.source)
  const backend = raw?.backend === 'geometry' || raw?.backend === 'assembly' ? raw.backend : null
  const operation = ['create', 'modify', 'measure', 'validate', 'assemble', 'drawing'].includes(String(raw?.operation))
    ? raw?.operation as PhotonCadOperationKind
    : null
  const title = photonCadDisplayText(raw?.title, 256)
  const category = photonCadDisplayText(raw?.category, 128)
  if (!raw || !isPhotonCadIdentifier(raw.id) || !source || !backend || !operation || !title || !category
    || !Array.isArray(raw.parameters) || raw.parameters.length > PHOTON_CAD_LIMITS.catalogParameters
    || typeof raw.previewSupported !== 'boolean' || typeof raw.experimental !== 'boolean') return null
  const normalizedParameters = raw.parameters.map(normalizeParameter)
  if (normalizedParameters.some((item) => item === null)) return null
  const parameters = normalizedParameters as PhotonCadParameterDefinition[]
  if (new Set(parameters.map((parameter) => parameter.id.toLowerCase())).size !== parameters.length) return null
  return {
    id: raw.id,
    backend,
    category,
    title,
    description: photonCadDisplayText(raw.description),
    operation,
    parameters,
    source,
    previewSupported: raw.previewSupported,
    experimental: raw.experimental,
  }
}

export function normalizePhotonCadCatalog(value: unknown): PhotonCadCatalog | null {
  const raw = record(value)
  const coverage = record(raw?.coverage)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.catalogRevision)
    || !isPhotonCadUtcTimestamp(raw.generatedAtUtc)
    || !Array.isArray(raw.capabilities) || raw.capabilities.length > PHOTON_CAD_LIMITS.catalogCapabilities || !coverage) return null
  const normalizedCapabilities = raw.capabilities.map(normalizeCapability)
  if (normalizedCapabilities.some((item) => item === null)) return null
  const capabilities = normalizedCapabilities as PhotonCadCapability[]
  if (new Set(capabilities.map((capability) => capability.id.toLowerCase())).size !== capabilities.length) return null
  const discovered = boundedRevision(coverage.discovered)
  const available = boundedRevision(coverage.available)
  const unavailable = boundedRevision(coverage.unavailable)
  if (discovered === null || available === null || unavailable === null || available + unavailable !== discovered || available !== capabilities.length) return null
  if (!Array.isArray(coverage.unavailableReasons) || coverage.unavailableReasons.length > 128
    || coverage.unavailableReasons.some((item) => !isPhotonCadSafeText(item, 512, true))) return null
  return {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    catalogRevision: raw.catalogRevision,
    generatedAtUtc: raw.generatedAtUtc,
    capabilities,
    coverage: {
      discovered,
      available,
      unavailable,
      unavailableReasons: [...coverage.unavailableReasons] as string[],
    },
  }
}

export function isOpaquePhotonCadReviewHandle(value: unknown): value is string {
  return typeof value === 'string' && /^cad-review:[A-Za-z0-9_-]{32,160}$/u.test(value)
}

export function validatePhotonCadStepExportRequest(request: PhotonCadStepExportRequest): string[] {
  const issues: string[] = []
  if (request.contractVersion !== PHOTON_CAD_CONTRACT_VERSION) issues.push('unsupported-contract')
  if (!isPhotonCadIdentifier(request.requestId)) issues.push('invalid-request-id')
  if (!isPhotonCadIdentifier(request.sessionId)) issues.push('invalid-session-id')
  if (!isPhotonCadIdentifier(request.projectId)) issues.push('invalid-project-id')
  if (boundedRevision(request.revision) === null) issues.push('invalid-revision')
  if (!isPhotonCadDigest(request.contentDigest)) issues.push('invalid-content-digest')
  if (!isPhotonCadIdentifier(request.entityId)) issues.push('invalid-entity-id')
  return issues
}

export function canonicalPhotonCadReleaseBinding(request: PhotonCadReleaseReviewRequest) {
  return JSON.stringify({
    contractVersion: request.contractVersion,
    sessionId: request.sessionId,
    projectId: request.projectId,
    revision: request.revision,
    formats: [...new Set(request.formats)].sort(),
    destinationHandle: request.destinationHandle,
  })
}

export function validatePhotonCadInterchangeManifest(manifest: PhotonCadInterchangeManifest): string[] {
  const issues: string[] = []
  if (manifest.schemaVersion !== PHOTON_CAD_INTERCHANGE_VERSION) issues.push('unsupported-schema')
  if (!isPhotonCadIdentifier(manifest.packageId)) issues.push('invalid-package-id')
  if (!isPhotonCadDigest(manifest.packageFingerprint)) issues.push('invalid-package-fingerprint')
  if (!isPhotonCadIdentifier(manifest.project.projectId) || boundedRevision(manifest.project.revision) === null) issues.push('invalid-project')
  if (!['millimeter', 'inch'].includes(manifest.project.units)) issues.push('invalid-units')
  if (!ISO_UTC.test(manifest.createdAtUtc)) issues.push('invalid-created-time')
  if (!isPhotonCadIdentifier(manifest.profile.id) || !['AP214', 'AP242'].includes(manifest.profile.stepApplicationProtocol)) issues.push('invalid-profile')
  if (manifest.profile.coordinateSystem.handedness !== 'right'
    || manifest.profile.coordinateSystem.upAxis !== 'z'
    || manifest.profile.coordinateSystem.matrixOrder !== 'row-major'
    || manifest.profile.coordinateSystem.vectorConvention !== 'column'
    || manifest.profile.coordinateSystem.transformMeaning !== 'local-to-parent') issues.push('invalid-coordinate-system')
  if (manifest.files.length > PHOTON_CAD_LIMITS.packageFiles || manifest.files.some((file) =>
    !normalizePhotonCadRelativePath(file.relativePath)
    || !isPhotonCadDigest(file.sha256)
    || !Number.isSafeInteger(file.byteLength)
    || file.byteLength < 0)) issues.push('invalid-file')
  const paths = manifest.files.map((file) => file.relativePath.toLowerCase())
  if (new Set(paths).size !== paths.length) issues.push('duplicate-file')
  if (manifest.bom.length > PHOTON_CAD_LIMITS.bomRows || manifest.bom.some((row) =>
    !isPhotonCadIdentifier(row.partNumber)
    || !isPhotonCadIdentifier(row.sourceEntityId)
    || !Number.isFinite(row.quantity)
    || row.quantity <= 0)) issues.push('invalid-bom')
  if (manifest.occurrences.length > PHOTON_CAD_LIMITS.occurrences || manifest.occurrences.some((occurrence) =>
    !isPhotonCadIdentifier(occurrence.occurrenceId)
    || !isPhotonCadIdentifier(occurrence.partNumber)
    || !isPhotonCadIdentifier(occurrence.sourceEntityId)
    || occurrence.transform.length !== 16
    || occurrence.transform.some((value) => boundedNumber(value) === null))) issues.push('invalid-occurrence')
  if (!isPhotonCadDigest(manifest.provenance.operationDigest)) issues.push('invalid-operation-digest')
  if (!sourceIdentity(manifest.provenance.geometryRuntime)
    || (manifest.provenance.assemblyRuntime && !sourceIdentity(manifest.provenance.assemblyRuntime))) issues.push('invalid-provenance')
  return [...new Set(issues)]
}

export function photonCadReasonText(reason: string) {
  const messages: Record<string, string> = {
    ready: 'Photon CAD is ready.',
    unavailable: 'The verified CAD runtime is not installed. Design data was not changed.',
    'stale-revision': 'The project changed while this work was running. Review the newest revision and try again.',
    'stale-result': 'A newer CAD result replaced this one.',
    'validation-failed': 'The design did not pass validation. Review the reported checks before releasing it.',
    'review-expired': 'The release review expired. Prepare a new review.',
    'review-replaced': 'A newer release review replaced this one.',
    'commit-failed': 'The package could not be committed. No success was recorded.',
    committed: 'The verified CAD package was created.',
  }
  return messages[reason] ?? 'Photon CAD could not complete that request. No success was recorded.'
}
