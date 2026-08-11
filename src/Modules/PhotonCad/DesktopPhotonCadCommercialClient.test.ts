import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DesktopPhotonCadCommercialClient,
  normalizePhotonCadCommercialHostFrame,
  type PhotonCadCommercialWebViewBridge,
} from './DesktopPhotonCadCommercialClient'
import type {
  PhotonCadBomExportFormat,
  PhotonCadBomExportReviewRequest,
  PhotonCadCommercialDraft,
  PhotonCadCommercialReviewRequest,
} from './PhotonCadCommercialContract'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const digestC = `sha256:${'c'.repeat(64)}`
const destination = `cad-destination:${'d'.repeat(32)}`
const printer = `cad-printer:${'p'.repeat(32)}`
const bomHandle = `cad-bom-review:${'b'.repeat(32)}`
const reviewHandle = `cad-commercial-review:${'r'.repeat(32)}`
const approvalHandle = `cad-commercial-approval:${'a'.repeat(32)}`

class FakeBridge implements PhotonCadCommercialWebViewBridge {
  public readonly messages: unknown[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  public postMessage = (message: unknown) => { this.messages.push(message) }
  public addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.add(listener) }
  public removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.delete(listener) }
  public emit(data: unknown) { this.listeners.forEach((listener) => listener({ data } as MessageEvent)) }
}

const party = {
  organization: 'Photon Machine Works', contactName: 'Bill', addressLines: ['100 Machine Way'], city: 'Andalusia',
  region: 'AL', postalCode: '36420', countryCode: 'US', email: 'bill@example.com', phone: '555-0100',
}

function draft(): PhotonCadCommercialDraft {
  return {
    contractVersion: 1, documentKind: 'invoice', documentNumber: 'INV-1001', projectId: 'project:gearbox', projectRevision: 4,
    bomDigest: digestA, issueDate: '2026-08-10', dueDate: '2026-09-09', currency: 'USD', currencyScale: 2,
    seller: { ...party }, customer: { ...party, organization: 'Scarlett Industrial' },
    lines: [{ lineId: 'line:shaft', sourceBomRowId: 'bom:shaft', partNumber: 'SHAFT-001', description: 'Input shaft', quantity: 2, unit: 'each', unitPriceMinorUnits: 12_500, taxable: true }],
    markupBasisPoints: 1_500, discountMinorUnits: 0, freightMinorUnits: 2_500, taxBasisPoints: 925, terms: 'Net 30', notes: '',
  }
}

function bomRequest(format: PhotonCadBomExportFormat, requestId = `bom-review:${format}`): PhotonCadBomExportReviewRequest {
  return { contractVersion: 1, requestId, projectId: 'project:gearbox', projectRevision: 4, bomDigest: digestA, format, destinationHandle: destination }
}

function documentRequest(requestId = 'document-review:1'): PhotonCadCommercialReviewRequest {
  return { contractVersion: 1, requestId, draft: draft(), action: { kind: 'print', printerHandle: printer, copies: 1 } }
}

function totals() {
  return {
    lineSubtotalMinorUnits: 25_000, markupMinorUnits: 3_750, discountMinorUnits: 0, freightMinorUnits: 2_500,
    taxableSubtotalMinorUnits: 28_750, taxMinorUnits: 2_659, totalMinorUnits: 33_909,
  }
}

function documentReady(requestId = 'document-review:1') {
  return {
    contractVersion: 1 as const, requestId, status: 'ready' as const, reason: 'ready', reviewHandle,
    documentFingerprint: digestB, actionFingerprint: digestC, expiresAtUtc: '2026-08-10T19:00:00Z',
    pages: [
      { pageNumber: 1, previewHandle: `cad-document-page:${'1'.repeat(32)}`, contentDigest: digestA },
      { pageNumber: 2, previewHandle: `cad-document-page:${'2'.repeat(32)}`, contentDigest: digestA },
    ],
    totals: totals(),
  }
}

afterEach(() => vi.useRealTimers())

