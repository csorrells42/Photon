import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { PhotonCadCommercialDraft, PhotonCadCommercialReviewRequest } from './PhotonCadCommercialContract'
import {
  PhotonCadDesktopCommercialAdapter,
  PhotonCadNewProjectDialogView,
  PhotonCadDesktopWorkspace,
  TrackingPhotonCadController,
  nextPhotonCadTabIndex,
  closePhotonCadNewProjectDialog,
  normalizePhotonCadNewProjectDetails,
  openPhotonCadNewProjectDialog,
  photonCadDescribeNeedsBridgeRetry,
  photonCadCloseRequiresConfirmation,
  photonCadAcceptedRuntimeSnapshot,
  photonCadControllerForAttachment,
  photonCadPersistedRefreshMatches,
  photonCadRuntimeAttachmentForLoad,
  projectStatusText,
  runPhotonCadNewProjectDialogAction,
  schedulePhotonCadLifecycleCleanup,
} from './PhotonCadDesktopWorkspace'
import type { PhotonCadController, PhotonCadOperationRequest, PhotonCadOperationResult } from './PhotonCadContract'
import type { PhotonCadProjectController, PhotonCadProjectDocument } from './PhotonCadProjectContract'
import { photonCadWorkspaceFixtureRuntime } from './PhotonCadWorkspaceFixture'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const digestC = `sha256:${'c'.repeat(64)}`
const workspaceHandle = `cad-workspace:${'w'.repeat(32)}`
const destination = `cad-destination:${'d'.repeat(32)}`
const reviewHandle = `cad-commercial-review:${'r'.repeat(32)}`
const approvalHandle = `cad-commercial-approval:${'a'.repeat(32)}`

function project(suffix: string, dirty = false): PhotonCadProjectDocument {
  return {
    contractVersion: 1,
    workspaceHandle,
    projectHandle: `cad-project:${suffix.repeat(32)}`,
    displayName: suffix === 'g' ? 'Gearbox' : 'Auger',
    snapshot: {
      contractVersion: 1,
      sessionId: `session:${suffix}`,
      projectId: `project:${suffix}`,
      revision: 4,
      title: suffix === 'g' ? 'Gearbox' : 'Auger',
      units: 'millimeter',
      mode: 'canonical',
      entities: [{ id: `part:${suffix}`, parentId: null, kind: 'part', name: 'Part', visible: true, suppressed: false }],
      operations: [],
      issues: [],
      dirty,
    },
    contentDigest: digestA,
    lastSavedContentDigest: dirty ? digestB : digestA,
    bomDigest: digestB,
    bom: [{ partNumber: `${suffix.toUpperCase()}-001`, description: 'Industrial part', quantity: 1, unit: 'each', sourceEntityId: `part:${suffix}` }],
    openedAtUtc: '2026-08-10T18:00:00Z',
    lastSavedRevision: dirty ? 3 : 4,
  }
}

function projectAt(revision: number, units: 'millimeter' | 'inch' = 'millimeter', dirty = false) {
  const document = project('g', dirty)
  document.snapshot = { ...document.snapshot, revision, units, dirty }
  document.lastSavedRevision = dirty ? Math.max(0, revision - 1) : revision
  document.lastSavedContentDigest = dirty ? digestB : document.contentDigest
  return document
}

function loadResult(document: PhotonCadProjectDocument) {
  return { contractVersion: 1 as const, requestId: 'load:1', status: 'opened' as const, reason: 'opened', document }
}

function primitiveRequest(baseRevision = 0): PhotonCadOperationRequest {
  return {
    contractVersion: 1,
    requestId: 'operation:1',
    sessionId: 'session:g',
    projectId: 'project:g',
    baseRevision,
    mode: 'scratch',
    capabilityId: 'geometry.box.create.v1',
    inputs: { length: 10, width: 20, height: 30 },
    targetEntityIds: [],
  }
}

