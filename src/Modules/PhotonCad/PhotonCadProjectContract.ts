import {
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  isPhotonCadUtcTimestamp,
  type PhotonCadBomRow,
  type PhotonCadProjectSnapshot,
  type PhotonCadUnit,
} from './PhotonCadContract'

export const PHOTON_CAD_PROJECT_CONTRACT_VERSION = 1 as const

export const PHOTON_CAD_PROJECT_LIMITS = {
  openDocuments: 32,
  recentDocuments: 20,
  bomRows: 50_000,
  title: 256,
  pickerLabel: 512,
} as const

export type PhotonCadProjectPickerPurpose = 'new' | 'open' | 'save-as'
export type PhotonCadProjectOperation = 'picker' | 'create' | 'open' | 'reopen' | 'refresh' | 'save' | 'save-as' | 'close'

export type PhotonCadProjectPickerRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  purpose: PhotonCadProjectPickerPurpose
}

export type PhotonCadProjectPickerResult = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  status: 'selected' | 'cancelled' | 'rejected' | 'unavailable'
  reason: string
  workspaceHandle?: string
  label?: string
}

export type PhotonCadProjectDocument = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  workspaceHandle: string
  projectHandle: string
  displayName: string
  snapshot: PhotonCadProjectSnapshot
  contentDigest: string
  lastSavedContentDigest: string
  bomDigest: string
  bom: PhotonCadBomRow[]
  openedAtUtc: string
  lastSavedRevision: number
}

export type PhotonCadProjectCreateRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  workspaceHandle: string
  title: string
  units: PhotonCadUnit
}

export type PhotonCadProjectOpenRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  workspaceHandle: string
}

export type PhotonCadProjectReopenRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  reopenHandle: string
}

export type PhotonCadProjectRefreshRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  projectHandle: string
  sessionId: string
  projectId: string
  knownRevision: number
}

export type PhotonCadProjectLoadRequest =
  | PhotonCadProjectCreateRequest
  | PhotonCadProjectOpenRequest
  | PhotonCadProjectReopenRequest
  | PhotonCadProjectRefreshRequest

export type PhotonCadProjectLoadResult = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  status: 'opened' | 'cancelled' | 'rejected' | 'unavailable'
  reason: string
  sourceHandle?: string
  document?: PhotonCadProjectDocument
}

export type PhotonCadProjectSaveRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  projectHandle: string
  sessionId: string
  projectId: string
  baseRevision: number
  contentDigest: string
}

export type PhotonCadProjectSaveAsRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  sourceProjectHandle: string
  destinationWorkspaceHandle: string
  sessionId: string
  projectId: string
  baseRevision: number
  contentDigest: string
}

export type PhotonCadProjectSaveReceipt = {
  receiptHandle: string
  sourceProjectHandle: string
  projectHandle: string
  sessionId: string
  projectId: string
  baseRevision: number
  savedRevision: number
  contentDigest: string
  savedAtUtc: string
  atomic: true
}

export type PhotonCadProjectSaveResult = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  status: 'saved' | 'rejected' | 'unavailable'
  reason: string
  receipt?: PhotonCadProjectSaveReceipt
  document?: PhotonCadProjectDocument
}

export type PhotonCadProjectCloseRequest = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  projectHandle: string
  sessionId: string
  projectId: string
  revision: number
  lastSavedRevision: number
  contentDigest: string
  lastSavedContentDigest: string
  discardUnsavedChanges: boolean
}

export type PhotonCadProjectReopenMetadata = {
  reopenHandle: string
  displayName: string
  projectId: string
  lastSavedRevision: number
  contentDigest: string
  closedAtUtc: string
}

export type PhotonCadProjectCloseResult = {
  contractVersion: typeof PHOTON_CAD_PROJECT_CONTRACT_VERSION
  requestId: string
  status: 'closed' | 'rejected' | 'unavailable'
  reason: string
  projectHandle?: string
  reopen?: PhotonCadProjectReopenMetadata
}

export type PhotonCadProjectController = {
  chooseWorkspace(request: PhotonCadProjectPickerRequest): Promise<PhotonCadProjectPickerResult>
  createProject(request: PhotonCadProjectCreateRequest): Promise<PhotonCadProjectLoadResult>
  openProject(request: PhotonCadProjectOpenRequest): Promise<PhotonCadProjectLoadResult>
  reopenProject(request: PhotonCadProjectReopenRequest): Promise<PhotonCadProjectLoadResult>
  refreshProject(request: PhotonCadProjectRefreshRequest): Promise<PhotonCadProjectLoadResult>
  saveProject(request: PhotonCadProjectSaveRequest): Promise<PhotonCadProjectSaveResult>
  saveProjectAs(request: PhotonCadProjectSaveAsRequest): Promise<PhotonCadProjectSaveResult>
  closeProject(request: PhotonCadProjectCloseRequest): Promise<PhotonCadProjectCloseResult>
}

