import { useMemo, useRef, useState, type PointerEvent, type WheelEvent } from 'react'
import { Circle, CornerDownLeft, Grid3X3, Hand, MoveUpRight, MousePointer2, Redo2, Ruler, Square, Undo2, X } from 'lucide-react'
import type { PhotonCadPreviewContext } from './PhotonCadWorkspace'
import { PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID, PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID } from './PhotonCadWorkspace'
import type { PhotonCadSurfacePick } from './PhotonCadViewer'
import './PhotonCadMouseSketcher.css'

type Plane = 'xy' | 'xz' | 'yz'
type Tool = 'select' | 'rectangle' | 'circle' | 'line' | 'dimension'
type Point = { x: number; y: number }
type Profile =
  | { kind: 'rectangle'; start: Point; end: Point }
  | { kind: 'circle'; center: Point; edge: Point }
  | { kind: 'wire'; points: Point[]; closed: boolean; cornerRadii?: number[] }
type SketchEdge = { index: number; start: Point; end: Point }
type SketchSelection = { kind: 'edge'; index: number } | { kind: 'vertex'; index: number } | null
type View = { x: number; y: number; width: number; height: number }

const WIDTH = 800
const HEIGHT = 500
const UNITS_PER_PIXEL = 0.125
const POINT_CLOSE_DISTANCE = 1.5
const EMPTY_VIEW: View = { x: 0, y: 0, width: WIDTH, height: HEIGHT }
const frames = {
  xy: { originMm: { x: 0, y: 0, z: 0 }, xDirection: { x: 1, y: 0, z: 0 }, normal: { x: 0, y: 0, z: 1 } },
  xz: { originMm: { x: 0, y: 0, z: 0 }, xDirection: { x: 1, y: 0, z: 0 }, normal: { x: 0, y: -1, z: 0 } },
  yz: { originMm: { x: 0, y: 0, z: 0 }, xDirection: { x: 0, y: 1, z: 0 }, normal: { x: 1, y: 0, z: 0 } },
} as const

