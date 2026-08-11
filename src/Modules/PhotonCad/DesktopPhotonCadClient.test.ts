import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DesktopPhotonCadClient,
  PHOTON_CAD_DESKTOP_PROTOCOL_VERSION,
  normalizePhotonCadHostFrame,
  normalizePhotonCadProjectSnapshot,
  type PhotonCadWebViewBridge,
} from './DesktopPhotonCadClient'
import {
  PHOTON_CAD_CONTRACT_VERSION,
  type PhotonCadOperationRequest,
  type PhotonCadProjectSnapshot,
  type PhotonCadRuntimeDescription,
  type PhotonCadStepExportRequest,
} from './PhotonCadContract'

const digest = `sha256:${'a'.repeat(64)}`

class FakeBridge implements PhotonCadWebViewBridge {
  public readonly messages: unknown[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  public postMessage = (message: unknown) => { this.messages.push(message) }
  public addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.add(listener) }
  public removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listeners.delete(listener) }
  public emit(data: unknown) { this.listeners.forEach((listener) => listener({ data } as MessageEvent)) }
}

function snapshot(revision = 4): PhotonCadProjectSnapshot {
  return {
    contractVersion: 1,
    sessionId: 'session:1',
    projectId: 'project:gearbox',
    revision,
    title: 'Gearbox',
    units: 'millimeter',
    mode: 'canonical',
    entities: [{ id: 'part:shaft', parentId: null, kind: 'part', name: 'Shaft', visible: true, suppressed: false }],
    occurrences: [],
    operations: [{ id: `operation:${revision}`, capabilityId: 'build123d.extrude', label: 'Extrude', createdAtUtc: '2026-08-10T18:00:00Z', state: 'applied' }],
    issues: [],
    dirty: false,
  }
}

function assembledSnapshot(): PhotonCadProjectSnapshot {
  const value = snapshot()
  return {
    ...value,
    entities: [
      { ...value.entities[0], sourceCapabilityId: 'fixture:source' },
      { id: 'occurrence:shaft-root', parentId: null, kind: 'occurrence', name: 'SHAFT-001', visible: true, suppressed: false, sourceCapabilityId: 'fixture:source' },
      { id: 'occurrence:shaft-child', parentId: 'occurrence:shaft-root', kind: 'occurrence', name: 'SHAFT-002', visible: true, suppressed: false, sourceCapabilityId: 'fixture:source' },
    ],
    occurrences: [
      {
        occurrenceId: 'occurrence:shaft-root', parentOccurrenceId: null, partNumber: 'SHAFT-001', sourceEntityId: 'part:shaft',
        transform: [0, -1, 0, 125, 1, 0, 0, -30, 0, 0, 1, 8, 0, 0, 0, 1],
      },
      {
        occurrenceId: 'occurrence:shaft-child', parentOccurrenceId: 'occurrence:shaft-root', partNumber: 'SHAFT-002', sourceEntityId: 'part:shaft',
        transform: [1, 0, 0, 5, 0, 1, 0, 6, 0, 0, 1, 7, 0, 0, 0, 1],
      },
    ],
  }
}

function runtime(): PhotonCadRuntimeDescription {
  return {
    contractVersion: 1,
    status: 'available',
    reason: 'ready',
    geometryBundleId: 'geometry:1',
    catalog: {
      contractVersion: 1,
      catalogRevision: 'catalog:1',
      generatedAtUtc: '2026-08-10T18:00:00Z',
      capabilities: [{
        id: 'build123d.extrude', backend: 'geometry', category: 'Features', title: 'Extrude', description: '', operation: 'create',
        parameters: [], source: { package: 'build123d', version: '0.10.0', digest, license: 'Apache-2.0' }, previewSupported: true, experimental: false,
      }],
      coverage: { discovered: 1, available: 1, unavailable: 0, unavailableReasons: [] },
    },
  }
}