const WORKSPACE_HANDLE = /^cad-workspace:[A-Za-z0-9_-]{32,160}$/u
const PROJECT_HANDLE = /^cad-project:[A-Za-z0-9_-]{32,160}$/u
const REOPEN_HANDLE = /^cad-reopen:[A-Za-z0-9_-]{32,160}$/u
const SAVE_RECEIPT_HANDLE = /^cad-save-receipt:[A-Za-z0-9_-]{32,160}$/u

export function isPhotonCadWorkspaceHandle(value: unknown): value is string {
  return typeof value === 'string' && WORKSPACE_HANDLE.test(value)
}

export function isPhotonCadProjectHandle(value: unknown): value is string {
  return typeof value === 'string' && PROJECT_HANDLE.test(value)
}

export function isPhotonCadReopenHandle(value: unknown): value is string {
  return typeof value === 'string' && REOPEN_HANDLE.test(value)
}

export function isPhotonCadSaveReceiptHandle(value: unknown): value is string {
  return typeof value === 'string' && SAVE_RECEIPT_HANDLE.test(value)
}

export function validatePhotonCadProjectPickerRequest(request: PhotonCadProjectPickerRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && ['new', 'open', 'save-as'].includes(request.purpose)
}

export function validatePhotonCadProjectCreateRequest(request: PhotonCadProjectCreateRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadWorkspaceHandle(request.workspaceHandle)
    && isDisplayLabel(request.title, PHOTON_CAD_PROJECT_LIMITS.title)
    && (request.units === 'millimeter' || request.units === 'inch')
}

export function validatePhotonCadProjectOpenRequest(request: PhotonCadProjectOpenRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadWorkspaceHandle(request.workspaceHandle)
}

export function validatePhotonCadProjectReopenRequest(request: PhotonCadProjectReopenRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadReopenHandle(request.reopenHandle)
}

export function validatePhotonCadProjectRefreshRequest(request: PhotonCadProjectRefreshRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadProjectHandle(request.projectHandle)
    && isPhotonCadIdentifier(request.sessionId)
    && isPhotonCadIdentifier(request.projectId)
    && validRevision(request.knownRevision)
}

export function validatePhotonCadProjectSaveRequest(request: PhotonCadProjectSaveRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadProjectHandle(request.projectHandle)
    && isPhotonCadIdentifier(request.sessionId)
    && isPhotonCadIdentifier(request.projectId)
    && validRevision(request.baseRevision)
    && isPhotonCadDigest(request.contentDigest)
}

export function validatePhotonCadProjectSaveAsRequest(request: PhotonCadProjectSaveAsRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadProjectHandle(request.sourceProjectHandle)
    && isPhotonCadWorkspaceHandle(request.destinationWorkspaceHandle)
    && isPhotonCadIdentifier(request.sessionId)
    && isPhotonCadIdentifier(request.projectId)
    && validRevision(request.baseRevision)
    && isPhotonCadDigest(request.contentDigest)
}

export function validatePhotonCadProjectCloseRequest(request: PhotonCadProjectCloseRequest) {
  return request.contractVersion === PHOTON_CAD_PROJECT_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadProjectHandle(request.projectHandle)
    && isPhotonCadIdentifier(request.sessionId)
    && isPhotonCadIdentifier(request.projectId)
    && validRevision(request.revision)
    && validRevision(request.lastSavedRevision)
    && request.lastSavedRevision <= request.revision
    && isPhotonCadDigest(request.contentDigest)
    && isPhotonCadDigest(request.lastSavedContentDigest)
    && typeof request.discardUnsavedChanges === 'boolean'
}

