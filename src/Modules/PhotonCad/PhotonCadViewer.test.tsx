import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { PhotonCadEntity, PhotonCadPreviewReceipt } from './PhotonCadContract'
import { PhotonCadViewer } from './PhotonCadViewer'

const receipt: PhotonCadPreviewReceipt = {
  previewId: 'preview-gearbox-1',
  projectId: 'project-gearbox-1',
  revision: 3,
  contentDigest: `sha256:${'a'.repeat(64)}`,
  units: 'millimeter',
  bounds: { minimum: { x: -120, y: -80, z: -65 }, maximum: { x: 120, y: 80, z: 65 } },
  entityCount: 2,
}

const entities: PhotonCadEntity[] = [
  { id: 'housing-1', parentId: null, kind: 'part', name: 'Split housing', visible: true, suppressed: false, sourceCapabilityId: 'build123d.box' },
  { id: 'shaft-1', parentId: null, kind: 'part', name: 'Input shaft', visible: true, suppressed: false, sourceCapabilityId: 'build123d.cylinder' },
]

describe('PhotonCadViewer', () => {
  it('renders honest unavailable state and disabled controls without loading Three.js during SSR', () => {
    const resolver = vi.fn()
    const markup = renderToStaticMarkup(<PhotonCadViewer receipt={receipt} entities={entities} resolveAsset={resolver} />)

    expect(resolver).not.toHaveBeenCalled()
    expect(markup).toContain('data-status="unavailable"')
    expect(markup).toContain('No verified preview asset is available.')
    expect(markup).toContain('A preview never counts as design verification or release evidence.')
    expect(markup).toContain('disabled=""')
    expect(markup).not.toContain('Verified preview ready')
    expect(markup).not.toContain('<canvas')
  })

  it('exposes fit, reset, isolate, edges, x-ray, and explode through native keyboard controls', () => {
    const markup = renderToStaticMarkup(<PhotonCadViewer receipt={receipt} entities={entities} />)

    expect(markup).toContain('aria-label="3D preview controls"')
    expect(markup).toContain('Fit')
    expect(markup).toContain('Reset')
    expect(markup).toContain('Isolate')
    expect(markup).toContain('Edges')
    expect(markup).toContain('X-ray')
    expect(markup).toContain('Explode')
    expect(markup).toContain('type="range"')
    expect(markup).toContain('Press F to fit, Home to reset, or Escape to clear selection.')
  })

  it('provides a non-canvas selectable model summary with controlled selection', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadViewer receipt={receipt} entities={entities} selectedEntityIds={['shaft-1']} />,
    )

    expect(markup).toContain('aria-label="Non-canvas model summary"')
    expect(markup).toContain('Split housing')
    expect(markup).toContain('Input shaft')
    expect(markup).toMatch(/aria-pressed="true"[^>]*>[\s\S]*?Input shaft/u)
    expect(markup).toContain('2')
  })

  it('escapes hostile entity labels and never injects HTML or asset URLs into SSR output', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadViewer
        receipt={receipt}
        entities={[{ ...entities[0], name: '<img src=x onerror=alert(1)>\u202e' }]}
        allowedAssetOrigin="https://127.0.0.1:9119"
      />,
    )

    expect(markup).toContain('&lt;img src=x onerror=alert(1)&gt;')
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('https://127.0.0.1:9119')
    expect(markup).not.toContain('dangerouslySetInnerHTML')
  })
})
