import {
  PHOTON_CAD_CONTRACT_VERSION,
  PHOTON_CAD_LIMITS,
  isOpaquePhotonCadReviewHandle,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  normalizePhotonCadCatalog,
  normalizePhotonCadRelativePath,
  photonCadDisplayText,
  validatePhotonCadOperationRequest,
  validatePhotonCadStepExportRequest,
  type PhotonCadController,
  type PhotonCadAssemblyOccurrence,
  type PhotonCadEntity,
  type PhotonCadIssue,
  type PhotonCadOperationRecord,
  type PhotonCadOperationRequest,
  type PhotonCadOperationResult,
  type PhotonCadPreviewReceipt,
  type PhotonCadProjectSnapshot,
  type PhotonCadReleaseCommitRequest,
  type PhotonCadReleaseCommitResult,
  type PhotonCadReleaseFormat,
  type PhotonCadReleaseReviewRequest,
  type PhotonCadReleaseReviewResult,
  type PhotonCadRuntimeDescription,
  type PhotonCadStepExportRequest,
  type PhotonCadStepExportResult,
  type PhotonCadVerificationRequest,
  type PhotonCadVerificationResult,
  type PhotonCadVector3,
} from './PhotonCadContract'

export const PHOTON_CAD_DESKTOP_PROTOCOL_VERSION = 1 as const

const MAXIMUM_REQUEST_HISTORY = 50_000
const DEFAULT_REQUEST_TIMEOUT_MS: Record<PendingKind, number> = {
  describe: 15_000,
  execute: 600_000,
  verify: 600_000,
  review: 600_000,
  commit: 120_000,
  'step-export': 600_000,
}
const MAXIMUM_REQUEST_TIMEOUT_MS = 600_000
const ISO_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u
const DESTINATION_HANDLE = /^cad-destination:[A-Za-z0-9_-]{32,160}$/u
const RELEASE_FORMATS = new Set<PhotonCadReleaseFormat>(['step-ap214', 'step-ap242', 'stl', 'dxf', 'svg'])

export type PhotonCadWebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

export type PhotonCadDesktopRequest =
  | { type: 'photonCad.describe'; version: 1; contractVersion: 1; requestId: string }
  | ({ type: 'photonCad.execute'; version: 1 } & PhotonCadOperationRequest)
  | ({ type: 'photonCad.verify'; version: 1 } & PhotonCadVerificationRequest)
  | ({ type: 'photonCad.release.review'; version: 1 } & PhotonCadReleaseReviewRequest)
  | ({ type: 'photonCad.release.commit'; version: 1 } & PhotonCadReleaseCommitRequest)
  | ({ type: 'photonCad.step.export'; version: 1 } & PhotonCadStepExportRequest)
  | { type: 'photonCad.release.discard'; version: 1; requestId: string; reviewHandle: string }
  | { type: 'photonCad.cancel'; version: 1; requestId: string; targetRequestId: string; operation: 'execute' | 'verify' | 'review' | 'step-export' }

export type PhotonCadHostFrame =
  | { type: 'describe'; requestId: string; value: PhotonCadRuntimeDescription }
  | { type: 'execute'; value: PhotonCadOperationResult }
  | { type: 'verify'; value: PhotonCadVerificationResult }
  | { type: 'review'; value: PhotonCadReleaseReviewResult }
  | { type: 'commit'; value: PhotonCadReleaseCommitResult }
  | { type: 'step-export'; value: PhotonCadStepExportResult }
  | { type: 'error'; requestId: string; code: string; retryable: boolean }

type PendingKind = 'describe' | 'execute' | 'verify' | 'review' | 'commit' | 'step-export'
type PendingRequest = {
  kind: PendingKind
  requestId: string
  request?: PhotonCadOperationRequest | PhotonCadVerificationRequest | PhotonCadReleaseReviewRequest | PhotonCadReleaseCommitRequest | PhotonCadStepExportRequest
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  timeout: ReturnType<typeof setTimeout>
}

export type DesktopPhotonCadClientOptions = {
  getBridge?: () => PhotonCadWebViewBridge | null
  createRequestId?: (kind: string, sequence: number) => string
  requestTimeoutMs?: number | Partial<Record<PendingKind, number>>
}

function browserBridge(): PhotonCadWebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: PhotonCadWebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function boundedRevision(value: unknown): number | null {
  return typeof value === 'number'
    && Number.isSafeInteger(value)
    && value >= 0
    && value <= PHOTON_CAD_LIMITS.absoluteMagnitude
    ? value
    : null
}

function boundedCount(value: unknown, maximum: number): number | null {
  const normalized = boundedRevision(value)
  return normalized !== null && normalized <= maximum ? normalized : null
}

function reasonCode(value: unknown): string | null {
  return isPhotonCadIdentifier(value) ? value : null
}

function optionalIdentifier(value: unknown): string | undefined | null {
  if (value === undefined) return undefined
  return isPhotonCadIdentifier(value) ? value : null
}

