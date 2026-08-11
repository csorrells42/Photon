import {
  PHOTON_CAD_CONTRACT_VERSION,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  isPhotonCadUtcTimestamp,
  type PhotonCadPackageFileRole,
} from './PhotonCadContract'

export const PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION = 1 as const

export const PHOTON_CAD_COMMERCIAL_LIMITS = {
  lines: 20_000,
  addressLines: 8,
  pagePreviews: 2_000,
  text: 4_096,
  shortText: 256,
  maximumMinorUnits: 9_007_199_254_740_991,
  maximumQuantity: 1_000_000_000,
  maximumBasisPoints: 100_000,
  maximumCopies: 10,
} as const

export type PhotonCadBomExportFormat = 'xlsx' | 'csv' | 'pdf'
export type PhotonCadCommercialDocumentKind = 'quote' | 'invoice'
export type PhotonCadCommercialUnit = 'each' | 'length' | 'hour'

export type PhotonCadCommercialParty = {
  organization: string
  contactName: string
  addressLines: string[]
  city: string
  region: string
  postalCode: string
  countryCode: string
  email: string
  phone: string
}

export type PhotonCadCommercialLine = {
  lineId: string
  sourceBomRowId?: string
  partNumber: string
  description: string
  quantity: number
  unit: PhotonCadCommercialUnit
  unitPriceMinorUnits: number
  taxable: boolean
}

export type PhotonCadCommercialDraft = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  documentKind: PhotonCadCommercialDocumentKind
  documentNumber: string
  projectId: string
  projectRevision: number
  bomDigest: string
  issueDate: string
  dueDate?: string
  currency: string
  currencyScale: number
  seller: PhotonCadCommercialParty
  customer: PhotonCadCommercialParty
  lines: PhotonCadCommercialLine[]
  markupBasisPoints: number
  discountMinorUnits: number
  freightMinorUnits: number
  taxBasisPoints: number
  terms: string
  notes: string
}

export type PhotonCadCommercialTotals = {
  lineSubtotalMinorUnits: number
  markupMinorUnits: number
  discountMinorUnits: number
  freightMinorUnits: number
  taxableSubtotalMinorUnits: number
  taxMinorUnits: number
  totalMinorUnits: number
}

export type PhotonCadCommercialOutputAction =
  | { kind: 'export-pdf'; destinationHandle: string }
  | { kind: 'print'; printerHandle: string; copies: number }

export type PhotonCadBomExportReviewRequest = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  projectId: string
  projectRevision: number
  bomDigest: string
  format: PhotonCadBomExportFormat
  destinationHandle: string
}

export type PhotonCadBomExportReviewResult = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  projectId: string
  projectRevision: number
  status: 'ready' | 'rejected' | 'unavailable'
  reason: string
  reviewHandle?: string
  exportFingerprint?: string
  files: Array<{ role: Extract<PhotonCadPackageFileRole, 'bom' | 'validation-report'>; relativePath: string }>
}

export type PhotonCadBomExportCommitRequest = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  reviewHandle: string
  exportFingerprint: string
}

export type PhotonCadBomExportCommitResult = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  status: 'exported' | 'rejected' | 'unavailable'
  reason: string
  exportFingerprint?: string
}

export type PhotonCadCommercialReviewRequest = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  draft: PhotonCadCommercialDraft
  action: PhotonCadCommercialOutputAction
}

export type PhotonCadCommercialPreviewPage = {
  pageNumber: number
  previewHandle: string
  contentDigest: string
}

export type PhotonCadCommercialReviewResult = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  status: 'ready' | 'rejected' | 'unavailable'
  reason: string
  reviewHandle?: string
  documentFingerprint?: string
  actionFingerprint?: string
  expiresAtUtc?: string
  pages: PhotonCadCommercialPreviewPage[]
  totals?: PhotonCadCommercialTotals
}

export type PhotonCadCommercialApprovalRequest = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  reviewHandle: string
  documentFingerprint: string
  actionFingerprint: string
  viewedPageDigests: string[]
}

export type PhotonCadCommercialApprovalResult = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  status: 'approved' | 'rejected' | 'unavailable'
  reason: string
  approvalHandle?: string
  documentFingerprint?: string
  actionFingerprint?: string
  expiresAtUtc?: string
}

export type PhotonCadCommercialCommitRequest = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  approvalHandle: string
  documentFingerprint: string
  actionFingerprint: string
}

export type PhotonCadCommercialCommitResult = {
  contractVersion: typeof PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
  requestId: string
  status: 'exported' | 'printed' | 'rejected' | 'unavailable'
  reason: string
  documentFingerprint?: string
  actionFingerprint?: string
}

