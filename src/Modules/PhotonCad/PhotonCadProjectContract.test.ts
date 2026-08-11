import { describe, expect, it } from 'vitest'
import type { PhotonCadProjectSnapshot } from './PhotonCadContract'
import {
  PHOTON_CAD_PROJECT_INVARIANT,
  isPhotonCadProjectHandle,
  isPhotonCadWorkspaceHandle,
  validatePhotonCadProjectCloseRequest,
  validatePhotonCadProjectCreateRequest,
  validatePhotonCadProjectDocumentMetadata,
  validatePhotonCadSaveReceiptBinding,
  type PhotonCadProjectDocument,
  type PhotonCadProjectSaveAsRequest,
  type PhotonCadProjectSaveRequest,
  type PhotonCadProjectSaveResult,
} from './PhotonCadProjectContract'

const digestA = `sha256:${'a'.repeat(64)}`
const digestB = `sha256:${'b'.repeat(64)}`
const workspaceHandle = `cad-workspace:${'w'.repeat(32)}`
const projectHandle = `cad-project:${'p'.repeat(32)}`

function snapshot(dirty = false): PhotonCadProjectSnapshot {
  return {
    contractVersion: 1,
    sessionId: 'session:1',
    projectId: 'project:gearbox',
    revision: 4,
    title: 'Gearbox',
    units: 'millimeter',
    mode: 'canonical',
    entities: [{ id: 'part:shaft', parentId: null, kind: 'part', name: 'Shaft', visible: true, suppressed: false }],
    operations: [],
    issues: [],
    dirty,
  }
}

function document(dirty = false): PhotonCadProjectDocument {
  return {
    contractVersion: 1,
    workspaceHandle,
    projectHandle,
    displayName: 'Gearbox',
    snapshot: snapshot(dirty),
    contentDigest: digestA,
    lastSavedContentDigest: dirty ? digestB : digestA,
    bomDigest: digestB,
    bom: [{ partNumber: 'SHAFT-001', description: 'Input shaft', quantity: 2, unit: 'each', sourceEntityId: 'part:shaft' }],
    openedAtUtc: '2026-08-10T18:00:00Z',
    lastSavedRevision: dirty ? 3 : 4,
  }
}

describe('PhotonCadProjectContract', () => {
  it('accepts only opaque workspace/project handles and display-safe titles', () => {
    expect(isPhotonCadWorkspaceHandle(workspaceHandle)).toBe(true)
    expect(isPhotonCadProjectHandle(projectHandle)).toBe(true)
    expect(isPhotonCadWorkspaceHandle('C:\\Users\\Bill\\gearbox')).toBe(false)
    expect(validatePhotonCadProjectCreateRequest({ contractVersion: 1, requestId: 'create:1', workspaceHandle, title: 'Gearbox', units: 'millimeter' })).toBe(true)
    expect(validatePhotonCadProjectCreateRequest({ contractVersion: 1, requestId: 'create:2', workspaceHandle, title: 'C:\\Gearbox', units: 'millimeter' })).toBe(false)
  })

  it('validates bounded clean and dirty project metadata without raw paths', () => {
    expect(validatePhotonCadProjectDocumentMetadata(document())).toBe(true)
    expect(validatePhotonCadProjectDocumentMetadata(document(true))).toBe(true)
    expect(validatePhotonCadProjectDocumentMetadata({ ...document(), displayName: '\\\\server\\share' })).toBe(false)
    expect(validatePhotonCadProjectDocumentMetadata({ ...document(), lastSavedRevision: 3 })).toBe(false)
    expect(validatePhotonCadProjectDocumentMetadata({ ...document(true), bom: [...document(true).bom, document(true).bom[0]] })).toBe(false)
  })

  it('accepts a save only with an exact atomic revision-and-digest receipt', () => {
    const request: PhotonCadProjectSaveRequest = {
      contractVersion: 1,
      requestId: 'save:1',
      projectHandle,
      sessionId: 'session:1',
      projectId: 'project:gearbox',
      baseRevision: 4,
      contentDigest: digestA,
    }
    const result: PhotonCadProjectSaveResult = {
      contractVersion: 1,
      requestId: request.requestId,
      status: 'saved',
      reason: 'saved',
      receipt: {
        receiptHandle: `cad-save-receipt:${'s'.repeat(32)}`,
        sourceProjectHandle: projectHandle,
        projectHandle,
        sessionId: request.sessionId,
        projectId: request.projectId,
        baseRevision: 4,
        savedRevision: 4,
        contentDigest: digestA,
        savedAtUtc: '2026-08-10T18:01:00Z',
        atomic: true,
      },
      document: document(),
    }
    expect(validatePhotonCadSaveReceiptBinding(request, result)).toBe(true)
    expect(validatePhotonCadSaveReceiptBinding(request, { ...result, receipt: { ...result.receipt!, contentDigest: digestB } })).toBe(false)
    expect(validatePhotonCadSaveReceiptBinding(request, { ...result, receipt: { ...result.receipt!, savedRevision: 5 } })).toBe(false)
    expect(validatePhotonCadSaveReceiptBinding(request, { ...result, receipt: { ...result.receipt!, atomic: false as true } })).toBe(false)
    expect(PHOTON_CAD_PROJECT_INVARIANT).toMatch(/atomic receipt.*exact session.*base revision.*content digest/i)
  })

  it('binds save-as to both the source project and selected destination workspace', () => {
    const destinationWorkspaceHandle = `cad-workspace:${'z'.repeat(32)}`
    const resultProjectHandle = `cad-project:${'q'.repeat(32)}`
    const request: PhotonCadProjectSaveAsRequest = {
      contractVersion: 1, requestId: 'save-as:1', sourceProjectHandle: projectHandle, destinationWorkspaceHandle,
      sessionId: 'session:1', projectId: 'project:gearbox', baseRevision: 4, contentDigest: digestA,
    }
    const result: PhotonCadProjectSaveResult = {
      contractVersion: 1, requestId: request.requestId, status: 'saved', reason: 'saved',
      receipt: {
        receiptHandle: `cad-save-receipt:${'s'.repeat(32)}`, sourceProjectHandle: projectHandle, projectHandle: resultProjectHandle,
        sessionId: request.sessionId, projectId: request.projectId, baseRevision: 4, savedRevision: 4, contentDigest: digestA,
        savedAtUtc: '2026-08-10T18:01:00Z', atomic: true,
      },
      document: { ...document(), workspaceHandle: destinationWorkspaceHandle, projectHandle: resultProjectHandle },
    }
    expect(validatePhotonCadSaveReceiptBinding(request, result)).toBe(true)
    expect(validatePhotonCadSaveReceiptBinding(request, { ...result, document: { ...result.document!, workspaceHandle } })).toBe(false)
  })

  it('requires the renderer to state whether unsaved changes are being discarded', () => {
    const base = {
      contractVersion: 1 as const,
      requestId: 'close:1',
      projectHandle,
      sessionId: 'session:1',
      projectId: 'project:gearbox',
      revision: 4,
      lastSavedRevision: 4,
      contentDigest: digestA,
      lastSavedContentDigest: digestA,
    }
    expect(validatePhotonCadProjectCloseRequest({ ...base, discardUnsavedChanges: false })).toBe(true)
    expect(validatePhotonCadProjectCloseRequest({ ...base, discardUnsavedChanges: undefined as unknown as boolean })).toBe(false)
  })
})
