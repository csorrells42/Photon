import {
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  type PhotonCadPreviewReceipt,
} from './PhotonCadContract'

export const PHOTON_CAD_PREVIEW_MEDIA_TYPE = 'model/gltf-binary' as const
export const PHOTON_CAD_PREVIEW_DEFAULT_MAXIMUM_BYTES = 128 * 1024 * 1024
export const PHOTON_CAD_PREVIEW_ABSOLUTE_MAXIMUM_BYTES = 512 * 1024 * 1024
export const PHOTON_CAD_PREVIEW_MAXIMUM_ENTITIES = 100_000
const PHOTON_CAD_PREVIEW_MAXIMUM_ABSOLUTE_COORDINATE = 1_000_000_000
const ISO_UTC = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,3})?Z$/u

export type PhotonCadPreviewAssetRequest = {
  previewId: string
  projectId: string
  revision: number
  expectedDigest: string
  maximumBytes: number
}

export type PhotonCadPreviewAsset = {
  url: string
  contentDigest: string
  byteLength: number
  mediaType: typeof PHOTON_CAD_PREVIEW_MEDIA_TYPE
  expiresAtUtc?: string
}

export type PhotonCadPreviewAssetResolver = (
  request: PhotonCadPreviewAssetRequest,
  signal: AbortSignal,
) => Promise<unknown>

export type PhotonCadPreviewAssetPolicy = {
  allowedOrigin: string
  pathPrefix?: string
  maximumBytes?: number
  now?: () => number
}

export type PhotonCadGlbMetadata = {
  byteLength: number
  jsonByteLength: number
}

export class PhotonCadPreviewAssetError extends Error {
  constructor(public readonly reason: string) {
    super(reason)
    this.name = 'PhotonCadPreviewAssetError'
  }
}

export function photonCadCanonicalDigest(value: string) {
  if (!isPhotonCadDigest(value)) throw new PhotonCadPreviewAssetError('invalid-asset-digest')
  const lower = value.toLowerCase()
  return lower.startsWith('sha256:') ? lower : `sha256:${lower}`
}

export class PhotonCadLoadGuard {
  private generation = 0
  private controller: AbortController | null = null

  begin() {
    this.controller?.abort()
    this.controller = new AbortController()
    const generation = ++this.generation
    return { generation, signal: this.controller.signal }
  }

  isCurrent(generation: number) {
    return generation === this.generation && this.controller?.signal.aborted === false
  }

  abort(generation: number) {
    if (!this.isCurrent(generation)) return false
    this.controller?.abort()
    this.controller = null
    this.generation += 1
    return true
  }

  close() {
    this.controller?.abort()
    this.controller = null
    this.generation += 1
  }
}

export function photonCadPreviewMaximumBytes(value: number | undefined) {
  const maximum = value ?? PHOTON_CAD_PREVIEW_DEFAULT_MAXIMUM_BYTES
  if (!Number.isSafeInteger(maximum) || maximum < 20 || maximum > PHOTON_CAD_PREVIEW_ABSOLUTE_MAXIMUM_BYTES) {
    throw new PhotonCadPreviewAssetError('invalid-byte-policy')
  }
  return maximum
}

function finiteVector(value: PhotonCadPreviewReceipt['bounds']['minimum']) {
  return [value.x, value.y, value.z].every((coordinate) => Number.isFinite(coordinate)
    && Math.abs(coordinate) <= PHOTON_CAD_PREVIEW_MAXIMUM_ABSOLUTE_COORDINATE)
}

export function validatePhotonCadPreviewReceiptForViewer(receipt: PhotonCadPreviewReceipt) {
  if (!isPhotonCadIdentifier(receipt.previewId) || !isPhotonCadIdentifier(receipt.projectId)
    || !Number.isSafeInteger(receipt.revision) || receipt.revision < 0
    || !isPhotonCadDigest(receipt.contentDigest)
    || !['millimeter', 'inch'].includes(receipt.units)
    || !Number.isSafeInteger(receipt.entityCount) || receipt.entityCount < 0 || receipt.entityCount > PHOTON_CAD_PREVIEW_MAXIMUM_ENTITIES
    || !finiteVector(receipt.bounds.minimum) || !finiteVector(receipt.bounds.maximum)
    || receipt.bounds.minimum.x > receipt.bounds.maximum.x
    || receipt.bounds.minimum.y > receipt.bounds.maximum.y
    || receipt.bounds.minimum.z > receipt.bounds.maximum.z) {
    throw new PhotonCadPreviewAssetError('invalid-preview-receipt')
  }
  return receipt
}

