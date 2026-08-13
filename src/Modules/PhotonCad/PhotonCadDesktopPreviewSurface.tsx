import { useCallback, useEffect, useRef, useState } from 'react'
import type { PhotonCadPreviewAssetResolver } from './PhotonCadPreviewAsset'
import type { PhotonCadPreviewContext } from './PhotonCadWorkspace'
import { DesktopPhotonCadPreviewClient } from './DesktopPhotonCadPreviewClient'
import { PhotonCadViewer, type PhotonCadSurfacePick } from './PhotonCadViewer'
import { PhotonCadMouseSketcher } from './PhotonCadMouseSketcher'

export function PhotonCadDesktopPreviewSurface({ context }: { context: PhotonCadPreviewContext }) {
  const client = useRef(new DesktopPhotonCadPreviewClient())
  const [sketching, setSketching] = useState(false)
  const [surfacePicking, setSurfacePicking] = useState(false)
  const [dimensionPicking, setDimensionPicking] = useState(false)
  const [surfaceFrame, setSurfaceFrame] = useState<PhotonCadSurfacePick | null>(null)
  const [dimensionFirstFace, setDimensionFirstFace] = useState<PhotonCadSurfacePick | null>(null)
  const [surfaceDimensionMm, setSurfaceDimensionMm] = useState<number | null>(null)
  const project = context.project
  const receipt = context.receipt
  useEffect(() => {
    if (!project || receipt || project.revision < 1 || project.entities.length === 0) return
    const cancellation = new AbortController()
    void client.current.hydrate({ sessionId: project.sessionId, projectId: project.projectId, revision: project.revision }, cancellation.signal)
      .then((hydrated) => { context.acceptHydratedReceipt(hydrated) })
      .catch(() => undefined)
    return () => cancellation.abort()
  }, [project?.sessionId, project?.projectId, project?.revision, receipt?.previewId])
  const resolveAsset = useCallback<PhotonCadPreviewAssetResolver>((request, signal) => {
    if (!project || !receipt || project.projectId !== receipt.projectId || project.revision !== receipt.revision) {
      return Promise.reject(new Error('preview-context-unavailable'))
    }
    return client.current.resolve({ sessionId: project.sessionId, projectId: project.projectId, revision: project.revision }, request, signal)
  }, [project?.sessionId, project?.projectId, project?.revision, receipt?.previewId])

  if (sketching) return <PhotonCadMouseSketcher context={context} surfaceFrame={surfaceFrame} onClose={() => { setSketching(false); setSurfaceFrame(null) }} />
  return (
    <div className="pcad-desktop-preview-surface">
      <PhotonCadViewer
        receipt={receipt}
        entities={project?.entities ?? []}
        selectedEntityIds={context.selectedEntityIds}
        resolveAsset={resolveAsset}
        allowedAssetOrigin={typeof window === 'undefined' ? undefined : window.location.origin}
        assetPathPrefix="/api/photon-cad/previews/"
        maximumBytes={128 * 1024 * 1024}
        surfacePicking={surfacePicking || dimensionPicking}
        onSurfacePick={(pick) => {
          if (surfacePicking) { setSurfaceFrame(pick); setSurfacePicking(false); setSketching(true); return }
          if (!dimensionFirstFace) { setDimensionFirstFace(pick); setSurfaceDimensionMm(null); return }
          const normalDot = dimensionFirstFace.normal.x * pick.normal.x + dimensionFirstFace.normal.y * pick.normal.y + dimensionFirstFace.normal.z * pick.normal.z
          if (Math.abs(normalDot) < .999) { setDimensionFirstFace(pick); setSurfaceDimensionMm(null); return }
          const delta = { x: pick.originMm.x - dimensionFirstFace.originMm.x, y: pick.originMm.y - dimensionFirstFace.originMm.y, z: pick.originMm.z - dimensionFirstFace.originMm.z }
          setSurfaceDimensionMm(Math.abs(delta.x * dimensionFirstFace.normal.x + delta.y * dimensionFirstFace.normal.y + delta.z * dimensionFirstFace.normal.z))
          setDimensionFirstFace(null); setDimensionPicking(false)
        }}
      />
      {context.stage === 'design' ? <aside className="pcad-desktop-tool-bins" aria-label="CAD tools">
        <details open><summary>Manual tools</summary><p>Mouse-first solid creation and modification are persisted through the sealed CAD runtime.</p></details>
        <details open><summary>Sketch tools</summary><div><button className="pcad-desktop-sketch-button" type="button" disabled={!context.manualSketchReady} onClick={() => { setSurfaceFrame(null); setSketching(true) }}>Create sketch</button><button className="pcad-desktop-sketch-button" type="button" disabled={!context.manualSketchReady} aria-pressed={surfacePicking} onClick={() => { setDimensionPicking(false); setDimensionFirstFace(null); setSurfacePicking((value) => !value) }}>{surfacePicking ? 'Click a model face…' : 'Sketch on face'}</button></div><p>Line, box, circle, parallel-edge dimension, sketch fillet, and sketch chamfer appear in the sketch canvas.</p></details>
        <details open><summary>3D tools</summary><div><button className="pcad-desktop-sketch-button" type="button" aria-pressed={dimensionPicking} onClick={() => { setSurfacePicking(false); setDimensionFirstFace(null); setSurfaceDimensionMm(null); setDimensionPicking((value) => !value) }}>{dimensionPicking ? dimensionFirstFace ? 'Click a parallel face…' : 'Click first face…' : 'Dimension faces'}</button><button className="pcad-desktop-sketch-button" type="button" disabled title="Stable selectable edge identities are required before a 3D fillet can be executed truthfully.">3D fillet</button><button className="pcad-desktop-sketch-button" type="button" disabled title="Stable selectable edge identities are required before a 3D chamfer can be executed truthfully.">3D chamfer</button></div><p>{surfaceDimensionMm === null ? dimensionPicking ? 'Select two parallel faces to measure their perpendicular separation.' : 'Select two parallel faces to inspect their distance.' : `Face-to-face distance: ${surfaceDimensionMm.toFixed(3)} mm`}</p></details>
      </aside> : null}
    </div>
  )
}