function round(value: number) { return Math.round(value * 100) / 100 }
function distance(left: Point, right: Point) { return Math.hypot(left.x - right.x, left.y - right.y) }
function toCanvas(point: Point) { return { x: WIDTH / 2 + point.x / UNITS_PER_PIXEL, y: HEIGHT / 2 - point.y / UNITS_PER_PIXEL } }
function fromCanvas(point: Point) { return { x: round((point.x - WIDTH / 2) * UNITS_PER_PIXEL), y: round((HEIGHT / 2 - point.y) * UNITS_PER_PIXEL) } }
function rectangleMetrics(profile: Extract<Profile, { kind: 'rectangle' }>) {
  return { width: Math.abs(profile.end.x - profile.start.x), height: Math.abs(profile.end.y - profile.start.y), center: { x: (profile.end.x + profile.start.x) / 2, y: (profile.end.y + profile.start.y) / 2 } }
}
function circleMetrics(profile: Extract<Profile, { kind: 'circle' }>) { return { radius: distance(profile.center, profile.edge), center: profile.center } }
function rectanglePoints(profile: Extract<Profile, { kind: 'rectangle' }>): Point[] {
  const { start, end } = profile
  return [{ x: start.x, y: start.y }, { x: end.x, y: start.y }, { x: end.x, y: end.y }, { x: start.x, y: end.y }]
}
function closedPoints(profile: Profile): Point[] | null {
  if (profile.kind === 'wire' && profile.closed) return profile.points
  if (profile.kind === 'rectangle') return rectanglePoints(profile)
  return null
}
function profileEdges(profile: Profile): SketchEdge[] {
  const points = closedPoints(profile)
  return !points ? [] : points.map((start, index) => ({ index, start, end: points[(index + 1) % points.length] }))
}
function pointSegmentDistance(point: Point, start: Point, end: Point) {
  const dx = end.x - start.x; const dy = end.y - start.y
  const lengthSquared = dx * dx + dy * dy
  if (lengthSquared < .0000001) return distance(point, start)
  const t = Math.max(0, Math.min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared))
  return distance(point, { x: start.x + dx * t, y: start.y + dy * t })
}
function closestEdge(point: Point, edges: SketchEdge[]) {
  return edges.reduce<SketchEdge | null>((closest, edge) => !closest || pointSegmentDistance(point, edge.start, edge.end) < pointSegmentDistance(point, closest.start, closest.end) ? edge : closest, null)
}
function closestVertex(point: Point, vertices: readonly Point[]) {
  return vertices.reduce<{ index: number; distance: number } | null>((closest, vertex, index) => {
    const candidate = distance(point, vertex)
    return !closest || candidate < closest.distance ? { index, distance: candidate } : closest
  }, null)
}
function parallel(left: SketchEdge, right: SketchEdge) {
  const lx = left.end.x - left.start.x; const ly = left.end.y - left.start.y
  const rx = right.end.x - right.start.x; const ry = right.end.y - right.start.y
  const denominator = Math.hypot(lx, ly) * Math.hypot(rx, ry)
  return denominator > .000001 && Math.abs(lx * ry - ly * rx) / denominator < .0001
}
function edgeDistance(left: SketchEdge, right: SketchEdge) {
  const dx = left.end.x - left.start.x; const dy = left.end.y - left.start.y
  return Math.abs((right.start.x - left.start.x) * -dy + (right.start.y - left.start.y) * dx) / Math.hypot(dx, dy)
}
function wirePath(points: readonly Point[], radii?: readonly number[]) {
  if (points.length < 2) return ''
  if (!radii?.some((radius) => radius > 0)) return `${points.map((point, index) => { const canvas = toCanvas(point); return `${index ? 'L' : 'M'} ${canvas.x} ${canvas.y}` }).join(' ')} Z`
  const corners = points.map((corner, index) => {
    const previous = points[(index - 1 + points.length) % points.length]; const next = points[(index + 1) % points.length]
    const previousLength = distance(corner, previous); const nextLength = distance(corner, next); const requestedRadius = radii[index] ?? 0
    if (requestedRadius <= 0 || previousLength < .000001 || nextLength < .000001) return { start: corner, end: corner, radius: 0, sweep: 0 }
    const previousUnit = { x: (previous.x - corner.x) / previousLength, y: (previous.y - corner.y) / previousLength }
    const nextUnit = { x: (next.x - corner.x) / nextLength, y: (next.y - corner.y) / nextLength }
    const angle = Math.acos(Math.max(-1, Math.min(1, previousUnit.x * nextUnit.x + previousUnit.y * nextUnit.y)))
    const offset = Math.min(requestedRadius / Math.tan(angle / 2), previousLength * .45, nextLength * .45)
    return { start: { x: corner.x + previousUnit.x * offset, y: corner.y + previousUnit.y * offset }, end: { x: corner.x + nextUnit.x * offset, y: corner.y + nextUnit.y * offset }, radius: requestedRadius, sweep: previousUnit.x * nextUnit.y - previousUnit.y * nextUnit.x < 0 ? 1 : 0 }
  })
  const first = toCanvas(corners[0].end)
  return corners.map((corner, index) => {
    const start = toCanvas(corner.start); const end = toCanvas(corner.end)
    return `${index === 0 ? `M ${first.x} ${first.y}` : ''} L ${start.x} ${start.y}${corner.radius > 0 ? ` A ${corner.radius / UNITS_PER_PIXEL} ${corner.radius / UNITS_PER_PIXEL} 0 0 ${corner.sweep} ${end.x} ${end.y}` : ''}`
  }).join(' ') + ' Z'
}

