import { describe, expect, it } from 'vitest'
import {
  DesktopPhotonCadStepImportClient,
  normalizePhotonCadStepImportResult,
} from './DesktopPhotonCadStepImportClient'
import type { PhotonCadProjectWebViewBridge } from './DesktopPhotonCadProjectClient'

const digest = `sha256:${'a'.repeat(64)}`

class FakeBridge implements PhotonCadProjectWebViewBridge {
  public readonly messages: unknown[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  public postMessage = (message: unknown) => { this.messages.push(message) }
  public addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.add(listener) }
  public removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.delete(listener) }
  public emit(data: unknown) { this.listeners.forEach((listener) => listener({ data } as MessageEvent)) }
}

function document() {
  return {
    contractVersion: 1,
    workspaceHandle: `cad-workspace:${'w'.repeat(32)}`,
    projectHandle: `cad-project:${'p'.repeat(32)}`,
    displayName: 'Imported bracket',
    snapshot: {
      contractVersion: 1,
      sessionId: 'session:imported',
      projectId: 'project:imported',
      revision: 2,
      title: 'Imported bracket',
      units: 'millimeter',
      mode: 'canonical',
      entities: [
        { id: 'imported-part', parentId: null, kind: 'part', name: 'Imported bracket', visible: true, suppressed: false },
        { id: 'imported-part.occ', parentId: null, kind: 'occurrence', name: 'IMPORT-AAAAAAAAAAAA', visible: true, suppressed: false },
      ],
      occurrences: [{
        occurrenceId: 'imported-part.occ', parentOccurrenceId: null, sourceEntityId: 'imported-part',
        partNumber: 'IMPORT-AAAAAAAAAAAA', transform: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
      }],
      operations: [],
      issues: [],
      dirty: false,
    },
    contentDigest: digest,
    lastSavedContentDigest: digest,
    bomDigest: `sha256:${'b'.repeat(64)}`,
    bom: [{ partNumber: 'IMPORT-AAAAAAAAAAAA', description: 'Imported bracket', quantity: 1, unit: 'each', sourceEntityId: 'imported-part' }],
    openedAtUtc: '2026-08-11T12:00:00Z',
    lastSavedRevision: 2,
  }
}

function openedFrame(requestId = 'step-import:1') {
  return {
    type: 'photonCad.step.import.result',
    version: 1,
    value: { contractVersion: 1, requestId, status: 'opened', reason: 'step-import-committed', document: document() },
  }
}

describe('DesktopPhotonCadStepImportClient', () => {
  it('sends one pathless import intent and accepts the exact canonical project result', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadStepImportClient({ getBridge: () => bridge })
    const pending = client.importStep('step-import:1')
    expect(bridge.messages).toEqual([{
      type: 'photonCad.step.import', version: 1, contractVersion: 1, requestId: 'step-import:1',
    }])
    bridge.emit(openedFrame())
    await expect(pending).resolves.toMatchObject({
      status: 'opened', reason: 'step-import-committed', document: { snapshot: { revision: 2 } },
    })
    client.close()
  })

  it('rejects any host response that leaks a native source path or adds unknown fields', () => {
    const value = openedFrame().value as Record<string, unknown>
    value.sourcePath = 'C:\\private\\bracket.step'
    expect(normalizePhotonCadStepImportResult(value)).toBeNull()
  })

  it('cancels only the exact active request and removes its listener', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadStepImportClient({ getBridge: () => bridge })
    const pending = client.importStep('step-import:cancel')
    client.cancelPending()
    await expect(pending).rejects.toThrow('cancelled')
    expect(bridge.messages.at(-1)).toEqual({
      type: 'photonCad.cancel', version: 1, contractVersion: 1,
      requestId: 'step-import:cancel:cancel', targetRequestId: 'step-import:cancel',
    })
    bridge.emit(openedFrame('step-import:cancel'))
    client.close()
  })

  it('reports an honest unavailable result when no desktop bridge exists', async () => {
    const client = new DesktopPhotonCadStepImportClient({ getBridge: () => null })
    await expect(client.importStep('step-import:offline')).resolves.toEqual({
      contractVersion: 1, requestId: 'step-import:offline', status: 'unavailable', reason: 'desktop-host-unavailable',
    })
  })
})
