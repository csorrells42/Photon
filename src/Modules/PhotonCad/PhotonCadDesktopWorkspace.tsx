import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
  type ReactNode,
  type RefObject,
} from 'react'
import { DesktopPhotonCadClient } from './DesktopPhotonCadClient'
import { DesktopPhotonCadCommercialClient } from './DesktopPhotonCadCommercialClient'
import { DesktopPhotonCadProjectClient, normalizePhotonCadProjectDocument } from './DesktopPhotonCadProjectClient'
import {
  PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
  isPhotonCadBomReviewHandle,
  validatePhotonCadBomExportReviewRequest,
  validatePhotonCadCommercialApprovalRequest,
  validatePhotonCadCommercialCommitRequest,
  validatePhotonCadCommercialReviewRequest,
  type PhotonCadBomExportCommitRequest,
  type PhotonCadBomExportCommitResult,
  type PhotonCadBomExportReviewRequest,
  type PhotonCadBomExportReviewResult,
  type PhotonCadCommercialApprovalRequest,
  type PhotonCadCommercialApprovalResult,
  type PhotonCadCommercialCommitRequest,
  type PhotonCadCommercialCommitResult,
  type PhotonCadCommercialController as PhotonCadCommercialTransport,
  type PhotonCadCommercialPreviewPage,
  type PhotonCadCommercialReviewRequest,
  type PhotonCadCommercialReviewResult,
} from './PhotonCadCommercialContract'
import { PhotonCadCommercialController as PhotonCadCommercialFacade } from './PhotonCadCommercialController'
import type { PhotonCadCommercialContext } from './PhotonCadCommercialOperationGate'
import {
  PHOTON_CAD_CONTRACT_VERSION,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  type PhotonCadController,
  type PhotonCadOperationRequest,
  type PhotonCadOperationResult,
  type PhotonCadReleaseCommitRequest,
  type PhotonCadReleaseCommitResult,
  type PhotonCadReleaseReviewRequest,
  type PhotonCadReleaseReviewResult,
  type PhotonCadRuntimeDescription,
  type PhotonCadVerificationRequest,
  type PhotonCadVerificationResult,
  type PhotonCadUnit,
  type PhotonCadProjectSnapshot,
} from './PhotonCadContract'
import {
  PHOTON_CAD_PROJECT_CONTRACT_VERSION,
  PHOTON_CAD_PROJECT_LIMITS,
  type PhotonCadProjectCloseResult,
  type PhotonCadProjectController,
  type PhotonCadProjectDocument,
  type PhotonCadProjectLoadResult,
  type PhotonCadProjectReopenMetadata,
} from './PhotonCadProjectContract'
import {
  PhotonCadWorkspace,
  type PhotonCadCommercialPreviewContext,
  type PhotonCadWorkspaceProps,
} from './PhotonCadWorkspace'
import './PhotonCadDesktopWorkspace.css'

type LifecycleProjectController = PhotonCadProjectController & { cancelPending?: () => void; close?: () => void }
type LifecycleCoreController = PhotonCadController & { cancelPending?: () => void; close?: () => void }
type LifecycleCommercialTransport = PhotonCadCommercialTransport & { cancelPending?: () => void; close?: () => void }

export type PhotonCadCommercialRenderedPageContext = PhotonCadCommercialPreviewContext & {
  reportRendered: () => boolean
}

export type PhotonCadDesktopWorkspaceProps = {
  projectActionsAvailable?: boolean
  runtimeProjectSyncAvailable?: boolean
  projectController?: PhotonCadProjectController
  coreController?: PhotonCadController
  runtimeDescription?: PhotonCadRuntimeDescription
  commercialController?: PhotonCadCommercialTransport
  initialDocuments?: readonly PhotonCadProjectDocument[]
  initialActiveProjectHandle?: string
  previewSurface?: PhotonCadWorkspaceProps['previewSurface']
  commercialPreviewSurface?: (context: PhotonCadCommercialRenderedPageContext) => ReactNode
  destination?: PhotonCadWorkspaceProps['destination']
  onChooseDestination?: PhotonCadWorkspaceProps['onChooseDestination']
  printer?: PhotonCadWorkspaceProps['printer']
  onChoosePrinter?: PhotonCadWorkspaceProps['onChoosePrinter']
  requestNewProject?: () => Promise<{ title: string; units: PhotonCadUnit } | null> | { title: string; units: PhotonCadUnit } | null
  confirmDiscardChanges?: (document: PhotonCadProjectDocument) => Promise<boolean> | boolean
  className?: string
}

type ProjectTab = { document: PhotonCadProjectDocument; metadataCurrent: boolean; runtimeAttached: boolean }
type LifecycleBusy = 'details' | 'picker' | 'create' | 'open' | 'reopen' | 'refresh' | 'save' | 'save-as' | 'close' | null
type NewProjectDraft = { title: string; units: PhotonCadUnit }
type PhotonCadProjectLoadSource = 'new' | 'open' | 'reopen'

type PhotonCadDesktopHostWindow = Window & {
  __HERMES_DESKTOP_HOST__?: {
    capabilities?: {
      photonCadProjectDetails?: {
        runtimeProjectSyncAvailable?: unknown
        runtimeHydrationAvailable?: unknown
      }
    }
  }
}

type PhotonCadNewProjectDialogViewProps = {
  dialogRef: RefObject<HTMLDialogElement | null>
  draft: NewProjectDraft
  nameTouched: boolean
  onTitleChange: (title: string) => void
  onUnitsChange: (units: PhotonCadUnit) => void
  onNameBlur: () => void
  onCancel: () => void
  onSubmit: (event: FormEvent<HTMLFormElement>) => void
}

