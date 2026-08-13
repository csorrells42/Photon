import { describe, expect, it, vi } from 'vitest'
import {
  DOCKER_CONTROL_PROTOCOL_VERSION,
  type DockerControlAdapter,
  type DockerMutationReviewRequest,
} from './contracts'
import { DockerControlController } from './DockerControlController'

const hash = `sha256:${'c'.repeat(64)}`
const now = Date.parse('2026-08-10T18:00:00Z')

function snapshot(revision = 1) {
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    revision,
    observedAtUtc: '2026-08-10T18:00:00Z',
    engine: { state: 'running', version: '28.1.0' },
    compose: { state: 'running', definitionFingerprint: hash, upstreamRevision: 'abc', runtimeProtocol: 'v2' },
    services: [
      { id: 'hermes', state: 'running', health: 'healthy', manageable: true, image: { imageId: hash, approvedDigest: hash, ociRevision: 'abc', verification: 'verified' }, ports: [{ address: '127.0.0.1', hostPort: 9119, containerPort: 8000, protocol: 'tcp' }] },
      { id: 'serena', state: 'running', health: 'healthy', ports: [] },
    ],
    volumes: [{ role: 'data', state: 'mounted', persistent: true }, { role: 'workspace', state: 'mounted', persistent: true }],
  }
}

function adapter(overrides: Partial<DockerControlAdapter> = {}): DockerControlAdapter {
  return {
    availability: { state: 'available' },
    describe: vi.fn(async () => ({
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      availability: { state: 'available' },
      services: ['hermes', 'serena', 'model-runner'],
      operations: { startStack: true, stopStack: true, startService: true, stopService: true, restartService: true, repairService: true, loadModel: false, unloadModel: true, update: false },
      updateReason: 'derived-runtime-updater-not-integrated',
    } as const)),
    refresh: vi.fn(async () => snapshot()),
    readLogs: vi.fn(async (request) => ({ protocolVersion: 1, requestId: request.requestId, service: request.service, entries: [], truncated: false })),
    reviewMutation: vi.fn(async (request: DockerMutationReviewRequest) => ({
      protocolVersion: 1, requestId: request.requestId, status: 'ready', snapshotRevision: request.snapshotRevision,
      reviewToken: 'T'.repeat(48), fingerprint: hash, expiresAtUtc: '2026-08-10T19:00:00Z',
      affectedServices: request.intent.kind === 'restart-service' ? [request.intent.service] : ['hermes', 'serena'],
      summary: 'Exact reviewed operation', warnings: [],
    })),
    commitMutation: vi.fn(async (request) => ({ protocolVersion: 1, requestId: request.requestId, status: 'succeeded', message: 'Operation completed.', snapshot: snapshot(2) })),
    discardReview: vi.fn(),
    ...overrides,
  }
}

describe('DockerControlController', () => {
  it('is unavailable without a trusted adapter and never invents snapshot data', async () => {
    const controller = new DockerControlController()
    expect(controller.getSnapshot().status).toBe('unavailable')
    expect(controller.getSnapshot().snapshot).toBeNull()
    expect(await controller.refresh()).toBe(false)
    expect(await controller.requestMutation({ kind: 'start-stack' })).toBe(false)
  })

  it('sends only typed review data and commits only the opaque one-use token', async () => {
    const bridge = adapter()
    const ids = ['docker-control:refresh', 'docker-control:review', 'docker-control:commit']
    const controller = new DockerControlController({ adapter: bridge, now: () => now, createRequestId: () => ids.shift()! })
    expect(await controller.refresh()).toBe(true)
    expect(await controller.requestMutation({ kind: 'restart-service', service: 'hermes' })).toBe(true)
    expect(controller.getSnapshot().review?.affectedServices).toEqual(['hermes'])
    expect(await controller.commitReviewed()).toBe(true)

    const reviewRequest = vi.mocked(bridge.reviewMutation).mock.calls[0][0]
    expect(reviewRequest).toEqual({ protocolVersion: 1, requestId: 'docker-control:refresh', snapshotRevision: 1, intent: { kind: 'restart-service', service: 'hermes' } })
    expect(Object.keys(vi.mocked(bridge.commitMutation).mock.calls[0][0]).sort()).toEqual(['protocolVersion', 'requestId', 'reviewToken'])
    expect(vi.mocked(bridge.commitMutation).mock.calls[0][0].reviewToken).toBe('T'.repeat(48))
    expect(controller.getSnapshot().snapshot?.revision).toBe(2)
    expect(await controller.commitReviewed()).toBe(false)
    expect(bridge.commitMutation).toHaveBeenCalledTimes(1)
  })

  it('blocks duplicate reviews and ignores a review superseded by refresh', async () => {
    let resolveReview!: (value: unknown) => void
    const bridge = adapter({ reviewMutation: vi.fn(() => new Promise((resolve) => { resolveReview = resolve })) })
    const controller = new DockerControlController({ adapter: bridge, now: () => now })
    await controller.refresh()
    const pending = controller.requestMutation({ kind: 'start-stack' })
    expect(await controller.requestMutation({ kind: 'stop-stack' })).toBe(false)
    const refreshed = controller.refresh()
    resolveReview({
      protocolVersion: 1, requestId: 'docker-control:2', status: 'ready', snapshotRevision: 1,
      reviewToken: 'T'.repeat(48), fingerprint: hash, expiresAtUtc: '2026-08-10T19:00:00Z',
      affectedServices: ['hermes', 'serena'], summary: 'Stale', warnings: [],
    })
    await pending
    await refreshed
    expect(controller.getSnapshot().review).toBeNull()
    expect(controller.getSnapshot().status).toBe('ready')
    expect(bridge.reviewMutation).toHaveBeenCalledTimes(1)
  })

  it('invalidates and discards a ready review when a new snapshot revision arrives', async () => {
    const bridge = adapter()
    const controller = new DockerControlController({ adapter: bridge, now: () => now })
    await controller.refresh()
    await controller.requestMutation({ kind: 'start-stack' })
    expect(controller.getSnapshot().review).not.toBeNull()
    vi.mocked(bridge.refresh).mockResolvedValueOnce(snapshot(3))
    await controller.refresh()
    expect(controller.getSnapshot().review).toBeNull()
    expect(bridge.discardReview).toHaveBeenCalledWith('T'.repeat(48))
  })

  it('requests bounded logs and projects redacted output only', async () => {
    const bridge = adapter({
      readLogs: vi.fn(async (request) => ({
        protocolVersion: 1, requestId: request.requestId, service: request.service, truncated: false,
        entries: [{ stream: 'stderr', text: 'password=do-not-render' }],
      })),
    })
    const controller = new DockerControlController({ adapter: bridge })
    await controller.refresh()
    expect(await controller.readLogs('hermes')).toBe(true)
    expect(vi.mocked(bridge.readLogs).mock.calls[0][0].maxLines).toBe(200)
    expect(controller.getSnapshot().logs[0].text).toBe('password=[REDACTED]')
  })

  it('blocks update review when the exact host description says update is unavailable', async () => {
    const bridge = adapter()
    const controller = new DockerControlController({ adapter: bridge })
    await controller.refresh()

    expect(controller.getSnapshot().operations.update).toBe(false)
    expect(await controller.requestMutation({ kind: 'request-update' })).toBe(false)
    expect(bridge.reviewMutation).not.toHaveBeenCalled()
    expect(controller.getSnapshot().message).toContain('trusted updater is not integrated')
  })
})