export function validatePhotonCadProjectDocumentMetadata(document: PhotonCadProjectDocument) {
  if (document.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION
    || !isPhotonCadWorkspaceHandle(document.workspaceHandle)
    || !isPhotonCadProjectHandle(document.projectHandle)
    || !isDisplayLabel(document.displayName, PHOTON_CAD_PROJECT_LIMITS.title)
    || !isPhotonCadDigest(document.contentDigest)
    || !isPhotonCadDigest(document.lastSavedContentDigest)
    || !isPhotonCadDigest(document.bomDigest)
    || !isPhotonCadUtcTimestamp(document.openedAtUtc)
    || !validRevision(document.lastSavedRevision)
    || !document.snapshot || document.lastSavedRevision > document.snapshot.revision
    || (!document.snapshot.dirty && document.lastSavedRevision !== document.snapshot.revision)
    || (!document.snapshot.dirty && document.lastSavedContentDigest.toLowerCase() !== document.contentDigest.toLowerCase())
    || !Array.isArray(document.bom) || document.bom.length > PHOTON_CAD_PROJECT_LIMITS.bomRows) return false
  const rowKeys = new Set<string>()
  return document.bom.every((row) => {
    if (!isPhotonCadSafeText(row.partNumber, PHOTON_CAD_PROJECT_LIMITS.title, true)
      || !isPhotonCadSafeText(row.description, 2_048)
      || !Number.isFinite(row.quantity) || row.quantity <= 0 || row.quantity > 1_000_000_000
      || (row.unit !== 'each' && row.unit !== 'length')
      || !isPhotonCadIdentifier(row.sourceEntityId)) return false
    const key = `${row.sourceEntityId}\u0000${row.partNumber}`
    if (rowKeys.has(key)) return false
    rowKeys.add(key)
    return true
  })
}

export function validatePhotonCadSaveReceiptBinding(
  request: PhotonCadProjectSaveRequest | PhotonCadProjectSaveAsRequest,
  result: PhotonCadProjectSaveResult,
) {
  const requestValid = 'sourceProjectHandle' in request
    ? validatePhotonCadProjectSaveAsRequest(request)
    : validatePhotonCadProjectSaveRequest(request)
  if (!requestValid || result.contractVersion !== PHOTON_CAD_PROJECT_CONTRACT_VERSION || result.requestId !== request.requestId
    || result.status !== 'saved' || !isPhotonCadIdentifier(result.reason) || !result.receipt || !result.document
    || !validatePhotonCadProjectDocumentMetadata(result.document)) return false
  const receipt = result.receipt
  const sourceProjectHandle = 'sourceProjectHandle' in request ? request.sourceProjectHandle : request.projectHandle
  const destinationWorkspaceHandle = 'destinationWorkspaceHandle' in request ? request.destinationWorkspaceHandle : null
  return isPhotonCadSaveReceiptHandle(receipt.receiptHandle)
    && isPhotonCadProjectHandle(receipt.sourceProjectHandle)
    && receipt.sourceProjectHandle === sourceProjectHandle
    && isPhotonCadProjectHandle(receipt.projectHandle)
    && isPhotonCadIdentifier(receipt.sessionId)
    && isPhotonCadIdentifier(receipt.projectId)
    && validRevision(receipt.baseRevision)
    && validRevision(receipt.savedRevision)
    && isPhotonCadDigest(receipt.contentDigest)
    && receipt.sessionId === request.sessionId
    && receipt.projectId === request.projectId
    && receipt.baseRevision === request.baseRevision
    && receipt.savedRevision === request.baseRevision
    && receipt.contentDigest.toLowerCase() === request.contentDigest.toLowerCase()
    && isPhotonCadUtcTimestamp(receipt.savedAtUtc)
    && receipt.atomic === true
    && result.document.projectHandle === receipt.projectHandle
    && result.document.snapshot.sessionId === request.sessionId
    && result.document.snapshot.projectId === request.projectId
    && result.document.snapshot.revision === receipt.savedRevision
    && result.document.contentDigest.toLowerCase() === receipt.contentDigest.toLowerCase()
    && result.document.lastSavedContentDigest.toLowerCase() === receipt.contentDigest.toLowerCase()
    && result.document.lastSavedRevision === receipt.savedRevision
    && result.document.snapshot.dirty === false
    && (destinationWorkspaceHandle === null || result.document.workspaceHandle === destinationWorkspaceHandle)
}

export function validatePhotonCadReopenMetadata(value: PhotonCadProjectReopenMetadata) {
  return isPhotonCadReopenHandle(value.reopenHandle)
    && isDisplayLabel(value.displayName, PHOTON_CAD_PROJECT_LIMITS.title)
    && isPhotonCadIdentifier(value.projectId)
    && validRevision(value.lastSavedRevision)
    && isPhotonCadDigest(value.contentDigest)
    && isPhotonCadUtcTimestamp(value.closedAtUtc)
}

function validRevision(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}

function isDisplayLabel(value: unknown, maximum: number): value is string {
  return isPhotonCadSafeText(value, maximum, true)
    && !/[\\/]/u.test(value)
    && !/^[A-Za-z]:/u.test(value)
}

export const PHOTON_CAD_PROJECT_INVARIANT =
  'A save is successful only when the host returns an atomic receipt bound to the exact session, project, source handle, base revision, saved revision, and content digest.'
