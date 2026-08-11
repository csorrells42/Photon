import { describe, expect, it, vi } from 'vitest'
import type {
  PhotonCadBomExportCommitRequest,
  PhotonCadBomExportReviewRequest,
  PhotonCadCommercialApprovalRequest,
  PhotonCadCommercialCommitRequest,
  PhotonCadCommercialDraft,
  PhotonCadCommercialReviewRequest,
} from './PhotonCadCommercialContract'
import { PhotonCadCommercialController } from './PhotonCadCommercialController'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const digestC = `sha256:${'c'.repeat(64)}`
const destination = `cad-destination:${'d'.repeat(32)}`
const printer = `cad-printer:${'p'.repeat(32)}`
const bomHandle = `cad-bom-review:${'b'.repeat(32)}`
const reviewHandle = `cad-commercial-review:${'r'.repeat(32)}`
const approvalHandle = `cad-commercial-approval:${'a'.repeat(32)}`
const context = { sessionId: 'session:1', projectId: 'project:gearbox', projectRevision: 4, bomDigest: digestA }
const now = Date.parse('2026-08-10T18:00:00Z')

const party = {
  organization: 'Photon Machine Works', contactName: 'Bill', addressLines: ['100 Machine Way'], city: 'Andalusia',
  region: 'AL', postalCode: '36420', countryCode: 'US', email: 'bill@example.com', phone: '555-0100',
}

function draft(): PhotonCadCommercialDraft {
  return {
    contractVersion: 1, documentKind: 'invoice', documentNumber: 'INV-1001', projectId: context.projectId,
    projectRevision: context.projectRevision, bomDigest: digestA, issueDate: '2026-08-10', dueDate: '2026-09-09',
    currency: 'USD', currencyScale: 2, seller: party, customer: { ...party, organization: 'Scarlett Industrial' },
    lines: [{ lineId: 'line:1', sourceBomRowId: 'bom:1', partNumber: 'GEAR-001', description: 'Drive gear', quantity: 1, unit: 'each', unitPriceMinorUnits: 10_000, taxable: true }],
    markupBasisPoints: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxBasisPoints: 0, terms: 'Net 30', notes: '',
  }
}

function totals() {
  return { lineSubtotalMinorUnits: 10_000, markupMinorUnits: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxableSubtotalMinorUnits: 10_000, taxMinorUnits: 0, totalMinorUnits: 10_000 }
}

