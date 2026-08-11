import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DesktopPhotonCadProjectClient,
  normalizePhotonCadProjectDocument,
  normalizePhotonCadProjectHostFrame,
  type PhotonCadProjectWebViewBridge,
} from './DesktopPhotonCadProjectClient'
import type { PhotonCadProjectDocument, PhotonCadProjectOpenRequest, PhotonCadProjectSaveRequest } from './PhotonCadProjectContract'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const workspaceHandle = `cad-workspace:${'w'.repeat(32)}`
const projectHandle = `cad-project:${'p'.repeat(32)}`

class FakeBridge implements PhotonCadProjectWebViewBridge {
  public readonly messages: unknown[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  public postMessage = (message: unknown) => { this.messages.push(message) }
  public addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.add(listener) }
  public removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.delete(listener) }
  public emit(data: unknown) { this.listeners.forEach((listener) => listener({ data } as MessageEvent)) }
}

function document(dirty = false): PhotonCadProjectDocument {
  return {
    contractVersion: 1,
    workspaceHandle,
    projectHandle,
    displayName: 'Gearbox',
    snapshot: {
      contractVersion: 1, sessionId: 'session:1', projectId: 'project:gearbox', revision: 4, title: 'Gearbox', units: 'millimeter', mode: 'canonical',
      entities: [{ id: 'part:shaft', parentId: null, kind: 'part', name: 'Shaft', visible: true, suppressed: false }],
      occurrences: [],
      operations: [], issues: [], dirty,
    },
    contentDigest: digestA,
    lastSavedContentDigest: dirty ? digestB : digestA,
    bomDigest: digestB,
    bom: [{ partNumber: 'SHAFT-001', description: 'Input shaft', quantity: 2, unit: 'each', sourceEntityId: 'part:shaft' }],
    openedAtUtc: '2026-08-10T18:00:00Z',
    lastSavedRevision: dirty ? 3 : 4,
  }
}

function openRequest(requestId = 'open:1'): PhotonCadProjectOpenRequest {
  return { contractVersion: 1, requestId, workspaceHandle }
}

function openFrame(requestId = 'open:1', sourceHandle = workspaceHandle, value = document()) {
  return {
    type: 'photonCad.project.open.result', version: 1,
    value: { contractVersion: 1, requestId, status: 'opened', reason: 'opened', sourceHandle, document: value },
  }
}

function saveRequest(requestId = 'save:1'): PhotonCadProjectSaveRequest {
  return { contractVersion: 1, requestId, projectHandle, sessionId: 'session:1', projectId: 'project:gearbox', baseRevision: 4, contentDigest: digestA }
}

function saveFrame(requestId = 'save:1', overrides: Record<string, unknown> = {}) {
  return {
    type: 'photonCad.project.save.result', version: 1,
    value: {
      contractVersion: 1,
      requestId,
      status: 'saved',
      reason: 'saved',
      receipt: {
        receiptHandle: `cad-save-receipt:${'s'.repeat(32)}`,
        sourceProjectHandle: projectHandle,
        projectHandle,
        sessionId: 'session:1',
        projectId: 'project:gearbox',
        baseRevision: 4,
        savedRevision: 4,
        contentDigest: digestA,
        savedAtUtc: '2026-08-10T18:01:00Z',
        atomic: true,
        ...overrides,
      },
      document: document(),
    },
  }
}

afterEach(() => vi.useRealTimers())

describe('Photon CAD project protocol normalization', () => {
  it('rejects raw paths, malformed snapshots, and non-atomic save receipts', () => {
    expect(normalizePhotonCadProjectDocument({ ...document(), displayName: 'C:\\Gearbox\\assembly' })).toBeNull()
    const malformed = document() as unknown as { snapshot: { entities: Array<Record<string, unknown>> } }
    malformed.snapshot.entities = [{ id: 'part:shaft', kind: 'part', name: 'Shaft', visible: true, suppressed: false }]
    expect(normalizePhotonCadProjectDocument(malformed)).toBeNull()
    expect(normalizePhotonCadProjectHostFrame(saveFrame('save:1', { atomic: false }))).toBeNull()
  })

  it('projects explicit occurrence hierarchy and transform data through persisted documents', () => {
    const value = document()
    value.snapshot.entities.push({
      id: 'occurrence:shaft', parentId: null, kind: 'occurrence', name: 'SHAFT-001', visible: true, suppressed: false,
    })
    value.snapshot.occurrences = [{
      occurrenceId: 'occurrence:shaft', parentOccurrenceId: null, partNumber: 'SHAFT-001', sourceEntityId: 'part:shaft',
      transform: [0, -1, 0, 125, 1, 0, 0, -30, 0, 0, 1, 8, 0, 0, 0, 1],
    }]
    const normalized = normalizePhotonCadProjectDocument(value)
    expect(normalized?.snapshot.occurrences).toEqual(value.snapshot.occurrences)
    expect(normalized?.snapshot.entities.at(-1)?.id).toBe('occurrence:shaft')

    const mismatched = structuredClone(value)
    mismatched.snapshot.entities.at(-1)!.name = 'WRONG'
    expect(normalizePhotonCadProjectDocument(mismatched)).toBeNull()
  })

  it('rejects picker results that expose a filesystem path instead of an opaque selection', () => {
    expect(normalizePhotonCadProjectHostFrame({
      type: 'photonCad.project.picker.result', version: 1,
      value: { contractVersion: 1, requestId: 'picker:1', status: 'selected', reason: 'selected', workspaceHandle, label: 'C:\\Projects\\Gearbox' },
    })).toBeNull()
  })
})