function acceptedPrimitiveResult(request: PhotonCadOperationRequest, resultingRevision = request.baseRevision + 2, stale = false): PhotonCadOperationResult {
  const snapshot = projectAt(resultingRevision).snapshot
  return {
    contractVersion: 1,
    requestId: request.requestId,
    projectId: request.projectId,
    baseRevision: request.baseRevision,
    resultingRevision,
    status: 'accepted',
    stale,
    reason: 'accepted-and-saved',
    issues: [],
    snapshot: { ...snapshot, sessionId: request.sessionId, projectId: request.projectId },
  }
}

function projectController(): PhotonCadProjectController {
  const unavailable = async (request: { requestId: string }) => ({ contractVersion: 1 as const, requestId: request.requestId, status: 'unavailable' as const, reason: 'test-unavailable' })
  return {
    chooseWorkspace: unavailable,
    createProject: unavailable,
    openProject: unavailable,
    reopenProject: unavailable,
    refreshProject: unavailable,
    saveProject: unavailable,
    saveProjectAs: unavailable,
    closeProject: unavailable,
  }
}

function coreController(): PhotonCadController {
  return {
    describe: async () => ({ contractVersion: 1, status: 'unavailable', reason: 'test-unavailable' }),
    execute: async (request) => ({ contractVersion: 1, requestId: request.requestId, projectId: request.projectId, baseRevision: request.baseRevision, resultingRevision: request.baseRevision, status: 'rejected', stale: false, reason: 'test-unavailable', issues: [] }),
    verify: async (request) => ({ contractVersion: 1, requestId: request.requestId, projectId: request.projectId, revision: request.revision, status: 'failed', stale: false, reason: 'test-unavailable', measuredAtUtc: '2026-08-10T18:00:00Z', checks: [], issues: [] }),
    reviewRelease: async (request) => ({ contractVersion: 1, requestId: request.requestId, projectId: request.projectId, revision: request.revision, status: 'unavailable', reason: 'test-unavailable', files: [], issues: [] }),
    commitRelease: async (request) => ({ contractVersion: 1, requestId: request.requestId, status: 'unavailable', reason: 'test-unavailable' }),
    discardRelease: () => undefined,
  }
}

const party = {
  organization: 'Photon Machine Works', contactName: 'Bill', addressLines: ['100 Machine Way'], city: 'Andalusia',
  region: 'AL', postalCode: '36420', countryCode: 'US', email: 'bill@example.com', phone: '555-0100',
}

function draft(): PhotonCadCommercialDraft {
  return {
    contractVersion: 1, documentKind: 'quote', documentNumber: 'Q-1001', projectId: 'project:g', projectRevision: 4,
    bomDigest: digestB, issueDate: '2026-08-10', currency: 'USD', currencyScale: 2, seller: party,
    customer: { ...party, organization: 'Scarlett Industrial' },
    lines: [{ lineId: 'line:1', partNumber: 'GEAR-001', description: 'Drive gear', quantity: 1, unit: 'each', unitPriceMinorUnits: 10_000, taxable: true }],
    markupBasisPoints: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxBasisPoints: 0, terms: 'Net 30', notes: '',
  }
}