function normalizeVector(value: unknown): PhotonCadVector3 | null {
  const raw = record(value)
  if (!raw) return null
  const values = [raw.x, raw.y, raw.z]
  if (values.some((item) => typeof item !== 'number' || !Number.isFinite(item) || Math.abs(item) > PHOTON_CAD_LIMITS.absoluteMagnitude)) return null
  return { x: values[0] as number, y: values[1] as number, z: values[2] as number }
}

function normalizeIssue(value: unknown): PhotonCadIssue | null {
  const raw = record(value)
  if (!raw || !isPhotonCadIdentifier(raw.code)
    || (raw.severity !== 'info' && raw.severity !== 'warning' && raw.severity !== 'error')
    || !Array.isArray(raw.entityIds)
    || raw.entityIds.length > PHOTON_CAD_LIMITS.entities
    || raw.entityIds.some((id) => !isPhotonCadIdentifier(id))) return null
  const message = photonCadDisplayText(raw.message)
  return message ? { code: raw.code, severity: raw.severity, message, entityIds: [...raw.entityIds] as string[] } : null
}

function normalizeIssues(value: unknown): PhotonCadIssue[] | null {
  if (!Array.isArray(value) || value.length > PHOTON_CAD_LIMITS.issues) return null
  const issues = value.map(normalizeIssue)
  return issues.some((issue) => issue === null) ? null : issues as PhotonCadIssue[]
}

function normalizeEntity(value: unknown): PhotonCadEntity | null {
  const raw = record(value)
  if (!raw || !Object.prototype.hasOwnProperty.call(raw, 'parentId')) return null
  const parentId = raw.parentId === null ? null : isPhotonCadIdentifier(raw.parentId) ? raw.parentId : undefined
  const sourceCapabilityId = optionalIdentifier(raw?.sourceCapabilityId)
  const name = photonCadDisplayText(raw?.name, 256)
  if (!isPhotonCadIdentifier(raw.id) || parentId === undefined
    || !['body', 'part', 'assembly', 'occurrence', 'drawing', 'datum'].includes(String(raw.kind))
    || !name || typeof raw.visible !== 'boolean' || typeof raw.suppressed !== 'boolean'
    || sourceCapabilityId === null) return null
  return {
    id: raw.id,
    parentId,
    kind: raw.kind as PhotonCadEntity['kind'],
    name,
    visible: raw.visible,
    suppressed: raw.suppressed,
    sourceCapabilityId,
  }
}

function normalizeOperation(value: unknown): PhotonCadOperationRecord | null {
  const raw = record(value)
  const label = photonCadDisplayText(raw?.label, 256)
  if (!raw || !isPhotonCadIdentifier(raw.id) || !isPhotonCadIdentifier(raw.capabilityId) || !label
    || typeof raw.createdAtUtc !== 'string' || !ISO_UTC.test(raw.createdAtUtc)
    || !['proposed', 'applied', 'rejected'].includes(String(raw.state))) return null
  return { id: raw.id, capabilityId: raw.capabilityId, label, createdAtUtc: raw.createdAtUtc, state: raw.state as PhotonCadOperationRecord['state'] }
}

const MATRIX_TOLERANCE = 1e-9
const MAXIMUM_OCCURRENCE_DEPTH = 256

function near(left: number, right: number) {
  return Math.abs(left - right) <= MATRIX_TOLERANCE
}

function normalizeRigidTransform(value: unknown): PhotonCadAssemblyOccurrence['transform'] | null {
  if (!Array.isArray(value) || value.length !== 16
    || value.some((item) => typeof item !== 'number' || !Number.isFinite(item)
      || Math.abs(item) > PHOTON_CAD_LIMITS.absoluteMagnitude)) return null
  const matrix = value as number[]
  if (!near(matrix[12], 0) || !near(matrix[13], 0) || !near(matrix[14], 0) || !near(matrix[15], 1)) return null
  for (let row = 0; row < 3; row += 1) {
    let length = 0
    for (let column = 0; column < 3; column += 1) length += matrix[(row * 4) + column] ** 2
    if (!near(length, 1)) return null
    for (let other = row + 1; other < 3; other += 1) {
      let dot = 0
      for (let column = 0; column < 3; column += 1) dot += matrix[(row * 4) + column] * matrix[(other * 4) + column]
      if (!near(dot, 0)) return null
    }
  }
  const determinant = matrix[0] * ((matrix[5] * matrix[10]) - (matrix[6] * matrix[9]))
    - matrix[1] * ((matrix[4] * matrix[10]) - (matrix[6] * matrix[8]))
    + matrix[2] * ((matrix[4] * matrix[9]) - (matrix[5] * matrix[8]))
  return near(determinant, 1) ? matrix as unknown as PhotonCadAssemblyOccurrence['transform'] : null
}

