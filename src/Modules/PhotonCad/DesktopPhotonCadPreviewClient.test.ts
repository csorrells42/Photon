import { describe, expect, it, vi } from 'vitest'
import { DesktopPhotonCadPreviewClient } from './DesktopPhotonCadPreviewClient'
import type { PhotonCadWebViewBridge } from './DesktopPhotonCadClient'

class FakeBridge implements PhotonCadWebViewBridge {
  readonly posted: unknown[] = []
  private readonly listeners = new Set<(event: MessageEvent) => void>()
  postMessage(message: unknown) { this.posted.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listeners.add(listener) }
  removeEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listeners.delete(listener) }
  emit(data: unknown) { for (const listener of this.listeners) listener({ data } as MessageEvent) }
}

const digest = `sha256:${'a'.repeat(64)}`
const context = { sessionId: 'session-1', projectId: 'project-1', revision: 2 }
const request = { previewId: 'preview-1', projectId: 'project-1', revision: 2, expectedDigest: digest, maximumBytes: 1_048_576 }

describe('DesktopPhotonCadPreviewClient', () => {
  it('binds an opaque preview resolve to the exact project context', async () => {
    const bridge = new FakeBridge()
    const client = new DesktopPhotonCadPreviewClient({ getBridge: () => bridge, createRequestId: () => 'preview-request-1' })
    const pending = client.resolve(context, request, new AbortController().signal)
    expect(bridge.posted).toEqual([{
      type: 'photonCad.preview.resolve', version: 1, contractVersion: 1, requestId: 'preview-request-1',
      sessionId: 'session-1', projectId: 'project-1', revision: 2, previewId: 'preview-1', expectedDigest: digest, maximumBytes: 1_048_576,
    }])
    bridge.emit({
      type: 'photonCad.preview.resolve.result', version: 1,
      value: { contractVersion: 1, requestId: 'preview-request-1', status: 'available', url: 'https://127.0.0.1:9119/api/photon-cad/previews/token', contentDigest: digest, byteLength: 256, mediaType: 'model/gltf-binary' },
    })
    await expect(pending).resolves.toMatchObject({ byteLength: 256, contentDigest: digest })
  })

  it('cancels the exact request and rejects foreign or malformed responses', async () => {
    const bridge = new FakeBridge()
    const controller = new AbortController()
    const client = new DesktopPhotonCadPreviewClient({ getBridge: () => bridge, createRequestId: () => 'preview-request-2' })
    const pending = client.resolve(context, request, controller.signal)
    bridge.emit({ type: 'photonCad.preview.resolve.result', version: 1, value: { contractVersion: 1, requestId: 'foreign', status: 'available' } })
    controller.abort()
    await expect(pending).rejects.toThrow('preview-request-cancelled')
    expect(bridge.posted[1]).toEqual({ type: 'photonCad.preview.cancel', version: 1, contractVersion: 1, requestId: 'preview-request-2:cancel', targetRequestId: 'preview-request-2' })
  })

  it('fails closed without a native bridge or with a mismatched project binding', async () => {
    const client = new DesktopPhotonCadPreviewClient({ getBridge: () => null, createRequestId: vi.fn(() => 'unused') })
    await expect(client.resolve(context, request, new AbortController().signal)).rejects.toThrow('preview-bridge-unavailable')
    await expect(client.resolve(context, { ...request, projectId: 'foreign-project' }, new AbortController().signal)).rejects.toThrow('invalid-preview-request')
  })
})