function commercialTransport() {
  return {
    reviewBomExport: vi.fn(),
    commitBomExport: vi.fn(),
    discardBomExport: vi.fn(),
    reviewCommercialDocument: vi.fn(async (request: PhotonCadCommercialReviewRequest) => ({
      contractVersion: 1 as const, requestId: request.requestId, status: 'ready' as const, reason: 'ready', reviewHandle,
      documentFingerprint: digestA, actionFingerprint: digestC, expiresAtUtc: '2099-08-10T19:00:00Z',
      pages: [
        { pageNumber: 1, previewHandle: `cad-document-page:${'1'.repeat(32)}`, contentDigest: digestA },
        { pageNumber: 2, previewHandle: `cad-document-page:${'2'.repeat(32)}`, contentDigest: digestA },
      ],
      totals: { lineSubtotalMinorUnits: 10_000, markupMinorUnits: 0, discountMinorUnits: 0, freightMinorUnits: 0, taxableSubtotalMinorUnits: 10_000, taxMinorUnits: 0, totalMinorUnits: 10_000 },
    })),
    approveCommercialDocument: vi.fn(async (request) => ({
      contractVersion: 1 as const, requestId: request.requestId, status: 'approved' as const, reason: 'approved', approvalHandle,
      documentFingerprint: request.documentFingerprint, actionFingerprint: request.actionFingerprint, expiresAtUtc: '2099-08-10T20:00:00Z',
    })),
    commitCommercialDocument: vi.fn(async (request) => ({
      contractVersion: 1 as const, requestId: request.requestId, status: 'exported' as const, reason: 'exported',
      documentFingerprint: request.documentFingerprint, actionFingerprint: request.actionFingerprint,
    })),
    discardCommercialDocument: vi.fn(),
  }
}

describe('PhotonCadDesktopWorkspace', () => {
  it('bounds and normalizes mounted new-project details without using a browser prompt', () => {
    expect(normalizePhotonCadNewProjectDetails({ title: '  Roll Guide  ', units: 'millimeter' })).toEqual({ title: 'Roll Guide', units: 'millimeter' })
    expect(normalizePhotonCadNewProjectDetails({ title: '', units: 'millimeter' })).toBeNull()
    expect(normalizePhotonCadNewProjectDetails({ title: 'x'.repeat(257), units: 'millimeter' })).toBeNull()
    expect(normalizePhotonCadNewProjectDetails({ title: 'Line\nBreak', units: 'inch' })).toBeNull()
  })

  it('opens exactly one real modal and closes it on cancellation or unmount', () => {
    const dialog = {
      open: false,
      showModal: vi.fn(function (this: { open: boolean }) { this.open = true }),
      close: vi.fn(function (this: { open: boolean }) { this.open = false }),
    }
    expect(openPhotonCadNewProjectDialog(dialog)).toBe(true)
    expect(openPhotonCadNewProjectDialog(dialog)).toBe(true)
    expect(dialog.showModal).toHaveBeenCalledTimes(1)
    expect(closePhotonCadNewProjectDialog(dialog)).toBe(true)
    expect(dialog.open).toBe(false)
    expect(dialog.close).toHaveBeenCalledTimes(1)

    expect(openPhotonCadNewProjectDialog({ open: false, close: vi.fn() })).toBe(false)
    expect(openPhotonCadNewProjectDialog({ open: false, showModal: vi.fn(), close: vi.fn() })).toBe(false)
    expect(openPhotonCadNewProjectDialog({ open: false, showModal: () => { throw new Error('blocked') }, close: vi.fn() })).toBe(false)
    expect(closePhotonCadNewProjectDialog({ open: true, showModal: vi.fn() })).toBe(false)
  })

  it('renders the scoped accessible dialog surface with explicit action and error states', () => {
    const base = {
      dialogRef: { current: null },
      onTitleChange: vi.fn(),
      onUnitsChange: vi.fn(),
      onNameBlur: vi.fn(),
      onCancel: vi.fn(),
      onSubmit: vi.fn(),
    }
    const validMarkup = renderToStaticMarkup(
      <PhotonCadNewProjectDialogView {...base} draft={{ title: 'Gearbox', units: 'millimeter' }} nameTouched={false} />,
    )
    expect(validMarkup).toContain('class="photon-cad-desktop__new-project-dialog"')
    expect(validMarkup).toContain('aria-modal="true"')
    expect(validMarkup).toContain('aria-describedby="photon-cad-new-project-privacy"')
    expect(validMarkup).toContain('autofocus=""')
    expect(validMarkup).toContain('The Windows picker opens after Continue. Photon never receives the selected filesystem path.')
    expect(validMarkup).toMatch(/>Cancel<\/button>[\s\S]*>Continue<\/button>/u)
    expect(validMarkup).not.toMatch(/type="submit" disabled=""/u)

    const invalidMarkup = renderToStaticMarkup(
      <PhotonCadNewProjectDialogView {...base} draft={{ title: '   ', units: 'inch' }} nameTouched />,
    )
    expect(invalidMarkup).toContain('aria-invalid="true"')
    expect(invalidMarkup).toContain('role="alert"')
    expect(invalidMarkup).toContain('Enter a valid project name before continuing.')
    expect(invalidMarkup).toMatch(/<button[^>]*type="submit"[^>]*disabled=""/u)
  })

  it('keeps cancellation local and issues one picker request only after a valid dialog continuation', async () => {
    const events: string[] = []
    const dismiss = vi.fn(() => { events.push('dismiss') })
    const setStatus = vi.fn((status: string) => { events.push(`status:${status}`) })
    const afterDismiss = vi.fn(async () => { events.push('paint') })
    const requestNativePicker = vi.fn(async () => { events.push('picker') })

    await expect(runPhotonCadNewProjectDialogAction({ kind: 'cancel', dismiss, setStatus })).resolves.toBe('cancelled')
    expect(events).toEqual(['dismiss', 'status:new-project-cancelled'])
    expect(requestNativePicker).not.toHaveBeenCalled()
    expect(afterDismiss).not.toHaveBeenCalled()

    events.length = 0
    dismiss.mockClear()
    setStatus.mockClear()
    await expect(runPhotonCadNewProjectDialogAction(
      { kind: 'continue', draft: { title: '  Auger Drive  ', units: 'inch' }, dismiss, setStatus, afterDismiss, requestNativePicker },
    )).resolves.toBe('continued')
    expect(events).toEqual(['dismiss', 'paint', 'picker'])
    expect(dismiss).toHaveBeenCalledTimes(1)
    expect(requestNativePicker).toHaveBeenCalledTimes(1)
    expect(requestNativePicker).toHaveBeenCalledWith({ title: 'Auger Drive', units: 'inch' })

    events.length = 0
    requestNativePicker.mockClear()
    afterDismiss.mockClear()
    await expect(runPhotonCadNewProjectDialogAction(
      { kind: 'continue', draft: { title: '\n', units: 'millimeter' }, dismiss, setStatus, afterDismiss, requestNativePicker },
    )).resolves.toBe('invalid')
    expect(requestNativePicker).not.toHaveBeenCalled()
    expect(afterDismiss).not.toHaveBeenCalled()
    expect(events).toEqual(['status:invalid-project-details'])
  })

  it('does not close desktop clients during the Strict Mode effect replay but closes on real unmount', () => {
    let generation = 1
    const scheduled: Array<() => void> = []
    const cleanup = vi.fn()

    schedulePhotonCadLifecycleCleanup(() => generation, 1, cleanup, (callback) => scheduled.push(callback))
    generation = 2
    scheduled.shift()!()
    expect(cleanup).not.toHaveBeenCalled()

    schedulePhotonCadLifecycleCleanup(() => generation, 2, cleanup, (callback) => scheduled.push(callback))
    scheduled.shift()!()
    expect(cleanup).toHaveBeenCalledTimes(1)
  })

  it('retries only the bounded pre-injection desktop bridge describe result', () => {
    expect(photonCadDescribeNeedsBridgeRetry({ contractVersion: 1, status: 'unavailable', reason: 'desktop-host-unavailable' }, 0)).toBe(true)
    expect(photonCadDescribeNeedsBridgeRetry({ contractVersion: 1, status: 'unavailable', reason: 'desktop-host-unavailable' }, 39)).toBe(true)
    expect(photonCadDescribeNeedsBridgeRetry({ contractVersion: 1, status: 'unavailable', reason: 'desktop-host-unavailable' }, 40)).toBe(false)
    expect(photonCadDescribeNeedsBridgeRetry({ contractVersion: 1, status: 'unavailable', reason: 'industrial_runtime_unavailable' }, 0)).toBe(false)
    expect(photonCadDescribeNeedsBridgeRetry({ contractVersion: 1, status: 'available', reason: 'ready' }, 0)).toBe(false)
  })

  it('renders an honest empty project shell without inventing a viewer or host success', () => {
    const markup = renderToStaticMarkup(<PhotonCadDesktopWorkspace projectController={projectController()} coreController={coreController()} />)
    expect(markup).toContain('Photon CAD')
    expect(markup).toContain('No project is open')
    expect(markup).toContain('opaque host-owned handles')
    expect(markup).not.toContain('Saved successfully')
    expect(markup).not.toContain('C:\\')
  })

  it('explains an existing New target without implying that the native picker failed', () => {
    expect(projectStatusText('target-exists-overwrite-confirmation-required')).toBe(
      'That project file already exists. Choose Open to use it, or choose a different name for New.',
    )
    expect(projectStatusText('native-picker-cancelled')).toContain('picker was cancelled')
    expect(projectStatusText('project-create-failed')).toContain('before a verified project could be opened')
    expect(projectStatusText('overwrite-confirmation-declined')).toContain('existing project file was not changed')
    expect(projectStatusText('desktop-host-unavailable')).toContain('desktop project bridge is unavailable')
    expect(projectStatusText('unexpected-project-code')).toContain('unexpected-project-code')
  })

  it('disables project entry actions when the native storage safety adapter is unavailable', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadDesktopWorkspace
        projectActionsAvailable={false}
        projectController={projectController()}
        coreController={coreController()}
      />,
    )
    expect(markup).toContain('CAD project storage is unavailable')
    expect(markup).toContain('Native CAD project storage is unavailable')
    expect(markup).not.toContain('Choose New or Open')
    expect(markup).toMatch(/<button[^>]*disabled=""[^>]*>New<\/button>/u)
    expect(markup).toMatch(/<button[^>]*disabled=""[^>]*>Open<\/button>/u)
  })

  it('disables every persisted-project action when the native storage safety adapter becomes unavailable', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadDesktopWorkspace
        projectActionsAvailable={false}
        projectController={projectController()}
        coreController={coreController()}
        initialDocuments={[project('g')]}
      />,
    )
    for (const label of ['New', 'Open', 'Save', 'Save as', 'Refresh', 'Close']) {
      expect(markup).toMatch(new RegExp(`<button[^>]*disabled=""[^>]*>${label}<\\/button>`, 'u'))
    }
    expect(markup).toMatch(/<button[^>]*aria-label="Close Gearbox"[^>]*disabled=""/u)
  })

  it('renders browser-style roving tabs with one active tab stop and dirty state', () => {
    const gearbox = project('g')
    const auger = project('a', true)
    const markup = renderToStaticMarkup(
      <PhotonCadDesktopWorkspace
        projectController={projectController()}
        coreController={coreController()}
        initialDocuments={[gearbox, auger]}
        initialActiveProjectHandle={auger.projectHandle}
      />,
    )
    expect((markup.match(/aria-controls="photon-cad-active-document"/gu) ?? [])).toHaveLength(2)
    expect((markup.match(/aria-controls="photon-cad-active-document" tabindex="0"/gu) ?? [])).toHaveLength(1)
    expect((markup.match(/aria-controls="photon-cad-active-document" tabindex="-1"/gu) ?? [])).toHaveLength(1)
    expect(markup).toContain('aria-selected="true"')
    expect(markup).toContain('Unsaved changes')
    expect(markup).toContain('Close Auger')
  })

  it('shows the global live catalog while keeping persisted projects view and save only', () => {
    const core = coreController()
    const execute = vi.spyOn(core, 'execute')
    const liveRuntime = { ...photonCadWorkspaceFixtureRuntime, status: 'available' as const, reason: 'ready' }
    const markup = renderToStaticMarkup(
      <PhotonCadDesktopWorkspace
        projectController={projectController()}
        coreController={core}
        runtimeDescription={liveRuntime}
        initialDocuments={[project('g')]}
      />,
    )
    expect(markup).toContain('Catalog connected · editing locked')
    expect(markup).toContain('Browse-only catalog')
    expect(markup).toContain('viewing and saving')
    expect(markup).not.toContain('Runtime ready')
    expect(markup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Prepare suggestion<\/button>/u)
    expect(execute).not.toHaveBeenCalled()
    expect(photonCadControllerForAttachment(false, core)).toBeUndefined()
  })

  it('distinguishes a saved revision-zero project from a nonzero project that needs hydration', () => {
    const revisionZero = project('g')
    revisionZero.snapshot = { ...revisionZero.snapshot, revision: 0 }
    revisionZero.lastSavedRevision = 0
    const liveRuntime = { ...photonCadWorkspaceFixtureRuntime, status: 'available' as const, reason: 'ready' }
    const markup = renderToStaticMarkup(
      <PhotonCadDesktopWorkspace
        projectController={projectController()}
        coreController={coreController()}
        runtimeDescription={liveRuntime}
        initialDocuments={[revisionZero]}
      />,
    )
    expect(markup).toContain('saved at revision 0')
    expect(markup).toContain('modeling remains disabled until accepted results can be committed')
  })

  it('grants runtime attachment only to an exact clean millimeter New result with the explicit host sync bit', () => {
    const revisionZero = loadResult(projectAt(0))
    expect(photonCadRuntimeAttachmentForLoad('new', revisionZero, true)).toBe(true)
    expect(photonCadRuntimeAttachmentForLoad('new', revisionZero, false)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('new', revisionZero, undefined)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('open', revisionZero, true)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('reopen', revisionZero, true)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('new', loadResult(projectAt(2)), true)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('new', loadResult(projectAt(0, 'inch')), true)).toBe(false)
    expect(photonCadRuntimeAttachmentForLoad('new', loadResult(projectAt(0, 'millimeter', true)), true)).toBe(false)

    const controller = coreController()
    expect(photonCadControllerForAttachment(true, controller, true, true)).toBe(controller)
    expect(photonCadControllerForAttachment(true, controller, true, false)).toBeUndefined()
    expect(photonCadControllerForAttachment(true, controller, false, true)).toBeUndefined()
  })

  it('accepts only exact non-stale +2 persisted primitive results and exact clean refresh metadata', () => {
    const request = primitiveRequest(0)
    const exact = acceptedPrimitiveResult(request)
    expect(photonCadAcceptedRuntimeSnapshot(request, exact)).toEqual(exact.snapshot)
    expect(photonCadAcceptedRuntimeSnapshot(request, acceptedPrimitiveResult(request, 1))).toBeNull()
    expect(photonCadAcceptedRuntimeSnapshot(request, acceptedPrimitiveResult(request, 3))).toBeNull()
    expect(photonCadAcceptedRuntimeSnapshot(request, acceptedPrimitiveResult(request, 2, true))).toBeNull()
    expect(photonCadAcceptedRuntimeSnapshot({ ...request, capabilityId: 'geometry.step.export.v1' }, exact)).toBeNull()

    const current = projectAt(0)
    const accepted = exact.snapshot!
    const persisted = projectAt(2)
    persisted.snapshot = accepted
    persisted.lastSavedRevision = 2
    persisted.lastSavedContentDigest = persisted.contentDigest
    expect(photonCadPersistedRefreshMatches(persisted, current, accepted)).toBe(true)
    expect(photonCadPersistedRefreshMatches({ ...persisted, lastSavedRevision: 1 }, current, accepted)).toBe(false)
    expect(photonCadPersistedRefreshMatches({ ...persisted, snapshot: { ...accepted, dirty: true } }, current, accepted)).toBe(false)
    expect(photonCadPersistedRefreshMatches({ ...persisted, snapshot: { ...accepted, revision: 3 } }, current, accepted)).toBe(false)
  })

  it('does not publish hostile accepted results or a result whose persisted refresh was not proven', async () => {
    const request = primitiveRequest(0)
    const refresh = vi.fn(async () => true)
    const busy = vi.fn()
    const hostileCore = coreController()
    hostileCore.execute = vi.fn(async () => acceptedPrimitiveResult(request, 1))
    const hostile = new TrackingPhotonCadController(hostileCore, refresh, busy)
    await expect(hostile.execute(request)).resolves.toMatchObject({ status: 'unavailable', resultingRevision: 0, reason: 'project-runtime-result-mismatch' })
    expect(refresh).not.toHaveBeenCalled()

    const exactCore = coreController()
    exactCore.execute = vi.fn(async () => acceptedPrimitiveResult(request))
    const exact = new TrackingPhotonCadController(exactCore, refresh, busy)
    await expect(exact.execute(request)).resolves.toMatchObject({ status: 'accepted', resultingRevision: 2 })
    expect(refresh).toHaveBeenCalledTimes(1)

    const rejectedRefresh = new TrackingPhotonCadController(exactCore, async () => false, busy)
    await expect(rejectedRefresh.execute(request)).resolves.toMatchObject({ status: 'unavailable', resultingRevision: 0, reason: 'project-metadata-refresh-failed' })
  })

  it('implements deterministic wraparound, Home, and End tab navigation', () => {
    expect(nextPhotonCadTabIndex(0, 3, 'ArrowLeft')).toBe(2)
    expect(nextPhotonCadTabIndex(2, 3, 'ArrowRight')).toBe(0)
    expect(nextPhotonCadTabIndex(2, 3, 'Home')).toBe(0)
    expect(nextPhotonCadTabIndex(0, 3, 'End')).toBe(2)
    expect(nextPhotonCadTabIndex(1, 3, 'Enter')).toBe(1)
  })

  it('requires explicit dirty-close confirmation only for unsaved documents', () => {
    expect(photonCadCloseRequiresConfirmation(project('g'))).toBe(false)
    expect(photonCadCloseRequiresConfirmation(project('a', true))).toBe(true)
  })

  it('adapts the stable workspace interface through exact rendered-page evidence', async () => {
    const transport = commercialTransport()
    const adapter = new PhotonCadDesktopCommercialAdapter(transport)
    adapter.setContext({ sessionId: 'session:g', projectId: 'project:g', projectRevision: 4, bomDigest: digestB })
    const review = await adapter.reviewCommercialDocument({
      contractVersion: 1, requestId: 'ui-review:1', draft: draft(), action: { kind: 'export-pdf', destinationHandle: destination },
    })
    expect(review).toMatchObject({ requestId: 'ui-review:1', status: 'ready' })
    const approvalRequest = {
      contractVersion: 1 as const,
      requestId: 'ui-approve:1',
      reviewHandle,
      documentFingerprint: digestA,
      actionFingerprint: digestC,
      viewedPageDigests: [digestA, digestA],
    }
    await expect(adapter.approveCommercialDocument(approvalRequest)).resolves.toMatchObject({ status: 'rejected', reason: 'all-pages-review-required' })
    expect(adapter.recordRenderedPage(review.pages[0])).toBe(true)
    expect(adapter.recordRenderedPage({ ...review.pages[1], previewHandle: review.pages[0].previewHandle })).toBe(false)
    expect(adapter.recordRenderedPage(review.pages[1])).toBe(true)
    await expect(adapter.approveCommercialDocument(approvalRequest)).resolves.toMatchObject({ requestId: 'ui-approve:1', status: 'approved', approvalHandle })
    await expect(adapter.commitCommercialDocument({ contractVersion: 1, requestId: 'ui-commit:1', approvalHandle, documentFingerprint: digestA, actionFingerprint: digestC }))
      .resolves.toMatchObject({ requestId: 'ui-commit:1', status: 'exported' })
    expect(transport.commitCommercialDocument).toHaveBeenCalledTimes(1)
  })
})
