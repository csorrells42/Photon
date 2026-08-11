import { describe, expect, it, vi } from 'vitest'
import {
  PhotonCadWorkspaceController,
  type PhotonCadOperationDraft,
} from './PhotonCadWorkspaceController'
import type {
  PhotonCadController,
  PhotonCadOperationRequest,
  PhotonCadOperationResult,
  PhotonCadProjectSnapshot,
  PhotonCadReleaseCommitRequest,
  PhotonCadReleaseCommitResult,
  PhotonCadReleaseReviewRequest,
  PhotonCadReleaseReviewResult,
  PhotonCadRuntimeDescription,
  PhotonCadVerificationResult,
} from './PhotonCadContract'

const digest = `sha256:${'a'.repeat(64)}`

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: Error) => void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail })
  return { promise, resolve, reject }
}

function snapshot(revision = 3, sessionId = 'session:1', projectId = 'project:1'): PhotonCadProjectSnapshot {
  return {
    contractVersion: 1,
    sessionId,
    projectId,
    revision,
    title: 'Gearbox',
    units: 'millimeter',
    mode: 'canonical',
    entities: [{ id: 'part:shaft', parentId: null, kind: 'part', name: 'Shaft', visible: true, suppressed: false }],
    occurrences: [],
    operations: [],
    issues: [],
    dirty: false,
  }
}

function runtime(catalogRevision = 'catalog:1'): PhotonCadRuntimeDescription {
  return {
    contractVersion: 1,
    status: 'available',
    reason: 'ready',
    geometryBundleId: 'geometry:1',
    catalog: {
      contractVersion: 1,
      catalogRevision,
      generatedAtUtc: '2026-08-10T18:00:00Z',
      capabilities: [{
        id: 'build123d.extrude', backend: 'geometry', category: 'Features', title: 'Extrude', description: '', operation: 'create',
        parameters: [], source: { package: 'build123d', version: '0.10.0', digest, license: 'Apache-2.0' }, previewSupported: true, experimental: false,
      }],
      coverage: { discovered: 1, available: 1, unavailable: 0, unavailableReasons: [] },
    },
  }
}

function draft(): PhotonCadOperationDraft {
  return { capabilityId: 'build123d.extrude', inputs: { length: 25.4 }, targetEntityIds: ['part:shaft'] }
}

function previewResult(request: PhotonCadOperationRequest, previewId: string): PhotonCadOperationResult {
  return {
    contractVersion: 1,
    requestId: request.requestId,
    projectId: request.projectId,
    baseRevision: request.baseRevision,
    resultingRevision: request.baseRevision,
    status: 'accepted',
    stale: false,
    reason: 'accepted',
    preview: {
      previewId,
      projectId: request.projectId,
      revision: request.baseRevision,
      contentDigest: digest,
      units: 'millimeter',
      bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 10, y: 10, z: 10 } },
      entityCount: 1,
    },
    issues: [],
  }
}

function appliedResult(request: PhotonCadOperationRequest): PhotonCadOperationResult {
  return {
    contractVersion: 1,
    requestId: request.requestId,
    projectId: request.projectId,
    baseRevision: request.baseRevision,
    resultingRevision: request.baseRevision + 1,
    status: 'accepted',
    stale: false,
    reason: 'accepted',
    snapshot: snapshot(request.baseRevision + 1, request.sessionId, request.projectId),
    issues: [],
  }
}

function readyReview(request: PhotonCadReleaseReviewRequest): PhotonCadReleaseReviewResult {
  return {
    contractVersion: 1,
    requestId: request.requestId,
    projectId: request.projectId,
    revision: request.revision,
    status: 'ready',
    reason: 'ready',
    reviewHandle: `cad-review:${'r'.repeat(32)}`,
    packageFingerprint: digest,
    expiresAtUtc: '2026-08-10T19:00:00Z',
    files: [{ role: 'assembly-step', relativePath: 'assembly/gearbox.step' }],
    issues: [],
  }
}