function normalizeOccurrence(value: unknown): PhotonCadAssemblyOccurrence | null {
  const raw = record(value)
  const parentOccurrenceId = raw?.parentOccurrenceId === null
    ? null
    : isPhotonCadIdentifier(raw?.parentOccurrenceId) ? raw.parentOccurrenceId : undefined
  const transform = normalizeRigidTransform(raw?.transform)
  if (!raw || !isPhotonCadIdentifier(raw.occurrenceId) || parentOccurrenceId === undefined
    || !isPhotonCadSafeText(raw.partNumber, 256, true) || !isPhotonCadIdentifier(raw.sourceEntityId)
    || !transform) return null
  return {
    occurrenceId: raw.occurrenceId,
    parentOccurrenceId,
    partNumber: raw.partNumber,
    sourceEntityId: raw.sourceEntityId,
    transform,
  }
}

function validOccurrenceGraph(occurrences: PhotonCadAssemblyOccurrence[], entities: PhotonCadEntity[]) {
  if (occurrences.length > 0 && occurrences.filter((value) => value.parentOccurrenceId === null).length !== 1) return false
  const byOccurrence = new Map<string, PhotonCadAssemblyOccurrence>()
  for (const occurrence of occurrences) {
    const key = occurrence.occurrenceId.toLowerCase()
    if (byOccurrence.has(key)) return false
    byOccurrence.set(key, occurrence)
  }
  const byEntity = new Map(entities.map((entity) => [entity.id.toLowerCase(), entity]))
  const pseudoEntityCount = entities.filter((entity) => entity.kind === 'occurrence').length
  if (pseudoEntityCount !== occurrences.length
    || entities.length - pseudoEntityCount > PHOTON_CAD_LIMITS.entities) return false
  for (const occurrence of occurrences) {
    const source = byEntity.get(occurrence.sourceEntityId.toLowerCase())
    const pseudo = byEntity.get(occurrence.occurrenceId.toLowerCase())
    if (!source || source.kind === 'occurrence' || !pseudo || pseudo.kind !== 'occurrence'
      || pseudo.parentId !== occurrence.parentOccurrenceId || pseudo.name !== occurrence.partNumber
      || pseudo.sourceCapabilityId !== source.sourceCapabilityId || !pseudo.visible || pseudo.suppressed) return false
    const visited = new Set<string>()
    let cursor: PhotonCadAssemblyOccurrence | undefined = occurrence
    let depth = 0
    while (cursor.parentOccurrenceId !== null) {
      const cursorKey = cursor.occurrenceId.toLowerCase()
      if (++depth > MAXIMUM_OCCURRENCE_DEPTH || visited.has(cursorKey)) return false
      visited.add(cursorKey)
      cursor = byOccurrence.get(cursor.parentOccurrenceId.toLowerCase())
      if (!cursor) return false
    }
  }
  return true
}

export function normalizePhotonCadProjectSnapshot(value: unknown): PhotonCadProjectSnapshot | null {
  const raw = record(value)
  const revision = boundedRevision(raw?.revision)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION
    || !isPhotonCadIdentifier(raw.sessionId) || !isPhotonCadIdentifier(raw.projectId) || revision === null
    || (raw.units !== 'millimeter' && raw.units !== 'inch')
    || (raw.mode !== 'canonical' && raw.mode !== 'scratch')
    || typeof raw.dirty !== 'boolean'
    || !Array.isArray(raw.entities) || raw.entities.length > PHOTON_CAD_LIMITS.entities + PHOTON_CAD_LIMITS.occurrences
    || !Array.isArray(raw.occurrences) || raw.occurrences.length > PHOTON_CAD_LIMITS.occurrences
    || !Array.isArray(raw.operations) || raw.operations.length > PHOTON_CAD_LIMITS.operations) return null
  const title = photonCadDisplayText(raw.title, 256)
  const entities = raw.entities.map(normalizeEntity)
  const occurrences = raw.occurrences.map(normalizeOccurrence)
  const operations = raw.operations.map(normalizeOperation)
  const issues = normalizeIssues(raw.issues)
  if (!title || issues === null || entities.some((item) => item === null) || occurrences.some((item) => item === null)
    || operations.some((item) => item === null)) return null
  const entityIds = entities.map((item) => item?.id.toLowerCase())
  const operationIds = operations.map((item) => item?.id.toLowerCase())
  if (new Set(entityIds).size !== entityIds.length || new Set(operationIds).size !== operationIds.length
    || !validOccurrenceGraph(occurrences as PhotonCadAssemblyOccurrence[], entities as PhotonCadEntity[])) return null
  return {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    sessionId: raw.sessionId,
    projectId: raw.projectId,
    revision,
    title,
    units: raw.units,
    mode: raw.mode,
    entities: entities as PhotonCadEntity[],
    occurrences: occurrences as PhotonCadAssemblyOccurrence[],
    operations: operations as PhotonCadOperationRecord[],
    issues,
    dirty: raw.dirty,
  }
}

