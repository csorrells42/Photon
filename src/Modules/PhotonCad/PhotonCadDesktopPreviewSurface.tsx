import { useCallback, useEffect, useRef } from 'react'
import type { PhotonCadPreviewAssetResolver } from './PhotonCadPreviewAsset'
import type { PhotonCadPreviewContext } from './PhotonCadWorkspace'
import { DesktopPhotonCadPreviewClient } from './DesktopPhotonCadPreviewClient'
import { PhotonCadViewer } from './PhotonCadViewer'

export function PhotonCadDesktopPreviewSurface({ context }: { context: PhotonCadPreviewContext }) {
  const client = useRef(new DesktopPhotonCadPreviewClient())
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

  return (
    <PhotonCadViewer
      receipt={receipt}
      entities={project?.entities ?? []}
      selectedEntityIds={context.selectedEntityIds}
      resolveAsset={resolveAsset}
      allowedAssetOrigin={typeof window === 'undefined' ? undefined : window.location.origin}
      assetPathPrefix="/api/photon-cad/previews/"
      maximumBytes={128 * 1024 * 1024}
    />
  )
}
