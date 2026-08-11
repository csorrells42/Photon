import { describe, expect, it, vi } from 'vitest'
import type { PhotonCadPreviewReceipt } from './PhotonCadContract'
import {
  PHOTON_CAD_PREVIEW_MEDIA_TYPE,
  PhotonCadLoadGuard,
  PhotonCadPreviewAssetError,
  disposePhotonCadObject,
  fetchVerifiedPhotonCadGlb,
  validatePhotonCadGlb,
  validatePhotonCadPreviewAsset,
  validatePhotonCadPreviewReceiptForViewer,
} from './PhotonCadPreviewAsset'

const digest = `sha256:${'a'.repeat(64)}`
const opaqueId = 'A'.repeat(32)

const receipt: PhotonCadPreviewReceipt = {
  previewId: 'preview-1',
  projectId: 'project-1',
  revision: 4,
  contentDigest: digest,
  units: 'millimeter',
  bounds: { minimum: { x: -1, y: -2, z: -3 }, maximum: { x: 1, y: 2, z: 3 } },
  entityCount: 3,
}

function glb(json: unknown) {
  const raw = new TextEncoder().encode(JSON.stringify(json))
  const paddedLength = Math.ceil(raw.byteLength / 4) * 4
  const result = new ArrayBuffer(12 + 8 + paddedLength)
  const view = new DataView(result)
  view.setUint32(0, 0x46546c67, true)
  view.setUint32(4, 2, true)
  view.setUint32(8, result.byteLength, true)
  view.setUint32(12, paddedLength, true)
  view.setUint32(16, 0x4e4f534a, true)
  const target = new Uint8Array(result, 20, paddedLength)
  target.fill(0x20)
  target.set(raw)
  return result
}

function appendGlbChunk(source: ArrayBuffer, chunkType: number, payload = new Uint8Array(4)) {
  if (payload.byteLength === 0 || payload.byteLength % 4 !== 0) throw new Error('Test chunks must be non-empty and four-byte aligned.')
  const result = new ArrayBuffer(source.byteLength + 8 + payload.byteLength)
  new Uint8Array(result).set(new Uint8Array(source))
  const view = new DataView(result)
  view.setUint32(8, result.byteLength, true)
  view.setUint32(source.byteLength, payload.byteLength, true)
  view.setUint32(source.byteLength + 4, chunkType, true)
  new Uint8Array(result, source.byteLength + 8).set(payload)
  return result
}

function triangleGlb() {
  const binary = new ArrayBuffer(44)
  new Float32Array(binary, 0, 9).set([0, 0, 0, 1, 0, 0, 0, 1, 0])
  new Uint16Array(binary, 36, 3).set([0, 1, 2])
  const document = {
    asset: { version: '2.0' },
    buffers: [{ byteLength: 44 }],
    bufferViews: [
      { buffer: 0, byteOffset: 0, byteLength: 36, target: 34962 },
      { buffer: 0, byteOffset: 36, byteLength: 6, target: 34963 },
    ],
    accessors: [
      { bufferView: 0, componentType: 5126, count: 3, type: 'VEC3', min: [0, 0, 0], max: [1, 1, 0] },
      { bufferView: 1, componentType: 5123, count: 3, type: 'SCALAR' },
    ],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1 }] }],
    nodes: [{ mesh: 0, name: 'Triangle', extras: { photonEntityId: 'part-1' } }],
    scenes: [{ nodes: [0] }],
    scene: 0,
  }
  const rawJson = new TextEncoder().encode(JSON.stringify(document))
  const jsonLength = Math.ceil(rawJson.byteLength / 4) * 4
  const result = new ArrayBuffer(12 + 8 + jsonLength + 8 + binary.byteLength)
  const view = new DataView(result)
  view.setUint32(0, 0x46546c67, true)
  view.setUint32(4, 2, true)
  view.setUint32(8, result.byteLength, true)
  view.setUint32(12, jsonLength, true)
  view.setUint32(16, 0x4e4f534a, true)
  const jsonTarget = new Uint8Array(result, 20, jsonLength)
  jsonTarget.fill(0x20)
  jsonTarget.set(rawJson)
  const binaryHeader = 20 + jsonLength
  view.setUint32(binaryHeader, binary.byteLength, true)
  view.setUint32(binaryHeader + 4, 0x004e4942, true)
  new Uint8Array(result, binaryHeader + 8).set(new Uint8Array(binary))
  return result
}

async function sha256(bytes: ArrayBuffer) {
  const result = await crypto.subtle.digest('SHA-256', bytes)
  return `sha256:${Array.from(new Uint8Array(result), (value) => value.toString(16).padStart(2, '0')).join('')}`
}