function normalizePreview(value: unknown): PhotonCadPreviewReceipt | null {
  const raw = record(value)
  const revision = boundedRevision(raw?.revision)
  const minimum = normalizeVector(record(raw?.bounds)?.minimum)
  const maximum = normalizeVector(record(raw?.bounds)?.maximum)
  const entityCount = boundedCount(raw?.entityCount, PHOTON_CAD_LIMITS.entities)
  if (!raw || !isPhotonCadIdentifier(raw.previewId) || !isPhotonCadIdentifier(raw.projectId)
    || revision === null || !isPhotonCadDigest(raw.contentDigest)
    || (raw.units !== 'millimeter' && raw.units !== 'inch') || !minimum || !maximum || entityCount === null) return null
  return { previewId: raw.previewId, projectId: raw.projectId, revision, contentDigest: raw.contentDigest.toLowerCase(), units: raw.units, bounds: { minimum, maximum }, entityCount }
}

function normalizeRuntimeDescription(value: unknown): PhotonCadRuntimeDescription | null {
  const raw = record(value)
  const reason = reasonCode(raw?.reason)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || !reason
    || !['available', 'unavailable', 'checking', 'error'].includes(String(raw.status))) return null
  const status = raw.status as PhotonCadRuntimeDescription['status']
  if (status !== 'available') return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status, reason }
  const geometryBundleId = optionalIdentifier(raw.geometryBundleId)
  const assemblyBundleId = optionalIdentifier(raw.assemblyBundleId)
  const catalog = normalizePhotonCadCatalog(raw.catalog)
  if (!geometryBundleId || assemblyBundleId === null || !catalog) return null
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'available', reason, geometryBundleId, assemblyBundleId, catalog }
}

function normalizeOperationResult(value: unknown): PhotonCadOperationResult | null {
  const raw = record(value)
  const baseRevision = boundedRevision(raw?.baseRevision)
  const resultingRevision = boundedRevision(raw?.resultingRevision)
  const reason = reasonCode(raw?.reason)
  const issues = normalizeIssues(raw?.issues)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION
    || !isPhotonCadIdentifier(raw.requestId) || !isPhotonCadIdentifier(raw.projectId)
    || baseRevision === null || resultingRevision === null || resultingRevision < baseRevision
    || !['accepted', 'rejected', 'unavailable'].includes(String(raw.status))
    || typeof raw.stale !== 'boolean' || !reason || issues === null) return null
  const snapshot = raw.snapshot === undefined ? undefined : normalizePhotonCadProjectSnapshot(raw.snapshot)
  const preview = raw.preview === undefined ? undefined : normalizePreview(raw.preview)
  if (snapshot === null || preview === null) return null
  if (snapshot && (snapshot.projectId !== raw.projectId || snapshot.revision !== resultingRevision)) return null
  if (preview && (preview.projectId !== raw.projectId || preview.revision !== resultingRevision)) return null
  if (raw.status === 'accepted' && !snapshot && !preview) return null
  if (raw.status !== 'accepted' && (snapshot || preview || resultingRevision !== baseRevision)) return null
  const status = raw.status as PhotonCadOperationResult['status']
  return {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    requestId: raw.requestId,
    projectId: raw.projectId,
    baseRevision,
    resultingRevision,
    status,
    stale: raw.stale,
    reason,
    snapshot,
    preview,
    issues,
  }
}

function normalizeVerificationResult(value: unknown): PhotonCadVerificationResult | null {
  const raw = record(value)
  const revision = boundedRevision(raw?.revision)
  const issues = normalizeIssues(raw?.issues)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION
    || !isPhotonCadIdentifier(raw.requestId) || !isPhotonCadIdentifier(raw.projectId) || revision === null
    || !['passed', 'failed', 'unavailable'].includes(String(raw.status))
    || typeof raw.stale !== 'boolean' || issues === null
    || typeof raw.measuredAtUtc !== 'string' || !ISO_UTC.test(raw.measuredAtUtc)) return null
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: raw.requestId, projectId: raw.projectId, revision, status: raw.status as PhotonCadVerificationResult['status'], stale: raw.stale, issues, measuredAtUtc: raw.measuredAtUtc }
}

function normalizeReviewResult(value: unknown): PhotonCadReleaseReviewResult | null {
  const raw = record(value)
  const revision = boundedRevision(raw?.revision)
  const reason = reasonCode(raw?.reason)
  const issues = normalizeIssues(raw?.issues)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION
    || !isPhotonCadIdentifier(raw.requestId) || !isPhotonCadIdentifier(raw.projectId) || revision === null
    || !['ready', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason || issues === null
    || !Array.isArray(raw.files) || raw.files.length > PHOTON_CAD_LIMITS.packageFiles) return null
  const files = raw.files.map((value) => {
    const file = record(value)
    const relativePath = normalizePhotonCadRelativePath(file?.relativePath)
    return file && ['manifest', 'part-step', 'assembly-step', 'bom', 'drawing', 'preview', 'validation-report'].includes(String(file.role)) && relativePath
      ? { role: file.role as PhotonCadReleaseReviewResult['files'][number]['role'], relativePath }
      : null
  })
  if (files.some((file) => file === null)) return null
  const canonicalPaths = files.map((file) => file?.relativePath.toLowerCase())
  if (new Set(canonicalPaths).size !== canonicalPaths.length) return null
  if (raw.status === 'ready') {
    if (!isOpaquePhotonCadReviewHandle(raw.reviewHandle) || !isPhotonCadDigest(raw.packageFingerprint)
      || typeof raw.expiresAtUtc !== 'string' || !ISO_UTC.test(raw.expiresAtUtc)) return null
    return {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: raw.requestId,
      projectId: raw.projectId,
      revision,
      status: 'ready',
      reason,
      reviewHandle: raw.reviewHandle,
      packageFingerprint: raw.packageFingerprint.toLowerCase(),
      expiresAtUtc: raw.expiresAtUtc,
      files: files as PhotonCadReleaseReviewResult['files'],
      issues,
    }
  }
  if (raw.reviewHandle !== undefined || raw.packageFingerprint !== undefined || raw.expiresAtUtc !== undefined) return null
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: raw.requestId, projectId: raw.projectId, revision, status: raw.status as PhotonCadReleaseReviewResult['status'], reason, files: files as PhotonCadReleaseReviewResult['files'], issues }
}