export type PhotonCadCommercialController = {
  reviewBomExport(request: PhotonCadBomExportReviewRequest): Promise<PhotonCadBomExportReviewResult>
  commitBomExport(request: PhotonCadBomExportCommitRequest): Promise<PhotonCadBomExportCommitResult>
  discardBomExport(reviewHandle: string): Promise<void> | void
  reviewCommercialDocument(request: PhotonCadCommercialReviewRequest): Promise<PhotonCadCommercialReviewResult>
  approveCommercialDocument(request: PhotonCadCommercialApprovalRequest): Promise<PhotonCadCommercialApprovalResult>
  commitCommercialDocument(request: PhotonCadCommercialCommitRequest): Promise<PhotonCadCommercialCommitResult>
  discardCommercialDocument(reviewOrApprovalHandle: string): Promise<void> | void
}

const SAFE_TEXT = /^[^\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]*$/u
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/u
const CURRENCY = /^[A-Z]{3}$/u
const COUNTRY = /^[A-Z]{2}$/u
const EMAIL = /^[^\s@]{1,128}@[^\s@]{1,128}\.[^\s@]{2,63}$/u
const DESTINATION_HANDLE = /^cad-destination:[A-Za-z0-9_-]{32,160}$/u
const PRINTER_HANDLE = /^cad-printer:[A-Za-z0-9_-]{32,160}$/u
const BOM_REVIEW_HANDLE = /^cad-bom-review:[A-Za-z0-9_-]{32,160}$/u
const COMMERCIAL_REVIEW_HANDLE = /^cad-commercial-review:[A-Za-z0-9_-]{32,160}$/u
const COMMERCIAL_APPROVAL_HANDLE = /^cad-commercial-approval:[A-Za-z0-9_-]{32,160}$/u
const PAGE_HANDLE = /^cad-document-page:[A-Za-z0-9_-]{32,160}$/u

function validText(value: unknown, maximum: number, required = false): value is string {
  return isPhotonCadSafeText(value, maximum, required) && SAFE_TEXT.test(value)
}

function validIsoDate(value: unknown): value is string {
  if (typeof value !== 'string') return false
  const match = ISO_DATE.exec(value)
  if (!match) return false
  const [, yearText, monthText, dayText] = match
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  if (month < 1 || month > 12 || day < 1) return false
  const date = new Date(0)
  date.setUTCFullYear(year, month - 1, day)
  date.setUTCHours(0, 0, 0, 0)
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
}

function safeInteger(value: unknown, minimum: number, maximum: number) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum
}

function validateParty(party: PhotonCadCommercialParty, field: string) {
  const issues: string[] = []
  if (!validText(party.organization, PHOTON_CAD_COMMERCIAL_LIMITS.shortText, true)) issues.push(`invalid-${field}-organization`)
  if (!validText(party.contactName, PHOTON_CAD_COMMERCIAL_LIMITS.shortText)) issues.push(`invalid-${field}-contact`)
  if (!Array.isArray(party.addressLines) || party.addressLines.length > PHOTON_CAD_COMMERCIAL_LIMITS.addressLines
    || party.addressLines.some((line) => !validText(line, PHOTON_CAD_COMMERCIAL_LIMITS.shortText, true))) issues.push(`invalid-${field}-address`)
  if (!validText(party.city, PHOTON_CAD_COMMERCIAL_LIMITS.shortText)) issues.push(`invalid-${field}-city`)
  if (!validText(party.region, PHOTON_CAD_COMMERCIAL_LIMITS.shortText)) issues.push(`invalid-${field}-region`)
  if (!validText(party.postalCode, 32)) issues.push(`invalid-${field}-postal-code`)
  if (!COUNTRY.test(party.countryCode)) issues.push(`invalid-${field}-country`)
  if (party.email && (!validText(party.email, 320) || !EMAIL.test(party.email))) issues.push(`invalid-${field}-email`)
  if (!validText(party.phone, 64)) issues.push(`invalid-${field}-phone`)
  return issues
}