function validateOrigin(raw: string) {
  let url: URL
  try {
    url = new URL(raw)
  } catch {
    throw new PhotonCadPreviewAssetError('invalid-asset-origin')
  }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.origin === 'null') {
    throw new PhotonCadPreviewAssetError('invalid-asset-origin')
  }
  return url.origin
}

function validatePathPrefix(raw: string | undefined) {
  const prefix = raw ?? '/api/photon-cad/previews/'
  if (!prefix.startsWith('/') || !prefix.endsWith('/') || prefix.includes('..') || prefix.includes('\\') || prefix.includes('%')) {
    throw new PhotonCadPreviewAssetError('invalid-asset-path-policy')
  }
  return prefix
}

function futureUtc(value: string, now: number) {
  const match = ISO_UTC.exec(value)
  const timestamp = Date.parse(value)
  if (!match || !Number.isFinite(timestamp) || timestamp <= now) return false
  const date = new Date(timestamp)
  return date.getUTCFullYear() === Number(match[1])
    && date.getUTCMonth() + 1 === Number(match[2])
    && date.getUTCDate() === Number(match[3])
    && date.getUTCHours() === Number(match[4])
    && date.getUTCMinutes() === Number(match[5])
    && date.getUTCSeconds() === Number(match[6])
}

export function validatePhotonCadPreviewAsset(
  raw: unknown,
  expectedDigest: string,
  policy: PhotonCadPreviewAssetPolicy,
): PhotonCadPreviewAsset {
  if (!raw || typeof raw !== 'object' || !isPhotonCadDigest(expectedDigest)) {
    throw new PhotonCadPreviewAssetError('invalid-asset-metadata')
  }
  const value = raw as Partial<PhotonCadPreviewAsset>
  const maximumBytes = photonCadPreviewMaximumBytes(policy.maximumBytes)
  const allowedOrigin = validateOrigin(policy.allowedOrigin)
  const pathPrefix = validatePathPrefix(policy.pathPrefix)
  let assetUrl: URL
  try {
    assetUrl = new URL(value.url ?? '')
  } catch {
    throw new PhotonCadPreviewAssetError('invalid-asset-url')
  }
  const opaqueId = assetUrl.pathname.slice(pathPrefix.length)
  if (assetUrl.origin !== allowedOrigin || !assetUrl.pathname.startsWith(pathPrefix)
    || !/^[A-Za-z0-9_-]{32,160}$/u.test(opaqueId)
    || assetUrl.search || assetUrl.hash || assetUrl.username || assetUrl.password) {
    throw new PhotonCadPreviewAssetError('untrusted-asset-url')
  }
  if (!isPhotonCadDigest(value.contentDigest)
    || photonCadCanonicalDigest(value.contentDigest) !== photonCadCanonicalDigest(expectedDigest)) {
    throw new PhotonCadPreviewAssetError('asset-digest-mismatch')
  }
  if (!Number.isSafeInteger(value.byteLength) || (value.byteLength ?? 0) < 20 || (value.byteLength ?? 0) > maximumBytes) {
    throw new PhotonCadPreviewAssetError('asset-size-out-of-bounds')
  }
  if (value.mediaType !== PHOTON_CAD_PREVIEW_MEDIA_TYPE) {
    throw new PhotonCadPreviewAssetError('invalid-asset-media-type')
  }
  if (value.expiresAtUtc !== undefined) {
    if (!futureUtc(value.expiresAtUtc, policy.now?.() ?? Date.now())) {
      throw new PhotonCadPreviewAssetError('asset-url-expired')
    }
  }
  return {
    url: assetUrl.href,
    contentDigest: photonCadCanonicalDigest(value.contentDigest),
    byteLength: value.byteLength!,
    mediaType: PHOTON_CAD_PREVIEW_MEDIA_TYPE,
    ...(value.expiresAtUtc ? { expiresAtUtc: value.expiresAtUtc } : {}),
  }
}

function rejectExternalUris(value: unknown) {
  const pending: Array<{ value: unknown; depth: number }> = [{ value, depth: 0 }]
  const visited = new Set<object>()
  let inspected = 0
  while (pending.length) {
    const current = pending.pop()!
    if (++inspected > 250_000 || current.depth > 64) throw new PhotonCadPreviewAssetError('glb-json-too-complex')
    if (!current.value || typeof current.value !== 'object') continue
    if (visited.has(current.value)) continue
    visited.add(current.value)
    for (const [key, nested] of Object.entries(current.value)) {
      if (key.toLowerCase() === 'uri' && typeof nested === 'string') {
        throw new PhotonCadPreviewAssetError('external-glb-resource-forbidden')
      }
      pending.push({ value: nested, depth: current.depth + 1 })
    }
  }
}

