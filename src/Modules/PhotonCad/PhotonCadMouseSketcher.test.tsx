import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PhotonCadMouseSketcher } from './PhotonCadMouseSketcher'

describe('PhotonCadMouseSketcher', () => {
  it('offers mouse-first sketch tools without claiming unsupported parametric authority', () => {
    const markup = renderToStaticMarkup(<PhotonCadMouseSketcher context={{
      project: null,
      receipt: null,
      selectedEntityIds: [],
      stage: 'design',
      manualSketchReady: true,
      operationBusy: false,
      acceptHydratedReceipt: () => false,
      runManualSketch: async () => false,
    }} onClose={() => undefined} />)

    expect(markup).toContain('Sketch on XY base plane')
    expect(markup).toContain('Select')
    expect(markup).toContain('Dimension')
    expect(markup).toContain('Undo')
    expect(markup).toContain('Extrude 10 mm')
    expect(markup).toContain('provisional')
    expect(markup).not.toContain('parametric constraint metadata is persisted')
  })
})