export function PhotonCadNewProjectDialogView({
  dialogRef,
  draft,
  nameTouched,
  onTitleChange,
  onUnitsChange,
  onNameBlur,
  onCancel,
  onSubmit,
}: PhotonCadNewProjectDialogViewProps) {
  const validDraft = normalizePhotonCadNewProjectDetails(draft)
  const nameInvalid = nameTouched && !validDraft
  const nameDescription = nameInvalid
    ? 'photon-cad-new-project-name-hint photon-cad-new-project-name-error'
    : 'photon-cad-new-project-name-hint'

  return (
    <dialog
      ref={dialogRef}
      className="photon-cad-desktop__new-project-dialog"
      aria-labelledby="photon-cad-new-project-title"
      aria-describedby="photon-cad-new-project-privacy"
      aria-modal="true"
      onCancel={(event) => {
        event.preventDefault()
        onCancel()
      }}
    >
      <form className="photon-cad-desktop__new-project-form" method="dialog" onSubmit={onSubmit}>
        <header className="photon-cad-desktop__new-project-header">
          <span>Project setup</span>
          <h3 id="photon-cad-new-project-title">New Photon CAD project</h3>
          <p>Name the project before choosing its host-owned file.</p>
        </header>

        <div className="photon-cad-desktop__new-project-fields">
          <label className="photon-cad-desktop__new-project-field photon-cad-desktop__new-project-field--name">
            <span>Project name</span>
            <input
              autoFocus
              aria-describedby={nameDescription}
              aria-errormessage={nameInvalid ? 'photon-cad-new-project-name-error' : undefined}
              aria-invalid={nameInvalid || undefined}
              maxLength={PHOTON_CAD_PROJECT_LIMITS.title}
              required
              value={draft.title}
              onBlur={onNameBlur}
              onChange={(event) => onTitleChange(event.target.value)}
            />
            <small id="photon-cad-new-project-name-hint">
              1-{PHOTON_CAD_PROJECT_LIMITS.title} visible, single-line characters.
            </small>
            {nameInvalid ? (
              <small id="photon-cad-new-project-name-error" className="photon-cad-desktop__new-project-error" role="alert">
                Enter a valid project name before continuing.
              </small>
            ) : null}
          </label>

          <label className="photon-cad-desktop__new-project-field photon-cad-desktop__new-project-field--units">
            <span>Units</span>
            <select value={draft.units} onChange={(event) => onUnitsChange(event.target.value as PhotonCadUnit)}>
              <option value="millimeter">Millimeters</option>
              <option value="inch">Inches</option>
            </select>
          </label>
        </div>

        <p id="photon-cad-new-project-privacy" className="photon-cad-desktop__new-project-privacy">
          The Windows picker opens after Continue. Photon never receives the selected filesystem path.
        </p>

        <div className="photon-cad-desktop__new-project-actions">
          <button className="photon-cad-desktop__dialog-button photon-cad-desktop__dialog-button--secondary" type="button" onClick={onCancel}>
            Cancel
          </button>
          <button className="photon-cad-desktop__dialog-button photon-cad-desktop__dialog-button--primary" type="submit" disabled={!validDraft}>
            Continue
          </button>
        </div>
      </form>
    </dialog>
  )
}

export function photonCadControllerForAttachment(
  runtimeAttached: boolean,
  controller: PhotonCadController | undefined,
  metadataCurrent = true,
  runtimeProjectSyncAvailable = true,
) {
  return runtimeAttached && metadataCurrent && runtimeProjectSyncAvailable ? controller : undefined
}

export function photonCadRuntimeAttachmentForLoad(
  source: PhotonCadProjectLoadSource,
  result: PhotonCadProjectLoadResult,
  runtimeProjectSyncAvailable: boolean | undefined,
) {
  if (source !== 'new' || runtimeProjectSyncAvailable !== true || result.status !== 'opened' || !result.document) return false
  const document = normalizePhotonCadProjectDocument(result.document)
  return Boolean(document
    && document.snapshot.revision === 0
    && document.lastSavedRevision === 0
    && !document.snapshot.dirty
    && document.snapshot.units === 'millimeter'
    && document.snapshot.mode === 'canonical'
    && document.contentDigest.toLowerCase() === document.lastSavedContentDigest.toLowerCase())
}

export function photonCadAcceptedRuntimeSnapshot(
  request: PhotonCadOperationRequest,
  result: PhotonCadOperationResult,
): PhotonCadProjectSnapshot | null {
  const snapshot = result.snapshot
  if (request.mode !== 'scratch'
    || request.capabilityId !== 'geometry.box.create.v1' && request.capabilityId !== 'geometry.cylinder.create.v1'
    || request.targetEntityIds.length !== 0
    || result.status !== 'accepted'
    || result.stale
    || !snapshot
    || result.requestId !== request.requestId
    || result.projectId !== request.projectId
    || result.baseRevision !== request.baseRevision
    || result.resultingRevision !== request.baseRevision + 2
    || snapshot.sessionId !== request.sessionId
    || snapshot.projectId !== request.projectId
    || snapshot.revision !== result.resultingRevision
    || snapshot.units !== 'millimeter'
    || snapshot.mode !== 'canonical'
    || snapshot.dirty) return null
  return snapshot
}

export function photonCadPersistedRefreshMatches(
  candidate: PhotonCadProjectDocument | undefined,
  current: PhotonCadProjectDocument,
  accepted: PhotonCadProjectSnapshot,
) {
  const document = candidate ? normalizePhotonCadProjectDocument(candidate) : null
  return Boolean(document
    && document.projectHandle === current.projectHandle
    && document.snapshot.sessionId === accepted.sessionId
    && document.snapshot.projectId === accepted.projectId
    && document.snapshot.revision === accepted.revision
    && document.snapshot.units === 'millimeter'
    && document.snapshot.mode === 'canonical'
    && !document.snapshot.dirty
    && document.lastSavedRevision === accepted.revision
    && document.contentDigest.toLowerCase() === document.lastSavedContentDigest.toLowerCase()
    && JSON.stringify(document.snapshot) === JSON.stringify(accepted))
}

function desktopPhotonCadRuntimeProjectSyncAvailable() {
  if (typeof window === 'undefined') return false
  return (window as PhotonCadDesktopHostWindow).__HERMES_DESKTOP_HOST__?.capabilities
    ?.photonCadProjectDetails?.runtimeProjectSyncAvailable === true
}

function browseOnlyProjectStatus(document: PhotonCadProjectDocument) {
  return document.snapshot.revision === 0
    ? 'project-runtime-commit-unavailable'
    : 'project-runtime-hydration-unavailable'
}

export class TrackingPhotonCadController implements PhotonCadController {
  private activeOperations = 0

  public constructor(
    private readonly inner: LifecycleCoreController,
    private readonly onAcceptedSnapshot: (snapshot: PhotonCadProjectSnapshot) => Promise<boolean>,
    private readonly onBusyChange: (busy: boolean) => void,
  ) {}

  public describe(): Promise<PhotonCadRuntimeDescription> { return this.inner.describe() }
  public async execute(request: PhotonCadOperationRequest): Promise<PhotonCadOperationResult> {
    return this.track(async () => {
      const result = await this.inner.execute(request)
      if (result.status !== 'accepted') return result
      const snapshot = photonCadAcceptedRuntimeSnapshot(request, result)
      if (!snapshot || !await this.onAcceptedSnapshot(snapshot)) {
        return {
          contractVersion: PHOTON_CAD_CONTRACT_VERSION,
          requestId: request.requestId,
          projectId: request.projectId,
          baseRevision: request.baseRevision,
          resultingRevision: request.baseRevision,
          status: 'unavailable',
          stale: false,
          reason: snapshot ? 'project-metadata-refresh-failed' : 'project-runtime-result-mismatch',
          issues: [],
        }
      }
      return result
    })
  }
  public verify(request: PhotonCadVerificationRequest): Promise<PhotonCadVerificationResult> { return this.track(() => this.inner.verify(request)) }
  public reviewRelease(request: PhotonCadReleaseReviewRequest): Promise<PhotonCadReleaseReviewResult> { return this.track(() => this.inner.reviewRelease(request)) }
  public commitRelease(request: PhotonCadReleaseCommitRequest): Promise<PhotonCadReleaseCommitResult> { return this.track(() => this.inner.commitRelease(request)) }
  public discardRelease(reviewHandle: string) { return this.inner.discardRelease(reviewHandle) }