export function validatePhotonCadCommercialDraft(draft: PhotonCadCommercialDraft): string[] {
  const issues: string[] = []
  if (draft.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION) issues.push('unsupported-contract')
  if (draft.documentKind !== 'quote' && draft.documentKind !== 'invoice') issues.push('invalid-document-kind')
  if (!validText(draft.documentNumber, 128, true)) issues.push('invalid-document-number')
  if (!isPhotonCadIdentifier(draft.projectId) || !safeInteger(draft.projectRevision, 0, Number.MAX_SAFE_INTEGER)) issues.push('invalid-project')
  if (!isPhotonCadDigest(draft.bomDigest)) issues.push('invalid-bom-digest')
  if (!validIsoDate(draft.issueDate) || (draft.dueDate !== undefined && !validIsoDate(draft.dueDate))) issues.push('invalid-date')
  if (draft.dueDate && draft.dueDate < draft.issueDate) issues.push('due-date-before-issue-date')
  if (!CURRENCY.test(draft.currency) || !safeInteger(draft.currencyScale, 0, 4)) issues.push('invalid-currency')
  issues.push(...validateParty(draft.seller, 'seller'), ...validateParty(draft.customer, 'customer'))
  if (!Array.isArray(draft.lines) || draft.lines.length === 0 || draft.lines.length > PHOTON_CAD_COMMERCIAL_LIMITS.lines) {
    issues.push('invalid-lines')
  } else {
    for (const line of draft.lines) {
      if (!isPhotonCadIdentifier(line.lineId)
        || (line.sourceBomRowId !== undefined && !isPhotonCadIdentifier(line.sourceBomRowId))
        || !validText(line.partNumber, PHOTON_CAD_COMMERCIAL_LIMITS.shortText, true)
        || !validText(line.description, PHOTON_CAD_COMMERCIAL_LIMITS.text, true)
        || typeof line.quantity !== 'number' || !Number.isFinite(line.quantity) || line.quantity <= 0 || line.quantity > PHOTON_CAD_COMMERCIAL_LIMITS.maximumQuantity
        || !['each', 'length', 'hour'].includes(line.unit)
        || !safeInteger(line.unitPriceMinorUnits, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumMinorUnits)
        || typeof line.taxable !== 'boolean') issues.push('invalid-line')
    }
    const ids = draft.lines.map((line) => line.lineId.toLowerCase())
    if (new Set(ids).size !== ids.length) issues.push('duplicate-line')
  }
  if (!safeInteger(draft.markupBasisPoints, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumBasisPoints)
    || !safeInteger(draft.taxBasisPoints, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumBasisPoints)) issues.push('invalid-rate')
  if (!safeInteger(draft.discountMinorUnits, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumMinorUnits)
    || !safeInteger(draft.freightMinorUnits, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumMinorUnits)) issues.push('invalid-adjustment')
  if (!validText(draft.terms, PHOTON_CAD_COMMERCIAL_LIMITS.text)
    || !validText(draft.notes, PHOTON_CAD_COMMERCIAL_LIMITS.text)) issues.push('invalid-commercial-text')
  return [...new Set(issues)]
}

export function canonicalPhotonCadCommercialDraft(draft: PhotonCadCommercialDraft) {
  return JSON.stringify({
    contractVersion: draft.contractVersion,
    documentKind: draft.documentKind,
    documentNumber: draft.documentNumber.trim(),
    projectId: draft.projectId,
    projectRevision: draft.projectRevision,
    bomDigest: draft.bomDigest.toLowerCase(),
    issueDate: draft.issueDate,
    dueDate: draft.dueDate ?? null,
    currency: draft.currency,
    currencyScale: draft.currencyScale,
    seller: canonicalParty(draft.seller),
    customer: canonicalParty(draft.customer),
    lines: draft.lines.map((line) => ({
      lineId: line.lineId,
      sourceBomRowId: line.sourceBomRowId ?? null,
      partNumber: line.partNumber,
      description: line.description.trim(),
      quantity: line.quantity,
      unit: line.unit,
      unitPriceMinorUnits: line.unitPriceMinorUnits,
      taxable: line.taxable,
    })),
    markupBasisPoints: draft.markupBasisPoints,
    discountMinorUnits: draft.discountMinorUnits,
    freightMinorUnits: draft.freightMinorUnits,
    taxBasisPoints: draft.taxBasisPoints,
    terms: draft.terms.trim(),
    notes: draft.notes.trim(),
  })
}

function canonicalParty(party: PhotonCadCommercialParty) {
  return {
    organization: party.organization.trim(),
    contactName: party.contactName.trim(),
    addressLines: party.addressLines.map((line) => line.trim()),
    city: party.city.trim(),
    region: party.region.trim(),
    postalCode: party.postalCode.trim(),
    countryCode: party.countryCode,
    email: party.email.trim().toLowerCase(),
    phone: party.phone.trim(),
  }
}

export function validatePhotonCadBomExportReviewRequest(request: PhotonCadBomExportReviewRequest) {
  return request.contractVersion === PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && isPhotonCadIdentifier(request.projectId)
    && safeInteger(request.projectRevision, 0, Number.MAX_SAFE_INTEGER)
    && isPhotonCadDigest(request.bomDigest)
    && ['xlsx', 'csv', 'pdf'].includes(request.format)
    && DESTINATION_HANDLE.test(request.destinationHandle)
}

export function validatePhotonCadCommercialReviewRequest(request: PhotonCadCommercialReviewRequest) {
  return request.contractVersion === PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && validatePhotonCadCommercialDraft(request.draft).length === 0
    && validateCommercialAction(request.action)
}