function normalizeCommitResult(value: unknown): PhotonCadReleaseCommitResult | null {
  const raw = record(value)
  const reason = reasonCode(raw?.reason)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !['committed', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason) return null
  if (raw.status === 'committed') {
    if (!isPhotonCadIdentifier(raw.packageId) || !isPhotonCadDigest(raw.packageFingerprint)) return null
    return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: raw.requestId, status: 'committed', reason, packageId: raw.packageId, packageFingerprint: raw.packageFingerprint.toLowerCase() }
  }
  if (raw.packageId !== undefined || raw.packageFingerprint !== undefined) return null
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: raw.requestId, status: raw.status as PhotonCadReleaseCommitResult['status'], reason }
}

function exactKeys(raw: Record<string, unknown>, expected: readonly string[]) {
  const actual = Object.keys(raw).sort()
  const wanted = [...expected].sort()
  return actual.length === wanted.length && actual.every((key, index) => key === wanted[index])
}

function normalizeStepExportResult(value: unknown): PhotonCadStepExportResult | null {
  const raw = record(value)
  const reason = reasonCode(raw?.reason)
  const revision = boundedRevision(raw?.revision)
  if (!raw || raw.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || !isPhotonCadIdentifier(raw.requestId)
    || !isPhotonCadIdentifier(raw.projectId) || revision === null
    || !['committed', 'cancelled', 'rejected', 'unavailable'].includes(String(raw.status)) || !reason) return null
  if (raw.status === 'committed') {
    if (!exactKeys(raw, ['contractVersion', 'requestId', 'projectId', 'revision', 'status', 'reason', 'entityId', 'contentDigest', 'byteLength', 'destinationLabel'])
      || !isPhotonCadIdentifier(raw.entityId) || !isPhotonCadDigest(raw.contentDigest)
      || typeof raw.byteLength !== 'number' || !Number.isSafeInteger(raw.byteLength) || raw.byteLength <= 0
      || raw.byteLength > PHOTON_CAD_LIMITS.absoluteMagnitude
      || !isPhotonCadSafeText(raw.destinationLabel, 256, true) || /[\\/:]/u.test(raw.destinationLabel)
      || raw.destinationLabel === '.' || raw.destinationLabel === '..') return null
    return {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: raw.requestId,
      projectId: raw.projectId,
      revision,
      status: 'committed',
      reason,
      entityId: raw.entityId,
      contentDigest: raw.contentDigest.toLowerCase(),
      byteLength: raw.byteLength,
      destinationLabel: raw.destinationLabel,
    }
  }
  if (!exactKeys(raw, ['contractVersion', 'requestId', 'projectId', 'revision', 'status', 'reason'])) return null
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: raw.requestId, projectId: raw.projectId, revision, status: raw.status as PhotonCadStepExportResult['status'], reason }
}

export function normalizePhotonCadHostFrame(value: unknown): PhotonCadHostFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== PHOTON_CAD_DESKTOP_PROTOCOL_VERSION) return null
  if (raw.type === 'photonCad.error') {
    return isPhotonCadIdentifier(raw.requestId) && isPhotonCadIdentifier(raw.code) && typeof raw.retryable === 'boolean'
      ? { type: 'error', requestId: raw.requestId, code: raw.code, retryable: raw.retryable }
      : null
  }
  if (raw.type === 'photonCad.describe.result') {
    const requestId = isPhotonCadIdentifier(raw.requestId) ? raw.requestId : null
    const description = normalizeRuntimeDescription(raw.value)
    return requestId && description ? { type: 'describe', requestId, value: description } : null
  }
  if (raw.type === 'photonCad.execute.result') {
    const result = normalizeOperationResult(raw.value)
    return result ? { type: 'execute', value: result } : null
  }
  if (raw.type === 'photonCad.verify.result') {
    const result = normalizeVerificationResult(raw.value)
    return result ? { type: 'verify', value: result } : null
  }
  if (raw.type === 'photonCad.release.review.result') {
    const result = normalizeReviewResult(raw.value)
    return result ? { type: 'review', value: result } : null
  }
  if (raw.type === 'photonCad.release.commit.result') {
    const result = normalizeCommitResult(raw.value)
    return result ? { type: 'commit', value: result } : null
  }
  if (raw.type === 'photonCad.step.export.result') {
    const result = normalizeStepExportResult(raw.value)
    return result ? { type: 'step-export', value: result } : null
  }
  return null
}

