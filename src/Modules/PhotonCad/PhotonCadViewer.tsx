import {
  Box,
  BoxSelect,
  CircleAlert,
  Eye,
  Focus,
  Layers3,
  LoaderCircle,
  Maximize2,
  RotateCcw,
  ScanLine,
  Split,
} from 'lucide-react'
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
} from 'react'
import type {
  Box3,
  BoxHelper,
  Group,
  LineSegments,
  Material,
  Object3D,
  PerspectiveCamera,
  Scene,
  Vector3,
  WebGLRenderer,
} from 'three'
import type { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import {
  isPhotonCadIdentifier,
  photonCadDisplayText,
  type PhotonCadEntity,
  type PhotonCadPreviewReceipt,
} from './PhotonCadContract'
import {
  PHOTON_CAD_PREVIEW_DEFAULT_MAXIMUM_BYTES,
  PhotonCadLoadGuard,
  PhotonCadPreviewAssetError,
  disposePhotonCadObject,
  fetchVerifiedPhotonCadGlb,
  photonCadCanonicalDigest,
  photonCadPreviewMaximumBytes,
  validatePhotonCadPreviewAsset,
  validatePhotonCadPreviewReceiptForViewer,
  type PhotonCadPreviewAssetPolicy,
  type PhotonCadPreviewAssetResolver,
} from './PhotonCadPreviewAsset'
import './PhotonCadViewer.css'

export interface PhotonCadViewerProps {
  receipt?: PhotonCadPreviewReceipt | null
  entities?: readonly PhotonCadEntity[]
  resolveAsset?: PhotonCadPreviewAssetResolver
  allowedAssetOrigin?: string
  assetPathPrefix?: string
  maximumBytes?: number
  loadTimeoutMs?: number
  selectedEntityIds?: readonly string[]
  onSelectionChange?: (entityIds: string[]) => void
  className?: string
}

type ViewerStatus = 'unavailable' | 'resolving' | 'downloading' | 'parsing' | 'ready' | 'error'

type ViewerStatistics = {
  objects: number
  meshes: number
  triangles: number
  taggedEntities: number
}

type MaterialState = {
  transparent: boolean
  opacity: number
  depthWrite: boolean
}

type ExplodeEntry = {
  object: Object3D
  position: Vector3
  offset: Vector3
}

type ViewerRuntime = {
  THREE: typeof import('three')
  scene: Scene
  camera: PerspectiveCamera
  renderer: WebGLRenderer
  controls: OrbitControls
  model: Group
  bounds: Box3
  taggedObjects: Map<string, Object3D>
  objectIds: Map<Object3D, string>
  baseVisibility: Map<Object3D, boolean>
  materialStates: Map<Material, MaterialState>
  selectionHelpers: BoxHelper[]
  edgeLines: LineSegments[]
  explodeEntries: ExplodeEntry[]
  resizeObserver: ResizeObserver | null
  resizeFallback: (() => void) | null
  canvasClick: (event: MouseEvent) => void
  contextLost: (event: Event) => void
  render: () => void
}

const MAXIMUM_OBJECTS = 100_000
const MAXIMUM_MESHES = 20_000
const MAXIMUM_TRIANGLES = 10_000_000
const MAXIMUM_EDGE_MESHES = 5_000
const MAXIMUM_EDGE_TRIANGLES = 2_000_000
const MAXIMUM_EXPLODE_OBJECTS = 10_000
const MAXIMUM_CANVAS_DIMENSION = 4_096
const DEFAULT_LOAD_TIMEOUT_MS = 120_000
export const PHOTON_CAD_VIEWER_ENTITY_TAG = 'photonEntityId' as const
const EMPTY_ENTITIES: readonly PhotonCadEntity[] = []

function statusCopy(status: ViewerStatus) {
  if (status === 'resolving') return 'Resolving the opaque preview asset…'
  if (status === 'downloading') return 'Downloading and verifying the bounded GLB…'
  if (status === 'parsing') return 'Building the verified scene…'
  if (status === 'ready') return 'Verified preview ready'
  if (status === 'error') return 'Preview unavailable. No geometry evidence is shown.'
  return 'No verified preview asset is available.'
}

function safeSelection(values: readonly string[] | undefined) {
  return [...new Set((values ?? []).filter((value) => isPhotonCadIdentifier(value)))].slice(0, 10_000)
}

function boundedViewerEntities(entities: readonly PhotonCadEntity[]) {
  const result: PhotonCadEntity[] = []
  const seen = new Set<string>()
  for (const entity of entities) {
    if (result.length >= MAXIMUM_OBJECTS) break
    if (!isPhotonCadIdentifier(entity.id) || seen.has(entity.id)) continue
    seen.add(entity.id)
    result.push(entity)
  }
  return result
}

function materialsFor(object: Object3D) {
  const material = (object as Object3D & { material?: Material | Material[] }).material
  return Array.isArray(material) ? material : material ? [material] : []
}

function inspectScene(THREE: typeof import('three'), model: Group, receipt: PhotonCadPreviewReceipt): ViewerStatistics {
  let objects = 0
  let meshes = 0
  let triangles = 0
  model.traverse((object) => {
    objects += 1
    if (objects > MAXIMUM_OBJECTS) throw new PhotonCadPreviewAssetError('scene-object-limit')
    const mesh = object as Object3D & { isMesh?: boolean; geometry?: { index?: { count: number } | null; attributes?: { position?: { count: number } } } }
    if (!mesh.isMesh || !mesh.geometry) return
    meshes += 1
    if (meshes > MAXIMUM_MESHES) throw new PhotonCadPreviewAssetError('scene-mesh-limit')
    const count = mesh.geometry.index?.count ?? mesh.geometry.attributes?.position?.count ?? 0
    triangles += Math.floor(count / 3)
    if (!Number.isFinite(triangles) || triangles > MAXIMUM_TRIANGLES) throw new PhotonCadPreviewAssetError('scene-triangle-limit')
  })
  const bounds = new THREE.Box3().setFromObject(model)
  if (bounds.isEmpty()) throw new PhotonCadPreviewAssetError('empty-preview-scene')
  const size = bounds.getSize(new THREE.Vector3())
  const boundCoordinates = [bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z]
  if (![size.x, size.y, size.z].every(Number.isFinite)
    || boundCoordinates.some((coordinate) => !Number.isFinite(coordinate) || Math.abs(coordinate) > 1_000_000_000)
    || Math.max(size.x, size.y, size.z) > 1_000_000_000) {
    throw new PhotonCadPreviewAssetError('scene-bounds-limit')
  }
  const declaredMinimum = new THREE.Vector3(receipt.bounds.minimum.x, receipt.bounds.minimum.y, receipt.bounds.minimum.z)
  const declaredMaximum = new THREE.Vector3(receipt.bounds.maximum.x, receipt.bounds.maximum.y, receipt.bounds.maximum.z)
  const declaredSize = declaredMaximum.clone().sub(declaredMinimum)
  const tolerance = Math.max(declaredSize.length() * .01, 1e-6)
  if (bounds.min.x < declaredMinimum.x - tolerance || bounds.min.y < declaredMinimum.y - tolerance || bounds.min.z < declaredMinimum.z - tolerance
    || bounds.max.x > declaredMaximum.x + tolerance || bounds.max.y > declaredMaximum.y + tolerance || bounds.max.z > declaredMaximum.z + tolerance) {
    throw new PhotonCadPreviewAssetError('scene-receipt-bounds-mismatch')
  }
  return { objects, meshes, triangles, taggedEntities: 0 }
}

function modelObjects(runtime: ViewerRuntime, entityIds: readonly string[]) {
  const objects = entityIds.map((id) => runtime.taggedObjects.get(id)).filter((value): value is Object3D => Boolean(value))
  return objects.length ? objects : entityIds.length ? [] : [runtime.model]
}

function fitObjects(runtime: ViewerRuntime, objects: readonly Object3D[], resetDirection = false) {
  const { THREE, camera, controls } = runtime
  const bounds = new THREE.Box3()
  for (const object of objects) bounds.expandByObject(object)
  if (bounds.isEmpty()) return
  const center = bounds.getCenter(new THREE.Vector3())
  const size = bounds.getSize(new THREE.Vector3())
  const radius = Math.max(size.length() / 2, 0.001)
  const direction = resetDirection
    ? new THREE.Vector3(1, .72, 1).normalize()
    : camera.position.clone().sub(controls.target).normalize()
  if (!Number.isFinite(direction.lengthSq()) || direction.lengthSq() < .001) direction.set(1, .72, 1).normalize()
  const distance = Math.max(radius / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * 1.3, radius * 2)
  camera.position.copy(center).addScaledVector(direction, distance)
  camera.near = Math.max(distance / 10_000, .0001)
  camera.far = Math.max(distance * 100, 10)
  camera.updateProjectionMatrix()
  controls.target.copy(center)
  controls.update()
  runtime.render()
}

function clearSelectionHelpers(runtime: ViewerRuntime) {
  for (const helper of runtime.selectionHelpers) {
    helper.removeFromParent()
    disposePhotonCadObject(helper)
  }
  runtime.selectionHelpers = []
}

function applySelection(runtime: ViewerRuntime, entityIds: readonly string[]) {
  clearSelectionHelpers(runtime)
  if (!entityIds.length) {
    runtime.render()
    return
  }
  for (const object of modelObjects(runtime, entityIds)) {
    if (object === runtime.model) continue
    const helper = new runtime.THREE.BoxHelper(object, 0x65d8bd)
    helper.material.depthTest = false
    helper.material.transparent = true
    helper.material.opacity = .92
    helper.renderOrder = 10
    runtime.scene.add(helper)
    runtime.selectionHelpers.push(helper)
  }
  runtime.render()
}

function applyIsolation(runtime: ViewerRuntime, isolated: boolean, entityIds: readonly string[]) {
  const included = new Set<Object3D>()
  const expanded = new Set<Object3D>()
  if (isolated) {
    for (const id of entityIds) {
      const selected = runtime.taggedObjects.get(id)
      if (!selected) continue
      let ancestor: Object3D | null = selected
      while (ancestor) {
        included.add(ancestor)
        ancestor = ancestor.parent
      }
      const pending = [selected]
      while (pending.length) {
        const object = pending.pop()!
        if (expanded.has(object)) continue
        expanded.add(object)
        included.add(object)
        pending.push(...object.children)
      }
    }
  }
  for (const [object, baseline] of runtime.baseVisibility) {
    object.visible = baseline && (!isolated || included.has(object))
  }
  runtime.render()
}

function applyXray(runtime: ViewerRuntime, active: boolean) {
  runtime.model.traverse((object) => {
    if (object.userData?.photonCadViewerEdge === true) return
    for (const material of materialsFor(object)) {
      if (!runtime.materialStates.has(material)) {
        runtime.materialStates.set(material, {
          transparent: material.transparent,
          opacity: material.opacity,
          depthWrite: material.depthWrite,
        })
      }
      const state = runtime.materialStates.get(material)!
      material.transparent = active ? true : state.transparent
      material.opacity = active ? Math.min(state.opacity, .24) : state.opacity
      material.depthWrite = active ? false : state.depthWrite
      material.needsUpdate = true
    }
  })
  runtime.render()
}

function clearEdges(runtime: ViewerRuntime) {
  for (const line of runtime.edgeLines) {
    for (const material of materialsFor(line)) runtime.materialStates.delete(material)
    line.removeFromParent()
    disposePhotonCadObject(line)
  }
  runtime.edgeLines = []
}

function applyEdges(runtime: ViewerRuntime, active: boolean) {
  clearEdges(runtime)
  if (!active) {
    runtime.render()
    return true
  }
  let count = 0
  let triangleCount = 0
  let failed = false
  let unsupported = false
  runtime.model.traverse((object) => {
    const mesh = object as Object3D & { isMesh?: boolean; geometry?: ConstructorParameters<typeof runtime.THREE.EdgesGeometry>[0] }
    if (failed || !mesh.isMesh || !mesh.geometry) return
    if (count >= MAXIMUM_EDGE_MESHES) {
      unsupported = true
      return
    }
    count += 1
    const source = mesh.geometry as typeof mesh.geometry & { index?: { count: number } | null; attributes?: { position?: { count: number } } }
    const meshTriangles = Math.floor((source.index?.count ?? source.attributes?.position?.count ?? 0) / 3)
    if (triangleCount + meshTriangles > MAXIMUM_EDGE_TRIANGLES) {
      unsupported = true
      return
    }
    triangleCount += meshTriangles
    try {
      const geometry = new runtime.THREE.EdgesGeometry(mesh.geometry, 24)
      const material = new runtime.THREE.LineBasicMaterial({ color: 0x263447, transparent: true, opacity: .72 })
      const line = new runtime.THREE.LineSegments(geometry, material)
      line.userData.photonCadViewerEdge = true
      line.raycast = () => undefined
      line.renderOrder = 4
      object.add(line)
      runtime.edgeLines.push(line)
    } catch {
      failed = true
      clearEdges(runtime)
    }
  })
  if (failed || unsupported) clearEdges(runtime)
  runtime.render()
  return !failed && !unsupported
}

function prepareExplodeEntries(runtime: ViewerRuntime) {
  const center = runtime.bounds.getCenter(new runtime.THREE.Vector3())
  const scale = Math.max(runtime.bounds.getSize(new runtime.THREE.Vector3()).length() * .18, .001)
  const tagged = new Set(runtime.taggedObjects.values())
  const hasTaggedDescendant = new Set<Object3D>()
  for (const object of tagged) {
    let parent = object.parent
    while (parent) {
      if (tagged.has(parent)) hasTaggedDescendant.add(parent)
      parent = parent.parent
    }
  }
  const leaves = [...tagged].filter((object) => !hasTaggedDescendant.has(object))
  if (leaves.length > MAXIMUM_EXPLODE_OBJECTS) {
    runtime.explodeEntries = []
    return false
  }
  runtime.explodeEntries = leaves.map((object, index) => {
    object.updateWorldMatrix(true, false)
    const worldCenter = new runtime.THREE.Box3().setFromObject(object).getCenter(new runtime.THREE.Vector3())
    const direction = worldCenter.sub(center)
    if (direction.lengthSq() < 1e-10) direction.set((index % 3) - 1, ((index + 1) % 3) - 1, ((index + 2) % 3) - 1)
    direction.normalize().multiplyScalar(scale)
    if (object.parent) {
      const origin = object.parent.worldToLocal(new runtime.THREE.Vector3())
      const endpoint = object.parent.worldToLocal(direction.clone())
      direction.copy(endpoint.sub(origin))
    }
    return { object, position: object.position.clone(), offset: direction }
  })
  return leaves.length > 1
}

function applyExplode(runtime: ViewerRuntime, value: number) {
  for (const entry of runtime.explodeEntries) entry.object.position.copy(entry.position).addScaledVector(entry.offset, value)
  runtime.model.updateMatrixWorld(true)
  for (const helper of runtime.selectionHelpers) helper.update()
  runtime.render()
}

function teardownRuntime(runtime: ViewerRuntime | null) {
  if (!runtime) return
  runtime.resizeObserver?.disconnect()
  if (runtime.resizeFallback) window.removeEventListener('resize', runtime.resizeFallback)
  runtime.renderer.domElement.removeEventListener('click', runtime.canvasClick)
  runtime.renderer.domElement.removeEventListener('webglcontextlost', runtime.contextLost)
  runtime.controls.removeEventListener('change', runtime.render)
  runtime.controls.dispose()
  clearSelectionHelpers(runtime)
  clearEdges(runtime)
  disposePhotonCadObject(runtime.scene)
  runtime.scene.clear()
  runtime.renderer.renderLists.dispose()
  runtime.renderer.dispose()
  runtime.renderer.forceContextLoss()
  runtime.renderer.domElement.remove()
}

export function PhotonCadViewer({
  receipt = null,
  entities = EMPTY_ENTITIES,
  resolveAsset,
  allowedAssetOrigin,
  assetPathPrefix,
  maximumBytes = PHOTON_CAD_PREVIEW_DEFAULT_MAXIMUM_BYTES,
  loadTimeoutMs = DEFAULT_LOAD_TIMEOUT_MS,
  selectedEntityIds,
  onSelectionChange,
  className = '',
}: PhotonCadViewerProps) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const runtimeRef = useRef<ViewerRuntime | null>(null)
  const guardRef = useRef(new PhotonCadLoadGuard())
  const [status, setStatus] = useState<ViewerStatus>('unavailable')
  const [statistics, setStatistics] = useState<ViewerStatistics | null>(null)
  const [selectableEntityIds, setSelectableEntityIds] = useState<string[]>([])
  const [internalSelection, setInternalSelection] = useState<string[]>(() => safeSelection(selectedEntityIds))
  const [isolated, setIsolated] = useState(false)
  const [edges, setEdges] = useState(false)
  const [xray, setXray] = useState(false)
  const [explode, setExplode] = useState(0)
  const [explodeAvailable, setExplodeAvailable] = useState(false)
  const [featureNotice, setFeatureNotice] = useState('')
  const featureState = useRef({ isolated, edges, xray, explode })
  featureState.current = { isolated, edges, xray, explode }
  const selection = selectedEntityIds === undefined ? internalSelection : safeSelection(selectedEntityIds)
  const selectionKey = selection.join('\u0000')
  const viewerEntities = useMemo(() => boundedViewerEntities(entities), [entities])
  const entityMap = useMemo(() => new Map(viewerEntities.map((entity) => [entity.id, entity])), [viewerEntities])
  const entityMapRef = useRef(entityMap)
  entityMapRef.current = entityMap
  const selectionRef = useRef(selection)
  selectionRef.current = selection
  const controlledSelectionRef = useRef(selectedEntityIds !== undefined)
  controlledSelectionRef.current = selectedEntityIds !== undefined
  const selectionCallbackRef = useRef(onSelectionChange)
  selectionCallbackRef.current = onSelectionChange
  const receiptKey = receipt ? JSON.stringify([
    receipt.previewId,
    receipt.projectId,
    receipt.revision,
    receipt.contentDigest,
    receipt.units,
    receipt.entityCount,
    receipt.bounds.minimum.x,
    receipt.bounds.minimum.y,
    receipt.bounds.minimum.z,
    receipt.bounds.maximum.x,
    receipt.bounds.maximum.y,
    receipt.bounds.maximum.z,
  ]) : ''

  useEffect(() => {
    if (selectedEntityIds !== undefined) setInternalSelection(safeSelection(selectedEntityIds))
  }, [selectedEntityIds])

  function publishSelection(next: readonly string[]) {
    const normalized = safeSelection(next)
    if (!controlledSelectionRef.current) setInternalSelection(normalized)
    selectionCallbackRef.current?.(normalized)
  }

  function toggleSelection(id: string, additive = true) {
    if (!isPhotonCadIdentifier(id)) return
    const current = new Set(selectionRef.current)
    if (!additive) current.clear()
    if (current.has(id)) current.delete(id)
    else current.add(id)
    publishSelection([...current])
  }

  useEffect(() => {
    const runtime = runtimeRef.current
    if (!runtime) return
    applySelection(runtime, selection)
    const mappedSelection = selection.filter((id) => runtime.taggedObjects.has(id))
    if (isolated && mappedSelection.length === 0) {
      applyIsolation(runtime, false, [])
      setIsolated(false)
    } else applyIsolation(runtime, isolated, mappedSelection)
  }, [selectionKey, isolated])

  useEffect(() => {
    if (!runtimeRef.current) return
    if (!applyEdges(runtimeRef.current, edges)) {
      setFeatureNotice('Edges stayed off because the complete scene exceeds the bounded edge budget.')
      setEdges(false)
    } else if (edges) setFeatureNotice('')
  }, [edges])
  useEffect(() => { if (runtimeRef.current) applyXray(runtimeRef.current, xray) }, [xray])
  useEffect(() => { if (runtimeRef.current) applyExplode(runtimeRef.current, explode) }, [explode])

  useEffect(() => {
    const host = containerRef.current
    teardownRuntime(runtimeRef.current)
    runtimeRef.current = null
    setStatistics(null)
    setSelectableEntityIds([])
    setIsolated(false)
    setEdges(false)
    setXray(false)
    setExplode(0)
    setExplodeAvailable(false)
    setFeatureNotice('')
    const guard = guardRef.current
    const load = guard.begin()
    if (!host || !receipt || !resolveAsset || !allowedAssetOrigin) {
      setStatus('unavailable')
      return () => guard.close()
    }
    if (!Number.isSafeInteger(loadTimeoutMs) || loadTimeoutMs < 5_000 || loadTimeoutMs > 300_000) {
      setStatus('error')
      return () => guard.close()
    }
    let runtime: ViewerRuntime | null = null
    let pendingScene: Scene | null = null
    let pendingModel: Group | null = null
    let pendingRenderer: WebGLRenderer | null = null
    let pendingControls: OrbitControls | null = null
    const current = () => guard.isCurrent(load.generation)
    const loadTimeout = window.setTimeout(() => {
      if (guard.abort(load.generation)) setStatus('error')
    }, loadTimeoutMs)

    void (async () => {
      try {
        validatePhotonCadPreviewReceiptForViewer(receipt)
        const boundedMaximumBytes = photonCadPreviewMaximumBytes(maximumBytes)
        const expectedDigest = photonCadCanonicalDigest(receipt.contentDigest)
        setStatus('resolving')
        const policy: PhotonCadPreviewAssetPolicy = { allowedOrigin: allowedAssetOrigin, pathPrefix: assetPathPrefix, maximumBytes: boundedMaximumBytes }
        const rawAsset = await resolveAsset({
          previewId: receipt.previewId,
          projectId: receipt.projectId,
          revision: receipt.revision,
          expectedDigest,
          maximumBytes: boundedMaximumBytes,
        }, load.signal)
        if (!current()) return
        const asset = validatePhotonCadPreviewAsset(rawAsset, expectedDigest, policy)
        setStatus('downloading')
        const bytes = await fetchVerifiedPhotonCadGlb(asset, boundedMaximumBytes, load.signal)
        if (!current()) return
        setStatus('parsing')
        const [THREE, loaderModule, controlsModule] = await Promise.all([
          import('three'),
          import('three/examples/jsm/loaders/GLTFLoader.js'),
          import('three/examples/jsm/controls/OrbitControls.js'),
        ])
        if (!current()) return
        const manager = new THREE.LoadingManager()
        manager.setURLModifier((url) => {
          if (url.startsWith('blob:')) return url
          throw new PhotonCadPreviewAssetError('external-loader-resource-forbidden')
        })
        const loader = new loaderModule.GLTFLoader(manager)
        const gltf = await loader.parseAsync(bytes, '')
        if (!current()) {
          disposePhotonCadObject(gltf.scene)
          return
        }
        const model = gltf.scene
        pendingModel = model
        const sceneStatistics = inspectScene(THREE, model, receipt)
        if (!host.isConnected) {
          disposePhotonCadObject(model)
          return
        }
        const scene = new THREE.Scene()
        pendingScene = scene
        scene.background = new THREE.Color(0x080b11)
        scene.add(model)
        scene.add(new THREE.HemisphereLight(0xe9f4ff, 0x303843, 2.2))
        const keyLight = new THREE.DirectionalLight(0xffffff, 2.7)
        keyLight.position.set(5, 8, 6)
        scene.add(keyLight)
        const fillLight = new THREE.DirectionalLight(0x8fb7ff, 1.25)
        fillLight.position.set(-6, 2, -4)
        scene.add(fillLight)
        const camera = new THREE.PerspectiveCamera(42, 1, .001, 100_000)
        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' })
        pendingRenderer = renderer
        renderer.outputColorSpace = THREE.SRGBColorSpace
        renderer.toneMapping = THREE.ACESFilmicToneMapping
        renderer.toneMappingExposure = 1.05
        const pixelRatio = Math.min(window.devicePixelRatio || 1, 2)
        renderer.setPixelRatio(pixelRatio)
        renderer.domElement.className = 'pcv-canvas'
        renderer.domElement.setAttribute('aria-hidden', 'true')
        host.replaceChildren(renderer.domElement)
        const controls = new controlsModule.OrbitControls(camera, renderer.domElement)
        pendingControls = controls
        controls.enableDamping = false
        controls.screenSpacePanning = true
        controls.minDistance = .0001
        controls.maxDistance = 1_000_000_000
        const bounds = new THREE.Box3().setFromObject(model)
        const taggedObjects = new Map<string, Object3D>()
        const objectIds = new Map<Object3D, string>()
        const baseVisibility = new Map<Object3D, boolean>()
        model.traverse((object) => {
          const candidate = typeof object.userData?.[PHOTON_CAD_VIEWER_ENTITY_TAG] === 'string' ? object.userData[PHOTON_CAD_VIEWER_ENTITY_TAG] : ''
          const tagIsUsable = isPhotonCadIdentifier(candidate)
            && (!entityMapRef.current.size || entityMapRef.current.has(candidate))
            && !taggedObjects.has(candidate)
          const entity = tagIsUsable ? entityMapRef.current.get(candidate) : undefined
          const baseline = object.visible && (entity ? entity.visible && !entity.suppressed : true)
          baseVisibility.set(object, baseline)
          object.visible = baseline
          if (!tagIsUsable) return
          taggedObjects.set(candidate, object)
          objectIds.set(object, candidate)
        })
        const canvasClick = (event: MouseEvent) => {
          const active = runtimeRef.current
          if (!active) return
          const rect = active.renderer.domElement.getBoundingClientRect()
          if (!rect.width || !rect.height) return
          const pointer = new THREE.Vector2(
            ((event.clientX - rect.left) / rect.width) * 2 - 1,
            -((event.clientY - rect.top) / rect.height) * 2 + 1,
          )
          const raycaster = new THREE.Raycaster()
          raycaster.setFromCamera(pointer, active.camera)
          const hit = raycaster.intersectObject(active.model, true).find((intersection) => {
            let object: Object3D | null = intersection.object
            while (object) {
              if (active.objectIds.has(object)) return true
              object = object.parent
            }
            return false
          })
          if (!hit) {
            if (!event.ctrlKey && !event.metaKey) publishSelection([])
            return
          }
          let object: Object3D | null = hit.object
          while (object && !active.objectIds.has(object)) object = object.parent
          const id = object ? active.objectIds.get(object) : undefined
          if (id) toggleSelection(id, event.ctrlKey || event.metaKey)
        }
        const contextLost = (event: Event) => {
          event.preventDefault()
          const active = runtimeRef.current
          if (active) active.controls.enabled = false
          setStatus('error')
        }
        renderer.domElement.addEventListener('click', canvasClick)
        renderer.domElement.addEventListener('webglcontextlost', contextLost)
        const render = () => {
          for (const helper of runtime?.selectionHelpers ?? []) helper.update()
          renderer.render(scene, camera)
        }
        controls.addEventListener('change', render)
        const resize = () => {
          const logicalLimit = Math.floor(MAXIMUM_CANVAS_DIMENSION / pixelRatio)
          const width = Math.max(1, Math.min(host.clientWidth, logicalLimit))
          const height = Math.max(1, Math.min(host.clientHeight, logicalLimit))
          camera.aspect = width / height
          camera.updateProjectionMatrix()
          renderer.setSize(width, height, false)
          render()
        }
        resize()
        const resizeObserver = typeof ResizeObserver === 'function' ? new ResizeObserver(resize) : null
        const resizeFallback = resizeObserver ? null : resize
        resizeObserver?.observe(host)
        if (resizeFallback) window.addEventListener('resize', resizeFallback)
        runtime = {
          THREE,
          scene,
          camera,
          renderer,
          controls,
          model,
          bounds,
          taggedObjects,
          objectIds,
          baseVisibility,
          materialStates: new Map(),
          selectionHelpers: [],
          edgeLines: [],
          explodeEntries: [],
          resizeObserver,
          resizeFallback,
          canvasClick,
          contextLost,
          render,
        }
        runtimeRef.current = runtime
        pendingScene = null
        pendingModel = null
        pendingRenderer = null
        pendingControls = null
        setExplodeAvailable(prepareExplodeEntries(runtime))
        fitObjects(runtime, [model], true)
        controls.saveState()
        applySelection(runtime, selectionRef.current)
        applyIsolation(runtime, featureState.current.isolated, selectionRef.current)
        if (!applyEdges(runtime, featureState.current.edges)) setEdges(false)
        applyXray(runtime, featureState.current.xray)
        applyExplode(runtime, featureState.current.explode)
        render()
        window.clearTimeout(loadTimeout)
        setStatistics({ ...sceneStatistics, taggedEntities: taggedObjects.size })
        setSelectableEntityIds([...taggedObjects.keys()])
        setStatus('ready')
      } catch (error) {
        window.clearTimeout(loadTimeout)
        if (!current() || (error instanceof DOMException && error.name === 'AbortError')) return
        teardownRuntime(runtime)
        if (!runtime) {
          pendingControls?.dispose()
          if (pendingScene) disposePhotonCadObject(pendingScene)
          else if (pendingModel) disposePhotonCadObject(pendingModel)
          pendingRenderer?.renderLists.dispose()
          pendingRenderer?.dispose()
          pendingRenderer?.forceContextLoss()
          pendingRenderer?.domElement.remove()
        }
        runtime = null
        runtimeRef.current = null
        setStatistics(null)
        setSelectableEntityIds([])
        setStatus('error')
      }
    })()

    return () => {
      window.clearTimeout(loadTimeout)
      guard.close()
      teardownRuntime(runtime)
      if (runtimeRef.current === runtime) runtimeRef.current = null
    }
  }, [receiptKey, resolveAsset, allowedAssetOrigin, assetPathPrefix, maximumBytes, loadTimeoutMs])

  function fitSelection() {
    const runtime = runtimeRef.current
    if (runtime) fitObjects(runtime, modelObjects(runtime, selection))
  }

  function resetView() {
    const runtime = runtimeRef.current
    if (!runtime) return
    setIsolated(false)
    setExplode(0)
    applyIsolation(runtime, false, selection)
    applyExplode(runtime, 0)
    fitObjects(runtime, [runtime.model], true)
  }

  function handleKeyboard(event: KeyboardEvent<HTMLElement>) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLButtonElement) return
    if (event.key.toLowerCase() === 'f') {
      event.preventDefault()
      fitSelection()
    } else if (event.key === 'Home') {
      event.preventDefault()
      resetView()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      publishSelection([])
    }
  }

  const ready = status === 'ready'
  const selectedSet = new Set(selection)
  const selectableSet = new Set(selectableEntityIds)
  const selectedGeometryCount = selection.filter((id) => selectableSet.has(id)).length
  return (
    <section className={`photon-cad-viewer ${className}`.trim()} data-status={status} onKeyDown={handleKeyboard}>
      <header className="pcv-toolbar" aria-label="3D preview controls">
        <button type="button" disabled={!ready || (selection.length > 0 && selectedGeometryCount === 0)} onClick={fitSelection} title="Fit selection (F)"><Focus size={13} /><span>Fit</span></button>
        <button type="button" disabled={!ready} onClick={resetView} title="Reset view (Home)"><RotateCcw size={13} /><span>Reset</span></button>
        <button type="button" disabled={!ready || selectedGeometryCount === 0} aria-pressed={isolated} onClick={() => setIsolated((value) => !value)}><BoxSelect size={13} /><span>{isolated ? 'Show all' : 'Isolate'}</span></button>
        <button type="button" disabled={!ready} aria-pressed={edges} title="Edges are enabled only when the complete scene remains within the bounded edge budget" onClick={() => setEdges((value) => !value)}><ScanLine size={13} /><span>Edges</span></button>
        <button type="button" disabled={!ready} aria-pressed={xray} onClick={() => setXray((value) => !value)}><Eye size={13} /><span>X-ray</span></button>
        <label className="pcv-explode" title={explodeAvailable ? 'Separate independently tagged leaf parts' : 'Explode requires two to 10,000 tagged leaf parts'}><Split size={13} /><span>Explode</span><input type="range" min="0" max="1" step="0.05" value={explode} disabled={!ready || !explodeAvailable} onChange={(event) => setExplode(Number(event.target.value))} /><output>{Math.round(explode * 100)}%</output></label>
      </header>
      <div className="pcv-content">
        <div className="pcv-stage" ref={containerRef} tabIndex={0} aria-label="Interactive verified 3D preview. Press F to fit, Home to reset, or Escape to clear selection." />
        <aside className="pcv-summary" aria-label="Non-canvas model summary">
          <header><Layers3 size={13} /><strong>Model summary</strong><span>{viewerEntities.length || receipt?.entityCount || 0}</span></header>
          {viewerEntities.length ? (
            <ul>
              {viewerEntities.slice(0, 10_000).map((entity) => (
                <li key={entity.id}>
                  <button type="button" disabled={!ready || !selectableSet.has(entity.id)} aria-pressed={selectedSet.has(entity.id)} onClick={() => toggleSelection(entity.id)}>
                    <Box size={12} /><span><strong>{photonCadDisplayText(entity.name, 180) || 'Unnamed entity'}</strong><small>{photonCadDisplayText(entity.kind, 48)}{entity.suppressed ? ' · suppressed' : !entity.visible ? ' · hidden' : ready && !selectableSet.has(entity.id) ? ' · not in preview' : ''}</small></span>
                  </button>
                </li>
              ))}
            </ul>
          ) : <p>No named entity inventory was supplied. Canvas selection is not a substitute for an accessible model summary.</p>}
          {viewerEntities.length > 10_000 ? <p>Showing the first 10,000 of {viewerEntities.length.toLocaleString()} bounded entities.</p> : null}
        </aside>
      </div>
      {status !== 'ready' ? (
        <div className={`pcv-state ${status}`} role={status === 'error' ? 'alert' : 'status'} aria-live="polite">
          {['resolving', 'downloading', 'parsing'].includes(status) ? <LoaderCircle className="pcv-spin" size={22} /> : <CircleAlert size={22} />}
          <strong>{statusCopy(status)}</strong>
          <p>{status === 'error' ? 'The asset failed closed before it could be treated as visible geometry.' : 'A preview never counts as design verification or release evidence.'}</p>
        </div>
      ) : null}
      <footer className="pcv-status" role="status">
        <span>{featureNotice || statusCopy(status)}</span>
        <span>{statistics ? `${statistics.meshes.toLocaleString()} meshes · ${statistics.triangles.toLocaleString()} triangles` : 'No scene statistics'}</span>
        <span>{statistics ? `${statistics.taggedEntities.toLocaleString()} selectable entities` : 'Selection unavailable'}</span>
      </footer>
    </section>
  )
}