function validateCommercialAction(action: PhotonCadCommercialOutputAction) {
  if (action.kind === 'export-pdf') return DESTINATION_HANDLE.test(action.destinationHandle)
  return action.kind === 'print'
    && PRINTER_HANDLE.test(action.printerHandle)
    && safeInteger(action.copies, 1, PHOTON_CAD_COMMERCIAL_LIMITS.maximumCopies)
}

export function validatePhotonCadCommercialReviewResult(result: PhotonCadCommercialReviewResult) {
  if (result.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION || !isPhotonCadIdentifier(result.requestId)
    || !['ready', 'rejected', 'unavailable'].includes(result.status) || !isPhotonCadIdentifier(result.reason)
    || !Array.isArray(result.pages) || result.pages.length > PHOTON_CAD_COMMERCIAL_LIMITS.pagePreviews) return false
  if (result.status !== 'ready') return result.pages.length === 0
    && result.reviewHandle === undefined && result.documentFingerprint === undefined && result.actionFingerprint === undefined
    && result.expiresAtUtc === undefined && result.totals === undefined
  if (!COMMERCIAL_REVIEW_HANDLE.test(result.reviewHandle ?? '')
    || !isPhotonCadDigest(result.documentFingerprint)
    || !isPhotonCadDigest(result.actionFingerprint)
    || !isPhotonCadUtcTimestamp(result.expiresAtUtc) || result.pages.length === 0 || !result.totals) return false
  const numbers = result.pages.map((page) => page.pageNumber)
  return result.pages.every((page) => safeInteger(page.pageNumber, 1, PHOTON_CAD_COMMERCIAL_LIMITS.pagePreviews)
      && PAGE_HANDLE.test(page.previewHandle) && isPhotonCadDigest(page.contentDigest))
    && new Set(numbers).size === numbers.length
    && numbers.every((number, index) => number === index + 1)
    && validateTotals(result.totals)
}

function validateTotals(totals: PhotonCadCommercialTotals) {
  return Object.values(totals).every((value) => safeInteger(value, 0, PHOTON_CAD_COMMERCIAL_LIMITS.maximumMinorUnits))
}

export function validatePhotonCadCommercialApprovalRequest(
  request: PhotonCadCommercialApprovalRequest,
  reviewedPages: readonly PhotonCadCommercialPreviewPage[],
) {
  return request.contractVersion === PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && COMMERCIAL_REVIEW_HANDLE.test(request.reviewHandle)
    && isPhotonCadDigest(request.documentFingerprint)
    && isPhotonCadDigest(request.actionFingerprint)
    && reviewedPages.length > 0
    && reviewedPages.length <= PHOTON_CAD_COMMERCIAL_LIMITS.pagePreviews
    && reviewedPages.every((page, index) => page.pageNumber === index + 1 && PAGE_HANDLE.test(page.previewHandle) && isPhotonCadDigest(page.contentDigest))
    && request.viewedPageDigests.length === reviewedPages.length
    && request.viewedPageDigests.every((digest, index) =>
      isPhotonCadDigest(digest) && digest.toLowerCase() === reviewedPages[index]?.contentDigest.toLowerCase())
}

export function validatePhotonCadCommercialCommitRequest(request: PhotonCadCommercialCommitRequest) {
  return request.contractVersion === PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
    && isPhotonCadIdentifier(request.requestId)
    && COMMERCIAL_APPROVAL_HANDLE.test(request.approvalHandle)
    && isPhotonCadDigest(request.documentFingerprint)
    && isPhotonCadDigest(request.actionFingerprint)
}

export function isPhotonCadBomReviewHandle(value: unknown): value is string {
  return typeof value === 'string' && BOM_REVIEW_HANDLE.test(value)
}

export function isPhotonCadCommercialReviewHandle(value: unknown): value is string {
  return typeof value === 'string' && COMMERCIAL_REVIEW_HANDLE.test(value)
}

export function isPhotonCadCommercialApprovalHandle(value: unknown): value is string {
  return typeof value === 'string' && COMMERCIAL_APPROVAL_HANDLE.test(value)
}

export const PHOTON_CAD_COMMERCIAL_INVARIANT =
  'A customer quote or invoice may be exported or printed only after every rendered page is shown and a human explicitly approves that exact document and output action.'

export const PHOTON_CAD_COMMERCIAL_FORBIDDEN_ACTIONS = [
  'email',
  'send',
  'post-to-accounting',
  'charge',
  'pay',
  'mark-paid',
] as const

export const PHOTON_CAD_CORE_CONTRACT_VERSION = PHOTON_CAD_CONTRACT_VERSION