function safeCount(value: unknown, maximum: number) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 && value <= maximum
}

function validateGlbDocument(document: Record<string, unknown>, maximumBytes: number) {
  const array = (key: string) => document[key] === undefined ? [] : Array.isArray(document[key]) ? document[key] as unknown[] : null
  const nodes = array('nodes')
  const meshes = array('meshes')
  const accessors = array('accessors')
  const bufferViews = array('bufferViews')
  const buffers = array('buffers')
  const materials = array('materials')
  if (!nodes || !meshes || !accessors || !bufferViews || !buffers || !materials
    || nodes.length > 100_000 || meshes.length > 20_000 || accessors.length > 100_000
    || bufferViews.length > 100_000 || buffers.length > 1 || materials.length > 20_000) {
    throw new PhotonCadPreviewAssetError('glb-resource-limit')
  }
  if ((Array.isArray(document.animations) && document.animations.length)
    || (Array.isArray(document.skins) && document.skins.length)) {
    throw new PhotonCadPreviewAssetError('animated-glb-forbidden')
  }
  const childIndexes: number[][] = []
  const indegree = new Array<number>(nodes.length).fill(0)
  let childReferences = 0
  for (const raw of nodes) {
    if (!raw || typeof raw !== 'object') throw new PhotonCadPreviewAssetError('glb-node-limit')
    const children = (raw as Record<string, unknown>).children ?? []
    if (!Array.isArray(children)) throw new PhotonCadPreviewAssetError('glb-node-limit')
    const indexes: number[] = []
    for (const child of children) {
      if (!safeCount(child, Math.max(nodes.length - 1, 0)) || ++childReferences > 200_000) {
        throw new PhotonCadPreviewAssetError('glb-node-limit')
      }
      const index = Number(child)
      indegree[index] += 1
      if (indegree[index] > 1) throw new PhotonCadPreviewAssetError('glb-node-limit')
      indexes.push(index)
    }
    childIndexes.push(indexes)
  }
  const pendingNodes = indegree.map((degree, index) => degree === 0 ? { index, depth: 1 } : null).filter((value): value is { index: number; depth: number } => Boolean(value))
  let visitedNodes = 0
  for (let cursor = 0; cursor < pendingNodes.length; cursor += 1) {
    const current = pendingNodes[cursor]
    if (current.depth > 256) throw new PhotonCadPreviewAssetError('glb-node-depth-limit')
    visitedNodes += 1
    for (const child of childIndexes[current.index]) pendingNodes.push({ index: child, depth: current.depth + 1 })
  }
  if (visitedNodes !== nodes.length) throw new PhotonCadPreviewAssetError('glb-node-cycle')
  const allowedRequiredExtensions = new Set(['KHR_mesh_quantization', 'KHR_materials_unlit'])
  if (document.extensionsRequired !== undefined
    && (!Array.isArray(document.extensionsRequired)
      || document.extensionsRequired.some((extension) => typeof extension !== 'string' || !allowedRequiredExtensions.has(extension)))) {
    throw new PhotonCadPreviewAssetError('unsupported-required-extension')
  }
  const declaredBufferLength = buffers.length
    ? (buffers[0] && typeof buffers[0] === 'object' ? (buffers[0] as Record<string, unknown>).byteLength : -1)
    : 0
  if (!safeCount(declaredBufferLength, maximumBytes) || (buffers.length > 0 && Number(declaredBufferLength) < 1)) {
    throw new PhotonCadPreviewAssetError('glb-buffer-limit')
  }
  for (const raw of bufferViews) {
    if (!raw || typeof raw !== 'object') throw new PhotonCadPreviewAssetError('glb-buffer-view-limit')
    const view = raw as Record<string, unknown>
    const offset = view.byteOffset ?? 0
    if (view.buffer !== 0 || !safeCount(offset, maximumBytes) || !safeCount(view.byteLength, maximumBytes)
      || Number(offset) + Number(view.byteLength) > Number(declaredBufferLength)) {
      throw new PhotonCadPreviewAssetError('glb-buffer-view-limit')
    }
  }
  let totalAccessorElements = 0
  for (const raw of accessors) {
    if (!raw || typeof raw !== 'object') throw new PhotonCadPreviewAssetError('glb-accessor-limit')
    const accessor = raw as Record<string, unknown>
    if (!safeCount(accessor.count, 5_000_000)) throw new PhotonCadPreviewAssetError('glb-accessor-limit')
    totalAccessorElements += Number(accessor.count)
    if (!Number.isSafeInteger(totalAccessorElements) || totalAccessorElements > 10_000_000) {
      throw new PhotonCadPreviewAssetError('glb-accessor-limit')
    }
  }
  let primitives = 0
  for (const raw of meshes) {
    if (!raw || typeof raw !== 'object' || !Array.isArray((raw as Record<string, unknown>).primitives)) {
      throw new PhotonCadPreviewAssetError('glb-mesh-limit')
    }
    primitives += ((raw as Record<string, unknown>).primitives as unknown[]).length
    if (primitives > 50_000) throw new PhotonCadPreviewAssetError('glb-mesh-limit')
  }
  return Number(declaredBufferLength)
}

