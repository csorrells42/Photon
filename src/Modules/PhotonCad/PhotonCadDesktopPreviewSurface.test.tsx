import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PhotonCadDesktopPreviewSurface } from './PhotonCadDesktopPreviewSurface'

describe('PhotonCadDesktopPreviewSurface', () => {
  it('mounts the existing bounded viewer without inventing preview evidence', () => {
    const markup = renderToStaticMarkup(<PhotonCadDesktopPreviewSurface context={{
      project: {
        contractVersion: 1, sessionId: 'session-1', projectId: 'project-1', revision: 0, title: 'Preview', units: 'millimeter', mode: 'canonical',
        entities: [], operations: [], issues: [], dirty: false,
      },
      receipt: null,
      selectedEntityIds: [],
      stage: 'design',
      acceptHydratedReceipt: () => false,
    }} />)
    expect(markup).toContain('No verified preview asset is available.')
    expect(markup).not.toContain('blob:')
    expect(markup).not.toContain('file:')
  })
})
