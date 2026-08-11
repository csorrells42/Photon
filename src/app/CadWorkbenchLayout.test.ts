import { describe, expect, it } from 'vitest'
import {
  CAD_WORKBENCH_BOUNDS,
  DEFAULT_CAD_WORKBENCH_LAYOUT,
  clampCadWorkbenchPaneWidth,
  enterCadWorkbenchLayout,
  keyboardCadWorkbenchPaneWidth,
  loadCadWorkbenchLayout,
  resizeCadWorkbenchPane,
  saveCadWorkbenchLayout,
  toggleCadWorkbenchPane,
} from './CadWorkbenchLayout'

describe('CAD Workbench pane layout', () => {
  it('clamps both panes to bounded usable widths', () => {
    expect(clampCadWorkbenchPaneWidth('explorer', -50)).toBe(CAD_WORKBENCH_BOUNDS.explorer.minimum)
    expect(clampCadWorkbenchPaneWidth('explorer', 5_000)).toBe(CAD_WORKBENCH_BOUNDS.explorer.maximum)
    expect(clampCadWorkbenchPaneWidth('photon', Number.NaN)).toBe(CAD_WORKBENCH_BOUNDS.photon.initial)
    expect(clampCadWorkbenchPaneWidth('photon', 512.4)).toBe(512)
  })

  it('preserves the last width while collapsing and restoring a pane', () => {
    const resized = resizeCadWorkbenchPane(DEFAULT_CAD_WORKBENCH_LAYOUT, 'explorer', 318)
    const collapsed = toggleCadWorkbenchPane(resized, 'explorer')
    const restored = toggleCadWorkbenchPane(collapsed, 'explorer')

    expect(collapsed).toMatchObject({ explorerWidth: 318, explorerCollapsed: true })
    expect(restored).toMatchObject({ explorerWidth: 318, explorerCollapsed: false })
  })

  it('enters CAD with Explorer collapsed while preserving its saved width', () => {
    const resized = resizeCadWorkbenchPane(DEFAULT_CAD_WORKBENCH_LAYOUT, 'explorer', 318)
    const entered = enterCadWorkbenchLayout(resized)

    expect(entered).toMatchObject({ explorerWidth: 318, explorerCollapsed: true, photonCollapsed: false })
    expect(enterCadWorkbenchLayout(entered)).toBe(entered)
  })

  it('supports directional keyboard resizing, large steps, and boundary keys', () => {
    expect(keyboardCadWorkbenchPaneWidth('explorer', 230, 'ArrowRight')).toBe(246)
    expect(keyboardCadWorkbenchPaneWidth('explorer', 230, 'ArrowLeft', true)).toBe(182)
    expect(keyboardCadWorkbenchPaneWidth('photon', 390, 'ArrowLeft')).toBe(406)
    expect(keyboardCadWorkbenchPaneWidth('photon', 390, 'ArrowRight', true)).toBe(342)
    expect(keyboardCadWorkbenchPaneWidth('photon', 390, 'Home')).toBe(CAD_WORKBENCH_BOUNDS.photon.minimum)
    expect(keyboardCadWorkbenchPaneWidth('photon', 390, 'End')).toBe(CAD_WORKBENCH_BOUNDS.photon.maximum)
    expect(keyboardCadWorkbenchPaneWidth('explorer', 230, 'Enter')).toBeNull()
  })

  it('round-trips a bounded versioned layout and fails closed on malformed storage', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value) },
    }
    const layout = { explorerWidth: 312, photonWidth: 488, explorerCollapsed: true, photonCollapsed: false }
    expect(saveCadWorkbenchLayout(layout, storage)).toBe(true)
    expect(loadCadWorkbenchLayout(storage)).toEqual(layout)

    values.set('hermes-workbench.photon-cad-layout.v1', '{"version":2,"explorerWidth":99999}')
    expect(loadCadWorkbenchLayout(storage)).toEqual(DEFAULT_CAD_WORKBENCH_LAYOUT)
  })
})
