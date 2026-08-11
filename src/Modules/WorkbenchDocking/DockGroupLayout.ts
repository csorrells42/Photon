export type DockMode = 'vertical' | 'horizontal' | 'tabs'
export type DockZone = 'top' | 'right' | 'bottom' | 'left' | 'center'

export type DockGroupLayout<TPanel extends string = string> = {
  mode: DockMode
  order: readonly TPanel[]
  activeTab: TPanel
  splitRatios?: readonly number[]
}

const defaultMinimumRatio = 0.05

export function equalDockGroupRatios(panelCount: number): readonly number[] {
  if (!Number.isInteger(panelCount) || panelCount <= 0) return []
  return Array.from({ length: panelCount }, () => 1 / panelCount)
}

export function normalizeDockGroupRatios(value: unknown, panelCount: number): readonly number[] {
  const equal = equalDockGroupRatios(panelCount)
  if (!Array.isArray(value) || value.length !== panelCount) return equal
  if (value.some((ratio) => typeof ratio !== 'number' || !Number.isFinite(ratio) || ratio <= 0)) return equal
  const values = value as number[]

  const total = values.reduce((sum, ratio) => sum + ratio, 0)
  if (!Number.isFinite(total) || total <= 0) return equal
  const normalized = values.map((ratio) => ratio / total)
  const floor = Math.min(defaultMinimumRatio, 0.5 / panelCount)
  const adjustable = normalized.map((ratio) => Math.max(0, ratio - floor))
  const adjustableTotal = adjustable.reduce((sum, ratio) => sum + ratio, 0)
  if (adjustableTotal <= Number.EPSILON) return equal
  const remaining = 1 - floor * panelCount
  return adjustable.map((ratio) => floor + remaining * ratio / adjustableTotal)
}

export function normalizeDockGroupLayout<TPanel extends string>(
  value: unknown,
  panels: readonly TPanel[],
  fallback: DockGroupLayout<TPanel>,
): DockGroupLayout<TPanel> {
  if (!value || typeof value !== 'object') return fallback
  const candidate = value as Partial<DockGroupLayout<TPanel>>
  if (candidate.mode !== 'vertical' && candidate.mode !== 'horizontal' && candidate.mode !== 'tabs') return fallback
  if (!Array.isArray(candidate.order) || candidate.order.length !== panels.length) return fallback
  if (new Set(candidate.order).size !== panels.length || panels.some((panel) => !candidate.order?.includes(panel))) return fallback
  const activeTab = panels.includes(candidate.activeTab as TPanel) ? candidate.activeTab as TPanel : candidate.order[0]
  if (candidate.mode === 'tabs') return { mode: candidate.mode, order: [...candidate.order], activeTab }
  return {
    mode: candidate.mode,
    order: [...candidate.order],
    activeTab,
    splitRatios: normalizeDockGroupRatios(candidate.splitRatios, panels.length),
  }
}

export function loadDockGroupLayout<TPanel extends string>(
  storageKey: string,
  panels: readonly TPanel[],
  fallback: DockGroupLayout<TPanel>,
): DockGroupLayout<TPanel> {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return stored ? normalizeDockGroupLayout(JSON.parse(stored), panels, fallback) : fallback
  } catch {
    return fallback
  }
}

export function saveDockGroupLayout<TPanel extends string>(storageKey: string, layout: DockGroupLayout<TPanel>) {
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(layout))
  } catch {
    // Docking remains available for this run when a WebView disables storage.
  }
}

export function dockPanel<TPanel extends string>(layout: DockGroupLayout<TPanel>, moving: TPanel, zone: DockZone): DockGroupLayout<TPanel> {
  if (!layout.order.includes(moving)) return layout
  if (zone === 'center') return { ...layout, mode: 'tabs', activeTab: moving }
  const others = layout.order.filter((panel) => panel !== moving)
  const leading = zone === 'top' || zone === 'left'
  const order = leading ? [moving, ...others] : [...others, moving]
  return {
    mode: zone === 'top' || zone === 'bottom' ? 'vertical' : 'horizontal',
    order,
    activeTab: moving,
    splitRatios: equalDockGroupRatios(order.length),
  }
}

export function activateDockTab<TPanel extends string>(layout: DockGroupLayout<TPanel>, panel: TPanel): DockGroupLayout<TPanel> {
  return layout.order.includes(panel) ? { ...layout, activeTab: panel } : layout
}

export function resetDockGroupRatios<TPanel extends string>(layout: DockGroupLayout<TPanel>): DockGroupLayout<TPanel> {
  if (layout.mode === 'tabs') return layout
  return { ...layout, splitRatios: equalDockGroupRatios(layout.order.length) }
}

export function resizeDockGroupSeparator<TPanel extends string>(
  layout: DockGroupLayout<TPanel>,
  separatorIndex: number,
  deltaRatio: number,
  minimumRatio = defaultMinimumRatio,
): DockGroupLayout<TPanel> {
  if (layout.mode === 'tabs' || !Number.isFinite(deltaRatio)) return layout
  if (!Number.isInteger(separatorIndex) || separatorIndex < 0 || separatorIndex >= layout.order.length - 1) return layout

  const ratios = [...normalizeDockGroupRatios(layout.splitRatios, layout.order.length)]
  const pairTotal = ratios[separatorIndex] + ratios[separatorIndex + 1]
  const effectiveMinimum = Math.min(Math.max(0, minimumRatio), pairTotal / 2)
  const nextLeading = Math.min(
    pairTotal - effectiveMinimum,
    Math.max(effectiveMinimum, ratios[separatorIndex] + deltaRatio),
  )
  ratios[separatorIndex] = nextLeading
  ratios[separatorIndex + 1] = pairTotal - nextLeading
  return { ...layout, splitRatios: ratios }
}

export function resizeDockGroupFromPointer<TPanel extends string>(
  layout: DockGroupLayout<TPanel>,
  separatorIndex: number,
  deltaPixels: number,
  availablePixels: number,
  minimumPanelPixels: number,
): DockGroupLayout<TPanel> {
  if (!Number.isFinite(deltaPixels) || !Number.isFinite(availablePixels) || availablePixels <= 0) return layout
  const minimumRatio = Number.isFinite(minimumPanelPixels) && minimumPanelPixels > 0
    ? minimumPanelPixels / availablePixels
    : defaultMinimumRatio
  return resizeDockGroupSeparator(layout, separatorIndex, deltaPixels / availablePixels, minimumRatio)
}

export function resizeDockGroupByKeyboard<TPanel extends string>(
  layout: DockGroupLayout<TPanel>,
  separatorIndex: number,
  key: string,
  shiftKey = false,
): DockGroupLayout<TPanel> | null {
  if (layout.mode === 'tabs') return null
  if (!Number.isInteger(separatorIndex) || separatorIndex < 0 || separatorIndex >= layout.order.length - 1) return null
  if (key === 'Enter') return resetDockGroupRatios(layout)

  const direction = layout.mode === 'horizontal'
    ? key === 'ArrowLeft' ? -1 : key === 'ArrowRight' ? 1 : 0
    : key === 'ArrowUp' ? -1 : key === 'ArrowDown' ? 1 : 0
  if (direction !== 0) return resizeDockGroupSeparator(layout, separatorIndex, direction * (shiftKey ? 0.1 : 0.025))

  const ratios = normalizeDockGroupRatios(layout.splitRatios, layout.order.length)
  const pairTotal = ratios[separatorIndex] + ratios[separatorIndex + 1]
  const minimum = Math.min(defaultMinimumRatio, pairTotal / 2)
  if (key === 'Home') return resizeDockGroupSeparator(layout, separatorIndex, minimum - ratios[separatorIndex], minimum)
  if (key === 'End') return resizeDockGroupSeparator(layout, separatorIndex, pairTotal - minimum - ratios[separatorIndex], minimum)
  return null
}
