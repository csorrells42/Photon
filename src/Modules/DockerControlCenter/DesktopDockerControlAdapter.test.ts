import { afterEach, describe, expect, it, vi } from 'vitest'
import { DOCKER_CONTROL_PROTOCOL_VERSION } from './contracts'
import { DesktopDockerControlAdapter } from './DesktopDockerControlAdapter'

class FakeBridge {
  readonly posted: unknown[] = []
  private listener?: (event: MessageEvent) => void
  postMessage = (message: unknown) => { this.posted.push(message) }
  addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listener = listener }
  removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { if (this.listener === listener) this.listener = undefined }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

const hash = `sha256:${'e'.repeat(64)}`
const snapshot = {
  protocolVersion: 1, revision: 3, observedAtUtc: '2026-08-10T18:00:00Z',
  engine: { state: 'running', version: '28.1.0' }, compose: { state: 'running', definitionFingerprint: hash },
  services: [{ id: 'hermes', state: 'running', health: 'healthy', ports: [] }, { id: 'serena', state: 'unknown', health: 'unknown', ports: [] }],
  volumes: [{ role: 'data', state: 'mounted', persistent: true }],
}

function install(bridge: FakeBridge) {
  vi.stubGlobal('window', { chrome: { webview: bridge } })
}

afterEach(() => vi.unstubAllGlobals())

describe('DesktopDockerControlAdapter', () => {
  it('uses exact versioned describe and snapshot frames and ignores mismatched replies', async () => {
    const bridge = new FakeBridge()
    install(bridge)
    const adapter = new DesktopDockerControlAdapter()
    const description = adapter.describe()
    const describeFrame = bridge.posted[0] as Record<string, unknown>
    expect(describeFrame).toMatchObject({ type: 'dockerControl.describe', version: 1 })
    bridge.emit({ type: 'dockerControl.snapshot.result', version: 1, requestId: describeFrame.requestId, value: snapshot })
    bridge.emit({
      type: 'dockerControl.describe.result', version: 1, requestId: describeFrame.requestId,
      value: { protocolVersion: 1, availability: { state: 'available' }, services: ['hermes', 'serena'], operations: { startStack: true, stopStack: true, startService: true, stopService: true, restartService: true, loadModel: false, unloadModel: true, update: false }, updateReason: 'derived-runtime-updater-not-integrated' },
    })
    expect((await description).operations.update).toBe(false)

    const pending = adapter.refresh({ signal: new AbortController().signal })
    const request = bridge.posted[1] as Record<string, unknown>
    expect(request).toMatchObject({ type: 'dockerControl.snapshot', version: 1 })
    bridge.emit({ type: 'dockerControl.snapshot.result', version: 1, requestId: request.requestId, value: snapshot })
    expect((await pending as typeof snapshot).revision).toBe(3)
  })

  it('posts only typed log/review/commit/discard fields and normalizes secret-bearing replies', async () => {
    const bridge = new FakeBridge()
    install(bridge)
    const adapter = new DesktopDockerControlAdapter()
    const signal = new AbortController().signal

    const logs = adapter.readLogs({ protocolVersion: 1, requestId: 'docker-control:logs', service: 'hermes', maxLines: 200 }, { signal })
    expect(bridge.posted[0]).toEqual({ type: 'dockerControl.logs', version: 1, requestId: 'docker-control:logs', service: 'hermes', maxLines: 200 })
    bridge.emit({ type: 'dockerControl.logs.result', version: 1, requestId: 'docker-control:logs', value: { protocolVersion: 1, requestId: 'docker-control:logs', service: 'hermes', entries: [{ stream: 'stderr', text: 'password=secret-value' }], truncated: false } })
    expect(JSON.stringify(await logs)).not.toContain('secret-value')

    const reviewRequest = {
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      requestId: 'docker-control:review',
      snapshotRevision: 3,
      intent: { kind: 'restart-service' as const, service: 'hermes' as const, command: 'docker rm --force arbitrary', environment: { TOKEN: 'secret' } },
    }
    const review = adapter.reviewMutation(reviewRequest, { signal })
    expect(bridge.posted[1]).toEqual({
      type: 'dockerControl.review', version: 1, requestId: reviewRequest.requestId,
      snapshotRevision: 3, intent: { kind: 'restart-service', service: 'hermes' },
    })
    bridge.emit({ type: 'dockerControl.review.result', version: 1, requestId: reviewRequest.requestId, value: { protocolVersion: 1, requestId: reviewRequest.requestId, status: 'ready', snapshotRevision: 3, reviewToken: 'V'.repeat(48), fingerprint: hash, expiresAtUtc: '2030-01-01T00:00:00Z', affectedServices: ['hermes'], summary: 'Restart Hermes', warnings: [] } })
    expect((await review as { status: string }).status).toBe('ready')

    const unloadRequest = {
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      requestId: 'docker-control:unload',
      snapshotRevision: 3,
      intent: { kind: 'unload-model' as const, model: 'docker.io/ai/qwen3:4b' },
    }
    const unload = adapter.reviewMutation(unloadRequest, { signal })
    expect(bridge.posted[2]).toEqual({
      type: 'dockerControl.review', version: 1, requestId: unloadRequest.requestId,
      snapshotRevision: 3, intent: { kind: 'unload-model', service: 'docker.io/ai/qwen3:4b' },
    })
    bridge.emit({ type: 'dockerControl.review.result', version: 1, requestId: unloadRequest.requestId, value: { protocolVersion: 1, requestId: unloadRequest.requestId, status: 'ready', snapshotRevision: 3, reviewToken: 'W'.repeat(48), fingerprint: hash, expiresAtUtc: '2030-01-01T00:00:00Z', affectedServices: ['model-runner'], summary: 'Unload qwen3', warnings: [] } })
    expect((await unload as { status: string }).status).toBe('ready')

    const commit = adapter.commitMutation({ protocolVersion: 1, requestId: 'docker-control:commit', reviewToken: 'V'.repeat(48) }, { signal })
    expect(bridge.posted[3]).toEqual({ type: 'dockerControl.commit', version: 1, requestId: 'docker-control:commit', reviewToken: 'V'.repeat(48) })
    bridge.emit({ type: 'dockerControl.commit.result', version: 1, requestId: 'docker-control:commit', value: { protocolVersion: 1, requestId: 'docker-control:commit', status: 'failed', message: 'authorization=private-value' } })
    expect(JSON.stringify(await commit)).not.toContain('private-value')

    const discard = adapter.discardReview('V'.repeat(48))
    const discardFrame = bridge.posted[4] as Record<string, unknown>
    expect(Object.keys(discardFrame).sort()).toEqual(['requestId', 'reviewToken', 'type', 'version'])
    bridge.emit({ type: 'dockerControl.discard.result', version: 1, requestId: discardFrame.requestId, value: { discarded: true } })
    await expect(discard).resolves.toBeUndefined()
  })

  it('fails closed without WebView and rejects aborted work before posting', async () => {
    const adapter = new DesktopDockerControlAdapter()
    expect(adapter.availability).toEqual({ state: 'unavailable', reason: 'not-registered' })
    await expect(adapter.refresh({ signal: new AbortController().signal })).rejects.toThrow(/trusted desktop app/u)

    const bridge = new FakeBridge()
    install(bridge)
    const cancellation = new AbortController()
    cancellation.abort()
    await expect(adapter.refresh({ signal: cancellation.signal })).rejects.toMatchObject({ name: 'AbortError' })
    expect(bridge.posted).toHaveLength(0)
  })
})