function reviewReady(requestId: string) {
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

function fakeTransport() {
  return {
    reviewBomExport: vi.fn(async (request: PhotonCadBomExportReviewRequest) => ({
      contractVersion: 1 as const, requestId: request.requestId, projectId: request.projectId, projectRevision: request.projectRevision,
      status: 'ready' as const, reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB,
      files: [{ role: 'bom' as const, relativePath: `bom/gearbox.${request.format}` }],
    })),
    commitBomExport: vi.fn(async (request: PhotonCadBomExportCommitRequest) => ({ contractVersion: 1 as const, requestId: request.requestId, status: 'exported' as const, reason: 'exported', exportFingerprint: request.exportFingerprint })),
    discardBomExport: vi.fn(),
    reviewCommercialDocument: vi.fn(async (request: PhotonCadCommercialReviewRequest) => reviewReady(request.requestId)),
    approveCommercialDocument: vi.fn(async (request: PhotonCadCommercialApprovalRequest) => ({
      contractVersion: 1 as const, requestId: request.requestId, status: 'approved' as const, reason: 'approved', approvalHandle,
      documentFingerprint: request.documentFingerprint, actionFingerprint: request.actionFingerprint, expiresAtUtc: '2026-08-10T18:30:00Z',
    })),
    commitCommercialDocument: vi.fn(async (request: PhotonCadCommercialCommitRequest) => ({
      contractVersion: 1 as const, requestId: request.requestId, status: 'printed' as const, reason: 'printed',
      documentFingerprint: request.documentFingerprint, actionFingerprint: request.actionFingerprint,
    })),
    discardCommercialDocument: vi.fn(),
  }
}

function requestIds(kind: string, sequence: number) {
  return `${kind}:${sequence}`
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((next) => { resolve = next })
  return { promise, resolve }
}

describe('PhotonCadCommercialController', () => {
  it.each(['csv', 'xlsx', 'pdf'] as const)('runs reviewed BOM %s export without a direct output shortcut', async (format) => {
    const transport = fakeTransport()
    const controller = new PhotonCadCommercialController({ controller: transport, context, createRequestId: requestIds, now: () => now })
    expect(await controller.commitReviewedBomExport()).toBeNull()
    expect(controller.state.reason).toBe('bom-review-required')
    await expect(controller.prepareBomExport(format, destination)).resolves.toMatchObject({ status: 'ready', reviewHandle: bomHandle })
    await expect(controller.commitReviewedBomExport()).resolves.toMatchObject({ status: 'exported', exportFingerprint: digestB })
    expect(transport.reviewBomExport).toHaveBeenCalledWith(expect.objectContaining({ format, destinationHandle: destination, bomDigest: digestA }))
    expect(transport.commitBomExport).toHaveBeenCalledTimes(1)
  })

  it('requires complete page-number evidence, explicit approval, then exact print commit', async () => {
    const transport = fakeTransport()
    const controller = new PhotonCadCommercialController({ controller: transport, context, createRequestId: requestIds, now: () => now })
    expect(await controller.commitApprovedCommercialDocument()).toBeNull()
    const review = await controller.prepareCommercialDocument(draft(), { kind: 'print', printerHandle: printer, copies: 1 })
    expect(review?.status).toBe('ready')
    expect(controller.recordRenderedPage(review!.pages[0])).toBe(true)
    expect(await controller.approveRenderedCommercialDocument()).toBeNull()
    expect(transport.approveCommercialDocument).not.toHaveBeenCalled()
    expect(controller.recordRenderedPage(review!.pages[1])).toBe(true)
    await expect(controller.approveRenderedCommercialDocument()).resolves.toMatchObject({ status: 'approved', approvalHandle })
    expect(transport.approveCommercialDocument).toHaveBeenCalledWith(expect.objectContaining({ viewedPageDigests: [digestA, digestA] }))
    await expect(controller.commitApprovedCommercialDocument()).resolves.toMatchObject({ status: 'printed' })
    expect(transport.commitCommercialDocument).toHaveBeenCalledTimes(1)
    expect(controller.state.documentReview).toBeNull()
    expect(controller.state.documentApproval).toBeNull()
  })

  it('discards a ready result that becomes stale after an edit', async () => {
    const transport = fakeTransport()
    const pendingResult = deferred<ReturnType<typeof reviewReady>>()
    transport.reviewCommercialDocument = vi.fn(() => pendingResult.promise)
    const controller = new PhotonCadCommercialController({ controller: transport, context, createRequestId: requestIds, now: () => now })
    const pending = controller.prepareCommercialDocument(draft(), { kind: 'export-pdf', destinationHandle: destination })
    controller.markEdited()
    pendingResult.resolve(reviewReady('document-review:1'))
    await expect(pending).resolves.toBeNull()
    expect(transport.discardCommercialDocument).toHaveBeenCalledWith(reviewHandle)
    expect(controller.state).toMatchObject({ documentReview: null, documentApproval: null, reason: 'commercial-draft-edited' })
  })

  it('invalidates review and approval authority on context or controller changes', async () => {
    const first = fakeTransport()
    const controller = new PhotonCadCommercialController({ controller: first, context, createRequestId: requestIds, now: () => now })
    const review = await controller.prepareCommercialDocument(draft(), { kind: 'export-pdf', destinationHandle: destination })
    controller.setContext({ ...context, projectRevision: 5, bomDigest: digestB })
    expect(first.discardCommercialDocument).toHaveBeenCalledWith(reviewHandle)
    expect(controller.state.documentReview).toBeNull()

    controller.setContext(context)
    const secondReview = await controller.prepareCommercialDocument(draft(), { kind: 'export-pdf', destinationHandle: destination })
    secondReview!.pages.forEach((page) => controller.recordRenderedPage(page))
    await controller.approveRenderedCommercialDocument()
    const second = fakeTransport()
    controller.replaceController(second)
    expect(first.discardCommercialDocument).toHaveBeenCalledWith(approvalHandle)
    expect(controller.state.documentApproval).toBeNull()
    expect(controller.state.reason).toBe('commercial-controller-changed')
  })

  it('serializes the commercial lane and exposes no send, post, pay, or mark-paid action', async () => {
    const transport = fakeTransport()
    const pendingReview = deferred<Awaited<ReturnType<typeof transport.reviewBomExport>>>()
    transport.reviewBomExport = vi.fn((_request: PhotonCadBomExportReviewRequest) => pendingReview.promise)
    const controller = new PhotonCadCommercialController({ controller: transport, context, createRequestId: requestIds, now: () => now })
    const active = controller.prepareBomExport('xlsx', destination)
    expect(await controller.prepareCommercialDocument(draft(), { kind: 'export-pdf', destinationHandle: destination })).toBeNull()
    expect(controller.state.reason).toBe('commercial-operation-busy')
    pendingReview.resolve({
      contractVersion: 1, requestId: 'bom-review:1', projectId: context.projectId, projectRevision: context.projectRevision,
      status: 'ready', reason: 'ready', reviewHandle: bomHandle, exportFingerprint: digestB, files: [{ role: 'bom', relativePath: 'bom/gearbox.xlsx' }],
    })
    await active
    const publicMethods = Object.getOwnPropertyNames(PhotonCadCommercialController.prototype).join(' ')
    expect(publicMethods).not.toMatch(/email|send|post|pay|mark.?paid/iu)
  })
})