describe('Photon CAD commercial desktop normalization', () => {
  it('fails closed on malformed pages, impossible expiry dates, and absolute output paths', () => {
    expect(normalizePhotonCadCommercialHostFrame({
      type: 'photonCad.commercial.document.review.result', version: 1,
      value: { ...documentReady(), expiresAtUtc: '2026-02-31T19:00:00Z' },
    })).toBeNull()
    expect(normalizePhotonCadCommercialHostFrame({
      type: 'photonCad.commercial.document.review.result', version: 1,
      value: { ...documentReady(), pages: [documentReady().pages[1]] },
    })).toBeNull()
    expect(normalizePhotonCadCommercialHostFrame({
      type: 'photonCad.commercial.bom.review.result', version: 1,
      value: {
        contractVersion: 1, requestId: 'bom-review:pdf', projectId: 'project:gearbox', projectRevision: 4,
        status: 'ready', reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB,
        files: [{ role: 'bom', relativePath: 'C:\\secrets\\bom.xlsx' }],
      },
    })).toBeNull()
    expect(normalizePhotonCadCommercialHostFrame({
      type: 'photonCad.commercial.bom.review.result', version: 1,
      value: {
        contractVersion: 1, requestId: 'bom-review:pdf', projectId: 'project:gearbox', projectRevision: 4,
        status: 'ready', reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB,
        files: [{ role: 'validation-report', relativePath: 'bom/validation.json' }],
      },
    })).toBeNull()
  })
})