function operation(requestId = 'operation:1'): PhotonCadOperationRequest {
  return {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    requestId,
    sessionId: 'session:1',
    projectId: 'project:gearbox',
    baseRevision: 3,
    mode: 'scratch',
    capabilityId: 'build123d.extrude',
    inputs: { length: 25.4 },
    targetEntityIds: ['part:shaft'],
  }
}

function operationFrame(requestId = 'operation:1', projectId = 'project:gearbox', baseRevision = 3) {
  return {
    type: 'photonCad.execute.result',
    version: PHOTON_CAD_DESKTOP_PROTOCOL_VERSION,
    value: {
      contractVersion: 1,
      requestId,
      projectId,
      baseRevision,
      resultingRevision: 4,
      status: 'accepted',
      stale: false,
      reason: 'accepted',
      snapshot: snapshot(),
      issues: [],
    },
  }
}

function stepExport(requestId = 'step-export:1'): PhotonCadStepExportRequest {
  return {
    contractVersion: 1,
    requestId,
    sessionId: 'session:1',
    projectId: 'project:gearbox',
    revision: 4,
    contentDigest: digest,
    entityId: 'part:shaft',
  }
}

function stepExportFrame(overrides: Record<string, unknown> = {}) {
  return {
    type: 'photonCad.step.export.result',
    version: 1,
    value: {
      contractVersion: 1,
      requestId: 'step-export:1',
      projectId: 'project:gearbox',
      revision: 4,
      status: 'committed',
      reason: 'step-export-committed',
      entityId: 'part:shaft',
      contentDigest: `sha256:${'e'.repeat(64)}`,
      byteLength: 4_096,
      destinationLabel: 'SHAFT-001.step',
      ...overrides,
    },
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('Photon CAD desktop protocol normalization', () => {
  it('fails closed on unknown versions, untyped reasons, and oversized issue arrays', () => {
    expect(normalizePhotonCadHostFrame({ type: 'photonCad.describe.result', version: 99, requestId: 'describe:1', value: runtime() })).toBeNull()
    expect(normalizePhotonCadHostFrame({ type: 'photonCad.describe.result', version: 1, requestId: 'describe:1', value: { ...runtime(), reason: 'C:\\runtime\\python.exe --unsafe' } })).toBeNull()
    expect(normalizePhotonCadHostFrame({
      ...operationFrame(),
      value: { ...operationFrame().value, issues: Array.from({ length: 2_001 }, () => ({ code: 'issue', severity: 'error', message: 'bad', entityIds: [] })) },
    })).toBeNull()
  })

  it('requires explicit parent identity and rejects malformed project snapshots', () => {
    const value = snapshot()
    const entity = { ...value.entities[0] } as Record<string, unknown>
    delete entity.parentId
    expect(normalizePhotonCadProjectSnapshot({ ...value, entities: [entity] })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot(value)).toEqual(value)
  })

  it('preserves ordered explicit occurrences and exact rigid transforms', () => {
    const value = assembledSnapshot()
    const normalized = normalizePhotonCadProjectSnapshot(value)
    expect(normalized?.occurrences?.map((occurrence) => occurrence.occurrenceId)).toEqual([
      'occurrence:shaft-root',
      'occurrence:shaft-child',
    ])
    expect(normalized?.occurrences?.[0].transform).toEqual(value.occurrences?.[0].transform)
    expect(normalized?.entities.map((entity) => entity.id)).toEqual(value.entities.map((entity) => entity.id))
  })

  it('fails closed on malformed occurrence graphs, pseudo-entities, and transforms', () => {
    const value = assembledSnapshot()
    const occurrences = value.occurrences!
    const replaceTransform = (transform: unknown) => ({
      ...value,
      occurrences: [{ ...occurrences[0], transform }, occurrences[1]],
    })
    expect(normalizePhotonCadProjectSnapshot(replaceTransform(occurrences[0].transform.slice(0, 15)))).toBeNull()
    expect(normalizePhotonCadProjectSnapshot(replaceTransform([2, 0, 0, 125, 0, 1, 0, -30, 0, 0, 1, 8, 0, 0, 0, 1]))).toBeNull()
    expect(normalizePhotonCadProjectSnapshot(replaceTransform([-1, 0, 0, 125, 0, 1, 0, -30, 0, 0, 1, 8, 0, 0, 0, 1]))).toBeNull()
    expect(normalizePhotonCadProjectSnapshot(replaceTransform([Number.NaN, 0, 0, 125, 0, 1, 0, -30, 0, 0, 1, 8, 0, 0, 0, 1]))).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({ ...value, occurrences: occurrences.map((item) => ({ ...item, parentOccurrenceId: null })) })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({
      ...value,
      occurrences: [
        { ...occurrences[0], parentOccurrenceId: 'occurrence:shaft-child' },
        { ...occurrences[1], parentOccurrenceId: 'occurrence:shaft-root' },
      ],
    })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({ ...value, entities: value.entities.slice(0, 2) })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({ ...value, entities: value.entities.map((entity) => entity.id === 'occurrence:shaft-child' ? { ...entity, name: 'WRONG' } : entity) })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({ ...value, entities: value.entities.map((entity) => entity.id === 'occurrence:shaft-child' ? { ...entity, sourceCapabilityId: 'fixture:wrong' } : entity) })).toBeNull()
    expect(normalizePhotonCadProjectSnapshot({ ...value, occurrences: [{ ...occurrences[0], sourceEntityId: 'part:missing' }, occurrences[1]] })).toBeNull()
  })

  it('rejects absolute release paths even behind an otherwise opaque review', () => {
    expect(normalizePhotonCadHostFrame({
      type: 'photonCad.release.review.result',
      version: 1,
      value: {
        contractVersion: 1,
        requestId: 'review:1',
        projectId: 'project:gearbox',
        revision: 3,
        status: 'ready',
        reason: 'ready',
        reviewHandle: `cad-review:${'r'.repeat(32)}`,
        packageFingerprint: digest,
        expiresAtUtc: '2026-08-10T19:00:00Z',
        files: [{ role: 'assembly-step', relativePath: 'C:\\exports\\gearbox.step' }],
        issues: [],
      },
    })).toBeNull()
  })

  it('normalizes only an exact pathless committed STEP receipt', () => {
    expect(normalizePhotonCadHostFrame(stepExportFrame())).toEqual({
      type: 'step-export',
      value: stepExportFrame().value,
    })
    for (const hostile of [
      stepExportFrame({ destinationLabel: 'C:\\exports\\shaft.step' }),
      stepExportFrame({ destinationLabel: 'shaft.step:secret' }),
      stepExportFrame({ byteLength: 0 }),
      stepExportFrame({ contentDigest: 'not-a-digest' }),
      { ...stepExportFrame(), value: { ...stepExportFrame().value, path: 'C:\\exports\\shaft.step' } },
      { ...stepExportFrame(), value: { ...stepExportFrame().value, entityId: undefined } },
    ]) expect(normalizePhotonCadHostFrame(hostile)).toBeNull()
    expect(normalizePhotonCadHostFrame({
      type: 'photonCad.step.export.result', version: 1,
      value: { contractVersion: 1, requestId: 'step-export:1', projectId: 'project:gearbox', revision: 4, status: 'cancelled', reason: 'picker-cancelled' },
    })).toMatchObject({ type: 'step-export', value: { status: 'cancelled' } })
  })
})

describe('DesktopPhotonCadClient', () => {
  it('reports an honest unavailable runtime without falling back to HTTP', async () => {
    const client = new DesktopPhotonCadClient({ getBridge: () => null })
    await expect(client.describe()).resolves.toEqual({ contractVersion: 1, status: 'unavailable', reason: 'desktop-host-unavailable' })
    await expect(client.execute(operation())).resolves.toMatchObject({ status: 'unavailable', resultingRevision: 3 })
  })

  it('uses only the injected WebView bridge and correlates a bounded description', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({ getBridge: () => bridge, createRequestId: () => 'describe:1' })
    const pending = client.describe()
    expect(bridge.messages).toEqual([{ type: 'photonCad.describe', version: 1, contractVersion: 1, requestId: 'describe:1' }])
    bridge.emit({ type: 'photonCad.describe.result', version: 1, requestId: 'foreign:1', value: runtime() })
    bridge.emit({ type: 'photonCad.describe.result', version: 1, requestId: 'describe:1', value: runtime() })
    await expect(pending).resolves.toEqual(runtime())
    client.close()
  })

  it('ignores foreign project/revision results and accepts only the exact operation identity', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({ getBridge: () => bridge })
    let settled = false
    const pending = client.execute(operation()).then((result) => { settled = true; return result })
    bridge.emit(operationFrame('operation:1', 'project:foreign'))
    bridge.emit({ ...operationFrame(), value: { ...operationFrame().value, baseRevision: 2 } })
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(operationFrame())
    await expect(pending).resolves.toMatchObject({ requestId: 'operation:1', projectId: 'project:gearbox', baseRevision: 3, resultingRevision: 4 })
    client.close()
  })

  it('rejects reuse of a completed request identity', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({ getBridge: () => bridge })
    const first = client.execute(operation())
    bridge.emit(operationFrame())
    await first
    await expect(client.execute(operation())).rejects.toThrow('already used')
    client.close()
  })

  it('posts typed cancellation before rejecting active work on close', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({ getBridge: () => bridge, createRequestId: (kind, sequence) => `${kind}:${sequence}` })
    const pending = client.execute(operation())
    client.close()
    await expect(pending).rejects.toThrow('closed')
    expect(bridge.messages).toContainEqual({ type: 'photonCad.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'operation:1', operation: 'execute' })
  })

  it('uses a bounded per-kind timeout and cancels orphanable work', async () => {
    vi.useFakeTimers()
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({
      getBridge: () => bridge,
      createRequestId: (kind, sequence) => `${kind}:${sequence}`,
      requestTimeoutMs: { execute: 100 },
    })
    const pending = client.execute(operation())
    const rejection = expect(pending).rejects.toThrow('timed out')
    await vi.advanceTimersByTimeAsync(100)
    await rejection
    expect(bridge.messages).toContainEqual({ type: 'photonCad.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'operation:1', operation: 'execute' })
    client.close()
  })

  it('correlates STEP export exactly and replaces an older in-flight native picker request', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadClient({ getBridge: () => bridge, createRequestId: (kind, sequence) => `${kind}:${sequence}` })
    const first = client.exportStep(stepExport())
    const firstRejection = expect(first).rejects.toThrow('newer')
    const secondRequest = stepExport('step-export:2')
    const second = client.exportStep(secondRequest)
    await firstRejection
    expect(bridge.messages[0]).toEqual({ type: 'photonCad.step.export', version: 1, ...stepExport() })
    expect(bridge.messages[1]).toEqual({ type: 'photonCad.cancel', version: 1, requestId: 'cancel:1', targetRequestId: 'step-export:1', operation: 'step-export' })
    bridge.emit(stepExportFrame({ requestId: 'step-export:2', projectId: 'project:foreign' }))
    bridge.emit(stepExportFrame({ requestId: 'step-export:2', revision: 3 }))
    bridge.emit(stepExportFrame({ requestId: 'step-export:2', entityId: 'part:foreign' }))
    let settled = false
    void second.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(stepExportFrame({ requestId: 'step-export:2' }))
    await expect(second).resolves.toMatchObject({ status: 'committed', entityId: 'part:shaft', destinationLabel: 'SHAFT-001.step' })
    client.close()
  })
})