export function validatePhotonCadGlb(bytes: ArrayBuffer, maximumBytes = PHOTON_CAD_PREVIEW_DEFAULT_MAXIMUM_BYTES): PhotonCadGlbMetadata {
  const maximum = photonCadPreviewMaximumBytes(maximumBytes)
  if (bytes.byteLength < 20 || bytes.byteLength > maximum) throw new PhotonCadPreviewAssetError('glb-size-out-of-bounds')
  const view = new DataView(bytes)
  if (view.getUint32(0, true) !== 0x46546c67 || view.getUint32(4, true) !== 2 || view.getUint32(8, true) !== bytes.byteLength) {
    throw new PhotonCadPreviewAssetError('invalid-glb-header')
  }
  let offset = 12
  let json: unknown = null
  let jsonByteLength = 0
  let declaredBufferLength = 0
  let binaryByteLength: number | null = null
  while (offset < bytes.byteLength) {
    if (offset + 8 > bytes.byteLength) throw new PhotonCadPreviewAssetError('invalid-glb-chunk')
    const chunkLength = view.getUint32(offset, true)
    const chunkType = view.getUint32(offset + 4, true)
    const chunkStart = offset + 8
    const chunkEnd = chunkStart + chunkLength
    if (chunkLength === 0 || chunkEnd > bytes.byteLength || chunkEnd % 4 !== 0) {
      throw new PhotonCadPreviewAssetError('invalid-glb-chunk')
    }
    if (chunkType === 0x4e4f534a) {
      if (json !== null || offset !== 12 || chunkLength > Math.min(maximum, 32 * 1024 * 1024)) {
        throw new PhotonCadPreviewAssetError('invalid-glb-json-chunk')
      }
      jsonByteLength = chunkLength
      try {
        const text = new TextDecoder('utf-8', { fatal: true }).decode(new Uint8Array(bytes, chunkStart, chunkLength)).replace(/[\u0000\u0020]+$/gu, '')
        json = JSON.parse(text)
      } catch {
        throw new PhotonCadPreviewAssetError('invalid-glb-json')
      }
      rejectExternalUris(json)
      const document = json && typeof json === 'object' ? json as Record<string, unknown> : null
      const asset = document?.asset && typeof document.asset === 'object' ? document.asset as Record<string, unknown> : null
      if (asset?.version !== '2.0') throw new PhotonCadPreviewAssetError('unsupported-glb-version')
      if ((Array.isArray(document?.images) && document.images.length)
        || (Array.isArray(document?.textures) && document.textures.length)) {
        throw new PhotonCadPreviewAssetError('glb-textures-forbidden')
      }
      declaredBufferLength = validateGlbDocument(document!, maximum)
    } else if (chunkType === 0x004e4942) {
      if (binaryByteLength !== null) throw new PhotonCadPreviewAssetError('invalid-glb-binary-chunk')
      binaryByteLength = chunkLength
    } else {
      throw new PhotonCadPreviewAssetError('unsupported-glb-chunk')
    }
    offset = chunkEnd
  }
  if (offset !== bytes.byteLength || json === null) throw new PhotonCadPreviewAssetError('missing-glb-json')
  if (declaredBufferLength === 0 ? binaryByteLength !== null
    : binaryByteLength === null || binaryByteLength < declaredBufferLength || binaryByteLength > declaredBufferLength + 3) {
    throw new PhotonCadPreviewAssetError('glb-buffer-length-mismatch')
  }
  return { byteLength: bytes.byteLength, jsonByteLength }
}