  private async track<T>(operation: () => Promise<T>) {
    this.activeOperations += 1
    if (this.activeOperations === 1) this.onBusyChange(true)
    try {
      return await operation()
    } finally {
      this.activeOperations -= 1
      if (this.activeOperations === 0) this.onBusyChange(false)
    }
  }
}

export class PhotonCadDesktopCommercialAdapter implements PhotonCadCommercialTransport {
  private facade: PhotonCadCommercialFacade | null = null
  private contextKey = ''

  public constructor(private readonly transport: LifecycleCommercialTransport) {}

  public setContext(context: PhotonCadCommercialContext) {
    const key = commercialContextKey(context)
    if (this.contextKey === key && this.facade) return
    if (!this.facade) this.facade = new PhotonCadCommercialFacade({ controller: this.transport, context })
    else this.facade.setContext(context)
    this.contextKey = key
  }

  public clearContext() {
    this.facade?.markEdited()
    this.contextKey = ''
  }

  public hasContext(context: PhotonCadCommercialContext) {
    return this.facade !== null && this.contextKey === commercialContextKey(context)
  }

  public recordRenderedPage(page: PhotonCadCommercialPreviewPage) {
    return this.facade?.recordRenderedPage(page) ?? false
  }

  public async reviewBomExport(request: PhotonCadBomExportReviewRequest): Promise<PhotonCadBomExportReviewResult> {
    const facade = this.facade
    if (!validatePhotonCadBomExportReviewRequest(request) || !facade || !this.matchesProject(request.projectId, request.projectRevision, request.bomDigest)) {
      return { contractVersion: 1, requestId: request.requestId, projectId: request.projectId, projectRevision: request.projectRevision, status: 'rejected', reason: 'project-context-mismatch', files: [] }
    }
    const result = await facade.prepareBomExport(request.format, request.destinationHandle)
    return result
      ? { ...result, requestId: request.requestId }
      : { contractVersion: 1, requestId: request.requestId, projectId: request.projectId, projectRevision: request.projectRevision, status: commercialFailureStatus(facade.state.reason), reason: facade.state.reason, files: [] }
  }

  public async commitBomExport(request: PhotonCadBomExportCommitRequest): Promise<PhotonCadBomExportCommitResult> {
    const facade = this.facade
    const review = facade?.state.bomReview
    if (request.contractVersion !== PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION || !isPhotonCadIdentifier(request.requestId)
      || !isPhotonCadBomReviewHandle(request.reviewHandle) || !isPhotonCadDigest(request.exportFingerprint)
      || !facade || review?.status !== 'ready' || review.reviewHandle !== request.reviewHandle
      || review.exportFingerprint?.toLowerCase() !== request.exportFingerprint.toLowerCase()) {
      return { contractVersion: 1, requestId: request.requestId, status: 'rejected', reason: 'bom-review-binding-mismatch' }
    }
    const result = await facade.commitReviewedBomExport()
    return result
      ? { ...result, requestId: request.requestId }
      : { contractVersion: 1, requestId: request.requestId, status: commercialFailureStatus(facade.state.reason), reason: facade.state.reason }
  }

  public discardBomExport(reviewHandle: string) {
    if (this.facade?.state.bomReview?.reviewHandle === reviewHandle) this.facade.markEdited()
    else return this.transport.discardBomExport(reviewHandle)
  }

  public async reviewCommercialDocument(request: PhotonCadCommercialReviewRequest): Promise<PhotonCadCommercialReviewResult> {
    const facade = this.facade
    if (!validatePhotonCadCommercialReviewRequest(request) || !facade
      || !this.matchesProject(request.draft.projectId, request.draft.projectRevision, request.draft.bomDigest)) {
      return { contractVersion: 1, requestId: request.requestId, status: 'rejected', reason: 'project-context-mismatch', pages: [] }
    }
    const result = await facade.prepareCommercialDocument(request.draft, request.action)
    return result
      ? { ...result, requestId: request.requestId }
      : { contractVersion: 1, requestId: request.requestId, status: commercialFailureStatus(facade.state.reason), reason: facade.state.reason, pages: [] }
  }

  public async approveCommercialDocument(request: PhotonCadCommercialApprovalRequest): Promise<PhotonCadCommercialApprovalResult> {
    const facade = this.facade
    const review = facade?.state.documentReview
    const exactPages = review?.status === 'ready' && review.pages.length === request.viewedPageDigests.length
      && review.pages.every((page, index) => page.contentDigest.toLowerCase() === request.viewedPageDigests[index]?.toLowerCase())
    if (!facade || review?.status !== 'ready' || !validatePhotonCadCommercialApprovalRequest(request, review.pages)
      || review.reviewHandle !== request.reviewHandle
      || review.documentFingerprint?.toLowerCase() !== request.documentFingerprint.toLowerCase()
      || review.actionFingerprint?.toLowerCase() !== request.actionFingerprint.toLowerCase() || !exactPages) {
      return { contractVersion: 1, requestId: request.requestId, status: 'rejected', reason: 'document-review-binding-mismatch' }
    }
    const result = await facade.approveRenderedCommercialDocument()
    return result
      ? { ...result, requestId: request.requestId }
      : { contractVersion: 1, requestId: request.requestId, status: commercialFailureStatus(facade.state.reason), reason: facade.state.reason }
  }

  public async commitCommercialDocument(request: PhotonCadCommercialCommitRequest): Promise<PhotonCadCommercialCommitResult> {
    const facade = this.facade
    const approval = facade?.state.documentApproval
    if (!validatePhotonCadCommercialCommitRequest(request) || !facade || approval?.status !== 'approved' || approval.approvalHandle !== request.approvalHandle
      || approval.documentFingerprint?.toLowerCase() !== request.documentFingerprint.toLowerCase()
      || approval.actionFingerprint?.toLowerCase() !== request.actionFingerprint.toLowerCase()) {
      return { contractVersion: 1, requestId: request.requestId, status: 'rejected', reason: 'document-approval-binding-mismatch' }
    }
    const result = await facade.commitApprovedCommercialDocument()
    return result
      ? { ...result, requestId: request.requestId }
      : { contractVersion: 1, requestId: request.requestId, status: commercialFailureStatus(facade.state.reason), reason: facade.state.reason }
  }

  public discardCommercialDocument(handle: string) {
    const state = this.facade?.state
    if (state?.documentReview?.reviewHandle === handle || state?.documentApproval?.approvalHandle === handle) this.facade?.markEdited()
    else return this.transport.discardCommercialDocument(handle)
  }

  public invalidate() { this.clearContext() }

  private matchesProject(projectId: string, revision: number, bomDigest: string) {
    const context = this.facade?.state.context
    return context?.projectId === projectId && context.projectRevision === revision
      && context.bomDigest === bomDigest.toLowerCase()
  }
}

export function nextPhotonCadTabIndex(current: number, count: number, key: string) {
  if (count <= 0) return -1
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  if (key === 'ArrowRight') return (Math.max(0, current) + 1) % count
  if (key === 'ArrowLeft') return (Math.max(0, current) - 1 + count) % count
  return current
}

export function photonCadCloseRequiresConfirmation(document: PhotonCadProjectDocument) {
  return document.snapshot.dirty
}

