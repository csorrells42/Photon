import { describe, expect, it } from 'vitest'
import type {
  PhotonCadBomExportReviewRequest,
  PhotonCadBomExportReviewResult,
  PhotonCadCommercialApprovalRequest,
  PhotonCadCommercialApprovalResult,
  PhotonCadCommercialDraft,
  PhotonCadCommercialReviewRequest,
  PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'
import { PhotonCadCommercialOperationGate, type PhotonCadCommercialContext } from './PhotonCadCommercialOperationGate'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const digestC = `sha256:${'c'.repeat(64)}`
const context: PhotonCadCommercialContext = { sessionId: 'session:1', projectId: 'project:gearbox', projectRevision: 4, bomDigest: digestA }
const party = {
  organization: 'Photon Machine Works', contactName: 'Bill', addressLines: ['100 Machine Way'], city: 'Andalusia',
  region: 'AL', postalCode: '36420', countryCode: 'US', email: 'bill@example.com', phone: '555-0100',
}
const draft: PhotonCadCommercialDraft = {
  contractVersion: 1, documentKind: 'quote', documentNumber: 'Q-1001', projectId: context.projectId, projectRevision: context.projectRevision,
  bomDigest: digestA, issueDate: '2026-08-10', currency: 'USD', currencyScale: 2, seller: party,
  customer: { ...party, organization: 'Scarlett Industrial' },
  lines: [{ lineId: 'line:1', partNumber: 'AUGER FLIGHT / LH-01', description: 'Auger flight', quantity: 1, unit: 'each', unitPriceMinorUnits: 1_000, taxable: true }],
  markupBasisPoints: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxBasisPoints: 0, terms: '', notes: '',
}

function bomRequest(requestId = 'bom-review:1'): PhotonCadBomExportReviewRequest {
  return { contractVersion: 1, requestId, projectId: context.projectId, projectRevision: context.projectRevision, bomDigest: digestA, format: 'xlsx', destinationHandle: `cad-destination:${'d'.repeat(32)}` }
}

function bomResult(requestId = 'bom-review:1'): PhotonCadBomExportReviewResult {
  return { contractVersion: 1, requestId, projectId: context.projectId, projectRevision: context.projectRevision, status: 'ready', reason: 'ready', reviewHandle: `cad-bom-review:${'b'.repeat(32)}`, exportFingerprint: digestB, files: [{ role: 'bom', relativePath: 'bom/gearbox.xlsx' }] }
}

function reviewRequest(requestId = 'document-review:1'): PhotonCadCommercialReviewRequest {
  return { contractVersion: 1, requestId, draft, action: { kind: 'export-pdf', destinationHandle: `cad-destination:${'d'.repeat(32)}` } }
}

function reviewResult(requestId = 'document-review:1'): PhotonCadCommercialReviewResult {
  return {
    contractVersion: 1, requestId, status: 'ready', reason: 'ready', reviewHandle: `cad-commercial-review:${'r'.repeat(32)}`,
    documentFingerprint: digestB, actionFingerprint: digestC, expiresAtUtc: '2026-08-10T19:00:00Z',
    pages: [
      { pageNumber: 1, previewHandle: `cad-document-page:${'1'.repeat(32)}`, contentDigest: digestA },
      { pageNumber: 2, previewHandle: `cad-document-page:${'2'.repeat(32)}`, contentDigest: digestA },
    ],
    totals: { lineSubtotalMinorUnits: 1_000, markupMinorUnits: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxableSubtotalMinorUnits: 1_000, taxMinorUnits: 0, totalMinorUnits: 1_000 },
  }
}

function approvalRequest(requestId = 'document-approve:1'): PhotonCadCommercialApprovalRequest {
  return { contractVersion: 1, requestId, reviewHandle: `cad-commercial-review:${'r'.repeat(32)}`, documentFingerprint: digestB, actionFingerprint: digestC, viewedPageDigests: [digestA, digestA] }
}

function approvalResult(requestId = 'document-approve:1'): PhotonCadCommercialApprovalResult {
  return { contractVersion: 1, requestId, status: 'approved', reason: 'approved', approvalHandle: `cad-commercial-approval:${'a'.repeat(32)}`, documentFingerprint: digestB, actionFingerprint: digestC, expiresAtUtc: '2026-08-10T18:30:00Z' }
}

describe('PhotonCadCommercialOperationGate', () => {
  it('allows one exclusive commercial operation and marks context-changed results stale', () => {
    const gate = new PhotonCadCommercialOperationGate()
    gate.transitionContext(context)
    const token = gate.start('bom-review', 'bom-review:1')!
    expect(gate.start('document-review', 'document-review:1')).toBeNull()
    gate.transitionContext({ ...context, projectRevision: 5, bomDigest: digestB })
    expect(gate.settle(token)).toBe('stale')
  })

  it('binds BOM review only to the exact request, project revision, digest, format, and destination', () => {
    const gate = new PhotonCadCommercialOperationGate()
    gate.transitionContext(context)
    expect(gate.bindBomReview(bomRequest(), bomResult('foreign:1'))).toBeNull()
    const bound = gate.bindBomReview(bomRequest(), bomResult())
    expect(bound).toMatchObject({ format: 'xlsx', destinationHandle: `cad-destination:${'d'.repeat(32)}`, exportFingerprint: digestB })
    expect(gate.currentBomReview()).toBe(bound)
    expect(gate.consumeBomReview()).toBe(bound)
    expect(gate.currentBomReview()).toBeNull()
  })

  it('requires each page number to be rendered even when content digests are duplicates', () => {
    const gate = new PhotonCadCommercialOperationGate()
    gate.transitionContext(context)
    const now = Date.parse('2026-08-10T18:00:00Z')
    const result = reviewResult()
    expect(gate.bindDocumentReview(reviewRequest(), result, now)).not.toBeNull()
    expect(gate.recordRenderedPage(result.pages[0])).toBe(true)
    expect(gate.allPagesRendered(now)).toBe(false)
    expect(gate.recordRenderedPage({ ...result.pages[1], previewHandle: result.pages[0].previewHandle })).toBe(false)
    expect(gate.recordRenderedPage(result.pages[1])).toBe(true)
    expect(gate.allPagesRendered(now)).toBe(true)
    expect(gate.orderedRenderedPageDigests(now)).toEqual([digestA, digestA])
  })

  it('binds approval only after all pages and to exact request/result fingerprints', () => {
    const gate = new PhotonCadCommercialOperationGate()
    gate.transitionContext(context)
    const now = Date.parse('2026-08-10T18:00:00Z')
    const review = reviewResult()
    gate.bindDocumentReview(reviewRequest(), review, now)
    gate.recordRenderedPage(review.pages[0])
    expect(gate.bindDocumentApproval(approvalRequest(), approvalResult(), now)).toBeNull()
    gate.recordRenderedPage(review.pages[1])
    expect(gate.bindDocumentApproval(approvalRequest(), approvalResult('foreign:1'), now)).toBeNull()
    const approval = gate.bindDocumentApproval(approvalRequest(), approvalResult(), now)
    expect(approval).toMatchObject({ approvalHandle: `cad-commercial-approval:${'a'.repeat(32)}`, action: { kind: 'export-pdf' } })
    expect(gate.expectedCommitStatus()).toBe('exported')
  })

  it('rejects expired review/approval and invalidates all authority on edit or controller change', () => {
    const gate = new PhotonCadCommercialOperationGate()
    gate.transitionContext(context)
    const review = reviewResult()
    expect(gate.bindDocumentReview(reviewRequest(), review, Date.parse('2026-08-10T19:00:00Z'))).toBeNull()
    gate.bindBomReview(bomRequest(), bomResult())
    gate.bindDocumentReview(reviewRequest(), review, Date.parse('2026-08-10T18:00:00Z'))
    const invalidated = gate.markEdited()
    expect(invalidated.bom?.reviewHandle).toMatch(/^cad-bom-review:/u)
    expect(invalidated.documentReview?.reviewHandle).toMatch(/^cad-commercial-review:/u)
    expect(gate.currentBomReview()).toBeNull()
    expect(gate.currentDocumentReview(Date.parse('2026-08-10T18:00:00Z'))).toBeNull()
    gate.replaceController()
    expect(gate.start('document-review', 'document-review:2')).not.toBeNull()
  })
})