async function sha256(arrayBuffer: ArrayBuffer) {
  if (!globalThis.crypto?.subtle) throw new PhotonCadPreviewAssetError('digest-verifier-unavailable')
  const digest = await globalThis.crypto.subtle.digest('SHA-256', arrayBuffer)
  return `sha256:${Array.from(new Uint8Array(digest), (value) => value.toString(16).padStart(2, '0')).join('')}`
}

async function readBoundedResponse(response: Response, expectedBytes: number, signal: AbortSignal) {
  if (!response.body) {
    const bytes = await response.arrayBuffer()
    if (bytes.byteLength !== expectedBytes) throw new PhotonCadPreviewAssetError('download-size-mismatch')
    return bytes
  }
  const reader = response.body.getReader()
  const output = new Uint8Array(expectedBytes)
  let offset = 0
  try {
    while (true) {
      if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
      const chunk = await reader.read()
      if (chunk.done) break
      if (offset + chunk.value.byteLength > expectedBytes) {
        await reader.cancel('asset-size-out-of-bounds')
        throw new PhotonCadPreviewAssetError('download-size-mismatch')
      }
      output.set(chunk.value, offset)
      offset += chunk.value.byteLength
    }
  } finally {
    reader.releaseLock()
  }
  if (offset !== expectedBytes) throw new PhotonCadPreviewAssetError('download-size-mismatch')
  return output.buffer
}

export async function fetchVerifiedPhotonCadGlb(
  asset: PhotonCadPreviewAsset,
  maximumBytes: number,
  signal: AbortSignal,
  fetcher: typeof fetch = fetch,
) {
  const maximum = photonCadPreviewMaximumBytes(maximumBytes)
  const response = await fetcher(asset.url, {
    method: 'GET',
    credentials: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    referrerPolicy: 'no-referrer',
    signal,
    headers: { Accept: PHOTON_CAD_PREVIEW_MEDIA_TYPE },
  })
  if (!response.ok || response.redirected) throw new PhotonCadPreviewAssetError('asset-download-failed')
  const mediaType = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase()
  if (mediaType !== PHOTON_CAD_PREVIEW_MEDIA_TYPE) throw new PhotonCadPreviewAssetError('download-media-type-mismatch')
  const declaredLength = Number(response.headers.get('content-length'))
  if (!Number.isSafeInteger(declaredLength) || declaredLength !== asset.byteLength || declaredLength > maximum) {
    throw new PhotonCadPreviewAssetError('download-size-mismatch')
  }
  const bytes = await readBoundedResponse(response, asset.byteLength, signal)
  if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
  if (bytes.byteLength !== asset.byteLength || bytes.byteLength > maximum) throw new PhotonCadPreviewAssetError('download-size-mismatch')
  const actualDigest = (await sha256(bytes)).toLowerCase()
  if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
  if (actualDigest !== photonCadCanonicalDigest(asset.contentDigest)) {
    throw new PhotonCadPreviewAssetError('download-digest-mismatch')
  }
  validatePhotonCadGlb(bytes, maximum)
  return bytes
}

type Disposable = { dispose?: () => void; isTexture?: boolean }
type DisposableNode = { geometry?: Disposable; material?: Disposable | Disposable[]; traverse?: (visit: (node: DisposableNode) => void) => void }

function disposeOnce(value: Disposable | null | undefined, disposed: Set<object>) {
  if (!value || typeof value !== 'object' || disposed.has(value)) return
  disposed.add(value)
  value.dispose?.()
}

function disposeTextures(material: Disposable, disposed: Set<object>) {
  const pending: unknown[] = Object.values(material)
  const inspected = new Set<object>()
  let count = 0
  while (pending.length && count++ < 4_096) {
    const value = pending.pop()
    if (!value || typeof value !== 'object' || inspected.has(value)) continue
    inspected.add(value)
    if ((value as Disposable).isTexture) {
      disposeOnce(value as Disposable, disposed)
      continue
    }
    if (Array.isArray(value)) pending.push(...value)
    else if (count < 2_048) pending.push(...Object.values(value))
  }
}

export function disposePhotonCadObject(root: DisposableNode | null | undefined) {
  if (!root) return
  const disposed = new Set<object>()
  const visit = (node: DisposableNode) => {
    disposeOnce(node.geometry, disposed)
    const materials = Array.isArray(node.material) ? node.material : node.material ? [node.material] : []
    for (const material of materials) {
      disposeTextures(material, disposed)
      disposeOnce(material, disposed)
    }
  }
  if (root.traverse) root.traverse(visit)
  else visit(root)
}