export function PhotonCadDesktopWorkspace({
  projectActionsAvailable = true,
  runtimeProjectSyncAvailable: suppliedRuntimeProjectSyncAvailable,
  projectController,
  coreController,
  runtimeDescription: suppliedRuntimeDescription,
  commercialController,
  initialDocuments = [],
  initialActiveProjectHandle,
  previewSurface,
  commercialPreviewSurface,
  destination = null,
  onChooseDestination,
  printer = null,
  onChoosePrinter,
  requestNewProject,
  confirmDiscardChanges,
  className = '',
}: PhotonCadDesktopWorkspaceProps) {
  const runtimeProjectSyncAvailable = suppliedRuntimeProjectSyncAvailable ?? desktopPhotonCadRuntimeProjectSyncAvailable()
  const initialTabs = useMemo(() => normalizeInitialDocuments(initialDocuments), [initialDocuments])
  const [documents, setDocuments] = useState<ProjectTab[]>(initialTabs)
  const [activeProjectHandle, setActiveProjectHandle] = useState(() =>
    initialTabs.some((tab) => tab.document.projectHandle === initialActiveProjectHandle)
      ? initialActiveProjectHandle!
      : initialTabs[0]?.document.projectHandle ?? '',
  )
  const [recent, setRecent] = useState<PhotonCadProjectReopenMetadata[]>([])
  const [busy, setBusy] = useState<LifecycleBusy>(null)
  const [newProjectDraft, setNewProjectDraft] = useState<NewProjectDraft | null>(null)
  const [newProjectNameTouched, setNewProjectNameTouched] = useState(false)
  const [coreBusy, setCoreBusy] = useState(false)
  const [status, setStatus] = useState(!projectActionsAvailable
    ? 'project-storage-unavailable'
    : initialTabs.length
      ? initialTabs[0].runtimeAttached ? 'project-ready' : browseOnlyProjectStatus(initialTabs[0].document)
      : 'no-project-open')
  const [describedRuntime, setDescribedRuntime] = useState<PhotonCadRuntimeDescription>({
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    status: 'checking',
    reason: 'unavailable',
  })
  const [, setCommercialEpoch] = useState(0)
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([])
  const newProjectDialogRef = useRef<HTMLDialogElement>(null)
  const newProjectSubmissionPending = useRef(false)
  const lifecycleGeneration = useRef(0)
  const newProjectDialogVisible = newProjectDraft !== null
  const documentsRef = useRef(documents)
  documentsRef.current = documents

  const projectControllerRef = useRef<LifecycleProjectController | null>(null)
  const ownsProjectController = useRef(false)
  if (!projectControllerRef.current) {
    projectControllerRef.current = projectController ?? new DesktopPhotonCadProjectClient()
    ownsProjectController.current = !projectController
  }

  const coreControllerRef = useRef<LifecycleCoreController | null>(null)
  const ownsCoreController = useRef(false)
  if (!coreControllerRef.current) {
    coreControllerRef.current = coreController ?? new DesktopPhotonCadClient()
    ownsCoreController.current = !coreController
  }

  const commercialTransportRef = useRef<LifecycleCommercialTransport | null>(null)
  const ownsCommercialTransport = useRef(false)
  if (!commercialTransportRef.current) {
    commercialTransportRef.current = commercialController ?? new DesktopPhotonCadCommercialClient()
    ownsCommercialTransport.current = !commercialController
  }

  const commercialAdapterRef = useRef<PhotonCadDesktopCommercialAdapter | null>(null)
  if (!commercialAdapterRef.current) commercialAdapterRef.current = new PhotonCadDesktopCommercialAdapter(commercialTransportRef.current)

  const requestSequence = useRef(0)
  const requestNonce = useRef(desktopWorkspaceNonce())
  const describeGeneration = useRef(0)
  const mutationHandler = useRef<(snapshot: PhotonCadProjectSnapshot) => Promise<boolean>>(async () => false)
  const coreBusyHandler = useRef<(value: boolean) => void>((value) => setCoreBusy(value))
  const trackingControllerRef = useRef<TrackingPhotonCadController | null>(null)
  if (!trackingControllerRef.current) {
    trackingControllerRef.current = new TrackingPhotonCadController(
      coreControllerRef.current,
      (snapshot) => mutationHandler.current(snapshot),
      (value) => coreBusyHandler.current(value),
    )
  }

  useEffect(() => {
    if (suppliedRuntimeDescription) {
      describeGeneration.current += 1
      return
    }
    let active = true
    const generation = ++describeGeneration.current
    setDescribedRuntime({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'checking', reason: 'unavailable' })
    void coreControllerRef.current!.describe().then((description) => {
      if (active && generation === describeGeneration.current) setDescribedRuntime(description)
    }).catch(() => {
      if (active && generation === describeGeneration.current) {
        setDescribedRuntime({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'error', reason: 'unavailable' })
      }
    })
    return () => { active = false }
  }, [suppliedRuntimeDescription])

  useEffect(() => {
    if (!newProjectDialogVisible) return
    const dialog = newProjectDialogRef.current
    if (!dialog || !openPhotonCadNewProjectDialog(dialog)) {
      setNewProjectDraft(null)
      setNewProjectNameTouched(false)
      setStatus('project-details-dialog-unavailable')
      return
    }
    return () => {
      closePhotonCadNewProjectDialog(dialog)
    }
  }, [newProjectDialogVisible])

  const runtimeDescription = suppliedRuntimeDescription ?? describedRuntime

  const activeTab = documents.find((tab) => tab.document.projectHandle === activeProjectHandle) ?? null
  const activeTabIndex = documents.findIndex((tab) => tab.document.projectHandle === activeProjectHandle)
  const activeContext = activeTab && activeTab.metadataCurrent && activeTab.runtimeAttached && runtimeProjectSyncAvailable
    ? commercialContextOf(activeTab.document)
    : null
  const commercialReady = activeContext ? commercialAdapterRef.current.hasContext(activeContext) : false

  useEffect(() => {
    const adapter = commercialAdapterRef.current!
    if (activeContext) adapter.setContext(activeContext)
    else adapter.clearContext()
    setCommercialEpoch((value) => value + 1)
  }, [activeContext?.sessionId, activeContext?.projectId, activeContext?.projectRevision, activeContext?.bomDigest])

  useEffect(() => {
    if (!projectActionsAvailable) setStatus('project-storage-unavailable')
    else setStatus((value) => value === 'project-storage-unavailable'
      ? activeTab?.runtimeAttached && runtimeProjectSyncAvailable ? 'project-ready' : activeTab ? browseOnlyProjectStatus(activeTab.document) : 'no-project-open'
      : value)
  }, [projectActionsAvailable, activeTab?.document.projectHandle, activeTab?.runtimeAttached, runtimeProjectSyncAvailable])

  useEffect(() => {
    const generation = ++lifecycleGeneration.current
    return () => schedulePhotonCadLifecycleCleanup(
      () => lifecycleGeneration.current,
      generation,
      () => {
        coreBusyHandler.current = () => undefined
        commercialAdapterRef.current?.invalidate()
        if (ownsProjectController.current) projectControllerRef.current?.close?.()
        if (ownsCoreController.current) coreControllerRef.current?.close?.()
        if (ownsCommercialTransport.current) commercialTransportRef.current?.close?.()
      },
    )
  }, [])

  function nextRequestId(kind: string) {
    requestSequence.current += 1
    return `cad-desktop-${kind}:${requestNonce.current}:${requestSequence.current.toString(36)}`
  }

  function installDocument(
    document: PhotonCadProjectDocument,
    replaceHandle?: string,
    activate = true,
    runtimeAttached?: boolean,
  ) {
    const normalized = normalizePhotonCadProjectDocument(document)
    if (!normalized) {
      setStatus('malformed-project-document')
      return false
    }
    const current = documentsRef.current
    const existing = current.findIndex((tab) => tab.document.projectHandle === normalized.projectHandle)
    const replacing = replaceHandle ? current.findIndex((tab) => tab.document.projectHandle === replaceHandle) : -1
    const attached = runtimeProjectSyncAvailable && (runtimeAttached
      ?? (existing >= 0 ? current[existing].runtimeAttached : replacing >= 0 ? current[replacing].runtimeAttached : false)
    )
    let next: ProjectTab[]
    if (existing >= 0) next = current.map((tab, index) => index === existing ? { document: normalized, metadataCurrent: true, runtimeAttached: attached } : tab)
    else if (replacing >= 0) next = current.map((tab, index) => index === replacing ? { document: normalized, metadataCurrent: true, runtimeAttached: attached } : tab)
    else if (current.length < PHOTON_CAD_PROJECT_LIMITS.openDocuments) next = [...current, { document: normalized, metadataCurrent: true, runtimeAttached: attached }]
    else {
      setStatus('open-document-limit-reached')
      return false
    }
    documentsRef.current = next
    setDocuments(next)
    if (activate) {
      if (attached) commercialAdapterRef.current!.setContext(commercialContextOf(normalized))
      else commercialAdapterRef.current!.clearContext()
      setCommercialEpoch((value) => value + 1)
      setActiveProjectHandle(normalized.projectHandle)
    }
    setStatus(attached ? 'project-ready' : browseOnlyProjectStatus(normalized))
    return true
  }

  async function refreshAfterAcceptedSnapshot(snapshot: PhotonCadProjectSnapshot) {
    const tab = documentsRef.current.find((candidate) => candidate.document.snapshot.sessionId === snapshot.sessionId
      && candidate.document.snapshot.projectId === snapshot.projectId)
    if (!tab) {
      setStatus('project-metadata-untracked')
      return false
    }
    const provisional = documentsRef.current.map((candidate) => candidate.document.projectHandle === tab.document.projectHandle
      ? { ...candidate, metadataCurrent: false }
      : candidate)
    documentsRef.current = provisional
    setDocuments(provisional)
    if (activeProjectHandle === tab.document.projectHandle) {
      commercialAdapterRef.current!.clearContext()
      setCommercialEpoch((value) => value + 1)
    }
    setBusy('refresh')
    setStatus('refreshing-project-metadata')
    try {
      const result = await projectControllerRef.current!.refreshProject({
        contractVersion: PHOTON_CAD_PROJECT_CONTRACT_VERSION,
        requestId: nextRequestId('refresh'),
        projectHandle: tab.document.projectHandle,
        sessionId: snapshot.sessionId,
        projectId: snapshot.projectId,
        knownRevision: snapshot.revision,
      })
      if (result.status === 'opened' && photonCadPersistedRefreshMatches(result.document, tab.document, snapshot)) {
        return installDocument(result.document!, tab.document.projectHandle, activeProjectHandle === tab.document.projectHandle, true)
      } else {
        setStatus(result.status === 'opened' ? 'project-metadata-revision-mismatch' : result.reason)
        return false
      }
    } catch {
      setStatus('project-metadata-refresh-failed')
      return false
    } finally {
      setBusy((value) => value === 'refresh' ? null : value)
    }
  }
  mutationHandler.current = refreshAfterAcceptedSnapshot

  async function runPicker(purpose: 'new' | 'open' | 'save-as', suggestedName?: string) {
    const result = await projectControllerRef.current!.chooseWorkspace({
      contractVersion: 1,
      requestId: nextRequestId('picker'),
      purpose,
      ...(suggestedName ? { suggestedName } : {}),
    })
    if (result.status !== 'selected' || !result.workspaceHandle) {
      setStatus(result.reason)
      return null
    }
    return result.workspaceHandle
  }

  async function beginCreateProject() {
    if (!projectActionsAvailable || busy || coreBusy || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments) return
    if (!requestNewProject) {
      setNewProjectDraft({ title: '', units: 'millimeter' })
      setNewProjectNameTouched(false)
      setStatus('entering-project-details')
      return
    }
    setBusy('details')
    try {
      const details = normalizePhotonCadNewProjectDetails(await requestNewProject())
      if (!details) {
        setStatus('new-project-cancelled')
        return
      }
      await createProject(details)
    } catch {
      setStatus('project-create-failed')
    } finally {
      setBusy(null)
    }
  }

  async function submitNewProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (newProjectSubmissionPending.current) return
    newProjectSubmissionPending.current = true
    try {
      const result = await runPhotonCadNewProjectDialogAction(
        {
          kind: 'continue',
          draft: newProjectDraft,
          dismiss: dismissNewProjectDialog,
          setStatus,
          afterDismiss: nextBrowserPaint,
          requestNativePicker: createProject,
        },
      )
      if (result === 'invalid') setNewProjectNameTouched(true)
    } finally {
      newProjectSubmissionPending.current = false
    }
  }

  function dismissNewProjectDialog() {
    setNewProjectDraft(null)
    setNewProjectNameTouched(false)
  }

  function cancelNewProject() {
    if (newProjectSubmissionPending.current) return
    void runPhotonCadNewProjectDialogAction(
      {
        kind: 'cancel',
        dismiss: dismissNewProjectDialog,
        setStatus,
      },
    )
  }

  async function createProject(details: NewProjectDraft) {
    if (!projectActionsAvailable || busy || coreBusy || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments) return
    setBusy('picker')
    setStatus('choosing-project-workspace')
    try {
      const workspaceHandle = await runPicker('new', details.title)
      if (!workspaceHandle) return
      setBusy('create')
      setStatus('creating-project')
      const result = await projectControllerRef.current!.createProject({
        contractVersion: 1,
        requestId: nextRequestId('create'),
        workspaceHandle,
        title: details.title,
        units: details.units,
      })
      acceptLoadResult(result, undefined, photonCadRuntimeAttachmentForLoad('new', result, runtimeProjectSyncAvailable))
    } catch {
      setStatus('project-create-failed')
    } finally {
      setBusy(null)
    }
  }

  async function openProject() {
    if (!projectActionsAvailable || busy || coreBusy || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments) return
    setBusy('picker')
    setStatus('choosing-project')
    try {
      const workspaceHandle = await runPicker('open')
      if (!workspaceHandle) return
      setBusy('open')
      setStatus('opening-project')
      const result = await projectControllerRef.current!.openProject({ contractVersion: 1, requestId: nextRequestId('open'), workspaceHandle })
      acceptLoadResult(result, undefined, photonCadRuntimeAttachmentForLoad('open', result, runtimeProjectSyncAvailable))
    } catch {
      setStatus('project-open-failed')
    } finally {
      setBusy(null)
    }
  }

  function acceptLoadResult(result: PhotonCadProjectLoadResult, replaceHandle?: string, runtimeAttached?: boolean) {
    if (result.status === 'opened' && result.document) installDocument(result.document, replaceHandle, true, runtimeAttached)
    else setStatus(result.reason)
  }

  async function refreshActiveProject() {
    if (!projectActionsAvailable || !activeTab || busy || coreBusy) return
    setBusy('refresh')
    setStatus('refreshing-project-metadata')
    try {
      const document = activeTab.document
      const result = await projectControllerRef.current!.refreshProject({
        contractVersion: 1,
        requestId: nextRequestId('refresh'),
        projectHandle: document.projectHandle,
        sessionId: document.snapshot.sessionId,
        projectId: document.snapshot.projectId,
        knownRevision: document.snapshot.revision,
      })
      acceptLoadResult(result, document.projectHandle)
    } catch {
      setStatus('project-metadata-refresh-failed')
    } finally {
      setBusy(null)
    }
  }

  async function saveActiveProject(saveAs: boolean) {
    if (!projectActionsAvailable || !activeTab || busy || coreBusy || !activeTab.metadataCurrent) return
    const document = activeTab.document
    setBusy(saveAs ? 'picker' : 'save')
    setStatus(saveAs ? 'choosing-save-as-workspace' : 'saving-project')
    try {
      if (saveAs) {
        const destinationWorkspaceHandle = await runPicker('save-as')
        if (!destinationWorkspaceHandle) return
        setBusy('save-as')
        setStatus('saving-project-as')
        const result = await projectControllerRef.current!.saveProjectAs({
          contractVersion: 1,
          requestId: nextRequestId('save-as'),
          sourceProjectHandle: document.projectHandle,
          destinationWorkspaceHandle,
          sessionId: document.snapshot.sessionId,
          projectId: document.snapshot.projectId,
          baseRevision: document.snapshot.revision,
          contentDigest: document.contentDigest,
        })
        if (result.status === 'saved' && result.document) installDocument(result.document, document.projectHandle)
        else setStatus(result.reason)
      } else {
        const result = await projectControllerRef.current!.saveProject({
          contractVersion: 1,
          requestId: nextRequestId('save'),
          projectHandle: document.projectHandle,
          sessionId: document.snapshot.sessionId,
          projectId: document.snapshot.projectId,
          baseRevision: document.snapshot.revision,
          contentDigest: document.contentDigest,
        })
        if (result.status === 'saved' && result.document) installDocument(result.document, document.projectHandle)
        else setStatus(result.reason)
      }
    } catch {
      setStatus(saveAs ? 'project-save-as-failed' : 'project-save-failed')
    } finally {
      setBusy(null)
    }
  }

  async function closeDocument(projectHandle: string) {
    if (!projectActionsAvailable || busy || coreBusy) return
    const index = documentsRef.current.findIndex((tab) => tab.document.projectHandle === projectHandle)
    if (index < 0) return
    const tab = documentsRef.current[index]
    const document = tab.document
    const discardUnsavedChanges = photonCadCloseRequiresConfirmation(document)
    if (discardUnsavedChanges && !ownsProjectController.current) {
      const confirmed = await (confirmDiscardChanges ? confirmDiscardChanges(document) : defaultConfirmDiscardChanges(document))
      if (!confirmed) {
        setStatus('close-cancelled-unsaved-changes-preserved')
        return
      }
    }
    setBusy('close')
    setStatus('closing-project')
    try {
      const result = await projectControllerRef.current!.closeProject({
        contractVersion: 1,
        requestId: nextRequestId('close'),
        projectHandle: document.projectHandle,
        sessionId: document.snapshot.sessionId,
        projectId: document.snapshot.projectId,
        revision: document.snapshot.revision,
        lastSavedRevision: document.lastSavedRevision,
        contentDigest: document.contentDigest,
        lastSavedContentDigest: document.lastSavedContentDigest,
        discardUnsavedChanges,
      })
      if (result.status === 'closed') acceptCloseResult(result, index)
      else setStatus(result.reason)
    } catch {
      setStatus('project-close-failed')
    } finally {
      setBusy(null)
    }
  }

  function acceptCloseResult(result: PhotonCadProjectCloseResult, closedIndex: number) {
    if (!result.projectHandle || !result.reopen) {
      setStatus('malformed-project-close-result')
      return
    }
    const remaining = documentsRef.current.filter((tab) => tab.document.projectHandle !== result.projectHandle)
    documentsRef.current = remaining
    setDocuments(remaining)
    setRecent((items) => [result.reopen!, ...items.filter((item) => item.reopenHandle !== result.reopen!.reopenHandle)]
      .slice(0, PHOTON_CAD_PROJECT_LIMITS.recentDocuments))
    const next = remaining[Math.min(closedIndex, remaining.length - 1)] ?? null
    if (next) commercialAdapterRef.current!.setContext(commercialContextOf(next.document))
    else commercialAdapterRef.current!.clearContext()
    setCommercialEpoch((value) => value + 1)
    setActiveProjectHandle(next?.document.projectHandle ?? '')
    setStatus('project-closed')
    if (next) queueMicrotask(() => tabRefs.current[Math.min(closedIndex, remaining.length - 1)]?.focus())
  }

  async function reopenProject(metadata: PhotonCadProjectReopenMetadata) {
    if (!projectActionsAvailable || busy || coreBusy || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments) return
    setBusy('reopen')
    setStatus('reopening-project')
    try {
      const result = await projectControllerRef.current!.reopenProject({ contractVersion: 1, requestId: nextRequestId('reopen'), reopenHandle: metadata.reopenHandle })
      if (result.status === 'opened' && result.document
        && installDocument(result.document, undefined, true, photonCadRuntimeAttachmentForLoad('reopen', result, runtimeProjectSyncAvailable))) {
        setRecent((items) => items.filter((item) => item.reopenHandle !== metadata.reopenHandle))
      } else setStatus(result.reason)
    } catch {
      setStatus('project-reopen-failed')
    } finally {
      setBusy(null)
    }
  }

  function activateTab(projectHandle: string, focus = false) {
    if (coreBusy) return
    const index = documentsRef.current.findIndex((tab) => tab.document.projectHandle === projectHandle)
    if (index < 0) return
    const tab = documentsRef.current[index]
    if (tab.metadataCurrent && tab.runtimeAttached && runtimeProjectSyncAvailable) commercialAdapterRef.current!.setContext(commercialContextOf(tab.document))
    else commercialAdapterRef.current!.clearContext()
    setCommercialEpoch((value) => value + 1)
    setActiveProjectHandle(projectHandle)
    setStatus(tab.runtimeAttached && runtimeProjectSyncAvailable ? 'project-ready' : browseOnlyProjectStatus(tab.document))
    if (focus) queueMicrotask(() => tabRefs.current[index]?.focus())
  }

  function handleTabKey(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const next = nextPhotonCadTabIndex(index, documents.length, event.key)
    if (next === index || next < 0) return
    event.preventDefault()
    activateTab(documents[next].document.projectHandle, true)
  }

  const wrappedCommercialPreview = commercialPreviewSurface
    ? (context: PhotonCadCommercialPreviewContext) => commercialPreviewSurface({
      ...context,
      reportRendered: () => commercialAdapterRef.current!.recordRenderedPage(context.page),
    })
    : undefined

  return (
    <section className={`photon-cad-desktop ${className}`.trim()} aria-label="Photon CAD desktop workspace">
      <header className="photon-cad-desktop__toolbar">
        <div className="photon-cad-desktop__identity"><strong>Photon CAD</strong><span>Project workspace</span></div>
        <div className="photon-cad-desktop__actions" aria-label="Project actions">
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments} onClick={() => void beginCreateProject()}>New</button>
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || documents.length >= PHOTON_CAD_PROJECT_LIMITS.openDocuments} onClick={() => void openProject()}>Open</button>
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || !activeTab?.metadataCurrent} onClick={() => void saveActiveProject(false)}>Save</button>
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || !activeTab?.metadataCurrent} onClick={() => void saveActiveProject(true)}>Save as</button>
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || !activeTab} onClick={() => void refreshActiveProject()}>Refresh</button>
          <button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null || !activeTab} onClick={() => activeTab && void closeDocument(activeTab.document.projectHandle)}>Close</button>
        </div>
        <p className="photon-cad-desktop__status" role="status">{projectStatusText(status)}</p>
      </header>

      {newProjectDraft ? (
        <PhotonCadNewProjectDialogView
          dialogRef={newProjectDialogRef}
          draft={newProjectDraft}
          nameTouched={newProjectNameTouched}
          onTitleChange={(title) => setNewProjectDraft((draft) => draft ? { ...draft, title } : draft)}
          onUnitsChange={(units) => setNewProjectDraft((draft) => draft ? { ...draft, units } : draft)}
          onNameBlur={() => setNewProjectNameTouched(true)}
          onCancel={cancelNewProject}
          onSubmit={(event) => void submitNewProject(event)}
        />
      ) : null}

      <div className="photon-cad-desktop__tabs" role="tablist" aria-label="Open CAD projects">
        {documents.map((tab, index) => {
          const selected = tab.document.projectHandle === activeProjectHandle
          return (
            <div role="presentation" className={`photon-cad-desktop__tab ${selected ? 'is-active' : ''}`.trim()} key={tab.document.projectHandle}>
              <button
                id={`photon-cad-document-tab-${index}`}
                ref={(element) => { tabRefs.current[index] = element }}
                type="button"
                role="tab"
                aria-selected={selected}
                aria-controls="photon-cad-active-document"
                tabIndex={selected ? 0 : -1}
                disabled={coreBusy}
                onClick={() => activateTab(tab.document.projectHandle)}
                onKeyDown={(event) => handleTabKey(event, index)}
              >
                <span>{tab.document.displayName}</span>
                {tab.document.snapshot.dirty ? <span className="photon-cad-desktop__dirty" aria-label="Unsaved changes">*</span> : null}
                {!tab.metadataCurrent ? <span className="photon-cad-desktop__stale">Metadata pending</span> : null}
              </button>
              <button type="button" className="photon-cad-desktop__tab-close" aria-label={`Close ${tab.document.displayName}`} disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null} onClick={() => void closeDocument(tab.document.projectHandle)}>x</button>
            </div>
          )
        })}
      </div>

      <div
        id="photon-cad-active-document"
        className="photon-cad-desktop__content"
        role="tabpanel"
        aria-labelledby={activeTabIndex >= 0 ? `photon-cad-document-tab-${activeTabIndex}` : undefined}
      >
        {activeTab ? (
          <PhotonCadWorkspace
            controller={photonCadControllerForAttachment(
              activeTab.runtimeAttached,
              trackingControllerRef.current ?? undefined,
              activeTab.metadataCurrent,
              runtimeProjectSyncAvailable,
            )}
            runtime={runtimeDescription}
            project={activeTab.document.snapshot}
            bom={activeTab.document.bom}
            bomDigest={activeTab.document.bomDigest}
            previewSurface={previewSurface}
            commercialController={activeTab.metadataCurrent && commercialReady ? commercialAdapterRef.current : undefined}
            commercialPreviewSurface={wrappedCommercialPreview}
            destination={destination}
            onChooseDestination={onChooseDestination}
            printer={printer}
            onChoosePrinter={onChoosePrinter}
            evidenceMode="runtime"
          />
        ) : (
          <div className="photon-cad-desktop__empty">
            <strong>No project is open</strong>
            <p>{projectActionsAvailable
              ? 'Choose New or Open. Project locations remain opaque host-owned handles; the renderer never receives a filesystem path.'
              : 'Native CAD project storage is unavailable while its safety adapter is being finalized. Project locations will remain opaque host-owned handles; the renderer will never receive a filesystem path.'}</p>
          </div>
        )}
      </div>

      {recent.length ? (
        <aside className="photon-cad-desktop__recent" aria-label="Recently closed CAD projects">
          <strong>Recently closed</strong>
          <ul>{recent.map((item) => <li key={item.reopenHandle}><span>{item.displayName}</span><button type="button" disabled={!projectActionsAvailable || Boolean(busy) || coreBusy || newProjectDraft !== null} onClick={() => void reopenProject(item)}>Reopen</button></li>)}</ul>
        </aside>
      ) : null}
    </section>
  )
}

