import { describe, expect, it } from 'vitest'
import {
  PHOTON_CAD_COMMERCIAL_FORBIDDEN_ACTIONS,
  PHOTON_CAD_COMMERCIAL_INVARIANT,
  canonicalPhotonCadCommercialDraft,
  isPhotonCadCommercialApprovalHandle,
  validatePhotonCadBomExportReviewRequest,
  validatePhotonCadCommercialApprovalRequest,
  validatePhotonCadCommercialCommitRequest,
  validatePhotonCadCommercialDraft,
  validatePhotonCadCommercialReviewRequest,
  validatePhotonCadCommercialReviewResult,
  type PhotonCadCommercialDraft,
  type PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'

const digest = `sha256:${'a'.repeat(64)}`
const destination = `cad-destination:${'d'.repeat(32)}`
const party = {
  organization: 'Scarlett Industrial',
  contactName: 'Bill',
  addressLines: ['100 Machine Way'],
  city: 'Andalusia',
  region: 'AL',
  postalCode: '36420',
  countryCode: 'US',
  email: 'bill@example.com',
  phone: '555-0100',
}
const draft: PhotonCadCommercialDraft = {
  contractVersion: 1,
  documentKind: 'invoice',
  documentNumber: 'INV-1001',
  projectId: 'gearbox:1',
  projectRevision: 4,
  bomDigest: digest,
  issueDate: '2026-08-10',
  dueDate: '2026-09-09',
  currency: 'USD',
  currencyScale: 2,
  seller: { ...party, organization: 'Photon Machine Works' },
  customer: party,
  lines: [{
    lineId: 'line:shaft',
    sourceBomRowId: 'bom:shaft',
    partNumber: 'SHAFT-001',
    description: 'Input shaft',
    quantity: 2,
    unit: 'each',
    unitPriceMinorUnits: 12_500,
    taxable: true,
  }],
  markupBasisPoints: 1_500,
  discountMinorUnits: 0,
  freightMinorUnits: 2_500,
  taxBasisPoints: 925,
  terms: 'Net 30',
  notes: 'Thank you.',
}

describe('PhotonCadCommercialContract', () => {
  it('validates a bounded invoice draft tied to the exact BOM revision', () => {
    expect(validatePhotonCadCommercialDraft(draft)).toEqual([])
    expect(validatePhotonCadCommercialReviewRequest({
      contractVersion: 1,
      requestId: 'commercial-review:1',
      draft,
      action: { kind: 'print', printerHandle: `cad-printer:${'p'.repeat(32)}`, copies: 1 },
    })).toBe(true)
  })

  it('rejects invalid money, dates, duplicate lines, and control text', () => {
    expect(validatePhotonCadCommercialDraft({
      ...draft,
      dueDate: '2026-01-01',
      freightMinorUnits: -1,
      notes: 'hidden\u202e.pdf',
      lines: [...draft.lines, draft.lines[0]],
    })).toEqual(expect.arrayContaining([
      'due-date-before-issue-date',
      'invalid-adjustment',
      'invalid-commercial-text',
      'duplicate-line',
    ]))
  })

  it('rejects impossible calendar dates while allowing ordinary industrial part numbers', () => {
    expect(validatePhotonCadCommercialDraft({
      ...draft,
      issueDate: '2026-02-31',
      lines: [{ ...draft.lines[0], partNumber: 'AUGER FLIGHT / LH-01' }],
    })).toEqual(['invalid-date'])
  })

  it('canonicalizes equivalent presentation whitespace deterministically', () => {
    expect(canonicalPhotonCadCommercialDraft({ ...draft, documentNumber: ' INV-1001 ', notes: ' Thank you. ' }))
      .toBe(canonicalPhotonCadCommercialDraft(draft))
  })

  it.each(['xlsx', 'csv', 'pdf'] as const)('supports reviewed BOM %s export', (format) => {
    expect(validatePhotonCadBomExportReviewRequest({
      contractVersion: 1,
      requestId: `bom-review:${format}`,
      projectId: draft.projectId,
      projectRevision: draft.projectRevision,
      bomDigest: digest,
      format,
      destinationHandle: destination,
    })).toBe(true)
  })

  it('requires a complete ordered rendered-page set before approval', () => {
    const pages = [
      { pageNumber: 1, previewHandle: `cad-document-page:${'a'.repeat(32)}`, contentDigest: digest },
      { pageNumber: 2, previewHandle: `cad-document-page:${'b'.repeat(32)}`, contentDigest: `sha256:${'b'.repeat(64)}` },
    ]
    const result: PhotonCadCommercialReviewResult = {
      contractVersion: 1,
      requestId: 'commercial-review:1',
      status: 'ready',
      reason: 'ready',
      reviewHandle: `cad-commercial-review:${'r'.repeat(32)}`,
      documentFingerprint: digest,
      actionFingerprint: `sha256:${'c'.repeat(64)}`,
      expiresAtUtc: '2026-08-10T19:00:00Z',
      pages,
      totals: {
        lineSubtotalMinorUnits: 25_000,
        markupMinorUnits: 3_750,
        discountMinorUnits: 0,
        freightMinorUnits: 2_500,
        taxableSubtotalMinorUnits: 28_750,
        taxMinorUnits: 2_659,
        totalMinorUnits: 33_909,
      },
    }
    expect(validatePhotonCadCommercialReviewResult(result)).toBe(true)
    expect(validatePhotonCadCommercialApprovalRequest({
      contractVersion: 1,
      requestId: 'commercial-approve:1',
      reviewHandle: result.reviewHandle!,
      documentFingerprint: result.documentFingerprint!,
      actionFingerprint: result.actionFingerprint!,
      viewedPageDigests: pages.map((page) => page.contentDigest),
    }, pages)).toBe(true)
    expect(validatePhotonCadCommercialApprovalRequest({
      contractVersion: 1,
      requestId: 'commercial-approve:1',
      reviewHandle: result.reviewHandle!,
      documentFingerprint: result.documentFingerprint!,
      actionFingerprint: result.actionFingerprint!,
      viewedPageDigests: [pages[0].contentDigest],
    }, pages)).toBe(false)
    expect(validatePhotonCadCommercialApprovalRequest({
      contractVersion: 1,
      requestId: 'commercial-approve:empty',
      reviewHandle: result.reviewHandle!,
      documentFingerprint: result.documentFingerprint!,
      actionFingerprint: result.actionFingerprint!,
      viewedPageDigests: [],
    }, [])).toBe(false)
    expect(validatePhotonCadCommercialReviewResult({ ...result, expiresAtUtc: '2026-02-31T19:00:00Z' })).toBe(false)
  })

  it('cannot use a review handle to print; commit requires a distinct approval handle', () => {
    const base = {
      contractVersion: 1 as const,
      requestId: 'commercial-commit:1',
      documentFingerprint: digest,
      actionFingerprint: `sha256:${'c'.repeat(64)}`,
    }
    expect(validatePhotonCadCommercialCommitRequest({ ...base, approvalHandle: `cad-commercial-review:${'r'.repeat(32)}` })).toBe(false)
    expect(validatePhotonCadCommercialCommitRequest({ ...base, approvalHandle: `cad-commercial-approval:${'a'.repeat(32)}` })).toBe(true)
    expect(isPhotonCadCommercialApprovalHandle(`cad-commercial-approval:${'a'.repeat(32)}`)).toBe(true)
  })

  it('defines no send, accounting, payment, or paid action', () => {
    expect(PHOTON_CAD_COMMERCIAL_INVARIANT).toMatch(/every rendered page.*human explicitly approves/i)
    expect(PHOTON_CAD_COMMERCIAL_FORBIDDEN_ACTIONS).toEqual([
      'email', 'send', 'post-to-accounting', 'charge', 'pay', 'mark-paid',
    ])
  })
})