function unavailableRuntime(reason: string): PhotonCadRuntimeDescription {
  return { contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'unavailable', reason }
}

function validateVerificationRequest(request: PhotonCadVerificationRequest) {
  const allowed = new Set(['valid-solids', 'interference', 'dimensions', 'assembly-structure', 'export-readiness'])
  return request.contractVersion === PHOTON_CAD_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId) && isPhotonCadIdentifier(request.sessionId) && isPhotonCadIdentifier(request.projectId)
    && boundedRevision(request.revision) !== null && request.checks.length > 0 && request.checks.length <= allowed.size
    && new Set(request.checks).size === request.checks.length && request.checks.every((check) => allowed.has(check))
}

function validateReviewRequest(request: PhotonCadReleaseReviewRequest) {
  return request.contractVersion === PHOTON_CAD_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId) && isPhotonCadIdentifier(request.sessionId) && isPhotonCadIdentifier(request.projectId)
    && boundedRevision(request.revision) !== null && request.formats.length > 0 && request.formats.length <= RELEASE_FORMATS.size
    && new Set(request.formats).size === request.formats.length && request.formats.every((format) => RELEASE_FORMATS.has(format))
    && DESTINATION_HANDLE.test(request.destinationHandle)
}

export class DesktopPhotonCadClient implements PhotonCadController {
  private readonly getBridge: () => PhotonCadWebViewBridge | null
  private readonly createId: (kind: string, sequence: number) => string
  private readonly requestTimeoutMs: Record<PendingKind, number>
  private bridge: PhotonCadWebViewBridge | null = null
  private sequence = 0
  private closed = false
  private readonly pending = new Map<string, PendingRequest>()
  private readonly usedRequestIds = new Set<string>()
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizePhotonCadHostFrame(event.data)
    if (!frame) return
    if (frame.type === 'error') {
      this.fail(frame.requestId, new Error(`Photon CAD host rejected the request (${frame.code}).`))
      return
    }
    const requestId = frame.type === 'describe' ? frame.requestId : frame.value.requestId
    const pending = this.pending.get(requestId)
    if (!pending || pending.kind !== frame.type || !this.matches(pending, frame)) return
    this.finish(pending)
    pending.resolve(frame.type === 'describe' ? frame.value : frame.value)
  }

  public constructor(options: DesktopPhotonCadClientOptions = {}) {
    this.getBridge = options.getBridge ?? browserBridge
    const nonce = defaultNonce()
    this.createId = options.createRequestId ?? ((kind, sequence) => `cad-${kind}:${nonce}:${sequence.toString(36)}`)
    this.requestTimeoutMs = normalizeTimeouts(options.requestTimeoutMs)
  }

  public get available() {
    return !this.closed && this.getBridge() !== null
  }

  public describe(): Promise<PhotonCadRuntimeDescription> {
    if (this.closed) return Promise.resolve(unavailableRuntime('client-closed'))
    if (!this.getBridge()) return Promise.resolve(unavailableRuntime('desktop-host-unavailable'))
    const requestId = this.nextRequestId('describe')
    return this.send('describe', requestId, undefined, {
      type: 'photonCad.describe',
      version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION,
      contractVersion: 1,
      requestId,
    })
  }

  public execute(request: PhotonCadOperationRequest): Promise<PhotonCadOperationResult> {
    const validation = validatePhotonCadOperationRequest(request)
    if (validation.length) return Promise.reject(new Error(`Invalid Photon CAD operation request (${validation.join(',')}).`))
    if (!this.available) return Promise.resolve({
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: request.requestId,
      projectId: request.projectId,
      baseRevision: request.baseRevision,
      resultingRevision: request.baseRevision,
      status: 'unavailable',
      stale: false,
      reason: this.closed ? 'client-closed' : 'desktop-host-unavailable',
      issues: [],
    })
    return this.send('execute', request.requestId, request, { type: 'photonCad.execute', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, ...request })
  }

  public verify(request: PhotonCadVerificationRequest): Promise<PhotonCadVerificationResult> {
    if (!validateVerificationRequest(request)) return Promise.reject(new Error('Invalid Photon CAD verification request.'))
    if (!this.available) return Promise.resolve({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: request.requestId, projectId: request.projectId, revision: request.revision, status: 'unavailable', stale: false, issues: [], measuredAtUtc: new Date(0).toISOString() })
    return this.send('verify', request.requestId, request, { type: 'photonCad.verify', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, ...request })
  }

  public reviewRelease(request: PhotonCadReleaseReviewRequest): Promise<PhotonCadReleaseReviewResult> {
    if (!validateReviewRequest(request)) return Promise.reject(new Error('Invalid Photon CAD release-review request.'))
    if (!this.available) return Promise.resolve({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: request.requestId, projectId: request.projectId, revision: request.revision, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable', files: [], issues: [] })
    return this.send('review', request.requestId, request, { type: 'photonCad.release.review', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, ...request })
  }

  public commitRelease(request: PhotonCadReleaseCommitRequest): Promise<PhotonCadReleaseCommitResult> {
    if (request.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || !isPhotonCadIdentifier(request.requestId)
      || !isOpaquePhotonCadReviewHandle(request.reviewHandle) || !isPhotonCadDigest(request.packageFingerprint)) {
      return Promise.reject(new Error('Invalid Photon CAD release-commit request.'))
    }
    if (!this.available) return Promise.resolve({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, requestId: request.requestId, status: 'unavailable', reason: this.closed ? 'client-closed' : 'desktop-host-unavailable' })
    return this.send('commit', request.requestId, request, { type: 'photonCad.release.commit', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, ...request })
  }

  public exportStep(request: PhotonCadStepExportRequest): Promise<PhotonCadStepExportResult> {
    if (validatePhotonCadStepExportRequest(request).length) return Promise.reject(new Error('Invalid Photon CAD STEP-export request.'))
    if (!this.available) return Promise.resolve({
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: request.requestId,
      projectId: request.projectId,
      revision: request.revision,
      status: 'unavailable',
      reason: this.closed ? 'client-closed' : 'desktop-host-unavailable',
    })
    this.replacePending('step-export')
    return this.send('step-export', request.requestId, request, { type: 'photonCad.step.export', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, ...request })
  }

  public discardRelease(reviewHandle: string) {
    if (!isOpaquePhotonCadReviewHandle(reviewHandle) || !this.available) return
    const requestId = this.nextRequestId('discard')
    try {
      this.reserveRequestId(requestId)
    } catch {
      return
    }
    const bridge = this.connect()
    if (!bridge) return
    try {
      bridge.postMessage({ type: 'photonCad.release.discard', version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION, requestId, reviewHandle } satisfies PhotonCadDesktopRequest)
    } catch {
      // Discard is best effort. A review handle is short-lived and commit still requires exact binding.
    }
  }

  public close() {
    if (this.closed) return
    this.cancelRequests([...this.pending.values()].filter(isCancellablePending), this.bridge)
    this.closed = true
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
    this.rejectAll(new Error('Photon CAD client closed before the host request completed.'))
  }

  public cancelPending() {
    const cancellable = [...this.pending.values()].filter(isCancellablePending)
    this.cancelRequests(cancellable, this.bridge)
    for (const pending of cancellable) {
      this.finish(pending)
      pending.reject(new Error('Photon CAD request was invalidated before completion.'))
    }
  }

  private send<T>(kind: PendingKind, requestId: string, request: PendingRequest['request'], message: PhotonCadDesktopRequest): Promise<T> {
    const bridge = this.connect()
    if (!bridge) return Promise.reject(new Error('Photon CAD desktop host is unavailable.'))
    try {
      this.reserveRequestId(requestId)
    } catch (reason) {
      return Promise.reject(reason)
    }
    return new Promise<T>((resolve, reject) => {
      const timeout = setTimeout(() => {
        const pending = this.pending.get(requestId)
        if (!pending) return
        if (isCancellablePending(pending)) this.cancelRequests([pending], this.bridge)
        this.finish(pending)
        pending.reject(new Error('Photon CAD host request timed out.'))
      }, this.requestTimeoutMs[kind])
      this.pending.set(requestId, { kind, requestId, request, resolve: resolve as (value: unknown) => void, reject, timeout })
      try {
        bridge.postMessage(message)
      } catch {
        const pending = this.pending.get(requestId)
        if (pending) this.finish(pending)
        reject(new Error('Photon CAD host could not receive the request.'))
      }
    })
  }

  private connect() {
    if (this.closed) return null
    const next = this.getBridge()
    if (next === this.bridge) return next
    if (this.bridge) {
      this.cancelRequests([...this.pending.values()].filter(isCancellablePending), this.bridge)
      this.bridge.removeEventListener('message', this.receive)
      this.rejectAll(new Error('Photon CAD desktop host session changed.'))
    }
    this.bridge = next
    this.bridge?.addEventListener('message', this.receive)
    return this.bridge
  }

  private matches(pending: PendingRequest, frame: Exclude<PhotonCadHostFrame, { type: 'error' }>) {
    if (!pending.request || frame.type === 'describe') return pending.kind === 'describe' && frame.type === 'describe'
    if (frame.type === 'execute' && pending.kind === 'execute') {
      const request = pending.request as PhotonCadOperationRequest
      const result = frame.value
      if (result.projectId !== request.projectId || result.baseRevision !== request.baseRevision) return false
      if (result.snapshot && result.snapshot.sessionId !== request.sessionId) return false
      if (request.mode === 'suggest') return result.resultingRevision === request.baseRevision
      if (result.status === 'accepted') return result.resultingRevision > request.baseRevision && result.snapshot !== undefined
      return result.resultingRevision === request.baseRevision
    }
    if (frame.type === 'verify' && pending.kind === 'verify') {
      const request = pending.request as PhotonCadVerificationRequest
      return frame.value.projectId === request.projectId && frame.value.revision === request.revision
    }
    if (frame.type === 'review' && pending.kind === 'review') {
      const request = pending.request as PhotonCadReleaseReviewRequest
      return frame.value.projectId === request.projectId && frame.value.revision === request.revision
    }
    if (frame.type === 'commit' && pending.kind === 'commit') {
      const request = pending.request as PhotonCadReleaseCommitRequest
      return frame.value.packageFingerprint === undefined
        || frame.value.packageFingerprint.toLowerCase() === request.packageFingerprint.toLowerCase()
    }
    if (frame.type === 'step-export' && pending.kind === 'step-export') {
      const request = pending.request as PhotonCadStepExportRequest
      return frame.value.projectId === request.projectId && frame.value.revision === request.revision
        && (frame.value.status !== 'committed' || frame.value.entityId === request.entityId)
    }
    return false
  }

  private replacePending(kind: PendingKind) {
    const stale = [...this.pending.values()].filter(isCancellablePending).filter((pending) => pending.kind === kind)
    this.cancelRequests(stale, this.bridge)
    for (const pending of stale) {
      this.finish(pending)
      pending.reject(new Error('A newer Photon CAD request replaced this request.'))
    }
  }

  private reserveRequestId(requestId: string) {
    if (!isPhotonCadIdentifier(requestId)) throw new Error('Photon CAD request identifier is invalid.')
    if (this.usedRequestIds.has(requestId)) throw new Error('Photon CAD request identifier was already used.')
    if (this.usedRequestIds.size >= MAXIMUM_REQUEST_HISTORY) throw new Error('Photon CAD request identity capacity was reached; create a new client session.')
    this.usedRequestIds.add(requestId)
  }

  private nextRequestId(kind: string) {
    this.sequence += 1
    const requestId = this.createId(kind, this.sequence)
    if (!isPhotonCadIdentifier(requestId)) throw new Error('Photon CAD request identifier generator returned an invalid value.')
    return requestId
  }

  private finish(pending: PendingRequest) {
    clearTimeout(pending.timeout)
    this.pending.delete(pending.requestId)
  }

  private fail(requestId: string, error: Error) {
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.finish(pending)
    pending.reject(error)
  }

  private rejectAll(error: Error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout)
      pending.reject(error)
    }
    this.pending.clear()
  }

  private cancelRequests(pendingRequests: Array<PendingRequest & { kind: 'execute' | 'verify' | 'review' | 'step-export' }>, bridge: PhotonCadWebViewBridge | null) {
    if (!bridge) return
    for (const pending of pendingRequests) {
      try {
        const requestId = this.nextRequestId('cancel')
        this.reserveRequestId(requestId)
        bridge.postMessage({
          type: 'photonCad.cancel',
          version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION,
          requestId,
          targetRequestId: pending.requestId,
          operation: pending.kind,
        } satisfies PhotonCadDesktopRequest)
      } catch {
        // Cancellation is best effort. Local settlement still fails closed.
      }
    }
  }
}