function normalizeInitialDocuments(values: readonly PhotonCadProjectDocument[]) {
  const documents: ProjectTab[] = []
  const handles = new Set<string>()
  for (const value of values.slice(0, PHOTON_CAD_PROJECT_LIMITS.openDocuments)) {
    const document = normalizePhotonCadProjectDocument(value)
    if (!document || handles.has(document.projectHandle)) continue
    handles.add(document.projectHandle)
    documents.push({ document, metadataCurrent: true, runtimeAttached: false })
  }
  return documents
}

function commercialContextOf(document: PhotonCadProjectDocument): PhotonCadCommercialContext {
  return {
    sessionId: document.snapshot.sessionId,
    projectId: document.snapshot.projectId,
    projectRevision: document.snapshot.revision,
    bomDigest: document.bomDigest,
  }
}

function commercialContextKey(context: PhotonCadCommercialContext) {
  return `${context.sessionId}\u0000${context.projectId}\u0000${context.projectRevision}\u0000${context.bomDigest.toLowerCase()}`
}

function commercialFailureStatus(reason: string): 'rejected' | 'unavailable' {
  return /unavailable|closed/u.test(reason) ? 'unavailable' : 'rejected'
}

export function projectStatusText(reason: string) {
  const messages: Record<string, string> = {
    'no-project-open': 'No CAD project is open.',
    'project-storage-unavailable': 'CAD project storage is unavailable while its native safety adapter is being finalized.',
    'project-ready': 'The active project metadata is current.',
    'project-runtime-commit-unavailable': 'This new project is saved at revision 0. Browse the connected catalog; modeling remains disabled until accepted results can be committed to the project file.',
    'project-runtime-hydration-unavailable': 'This stored project is open for viewing and saving. Browse the connected catalog; editing remains disabled until trusted runtime hydration is available.',
    'choosing-project-workspace': 'Choose an opaque workspace for the new project.',
    'choosing-project': 'Choose a project through the desktop picker.',
    'choosing-save-as-workspace': 'Choose a new opaque workspace for this project.',
    'creating-project': 'Creating a new host-owned CAD project session.',
    'opening-project': 'Opening the selected CAD project.',
    'saving-project': 'Saving the exact current project revision atomically.',
    'saving-project-as': 'Saving the exact current revision to the selected workspace atomically.',
    'refreshing-project-metadata': 'Refreshing authoritative project metadata.',
    'closing-project': 'Closing the exact active project session.',
    'reopening-project': 'Reopening the selected host-owned project reference.',
    'project-closed': 'The project was closed. Reopen metadata is retained without a path.',
    'entering-project-details': 'Enter a bounded project name before choosing the native project file.',
    'invalid-project-details': 'Enter a valid project name before continuing.',
    'project-details-dialog-unavailable': 'The project-name dialog could not open. No project request was sent.',
    'new-project-cancelled': 'New project creation was cancelled.',
    'native-picker-cancelled': 'The native project picker was cancelled. No project was changed.',
    'windows-native-picker-unavailable': 'The native project picker is unavailable. No project was changed.',
    'windows-native-picker-failed': 'The native project picker could not open. No project was changed.',
    'desktop-host-unavailable': 'The native desktop project bridge is unavailable. Reload the Hermes desktop window and try again.',
    'client-closed': 'The CAD project bridge was closed during a desktop reload. Reload the Hermes desktop window and try again.',
    'target-exists-overwrite-confirmation-required': 'That project file already exists. Choose Open to use it, or choose a different name for New.',
    'overwrite-confirmation-declined': 'Replacement was cancelled or not confirmed. The existing project file was not changed.',
    'project-action-rejected': 'The project request was rejected without changing the existing project file.',
    'project-host-failure': 'The native project host failed safely without changing the existing project file.',
    'project-create-failed': 'The native project request failed before a verified project could be opened. No project was accepted.',
    'project-open-failed': 'The selected project could not be opened safely.',
    'project-save-failed': 'The current project could not be saved safely.',
    'project-save-as-failed': 'The project could not be saved to the selected destination safely.',
    'project-close-failed': 'The project could not be closed safely and remains open.',
    'project-reopen-failed': 'The project could not be reopened safely.',
    'close-cancelled-unsaved-changes-preserved': 'Close was cancelled. Unsaved changes remain open.',
    'open-document-limit-reached': 'The 32-project tab limit is reached. Close a project before opening another.',
    'project-metadata-revision-mismatch': 'Project metadata advanced unexpectedly. Save and commercial output remain disabled until refreshed.',
    'project-metadata-refresh-failed': 'Project metadata could not be refreshed. Save and commercial output remain disabled.',
    'project-metadata-untracked': 'The accepted project revision is not associated with an open tab.',
  }
  return messages[reason] ?? `The project action failed (${reason || 'reason-unavailable'}).`
}