describe('DesktopPhotonCadProjectClient', () => {
  it('sends the modal project name as a pathless native-picker suggestion', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => bridge })
    const pending = client.chooseWorkspace({ contractVersion: 1, requestId: 'picker:prefill', purpose: 'new', suggestedName: 'Gearbox Input' })
    expect(bridge.messages).toContainEqual({
      type: 'photonCad.project.picker', version: 1, contractVersion: 1,
      requestId: 'picker:prefill', purpose: 'new', suggestedName: 'Gearbox Input',
    })
    bridge.emit({
      type: 'photonCad.project.picker.result', version: 1,
      value: { contractVersion: 1, requestId: 'picker:prefill', status: 'cancelled', reason: 'native-picker-cancelled' },
    })
    await expect(pending).resolves.toMatchObject({ status: 'cancelled' })
    client.close()
  })

  it('reports honest unavailable lifecycle results without an HTTP fallback', async () => {
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => null })
    await expect(client.openProject(openRequest())).resolves.toEqual({ contractVersion: 1, requestId: 'open:1', status: 'unavailable', reason: 'desktop-host-unavailable' })
    await expect(client.saveProject(saveRequest())).resolves.toEqual({ contractVersion: 1, requestId: 'save:1', status: 'unavailable', reason: 'desktop-host-unavailable' })
  })

  it('makes open latest-wins, cancels the older request, and ignores its late frame', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => bridge, createRequestId: (kind, sequence) => `${kind}:${sequence}` })
    const first = client.openProject(openRequest('open:1'))
    const firstRejection = expect(first).rejects.toThrow('superseded')
    const second = client.openProject(openRequest('open:2'))
    await firstRejection
    expect(bridge.messages).toContainEqual({ type: 'photonCad.project.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'open:1', operation: 'open' })
    bridge.emit(openFrame('open:1'))
    bridge.emit(openFrame('open:2'))
    await expect(second).resolves.toMatchObject({ status: 'opened', document: { projectHandle } })
    client.close()
  })

  it('ignores foreign open handles and accepts only the exact opaque source binding', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => bridge })
    let settled = false
    const pending = client.openProject(openRequest()).then((result) => { settled = true; return result })
    bridge.emit(openFrame('open:1', `cad-workspace:${'x'.repeat(32)}`))
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(openFrame())
    await expect(pending).resolves.toMatchObject({ status: 'opened', sourceHandle: workspaceHandle })
    client.close()
  })

  it('accepts save only after an exact atomic receipt and serializes other writes', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => bridge })
    const pending = client.saveProject(saveRequest())
    await expect(client.closeProject({
      contractVersion: 1, requestId: 'close:1', projectHandle, sessionId: 'session:1', projectId: 'project:gearbox', revision: 4, lastSavedRevision: 4, contentDigest: digestA, lastSavedContentDigest: digestA, discardUnsavedChanges: false,
    })).rejects.toThrow('transition is active')
    bridge.emit(saveFrame('save:1', { contentDigest: digestB }))
    let settled = false
    void pending.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(saveFrame())
    await expect(pending).resolves.toMatchObject({ status: 'saved', receipt: { atomic: true, baseRevision: 4, savedRevision: 4, contentDigest: digestA } })
    client.close()
  })

  it('binds dirty-close reopen metadata to the last saved digest, never discarded content', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({ getBridge: () => bridge })
    const request = {
      contractVersion: 1 as const,
      requestId: 'close:dirty',
      projectHandle,
      sessionId: 'session:1',
      projectId: 'project:gearbox',
      revision: 4,
      lastSavedRevision: 3,
      contentDigest: digestA,
      lastSavedContentDigest: digestB,
      discardUnsavedChanges: true,
    }
    const pending = client.closeProject(request)
    const frame = (contentDigest: string) => ({
      type: 'photonCad.project.close.result', version: 1,
      value: {
        contractVersion: 1, requestId: request.requestId, status: 'closed', reason: 'closed', projectHandle,
        reopen: { reopenHandle: `cad-reopen:${'r'.repeat(32)}`, displayName: 'Gearbox', projectId: request.projectId, lastSavedRevision: 3, contentDigest, closedAtUtc: '2026-08-10T18:02:00Z' },
      },
    })
    bridge.emit(frame(digestA))
    let settled = false
    void pending.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(frame(digestB))
    await expect(pending).resolves.toMatchObject({ status: 'closed', reopen: { contentDigest: digestB } })
    client.close()
  })

  it('posts typed cancellation when bounded work times out or the client closes', async () => {
    vi.useFakeTimers()
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadProjectClient({
      getBridge: () => bridge,
      createRequestId: (kind, sequence) => `${kind}:${sequence}`,
      requestTimeoutMs: { open: 100 },
    })
    const timedOut = client.openProject(openRequest())
    const rejection = expect(timedOut).rejects.toThrow('timed out')
    await vi.advanceTimersByTimeAsync(100)
    await rejection
    expect(bridge.messages).toContainEqual({ type: 'photonCad.project.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'open:1', operation: 'open' })
    const active = client.openProject(openRequest('open:2'))
    client.close()
    await expect(active).rejects.toThrow('invalidated')
    expect(bridge.messages).toContainEqual({ type: 'photonCad.project.cancel', version: 1, requestId: 'cancel:2', targetRequestId: 'open:2', operation: 'open' })
  })
})
