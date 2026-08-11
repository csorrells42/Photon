import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  activateDockTab,
  dockPanel,
  equalDockGroupRatios,
  loadDockGroupLayout,
  normalizeDockGroupLayout,
  normalizeDockGroupRatios,
  resetDockGroupRatios,
  resizeDockGroupByKeyboard,
  resizeDockGroupFromPointer,
  saveDockGroupLayout,
} from './DockGroupLayout'
import type { DockGroupLayout } from './DockGroupLayout'

type Panel = 'hermes' | 'codex' | 'claude'
const panels: readonly Panel[] = ['hermes', 'codex', 'claude']
const fallback: DockGroupLayout<Panel> = { mode: 'vertical', order: panels, activeTab: 'hermes' }

afterEach(() => vi.unstubAllGlobals())

describe('workbench dock-group layout', () => {
  it('rejects stale, duplicate, and unknown persisted panel registries', () => {
    expect(normalizeDockGroupLayout({ mode: 'tabs', order: ['hermes', 'codex'], activeTab: 'hermes' }, panels, fallback)).toBe(fallback)
    expect(normalizeDockGroupLayout({ mode: 'tabs', order: ['hermes', 'hermes', 'claude'], activeTab: 'hermes' }, panels, fallback)).toBe(fallback)
    expect(normalizeDockGroupLayout({ mode: 'tabs', order: ['hermes', 'codex', 'other'], activeTab: 'hermes' }, panels, fallback)).toBe(fallback)
  })

  it('docks any registered panel on all four edges', () => {
    expect(dockPanel(fallback, 'claude', 'top')).toMatchObject({ mode: 'vertical', order: ['claude', 'hermes', 'codex'] })
    expect(dockPanel(fallback, 'claude', 'bottom')).toMatchObject({ mode: 'vertical', order: ['hermes', 'codex', 'claude'] })
    expect(dockPanel(fallback, 'claude', 'left')).toMatchObject({ mode: 'horizontal', order: ['claude', 'hermes', 'codex'] })
    expect(dockPanel(fallback, 'claude', 'right')).toMatchObject({ mode: 'horizontal', order: ['hermes', 'codex', 'claude'] })
  })

  it('stacks an arbitrary panel count as tabs and changes the active tab', () => {
    const tabbed = dockPanel(fallback, 'claude', 'center')
    expect(tabbed).toEqual({ mode: 'tabs', order: panels, activeTab: 'claude' })
    expect(activateDockTab(tabbed, 'codex').activeTab).toBe('codex')
  })

  it('ignores panels that are not registered in the group', () => {
    expect(dockPanel(fallback, 'other' as Panel, 'left')).toBe(fallback)
    expect(activateDockTab(fallback, 'other' as Panel)).toBe(fallback)
  })

  it('normalizes missing, malformed, stale, and extreme persisted split ratios safely', () => {
    expect(normalizeDockGroupRatios(undefined, 3)).toEqual(equalDockGroupRatios(3))
    expect(normalizeDockGroupRatios([0.6, -0.2, 0.6], 3)).toEqual(equalDockGroupRatios(3))
    expect(normalizeDockGroupRatios([0.6, Number.POSITIVE_INFINITY, 0.4], 3)).toEqual(equalDockGroupRatios(3))
    expect(normalizeDockGroupRatios([0.5, 0.5], 3)).toEqual(equalDockGroupRatios(3))
    expect(normalizeDockGroupRatios(['0.5', '0.3', '0.2'], 3)).toEqual(equalDockGroupRatios(3))

    const extreme = normalizeDockGroupRatios([1_000_000, 1, 1], 3)
    expect(extreme).toHaveLength(3)
    expect(extreme.reduce((sum, ratio) => sum + ratio, 0)).toBeCloseTo(1)
    expect(extreme.every((ratio) => Number.isFinite(ratio) && ratio >= 0.05)).toBe(true)

    const normalized = normalizeDockGroupLayout({
      mode: 'horizontal',
      order: panels,
      activeTab: 'hermes',
      splitRatios: [6, 3, 1],
    }, panels, fallback)
    expect(normalized.splitRatios).toEqual([0.6, 0.3, 0.1])
  })

  it('persists normalized split proportions through the existing layout storage contract', () => {
    const values = new Map<string, string>()
    vi.stubGlobal('window', {
      localStorage: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => values.set(key, value),
      },
    })
    const layout: DockGroupLayout<Panel> = {
      mode: 'vertical',
      order: panels,
      activeTab: 'codex',
      splitRatios: [0.5, 0.3, 0.2],
    }
    saveDockGroupLayout('dock-smoke', layout)
    expect(loadDockGroupLayout('dock-smoke', panels, fallback)).toEqual(layout)
  })

  it('resizes adjacent panels from pointer movement while preserving totals and minimums', () => {
    const layout: DockGroupLayout<Panel> = {
      mode: 'horizontal',
      order: panels,
      activeTab: 'hermes',
      splitRatios: [0.4, 0.35, 0.25],
    }
    const resized = resizeDockGroupFromPointer(layout, 0, 100, 1_000, 150)
    expect(resized.splitRatios).toEqual([0.5, 0.25, 0.25])

    const clamped = resizeDockGroupFromPointer(layout, 0, -2_000, 1_000, 150)
    expect(clamped.splitRatios).toEqual([0.15, 0.6, 0.25])
    expect(clamped.splitRatios?.reduce((sum, ratio) => sum + ratio, 0)).toBeCloseTo(1)
  })

  it('supports axis-aware keyboard resizing, accelerated steps, bounds, and equal reset', () => {
    const horizontal: DockGroupLayout<Panel> = {
      mode: 'horizontal',
      order: panels,
      activeTab: 'hermes',
      splitRatios: [0.4, 0.35, 0.25],
    }
    expect(resizeDockGroupByKeyboard(horizontal, 0, 'ArrowDown')).toBeNull()
    const oneStep = resizeDockGroupByKeyboard(horizontal, 0, 'ArrowRight')?.splitRatios
    expect(oneStep?.[0]).toBeCloseTo(0.425)
    expect(oneStep?.[1]).toBeCloseTo(0.325)
    expect(oneStep?.[2]).toBeCloseTo(0.25)
    expect(resizeDockGroupByKeyboard(horizontal, 0, 'ArrowRight', true)?.splitRatios).toEqual([0.5, 0.25, 0.25])
    const home = resizeDockGroupByKeyboard(horizontal, 0, 'Home')?.splitRatios
    expect(home?.[0]).toBeCloseTo(0.05)
    expect(home?.[1]).toBeCloseTo(0.7)
    expect(home?.[2]).toBeCloseTo(0.25)
    const end = resizeDockGroupByKeyboard(horizontal, 0, 'End')?.splitRatios
    expect(end?.[0]).toBeCloseTo(0.7)
    expect(end?.[1]).toBeCloseTo(0.05)
    expect(end?.[2]).toBeCloseTo(0.25)
    expect(resizeDockGroupByKeyboard(horizontal, 0, 'Enter')).toEqual(resetDockGroupRatios(horizontal))

    const vertical = { ...horizontal, mode: 'vertical' as const }
    const verticalStep = resizeDockGroupByKeyboard(vertical, 1, 'ArrowUp')?.splitRatios
    expect(verticalStep?.[0]).toBeCloseTo(0.4)
    expect(verticalStep?.[1]).toBeCloseTo(0.325)
    expect(verticalStep?.[2]).toBeCloseTo(0.275)
    expect(resizeDockGroupByKeyboard(vertical, 3, 'ArrowUp')).toBeNull()
  })

  it('keeps tab mode free of split proportions and resize behavior', () => {
    const tabbed = normalizeDockGroupLayout({
      mode: 'tabs',
      order: panels,
      activeTab: 'codex',
      splitRatios: [0.8, 0.1, 0.1],
    }, panels, fallback)
    expect(tabbed).toEqual({ mode: 'tabs', order: panels, activeTab: 'codex' })
    expect(resizeDockGroupByKeyboard(tabbed, 0, 'ArrowRight')).toBeNull()
    expect(resetDockGroupRatios(tabbed)).toBe(tabbed)
  })
})