export function PhotonCadMouseSketcher({ context, onClose, surfaceFrame }: { context: PhotonCadPreviewContext; onClose: () => void; surfaceFrame?: PhotonCadSurfacePick | null }) {
  const svgRef = useRef<SVGSVGElement | null>(null)
  const [plane, setPlane] = useState<Plane>('xy')
  const [tool, setTool] = useState<Tool>('select')
  const [profile, setProfile] = useState<Profile | null>(null)
  const [dragStart, setDragStart] = useState<Point | null>(null)
  const [wirePoints, setWirePoints] = useState<Point[]>([])
  const [edgeSelection, setEdgeSelection] = useState<SketchEdge | null>(null)
  const [selection, setSelection] = useState<SketchSelection>(null)
  const [dimension, setDimension] = useState<{ fixed: SketchEdge; moving: SketchEdge; value: string } | null>(null)
  const [undoStack, setUndoStack] = useState<(Profile | null)[]>([])
  const [redoStack, setRedoStack] = useState<(Profile | null)[]>([])
  const [view, setView] = useState<View>(EMPTY_VIEW)
  const navigation = useRef<{ pointerId: number; x: number; y: number; view: View } | null>(null)
  const draftOriginal = useRef<Profile | null>(null)
  const [message, setMessage] = useState(surfaceFrame ? 'Selected face is the sketch plane. Draw a closed profile, then use the real Extrude or Cut operation.' : 'Choose a base plane or draw immediately. The centered crosshair is the model origin.')
  const completeProfile = Boolean(profile && (profile.kind !== 'wire' || profile.closed))
  const selectedBody = context.selectedEntityIds.length === 1 ? context.selectedEntityIds[0] : null
  const depth = useMemo(() => !profile ? 10 : profile.kind === 'rectangle' ? Math.max(2, round(Math.max(rectangleMetrics(profile).width, rectangleMetrics(profile).height) / 2)) : profile.kind === 'circle' ? Math.max(2, round(circleMetrics(profile).radius)) : 10, [profile])
  const closed = profile ? closedPoints(profile) : null
  const edges = profile ? profileEdges(profile) : []
  const rect = profile?.kind === 'rectangle' ? rectangleMetrics(profile) : null
  const circle = profile?.kind === 'circle' ? circleMetrics(profile) : null
  const wire = profile?.kind === 'wire' ? profile.points.map(toCanvas) : []

  function coordinate(event: PointerEvent<SVGSVGElement> | WheelEvent<SVGSVGElement>): Point | null {
    const box = svgRef.current?.getBoundingClientRect()
    if (!box?.width || !box.height) return null
    return fromCanvas({ x: view.x + ((event.clientX - box.left) / box.width) * view.width, y: view.y + ((event.clientY - box.top) / box.height) * view.height })
  }
  function canvasCoordinate(event: PointerEvent<SVGSVGElement> | WheelEvent<SVGSVGElement>) {
    const box = svgRef.current?.getBoundingClientRect()
    if (!box?.width || !box.height) return null
    return { x: view.x + ((event.clientX - box.left) / box.width) * view.width, y: view.y + ((event.clientY - box.top) / box.height) * view.height }
  }
  function replaceProfile(next: Profile | null, undoable = true) {
    if (undoable) { setUndoStack((current) => [...current, profile].slice(-32)); setRedoStack([]) }
    setProfile(next)
    setWirePoints(next?.kind === 'wire' ? next.points : [])
    setSelection(null); setEdgeSelection(null); setDimension(null)
  }
  function chooseTool(next: Tool) {
    setTool(next); setEdgeSelection(null); setDimension(null)
    const copy: Record<Tool, string> = {
      select: 'Select an edge or vertex. Use H/V to align a selected wire edge; these edits stay provisional until a CAD operation is accepted.',
      line: 'Click points to draw a line. Click the first point to close the profile. Escape cancels the current wire.',
      rectangle: 'Drag two corners to create a closed rectangle.',
      circle: 'Drag from the center to set a circle radius.',
      dimension: 'Choose two parallel closed-profile edges, then set their perpendicular separation.',
    }
    setMessage(copy[next])
  }
  function choosePlane(next: Plane) { setPlane(next); replaceProfile(null, Boolean(profile)); setMessage(`${next.toUpperCase()} sketch plane selected; its origin is centered.`) }
  function beginNavigation(event: PointerEvent<SVGSVGElement>) {
    const canvas = canvasCoordinate(event); if (!canvas) return
    navigation.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, view }
    svgRef.current?.setPointerCapture(event.pointerId)
    setMessage('Panning view. Release to return to the active sketch tool.')
  }
  function selectAt(point: Point) {
    if (!profile || !closed) { setMessage('No closed profile is available to select. Draw a box, circle, or close a line loop first.'); return }
    if (profile.kind === 'circle') { setSelection({ kind: 'vertex', index: 0 }); setMessage(`Circle selected: radius ${round(circleMetrics(profile).radius)} mm. Diameter constraints are not available in the current runtime.`); return }
    const vertex = closestVertex(point, closed)
    if (vertex && vertex.distance <= 1.5) { setSelection({ kind: 'vertex', index: vertex.index }); setMessage(`Vertex ${vertex.index + 1} selected. Dragging vertices is unavailable; choose a supported edge alignment or dimension.`); return }
    const edge = closestEdge(point, edges)
    if (edge && pointSegmentDistance(point, edge.start, edge.end) <= 1.75) { setSelection({ kind: 'edge', index: edge.index }); setMessage(`Edge ${edge.index + 1} selected. H/V adjusts this provisional wire geometry; dimension needs two parallel edges.`); return }
    setSelection(null); setMessage('Nothing selected. Click an edge or vertex within the closed profile.')
  }
  function begin(event: PointerEvent<SVGSVGElement>) {
    if (context.operationBusy) return
    if (event.button === 1 || event.shiftKey) { event.preventDefault(); beginNavigation(event); return }
    if (event.button !== 0 || navigation.current) return
    const point = coordinate(event); if (!point) return
    if (tool === 'select') { selectAt(point); return }
    if (tool === 'dimension') {
      const candidate = profile ? closestEdge(point, edges) : null
      if (!candidate || pointSegmentDistance(point, candidate.start, candidate.end) > 2) { setMessage('Click directly on a closed profile edge.'); return }
      if (!edgeSelection) { setEdgeSelection(candidate); setSelection({ kind: 'edge', index: candidate.index }); setMessage('One edge selected. Click a distinct parallel edge.'); return }
      if (candidate.index === edgeSelection.index || !parallel(edgeSelection, candidate)) { setEdgeSelection(candidate); setSelection({ kind: 'edge', index: candidate.index }); setMessage('Those edges are not parallel. Choose a distinct parallel edge.'); return }
      setDimension({ fixed: edgeSelection, moving: candidate, value: String(round(edgeDistance(edgeSelection, candidate))) })
      setEdgeSelection(null); setSelection(null); setMessage('Set the perpendicular distance between the two selected parallel edges.')
      return
    }
    if (tool === 'line') {
      if (wirePoints.length >= 3 && distance(point, wirePoints[0]) <= POINT_CLOSE_DISTANCE) { replaceProfile({ kind: 'wire', points: wirePoints, closed: true }); setMessage('Closed wire profile created. It remains provisional until Extrude or Cut is accepted.'); return }
      const next = [...wirePoints, point]; setWirePoints(next); setProfile({ kind: 'wire', points: next, closed: false }); setMessage(next.length < 3 ? 'Click the next point.' : 'Click the first point to close this profile, or Escape to cancel it.')
      return
    }
    draftOriginal.current = profile; setDragStart(point); svgRef.current?.setPointerCapture(event.pointerId)
  }
  function move(event: PointerEvent<SVGSVGElement>) {
    if (navigation.current?.pointerId === event.pointerId) {
      const box = svgRef.current?.getBoundingClientRect(); if (!box) return
      const deltaX = (event.clientX - navigation.current.x) / box.width * navigation.current.view.width
      const deltaY = (event.clientY - navigation.current.y) / box.height * navigation.current.view.height
      setView({ ...navigation.current.view, x: navigation.current.view.x - deltaX, y: navigation.current.view.y - deltaY }); return
    }
    if (!dragStart || (tool !== 'rectangle' && tool !== 'circle')) return
    const point = coordinate(event); if (!point) return
    setProfile(tool === 'rectangle' ? { kind: 'rectangle', start: dragStart, end: point } : { kind: 'circle', center: dragStart, edge: point })
  }
  function end(event: PointerEvent<SVGSVGElement>) {
    if (navigation.current?.pointerId === event.pointerId) { navigation.current = null; if (svgRef.current?.hasPointerCapture(event.pointerId)) svgRef.current.releasePointerCapture(event.pointerId); setMessage('View panned. Scroll to zoom; middle-drag, Shift-drag, or the Pan tool moves the sketch view.'); return }
    if (!dragStart) return
    const point = coordinate(event); setDragStart(null)
    if (svgRef.current?.hasPointerCapture(event.pointerId)) svgRef.current.releasePointerCapture(event.pointerId)
    if (!point) return
    const next = tool === 'rectangle' ? { kind: 'rectangle', start: dragStart, end: point } as const : { kind: 'circle', center: dragStart, edge: point } as const
    if ((next.kind === 'rectangle' && (rectangleMetrics(next).width < .25 || rectangleMetrics(next).height < .25)) || (next.kind === 'circle' && circleMetrics(next).radius < .25)) { setProfile(draftOriginal.current); draftOriginal.current = null; setMessage('That profile is too small. Drag farther and try again.'); return }
    setUndoStack((current) => [...current, draftOriginal.current].slice(-32)); setRedoStack([]); setProfile(next); setSelection(null); draftOriginal.current = null
    setMessage('Closed profile created. Its sketch is provisional; Extrude adds material and Cut removes material only after the sealed CAD host accepts it.')
  }
  function cancelCurrent() {
    if (dimension) { setDimension(null); setMessage('Dimension edit cancelled; sketch geometry was not changed.'); return }
    if (dragStart) { setDragStart(null); setProfile(draftOriginal.current); draftOriginal.current = null; setMessage('Current drag cancelled.'); return }
    if (tool === 'line' && wirePoints.length) { setWirePoints([]); setProfile(null); setMessage('Open wire cancelled.'); return }
    if (selection || edgeSelection) { setSelection(null); setEdgeSelection(null); setMessage('Selection cleared.'); return }
    setMessage('Nothing is pending to cancel.')
  }
  function undo() {
    if (!undoStack.length || context.operationBusy) { setMessage('Nothing local to undo. Accepted CAD operations are not reversed from this sketch canvas.'); return }
    const previous = undoStack[undoStack.length - 1]; setUndoStack((current) => current.slice(0, -1)); setRedoStack((current) => [...current, profile].slice(-32)); setProfile(previous); setWirePoints(previous?.kind === 'wire' ? previous.points : []); setSelection(null); setDimension(null); setMessage('Reverted a provisional sketch edit. No accepted CAD operation was changed.')
  }
  function redo() {
    if (!redoStack.length || context.operationBusy) { setMessage('Nothing local to redo.'); return }
    const next = redoStack[redoStack.length - 1]; setRedoStack((current) => current.slice(0, -1)); setUndoStack((current) => [...current, profile].slice(-32)); setProfile(next); setWirePoints(next?.kind === 'wire' ? next.points : []); setSelection(null); setDimension(null); setMessage('Restored a provisional sketch edit.')
  }
  function applyDimension() {
    if (!dimension || !profile) return
    const value = Number(dimension.value); if (!Number.isFinite(value) || value <= 0 || value > 1_000_000) { setMessage('Enter a finite positive millimeter distance.'); return }
    const points = closedPoints(profile); if (!points) return
    const moving = dimension.moving; const dx = moving.end.x - moving.start.x; const dy = moving.end.y - moving.start.y
    const length = Math.hypot(dx, dy); if (length < .000001) return
    const normal = { x: -dy / length, y: dx / length }; const signedDistance = (moving.start.x - dimension.fixed.start.x) * normal.x + (moving.start.y - dimension.fixed.start.y) * normal.y
    const delta = (signedDistance < 0 ? -value : value) - signedDistance; const next = points.map((candidate) => ({ ...candidate })); const indices = [moving.index, (moving.index + 1) % next.length]
    for (const index of indices) { next[index].x = round(next[index].x + normal.x * delta); next[index].y = round(next[index].y + normal.y * delta) }
    replaceProfile({ kind: 'wire', points: next, closed: true }); setMessage(`Updated the provisional parallel-edge distance to ${round(value)} mm. Constraint metadata is not persisted by the current manual runtime.`)
  }
  function alignSelected(axis: 'horizontal' | 'vertical') {
    if (!profile || selection?.kind !== 'edge') { setMessage('Select one closed wire edge before applying an H or V alignment.'); return }
    if (profile.kind !== 'wire' || !profile.closed) { setMessage('Boxes are already H/V aligned. Alignment of circles and the current non-wire profile form is unavailable.'); return }
    const next = profile.points.map((point) => ({ ...point })); const edge = edges[selection.index]; if (!edge) return
    const endIndex = (edge.index + 1) % next.length
    if (axis === 'horizontal') next[endIndex].y = next[edge.index].y; else next[endIndex].x = next[edge.index].x
    replaceProfile({ ...profile, points: next }); setSelection({ kind: 'edge', index: edge.index }); setMessage(`${axis === 'horizontal' ? 'Horizontal' : 'Vertical'} alignment applied to the provisional edge. This runtime persists geometry, not parametric constraint metadata.`)
  }
  function zoom(event: WheelEvent<SVGSVGElement>) {
    event.preventDefault(); const canvas = canvasCoordinate(event); if (!canvas) return
    const factor = event.deltaY > 0 ? 1.12 : .89; const width = Math.max(90, Math.min(WIDTH * 3, view.width * factor)); const height = width * HEIGHT / WIDTH
    const relativeX = (canvas.x - view.x) / view.width; const relativeY = (canvas.y - view.y) / view.height
    setView({ width, height, x: canvas.x - width * relativeX, y: canvas.y - height * relativeY })
  }
  function keyDown(event: React.KeyboardEvent<SVGSVGElement>) {
    if (event.key === 'Escape') { event.preventDefault(); cancelCurrent(); return }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); return }
    if (event.key === 'Delete' || event.key === 'Backspace') { if (profile && !context.operationBusy) { event.preventDefault(); replaceProfile(null); setMessage('Provisional profile cleared. No accepted CAD operation was changed.') } return }
    if (event.key.toLowerCase() === 'h') alignSelected('horizontal')
    if (event.key.toLowerCase() === 'v') alignSelected('vertical')
    if (event.key.toLowerCase() === 'f') { setView(EMPTY_VIEW); setMessage('Sketch view fitted to the base plane.') }
  }
  async function commit(kind: 'add' | 'cut') {
    if (!profile || !completeProfile || !context.runManualSketch || !context.manualSketchReady || context.operationBusy) return
    const encoded = profile.kind === 'rectangle' ? { kind: 'rectangle', points: [profile.start, profile.end] } : profile.kind === 'circle' ? { kind: 'circle', points: [profile.center, profile.edge] } : profile.cornerRadii?.some((radius) => radius > 0) ? { kind: 'filletedPolygon', points: profile.points, cornerRadiiMm: profile.cornerRadii } : { kind: 'polygon', points: profile.points }
    const frame = surfaceFrame ?? frames[plane]; const operationFrame = kind === 'cut' ? { ...frame, normal: { x: -frame.normal.x, y: -frame.normal.y, z: -frame.normal.z } } : frame
    const sketch = JSON.stringify({ ...encoded, ...operationFrame }); const targets = surfaceFrame && selectedBody ? [selectedBody] : kind === 'cut' && selectedBody ? [selectedBody] : []
    const accepted = await context.runManualSketch(kind === 'add' ? PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID : PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID, kind === 'add' ? { sketch, extrusionDepthMm: depth } : { sketch, cutDepthMm: depth }, targets)
    setMessage(accepted ? kind === 'add' ? surfaceFrame && selectedBody ? 'Surface extrusion fused into the selected solid.' : 'Solid persisted from this mouse-drawn surface.' : 'Cut persisted from this mouse-drawn surface.' : 'The CAD host did not accept the operation. The sketch remains on screen; no geometry was claimed.')
    if (accepted) onClose()
  }
  const sketchPlaneLabel = surfaceFrame ? 'Selected model face' : `${plane.toUpperCase()} base plane`
  return <section className="pcad-mouse-sketcher" aria-label="Mouse-first CAD sketcher">
    <header className="pcad-sketch-toolbar">
      <span><Grid3X3 size={14} /><strong>Sketch on {sketchPlaneLabel}</strong></span>
      {!surfaceFrame ? <div role="group" aria-label="Sketch plane">{(['xy', 'xz', 'yz'] as const).map((candidate) => <button key={candidate} type="button" aria-pressed={plane === candidate} onClick={() => choosePlane(candidate)}>{candidate.toUpperCase()}</button>)}</div> : <span className="pcad-sketch-plane-lock">Face frame locked</span>}
      <div role="group" aria-label="Sketch tools">
        <button type="button" aria-pressed={tool === 'select'} onClick={() => chooseTool('select')} title="Select edge or vertex (S)"><MousePointer2 size={13} />Select</button><button type="button" aria-pressed={tool === 'line'} onClick={() => chooseTool('line')}><MoveUpRight size={13} />Line</button><button type="button" aria-pressed={tool === 'rectangle'} onClick={() => chooseTool('rectangle')}><Square size={13} />Box</button><button type="button" aria-pressed={tool === 'circle'} onClick={() => chooseTool('circle')}><Circle size={13} />Circle</button><button type="button" aria-pressed={tool === 'dimension'} onClick={() => chooseTool('dimension')}><Ruler size={13} />Dimension</button>
      </div>
      <div role="group" aria-label="Sketch navigation"><button type="button" onClick={() => { setView(EMPTY_VIEW); setMessage('Sketch view fitted to the base plane.') }} title="Fit sketch view (F)">Fit</button><button type="button" onClick={() => { setTool('select'); setMessage('Pan with middle-drag or Shift-drag. Scroll to zoom at the pointer.') }} title="Pan with middle-drag or Shift-drag"><Hand size={13} />Pan</button></div>
      <button type="button" className="pcad-sketch-close" onClick={onClose}><X size={14} />Back to model</button>
    </header>
    <svg ref={svgRef} className="pcad-sketch-canvas" viewBox={`${view.x} ${view.y} ${view.width} ${view.height}`} onPointerDown={begin} onPointerMove={move} onPointerUp={end} onPointerCancel={cancelCurrent} onWheel={zoom} onKeyDown={keyDown} tabIndex={0} aria-label="Interactive sketch plane. Scroll to zoom, middle-drag or Shift-drag to pan, Escape to cancel, and Control Z to undo local sketch edits." role="application">
      <defs><pattern id="pcad-sketch-grid" width="40" height="40" patternUnits="userSpaceOnUse"><path d="M 40 0 L 0 0 0 40" fill="none" stroke="currentColor" strokeOpacity=".14" /></pattern></defs><rect x={view.x} y={view.y} width={view.width} height={view.height} fill="url(#pcad-sketch-grid)" /><line x1={0} y1={HEIGHT / 2} x2={WIDTH} y2={HEIGHT / 2} className="pcad-sketch-axis" /><line x1={WIDTH / 2} y1={0} x2={WIDTH / 2} y2={HEIGHT} className="pcad-sketch-axis" /><circle cx={WIDTH / 2} cy={HEIGHT / 2} r="6" className="pcad-sketch-origin" /><text x={WIDTH / 2 + 10} y={HEIGHT / 2 - 10} className="pcad-sketch-origin-label">Origin</text>
      {rect ? (() => { const start = toCanvas(rect.center); return <><rect x={start.x - rect.width / (2 * UNITS_PER_PIXEL)} y={start.y - rect.height / (2 * UNITS_PER_PIXEL)} width={rect.width / UNITS_PER_PIXEL} height={rect.height / UNITS_PER_PIXEL} className="pcad-sketch-profile" /><text x={start.x} y={start.y - rect.height / (2 * UNITS_PER_PIXEL) - 10} className="pcad-sketch-measure">{round(rect.width)} × {round(rect.height)} mm</text></> })() : null}
      {circle ? (() => { const center = toCanvas(circle.center); return <><circle cx={center.x} cy={center.y} r={circle.radius / UNITS_PER_PIXEL} className="pcad-sketch-profile" /><text x={center.x + 10} y={center.y - 10} className="pcad-sketch-measure">Ø {round(circle.radius * 2)} mm</text></> })() : null}
      {profile?.kind === 'wire' && profile.closed ? <path d={wirePath(profile.points, profile.cornerRadii)} className="pcad-sketch-wire closed" /> : wire.length > 1 ? <polyline points={wire.map((point) => `${point.x},${point.y}`).join(' ')} className="pcad-sketch-wire" /> : null}
      {edges.map((edge) => { const start = toCanvas(edge.start); const end = toCanvas(edge.end); return selection?.kind === 'edge' && selection.index === edge.index ? <line key={`selection-${edge.index}`} x1={start.x} y1={start.y} x2={end.x} y2={end.y} className="pcad-sketch-selection" /> : null })}
      {closed?.map((point, index) => { const canvas = toCanvas(point); return <circle key={`vertex-${index}`} cx={canvas.x} cy={canvas.y} r={selection?.kind === 'vertex' && selection.index === index ? 7 : 4} className={selection?.kind === 'vertex' && selection.index === index ? 'pcad-sketch-vertex selected' : 'pcad-sketch-vertex'} /> })}
    </svg>
    <footer className="pcad-sketch-footer">
      <p role="status">{message}</p>
      {dimension ? <form className="pcad-sketch-dimension" onSubmit={(event) => { event.preventDefault(); applyDimension() }}><label>Distance (mm)<input autoFocus inputMode="decimal" value={dimension.value} onChange={(event) => setDimension({ ...dimension, value: event.target.value })} /></label><button type="submit">Set</button><button type="button" onClick={cancelCurrent}>Cancel</button></form> : null}
      <div className="pcad-sketch-actions"><button type="button" disabled={!undoStack.length || context.operationBusy} onClick={undo} title="Undo provisional sketch edit (Ctrl Z)"><Undo2 size={13} />Undo</button><button type="button" disabled={!redoStack.length || context.operationBusy} onClick={redo} title="Redo provisional sketch edit (Ctrl Shift Z)"><Redo2 size={13} />Redo</button><button type="button" disabled={selection?.kind !== 'edge' || context.operationBusy} onClick={() => alignSelected('horizontal')} title="Align selected wire edge horizontally (H)">H</button><button type="button" disabled={selection?.kind !== 'edge' || context.operationBusy} onClick={() => alignSelected('vertical')} title="Align selected wire edge vertically (V)">V</button><button type="button" disabled={!profile || context.operationBusy} onClick={() => replaceProfile(null)}><X size={13} />Clear</button><button type="button" disabled={!completeProfile || !context.manualSketchReady || context.operationBusy} onClick={() => void commit('add')}><CornerDownLeft size={13} />Extrude {depth} mm</button><button type="button" disabled={!completeProfile || !selectedBody || !context.manualSketchReady || context.operationBusy} onClick={() => void commit('cut')}><CornerDownLeft size={13} />Cut {depth} mm</button></div>
    </footer>
  </section>
}