describe('DesktopPhotonCadCommercialClient', () => {
  it.each(['csv', 'xlsx', 'pdf'] as const)('requires reviewed BOM binding before %s commit', async (format) => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadCommercialClient({ getBridge: () => bridge })
    await expect(client.commitBomExport({ contractVersion: 2 as 1, requestId: 'bom-commit:wrong-version', reviewHandle: bomHandle, exportFingerprint: digestB }))
      .rejects.toThrow('Invalid')
    await expect(client.commitBomExport({ contractVersion: 1, requestId: 'bom-commit:early', reviewHandle: bomHandle, exportFingerprint: digestB }))
      .rejects.toThrow('exact current review')
    const pendingReview = client.reviewBomExport(bomRequest(format))
    bridge.emit({
      type: 'photonCad.commercial.bom.review.result', version: 1,
      value: {
        contractVersion: 1, requestId: `bom-review:${format}`, projectId: 'project:foreign', projectRevision: 4,
        status: 'ready', reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB,
        files: [{ role: 'bom', relativePath: `bom/gearbox.${format}` }],
      },
    })
    bridge.emit({
      type: 'photonCad.commercial.bom.review.result', version: 1,
      value: {
        contractVersion: 1, requestId: `bom-review:${format}`, projectId: 'project:gearbox', projectRevision: 4,
        status: 'ready', reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB,
        files: [{ role: 'bom', relativePath: `bom/gearbox.${format}` }],
      },
    })
    await expect(pendingReview).resolves.toMatchObject({ status: 'ready', reviewHandle: bomHandle })

    const pendingCommit = client.commitBomExport({ contractVersion: 1, requestId: `bom-commit:${format}`, reviewHandle: bomHandle, exportFingerprint: digestB })
    bridge.emit({
      type: 'photonCad.commercial.bom.commit.result', version: 1,
      value: { contractVersion: 1, requestId: `bom-commit:${format}`, status: 'exported', reason: 'exported', exportFingerprint: digestB },
    })
    await expect(pendingCommit).resolves.toMatchObject({ status: 'exported', exportFingerprint: digestB })
    client.close()
  })

  it('enforces review, every ordered page, approval, and exact output action', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadCommercialClient({ getBridge: () => bridge, now: () => Date.parse('2026-08-10T18:00:00Z') })
    const review = documentReady()
    const pendingReview = client.reviewCommercialDocument(documentRequest())
    bridge.emit({ type: 'photonCad.commercial.document.review.result', version: 1, value: review })
    await expect(pendingReview).resolves.toMatchObject({ status: 'ready', pages: review.pages })

    await expect(client.approveCommercialDocument({
      contractVersion: 1, requestId: 'document-approve:partial', reviewHandle,
      documentFingerprint: digestB, actionFingerprint: digestC, viewedPageDigests: [digestA],
    })).rejects.toThrow('every ordered page')
    await expect(client.commitCommercialDocument({
      contractVersion: 1, requestId: 'document-commit:early', approvalHandle, documentFingerprint: digestB, actionFingerprint: digestC,
    })).rejects.toThrow('human-approval')

    const pendingApproval = client.approveCommercialDocument({
      contractVersion: 1, requestId: 'document-approve:1', reviewHandle,
      documentFingerprint: digestB, actionFingerprint: digestC, viewedPageDigests: [digestA, digestA],
    })
    bridge.emit({
      type: 'photonCad.commercial.document.approve.result', version: 1,
      value: { contractVersion: 1, requestId: 'document-approve:1', status: 'approved', reason: 'approved', approvalHandle, documentFingerprint: digestB, actionFingerprint: digestC, expiresAtUtc: '2026-08-10T18:30:00Z' },
    })
    await expect(pendingApproval).resolves.toMatchObject({ status: 'approved', approvalHandle })

    const pendingCommit = client.commitCommercialDocument({
      contractVersion: 1, requestId: 'document-commit:1', approvalHandle, documentFingerprint: digestB, actionFingerprint: digestC,
    })
    bridge.emit({
      type: 'photonCad.commercial.document.commit.result', version: 1,
      value: { contractVersion: 1, requestId: 'document-commit:1', status: 'exported', reason: 'wrong-action', documentFingerprint: digestB, actionFingerprint: digestC },
    })
    let settled = false
    void pendingCommit.finally(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit({
      type: 'photonCad.commercial.document.commit.result', version: 1,
      value: { contractVersion: 1, requestId: 'document-commit:1', status: 'printed', reason: 'printed', documentFingerprint: digestB, actionFingerprint: digestC },
    })
    await expect(pendingCommit).resolves.toMatchObject({ status: 'printed' })
    const messageTypes = bridge.messages.map((message) => (message as { type?: string }).type)
    expect(messageTypes.some((type) => /send|email|post|pay|mark-paid/iu.test(type ?? ''))).toBe(false)
    client.close()
  })

  it('invalidates expired review and approval handles before use', async () => {
    const bridge = new FakeBridge()
    let now = Date.parse('2026-08-10T18:00:00Z')
    const client = new DesktopPhotonCadCommercialClient({ getBridge: () => bridge, now: () => now, createRequestId: (kind, sequence) => `${kind}:${sequence}` })
    const pendingReview = client.reviewCommercialDocument(documentRequest())
    bridge.emit({ type: 'photonCad.commercial.document.review.result', version: 1, value: documentReady() })
    await pendingReview
    now = Date.parse('2026-08-10T19:00:00Z')
    await expect(client.approveCommercialDocument({
      contractVersion: 1, requestId: 'approve:expired', reviewHandle, documentFingerprint: digestB, actionFingerprint: digestC, viewedPageDigests: [digestA, digestA],
    })).rejects.toThrow('expired')
    expect(bridge.messages).toContainEqual({ type: 'photonCad.commercial.document.discard', version: 1, requestId: 'document-discard:1', handle: reviewHandle })
    client.close()
  })

  it('posts typed cancellation for orphanable work on timeout and close', async () => {
    vi.useFakeTimers()
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadCommercialClient({
      getBridge: () => bridge,
      createRequestId: (kind, sequence) => `${kind}:${sequence}`,
      requestTimeoutMs: { 'document-review': 100 },
    })
    const pending = client.reviewCommercialDocument(documentRequest())
    const rejection = expect(pending).rejects.toThrow('timed out')
    await vi.advanceTimersByTimeAsync(100)
    await rejection
    expect(bridge.messages).toContainEqual({
      type: 'photonCad.commercial.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'document-review:1', operation: 'document-review',
    })

    const active = client.reviewCommercialDocument(documentRequest('document-review:2'))
    client.close()
    await expect(active).rejects.toThrow('invalidated')
    expect(bridge.messages).toContainEqual({
      type: 'photonCad.commercial.cancel', version: 1, requestId: 'cancel:2', targetRequestId: 'document-review:2', operation: 'document-review',
    })
  })
})