export function schedulePhotonCadLifecycleCleanup(
  currentGeneration: () => number,
  scheduledGeneration: number,
  cleanup: () => void,
  schedule: (callback: () => void) => void = queueMicrotask,
) {
  schedule(() => {
    if (currentGeneration() === scheduledGeneration) cleanup()
  })
}

export function normalizePhotonCadNewProjectDetails(value: NewProjectDraft | null | undefined): NewProjectDraft | null {
  if (!value || value.units !== 'millimeter' && value.units !== 'inch') return null
  const title = value.title.trim()
  if (!isPhotonCadSafeText(title, PHOTON_CAD_PROJECT_LIMITS.title, true)) return null
  return { title, units: value.units }
}

type PhotonCadDialogSurface = {
  readonly open: boolean
  showModal?: () => void
  close?: () => void
}

export function openPhotonCadNewProjectDialog(dialog: PhotonCadDialogSurface): boolean {
  try {
    if (photonCadDialogIsOpen(dialog)) return true
    if (typeof dialog.showModal !== 'function') return false
    dialog.showModal()
    return photonCadDialogIsOpen(dialog)
  } catch {
    return false
  }
}

export function closePhotonCadNewProjectDialog(dialog: PhotonCadDialogSurface): boolean {
  try {
    if (!photonCadDialogIsOpen(dialog)) return true
    if (typeof dialog.close !== 'function') return false
    dialog.close()
    return !photonCadDialogIsOpen(dialog)
  } catch {
    return false
  }
}