describe('PhotonCadPreviewAsset', () => {
  it('accepts finite, ordered, digest-bound preview receipts and rejects malformed bounds', () => {
    expect(validatePhotonCadPreviewReceiptForViewer(receipt)).toBe(receipt)
    expect(() => validatePhotonCadPreviewReceiptForViewer({ ...receipt, bounds: { minimum: { x: 2, y: 0, z: 0 }, maximum: { x: 1, y: 1, z: 1 } } }))
      .toThrowError(PhotonCadPreviewAssetError)
    expect(() => validatePhotonCadPreviewReceiptForViewer({ ...receipt, entityCount: 100_001 }))
      .toThrowError(/invalid-preview-receipt/u)
    expect(() => validatePhotonCadPreviewReceiptForViewer({ ...receipt, bounds: { ...receipt.bounds, maximum: { x: 1_000_000_001, y: 2, z: 3 } } }))
      .toThrowError(/invalid-preview-receipt/u)
  })

  it('accepts only a fixed-origin opaque GLB endpoint with matching digest and bounded bytes', () => {
    const asset = validatePhotonCadPreviewAsset({
      url: `https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`,
      contentDigest: digest.toUpperCase(),
      byteLength: 4_096,
      mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE,
      expiresAtUtc: '2030-01-01T00:00:00Z',
    }, digest.slice('sha256:'.length), { allowedOrigin: 'https://127.0.0.1:9119', maximumBytes: 8_192, now: () => Date.parse('2029-01-01T00:00:00Z') })

    expect(asset).toMatchObject({ contentDigest: digest, byteLength: 4_096, mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE })
  })

  it.each([
    [`file:///C:/secret.glb`, digest, 4_096, 'untrusted-asset-url'],
    [`data:model/gltf-binary;base64,AAAA`, digest, 4_096, 'untrusted-asset-url'],
    [`https://evil.example/api/photon-cad/previews/${opaqueId}`, digest, 4_096, 'untrusted-asset-url'],
    [`https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}?token=secret`, digest, 4_096, 'untrusted-asset-url'],
    [`https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`, `sha256:${'b'.repeat(64)}`, 4_096, 'asset-digest-mismatch'],
    [`https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`, digest, 9_000, 'asset-size-out-of-bounds'],
  ])('rejects an untrusted asset candidate', (url, contentDigest, byteLength, reason) => {
    expect(() => validatePhotonCadPreviewAsset({ url, contentDigest, byteLength, mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE }, digest, {
      allowedOrigin: 'https://127.0.0.1:9119', maximumBytes: 8_192,
    })).toThrowError(reason)
  })

  it('rejects an impossible or expired resolver timestamp', () => {
    const candidate = { url: `https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`, contentDigest: digest, byteLength: 4_096, mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE }
    expect(() => validatePhotonCadPreviewAsset({ ...candidate, expiresAtUtc: '2030-02-31T00:00:00Z' }, digest, {
      allowedOrigin: 'https://127.0.0.1:9119', maximumBytes: 8_192, now: () => Date.parse('2029-01-01T00:00:00Z'),
    })).toThrowError(/asset-url-expired/u)
    expect(() => validatePhotonCadPreviewAsset({ ...candidate, expiresAtUtc: '2028-01-01T00:00:00Z' }, digest, {
      allowedOrigin: 'https://127.0.0.1:9119', maximumBytes: 8_192, now: () => Date.parse('2029-01-01T00:00:00Z'),
    })).toThrowError(/asset-url-expired/u)
  })

  it('validates an embedded GLB and rejects every external or data URI before Three.js parsing', () => {
    expect(validatePhotonCadGlb(glb({ asset: { version: '2.0' }, scenes: [{}], scene: 0 }))).toMatchObject({ jsonByteLength: expect.any(Number) })
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, buffers: [{ uri: 'part.bin' }] })))
      .toThrowError(/external-glb-resource-forbidden/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, images: [{ uri: 'data:image/png;base64,AAAA' }] })))
      .toThrowError(/external-glb-resource-forbidden/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, images: [{ bufferView: 0, mimeType: 'image/png' }] })))
      .toThrowError(/glb-textures-forbidden/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '1.0' } })))
      .toThrowError(/unsupported-glb-version/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, accessors: [{ count: 4_000_000_000 }] })))
      .toThrowError(/glb-accessor-limit/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, animations: [{}] })))
      .toThrowError(/animated-glb-forbidden/u)
    expect(() => validatePhotonCadGlb(glb({ asset: { version: '2.0' }, nodes: [{ children: [1] }, { children: [0] }] })))
      .toThrowError(/glb-node-cycle/u)
    expect(() => validatePhotonCadGlb(appendGlbChunk(glb({ asset: { version: '2.0' } }), 0x004e4942)))
      .toThrowError(/glb-buffer-length-mismatch/u)
    expect(() => validatePhotonCadGlb(appendGlbChunk(glb({ asset: { version: '2.0' } }), 0x12345678)))
      .toThrowError(/unsupported-glb-chunk/u)
    const embedded = appendGlbChunk(glb({ asset: { version: '2.0' }, buffers: [{ byteLength: 4 }] }), 0x004e4942)
    expect(() => validatePhotonCadGlb(appendGlbChunk(embedded, 0x004e4942)))
      .toThrowError(/invalid-glb-binary-chunk/u)
  })

  it('parses a verified embedded triangle with the pinned lazy GLTFLoader API', async () => {
    const bytes = triangleGlb()
    expect(validatePhotonCadGlb(bytes)).toMatchObject({ byteLength: bytes.byteLength })
    const { GLTFLoader } = await import('three/examples/jsm/loaders/GLTFLoader.js')
    const result = await new GLTFLoader().parseAsync(bytes, '')

    expect(result.scene.getObjectByName('Triangle')?.userData.photonEntityId).toBe('part-1')
    disposePhotonCadObject(result.scene)
  })

  it('aborts an older load and makes closed generations stale', () => {
    const guard = new PhotonCadLoadGuard()
    const first = guard.begin()
    const second = guard.begin()
    expect(first.signal.aborted).toBe(true)
    expect(guard.isCurrent(first.generation)).toBe(false)
    expect(guard.isCurrent(second.generation)).toBe(true)
    expect(guard.abort(second.generation)).toBe(true)
    expect(guard.abort(second.generation)).toBe(false)
    expect(second.signal.aborted).toBe(true)
    expect(guard.isCurrent(second.generation)).toBe(false)
    guard.close()
  })

  it('disposes shared geometry, material, and nested textures exactly once', () => {
    const geometry = { dispose: vi.fn() }
    const texture = { isTexture: true, dispose: vi.fn() }
    const material = { map: texture, uniforms: { detail: { value: texture } }, dispose: vi.fn() }
    const children = [{ geometry, material }, { geometry, material: [material] }]
    const root = { traverse: (visit: (node: typeof children[number]) => void) => children.forEach(visit) }

    disposePhotonCadObject(root)

    expect(geometry.dispose).toHaveBeenCalledTimes(1)
    expect(texture.dispose).toHaveBeenCalledTimes(1)
    expect(material.dispose).toHaveBeenCalledTimes(1)
  })

  it('downloads with no-store same-origin policy and verifies byte length, digest, and GLB structure', async () => {
    const bytes = glb({ asset: { version: '2.0' }, scenes: [{}], scene: 0 })
    const contentDigest = await sha256(bytes)
    const asset = {
      url: `https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`,
      contentDigest,
      byteLength: bytes.byteLength,
      mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE,
    } as const
    const fetcher = vi.fn(async () => new Response(bytes, {
      status: 200,
      headers: { 'content-type': PHOTON_CAD_PREVIEW_MEDIA_TYPE, 'content-length': String(bytes.byteLength) },
    })) as unknown as typeof fetch

    await expect(fetchVerifiedPhotonCadGlb(asset, 8_192, new AbortController().signal, fetcher)).resolves.toEqual(bytes)
    expect(fetcher).toHaveBeenCalledWith(asset.url, expect.objectContaining({ credentials: 'same-origin', cache: 'no-store', redirect: 'error' }))
  })

  it('rejects downloaded bytes that do not match the host-bound digest', async () => {
    const bytes = glb({ asset: { version: '2.0' }, scenes: [{}], scene: 0 })
    const fetcher = vi.fn(async () => new Response(bytes, {
      status: 200,
      headers: { 'content-type': PHOTON_CAD_PREVIEW_MEDIA_TYPE, 'content-length': String(bytes.byteLength) },
    })) as unknown as typeof fetch
    const asset = {
      url: `https://127.0.0.1:9119/api/photon-cad/previews/${opaqueId}`,
      contentDigest: digest,
      byteLength: bytes.byteLength,
      mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE,
    } as const

    await expect(fetchVerifiedPhotonCadGlb(asset, 8_192, new AbortController().signal, fetcher))
      .rejects.toThrowError(/download-digest-mismatch/u)
  })
})