function fakeController(overrides: Partial<PhotonCadController> = {}) {
  const controller: PhotonCadController & { cancelPending: ReturnType<typeof vi.fn>; close: ReturnType<typeof vi.fn> } = {
    describe: vi.fn(async () => runtime()),
    execute: vi.fn(async (request) => request.mode === 'suggest' ? previewResult(request, 'preview:default') : appliedResult(request)),
    verify: vi.fn(async (request): Promise<PhotonCadVerificationResult> => ({ contractVersion: 1, requestId: request.requestId, projectId: request.projectId, revision: request.revision, status: 'passed', stale: false, issues: [], measuredAtUtc: '2026-08-10T18:00:00Z' })),
    reviewRelease: vi.fn(async (request) => readyReview(request)),
    commitRelease: vi.fn(async (request): Promise<PhotonCadReleaseCommitResult> => ({ contractVersion: 1, requestId: request.requestId, status: 'committed', reason: 'committed', packageId: 'package:1', packageFingerprint: request.packageFingerprint })),
    discardRelease: vi.fn(),
    cancelPending: vi.fn(),
    close: vi.fn(),
    ...overrides,
  }
  return controller
}

function workspace(controller = fakeController(), initial = snapshot()) {
  return new PhotonCadWorkspaceController({
    controller,
    initialSnapshot: initial,
    createRequestId: (kind, sequence) => `${kind}:${sequence}`,
    now: () => Date.parse('2026-08-10T18:00:00Z'),
  })
}

