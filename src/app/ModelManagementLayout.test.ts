import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

function compactCss(source: string) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\s+/g, ' ').trim()
}

describe('Hermes model management workspace layout', () => {
  it('owns the full workbench canvas at every window width', () => {
    const cssPath = fileURLToPath(new URL('./styles.css', import.meta.url))
    const css = compactCss(readFileSync(cssPath, 'utf8'))
    const responsiveBoundary = css.indexOf('@container')
    const layoutRule = css.indexOf('.workbench.layout-code.panel-models { grid-template-columns: 48px minmax(0, 1fr);')
    const placementRule = css.indexOf('.layout-code.panel-models .hermes-model-management { display: grid; grid-area: editor; grid-column: 2; grid-row: 2;')

    expect(layoutRule).toBeGreaterThan(-1)
    expect(placementRule).toBeGreaterThan(-1)
    expect(layoutRule).toBeLessThan(responsiveBoundary)
    expect(placementRule).toBeLessThan(responsiveBoundary)
  })
})