let fallbackNonce = 0
function defaultNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackNonce += 1
  return `${Date.now().toString(36)}-${fallbackNonce.toString(36)}`
}

function isCancellablePending(pending: PendingRequest): pending is PendingRequest & { kind: 'execute' | 'verify' | 'review' | 'step-export' } {
  return pending.kind === 'execute' || pending.kind === 'verify' || pending.kind === 'review' || pending.kind === 'step-export'
}

function normalizeTimeouts(value: DesktopPhotonCadClientOptions['requestTimeoutMs']): Record<PendingKind, number> {
  const normalize = (candidate: unknown, fallback: number) => typeof candidate === 'number' && Number.isFinite(candidate) && candidate > 0
    ? Math.min(MAXIMUM_REQUEST_TIMEOUT_MS, Math.trunc(candidate))
    : fallback
  if (typeof value === 'number') {
    const timeout = normalize(value, DEFAULT_REQUEST_TIMEOUT_MS.execute)
    return { describe: timeout, execute: timeout, verify: timeout, review: timeout, commit: timeout, 'step-export': timeout }
  }
  return {
    describe: normalize(value?.describe, DEFAULT_REQUEST_TIMEOUT_MS.describe),
    execute: normalize(value?.execute, DEFAULT_REQUEST_TIMEOUT_MS.execute),
    verify: normalize(value?.verify, DEFAULT_REQUEST_TIMEOUT_MS.verify),
    review: normalize(value?.review, DEFAULT_REQUEST_TIMEOUT_MS.review),
    commit: normalize(value?.commit, DEFAULT_REQUEST_TIMEOUT_MS.commit),
    'step-export': normalize(value?.['step-export'], DEFAULT_REQUEST_TIMEOUT_MS['step-export']),
  }
}

export const desktopPhotonCadClient = new DesktopPhotonCadClient()
