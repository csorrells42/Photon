import { isPhotonCadDigest, isPhotonCadIdentifier } from './PhotonCadContract'
import type { PhotonCadPreviewReceipt } from './PhotonCadContract'
import type { PhotonCadPreviewAsset, PhotonCadPreviewAssetRequest } from './PhotonCadPreviewAsset'
import type { PhotonCadWebViewBridge } from './DesktopPhotonCadClient'

const REQUEST_TIMEOUT_MS = 30_000

type PreviewContext = {
  sessionId: string
  projectId: string
  revision: number
}

type Options = {
  getBridge?: () => PhotonCadWebViewBridge | null
  createRequestId?: () => string
  timeoutMs?: number
}

function browserBridge(): PhotonCadWebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: PhotonCadWebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export class DesktopPhotonCadPreviewClient {
  private readonly getBridge: () => PhotonCadWebViewBridge | null
  private readonly createRequestId: () => string
  private readonly timeoutMs: number
  private sequence = 0

  constructor(options: Options = {}) {
    this.getBridge = options.getBridge ?? browserBridge
    this.createRequestId = options.createRequestId ?? (() => `cad-preview:${Date.now().toString(36)}:${(++this.sequence).toString(36)}`)
    this.timeoutMs = options.timeoutMs ?? REQUEST_TIMEOUT_MS
    if (!Number.isSafeInteger(this.timeoutMs) || this.timeoutMs < 1_000 || this.timeoutMs > 120_000) {
      throw new Error('invalid-preview-timeout')
    }
  }

  hydrate(context: PreviewContext, signal: AbortSignal): Promise<PhotonCadPreviewReceipt> {
    if (!isPhotonCadIdentifier(context.sessionId) || !isPhotonCadIdentifier(context.projectId)
      || !Number.isSafeInteger(context.revision) || context.revision < 1) {
      return Promise.reject(new Error('invalid-preview-hydration-request'))
    }
    const bridge = this.getBridge()
    if (!bridge) return Promise.reject(new Error('preview-bridge-unavailable'))
    const requestId = this.createRequestId()
    if (!isPhotonCadIdentifier(requestId)) return Promise.reject(new Error('invalid-preview-request-id'))

    return new Promise((resolve, reject) => {
      let settled = false
      const finish = (error?: Error, value?: PhotonCadPreviewReceipt) => {
        if (settled) return
        settled = true
        clearTimeout(timeout)
        signal.removeEventListener('abort', abort)
        bridge.removeEventListener('message', receive)
        if (error) reject(error)
        else if (value) resolve(value)
        else reject(new Error('preview-unavailable'))
      }
      const cancel = () => bridge.postMessage({
        type: 'photonCad.preview.cancel', version: 1, contractVersion: 1,
        requestId: `${requestId}:cancel`, targetRequestId: requestId,
      })
      const abort = () => { cancel(); finish(new Error('preview-request-cancelled')) }
      const receive = (event: MessageEvent) => {
        const frame = record(event.data)
        const value = record(frame?.value)
        if (frame?.type !== 'photonCad.preview.hydrate.result' || value?.requestId !== requestId) return
        const preview = record(value.preview)
        const bounds = record(preview?.bounds)
        const minimum = record(bounds?.minimum)
        const maximum = record(bounds?.maximum)
        const revision = preview?.revision
        const entityCount = preview?.entityCount
        const finiteVector = (vector: Record<string, unknown> | null) => vector
          && ['x', 'y', 'z'].every((axis) => typeof vector[axis] === 'number' && Number.isFinite(vector[axis]))
        if (frame.version !== 1 || value.contractVersion !== 1 || value.status !== 'available' || !preview
          || !isPhotonCadIdentifier(preview.previewId) || preview.projectId !== context.projectId
          || revision !== context.revision || !isPhotonCadDigest(preview.contentDigest)
          || (preview.units !== 'millimeter' && preview.units !== 'inch')
          || !finiteVector(minimum) || !finiteVector(maximum)
          || !Number.isSafeInteger(entityCount) || (entityCount as number) < 1) {
          finish(new Error(value.status === 'unavailable' ? 'preview-unavailable' : 'invalid-preview-hydration-response'))
          return
        }
        finish(undefined, {
          previewId: preview.previewId as string,
          projectId: preview.projectId as string,
          revision: revision as number,
          contentDigest: (preview.contentDigest as string).toLowerCase(),
          units: preview.units as 'millimeter' | 'inch',
          bounds: {
            minimum: minimum as { x: number; y: number; z: number },
            maximum: maximum as { x: number; y: number; z: number },
          },
          entityCount: entityCount as number,
        })
      }
      const timeout = setTimeout(() => { cancel(); finish(new Error('preview-request-timeout')) }, this.timeoutMs)
      bridge.addEventListener('message', receive)
      signal.addEventListener('abort', abort, { once: true })
      if (signal.aborted) { abort(); return }
      bridge.postMessage({
        type: 'photonCad.preview.hydrate', version: 1, contractVersion: 1,
        requestId, sessionId: context.sessionId, projectId: context.projectId, revision: context.revision,
      })
    })
  }

  resolve(context: PreviewContext, request: PhotonCadPreviewAssetRequest, signal: AbortSignal): Promise<unknown> {
    if (!isPhotonCadIdentifier(context.sessionId) || !isPhotonCadIdentifier(context.projectId)
      || context.projectId !== request.projectId || !Number.isSafeInteger(context.revision) || context.revision !== request.revision
      || !isPhotonCadIdentifier(request.previewId) || !isPhotonCadDigest(request.expectedDigest)
      || !Number.isSafeInteger(request.maximumBytes) || request.maximumBytes < 20 || request.maximumBytes > 128 * 1024 * 1024) {
      return Promise.reject(new Error('invalid-preview-request'))
    }
    const bridge = this.getBridge()
    if (!bridge) return Promise.reject(new Error('preview-bridge-unavailable'))
    const requestId = this.createRequestId()
    if (!isPhotonCadIdentifier(requestId)) return Promise.reject(new Error('invalid-preview-request-id'))

    return new Promise((resolve, reject) => {
      let settled = false
      const finish = (error?: Error, value?: PhotonCadPreviewAsset) => {
        if (settled) return
        settled = true
        clearTimeout(timeout)
        signal.removeEventListener('abort', abort)
        bridge.removeEventListener('message', receive)
        if (error) reject(error)
        else resolve(value)
      }
      const cancel = () => bridge.postMessage({
        type: 'photonCad.preview.cancel',
        version: 1,
        contractVersion: 1,
        requestId: `${requestId}:cancel`,
        targetRequestId: requestId,
      })
      const abort = () => { cancel(); finish(new Error('preview-request-cancelled')) }
      const receive = (event: MessageEvent) => {
        const frame = record(event.data)
        const value = record(frame?.value)
        if (frame?.type !== 'photonCad.preview.resolve.result' || value?.requestId !== requestId) return
        if (frame.version !== 1 || value.contractVersion !== 1 || value.status !== 'available') {
          finish(new Error('preview-unavailable'))
          return
        }
        if (typeof value.url !== 'string' || !isPhotonCadDigest(value.contentDigest)
          || !Number.isSafeInteger(value.byteLength) || (value.byteLength as number) < 20
          || value.mediaType !== 'model/gltf-binary'
          || (value.expiresAtUtc !== undefined && typeof value.expiresAtUtc !== 'string')) {
          finish(new Error('invalid-preview-response'))
          return
        }
        finish(undefined, {
          url: value.url,
          contentDigest: value.contentDigest,
          byteLength: value.byteLength as number,
          mediaType: 'model/gltf-binary',
          expiresAtUtc: value.expiresAtUtc as string | undefined,
        })
      }
      const timeout = setTimeout(() => { cancel(); finish(new Error('preview-request-timeout')) }, this.timeoutMs)
      bridge.addEventListener('message', receive)
      signal.addEventListener('abort', abort, { once: true })
      if (signal.aborted) { abort(); return }
      bridge.postMessage({
        type: 'photonCad.preview.resolve',
        version: 1,
        contractVersion: 1,
        requestId,
        sessionId: context.sessionId,
        projectId: context.projectId,
        revision: context.revision,
        previewId: request.previewId,
        expectedDigest: request.expectedDigest,
        maximumBytes: request.maximumBytes,
      })
    })
  }
}