describe('PhotonCadWorkspaceController', () => {
  it('publishes only the latest runtime description', async () => {
    const first = deferred<PhotonCadRuntimeDescription>()
    const second = deferred<PhotonCadRuntimeDescription>()
    const controller = fakeController({ describe: vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise) })
    const subject = workspace(controller)
    const oldRequest = subject.refreshRuntime()
    const newRequest = subject.refreshRuntime()
    second.resolve(runtime('catalog:new'))
    await newRequest
    first.resolve(runtime('catalog:old'))
    await oldRequest
    expect(subject.state.runtime.catalog?.catalogRevision).toBe('catalog:new')
    expect(subject.state.busy.describe).toBe(false)
  })

  it('uses latest-wins preview semantics and never publishes an older receipt', async () => {
    const first = deferred<PhotonCadOperationResult>()
    const second = deferred<PhotonCadOperationResult>()
    const requests: PhotonCadOperationRequest[] = []
    const controller = fakeController({
      execute: vi.fn((request: PhotonCadOperationRequest) => {
        requests.push(request)
        return requests.length === 1 ? first.promise : second.promise
      }),
    })
    const subject = workspace(controller)
    await subject.refreshRuntime()
    const oldPreview = subject.previewOperation(draft())
    const newPreview = subject.previewOperation(draft())
    second.resolve(previewResult(requests[1], 'preview:new'))
    await newPreview
    first.resolve(previewResult(requests[0], 'preview:old'))
    await oldPreview
    expect(subject.state.preview?.previewId).toBe('preview:new')
    expect(subject.state.busy.preview).toBe(false)
  })

  it('allows one mutating operation and accepts only its revision-bound snapshot', async () => {
    const running = deferred<PhotonCadOperationResult>()
    let request!: PhotonCadOperationRequest
    const controller = fakeController({ execute: vi.fn((value: PhotonCadOperationRequest) => { request = value; return running.promise }) })
    const subject = workspace(controller)
    await subject.refreshRuntime()
    const first = subject.applyOperation(draft())
    await expect(subject.applyOperation(draft())).resolves.toBeNull()
    expect(controller.execute).toHaveBeenCalledTimes(1)
    running.resolve(appliedResult(request))
    await expect(first).resolves.toMatchObject({ resultingRevision: 4 })
    expect(subject.state.snapshot?.revision).toBe(4)
    expect(subject.state.busy.exclusive).toBeNull()
  })

  it('ignores a foreign mutation result without changing the canonical snapshot', async () => {
    const controller = fakeController({
      execute: vi.fn(async (request: PhotonCadOperationRequest) => ({ ...appliedResult(request), projectId: 'project:foreign' })),
    })
    const subject = workspace(controller)
    await subject.refreshRuntime()
    await expect(subject.applyOperation(draft())).resolves.toBeNull()
    expect(subject.state.snapshot?.projectId).toBe('project:1')
    expect(subject.state.snapshot?.revision).toBe(3)
  })

  it('invalidates and discards a release review on any edit', async () => {
    const controller = fakeController()
    const subject = workspace(controller)
    await subject.prepareRelease(['step-ap214'], `cad-destination:${'d'.repeat(32)}`)
    expect(subject.state.releaseReview?.status).toBe('ready')
    subject.markEdited()
    expect(controller.discardRelease).toHaveBeenCalledWith(`cad-review:${'r'.repeat(32)}`)
    expect(controller.cancelPending).toHaveBeenCalled()
    expect(subject.state.releaseReview).toBeNull()
    await expect(subject.commitRelease()).resolves.toBeNull()
    expect(controller.commitRelease).not.toHaveBeenCalled()
  })

  it('commits only the stored opaque review handle and bound fingerprint', async () => {
    let commitRequest!: PhotonCadReleaseCommitRequest
    const controller = fakeController({
      commitRelease: vi.fn(async (request: PhotonCadReleaseCommitRequest): Promise<PhotonCadReleaseCommitResult> => {
        commitRequest = request
        return { contractVersion: 1, requestId: request.requestId, status: 'committed', reason: 'committed', packageId: 'package:1', packageFingerprint: request.packageFingerprint }
      }),
    })
    const subject = workspace(controller)
    await subject.prepareRelease(['step-ap214'], `cad-destination:${'d'.repeat(32)}`)
    await expect(subject.commitRelease()).resolves.toMatchObject({ status: 'committed', packageId: 'package:1' })
    expect(commitRequest).toEqual({ contractVersion: 1, requestId: 'commit:2', reviewHandle: `cad-review:${'r'.repeat(32)}`, packageFingerprint: digest })
    expect(controller.discardRelease).not.toHaveBeenCalled()
  })

  it('cancels stale work and invalidates review when session or controller changes', async () => {
    const oldController = fakeController()
    const subject = workspace(oldController)
    await subject.prepareRelease(['step-ap214'], `cad-destination:${'d'.repeat(32)}`)
    subject.setSnapshot(snapshot(3, 'session:2'))
    expect(oldController.cancelPending).toHaveBeenCalled()
    expect(oldController.discardRelease).toHaveBeenCalledWith(`cad-review:${'r'.repeat(32)}`)

    await subject.prepareRelease(['step-ap214'], `cad-destination:${'d'.repeat(32)}`)
    const replacement = fakeController()
    subject.replaceController(replacement)
    expect(oldController.cancelPending).toHaveBeenCalledTimes(2)
    expect(subject.state.runtime.status).toBe('checking')
    expect(subject.state.releaseReview).toBeNull()
  })

  it('closes the owned controller, discards review, and becomes terminal', async () => {
    const controller = fakeController()
    const subject = workspace(controller)
    await subject.prepareRelease(['step-ap214'], `cad-destination:${'d'.repeat(32)}`)
    subject.close()
    expect(controller.cancelPending).toHaveBeenCalled()
    expect(controller.close).toHaveBeenCalled()
    expect(controller.discardRelease).toHaveBeenCalledWith(`cad-review:${'r'.repeat(32)}`)
    expect(subject.state.closed).toBe(true)
    await expect(subject.refreshRuntime()).resolves.toBeNull()
  })
})