function photonCadDialogIsOpen(dialog: PhotonCadDialogSurface) {
  return Reflect.get(dialog, 'open') === true
}

type PhotonCadNewProjectDialogActionBase = {
  dismiss: () => void
  setStatus: (status: string) => void
}

type PhotonCadNewProjectDialogAction =
  | PhotonCadNewProjectDialogActionBase & { kind: 'cancel' }
  | PhotonCadNewProjectDialogActionBase & {
    kind: 'continue'
    draft: NewProjectDraft | null
    afterDismiss: () => Promise<void>
    requestNativePicker: (details: NewProjectDraft) => Promise<void>
  }

export async function runPhotonCadNewProjectDialogAction(
  action: PhotonCadNewProjectDialogAction,
): Promise<'cancelled' | 'invalid' | 'continued'> {
  if (action.kind === 'cancel') {
    action.dismiss()
    action.setStatus('new-project-cancelled')
    return 'cancelled'
  }

  const details = normalizePhotonCadNewProjectDetails(action.draft)
  if (!details) {
    action.setStatus('invalid-project-details')
    return 'invalid'
  }

  action.dismiss()
  await action.afterDismiss()
  await action.requestNativePicker(details)
  return 'continued'
}

function nextBrowserPaint(): Promise<void> {
  if (typeof window === 'undefined' || typeof window.requestAnimationFrame !== 'function') return Promise.resolve()
  return new Promise((resolve) => window.requestAnimationFrame(() => resolve()))
}

async function defaultConfirmDiscardChanges(document: PhotonCadProjectDocument) {
  if (typeof window === 'undefined' || typeof window.confirm !== 'function') return false
  return window.confirm(`Close ${document.displayName} and discard its unsaved changes?`)
}

let fallbackNonce = 0
function desktopWorkspaceNonce() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  fallbackNonce += 1
  return `${Date.now().toString(36)}-${fallbackNonce.toString(36)}`
}

export const PHOTON_CAD_DESKTOP_COMPOSITION_VERSION = PHOTON_CAD_CONTRACT_VERSION
export const PHOTON_CAD_DESKTOP_COMMERCIAL_VERSION = PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION
