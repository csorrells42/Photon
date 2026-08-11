export type CadWorkbenchPane = 'explorer' | 'photon'

export type CadWorkbenchLayout = {
  explorerWidth: number
  photonWidth: number
  explorerCollapsed: boolean
  photonCollapsed: boolean
}

type CadWorkbenchLayoutStorage = Pick<Storage, 'getItem' | 'setItem'>

export const CAD_WORKBENCH_LAYOUT_STORAGE_KEY = 'hermes-workbench.photon-cad-layout.v1'

export const CAD_WORKBENCH_BOUNDS = {
  explorer: { minimum: 160, maximum: 440, initial: 230 },
  photon: { minimum: 300, maximum: 640, initial: 390 },
} as const

export const DEFAULT_CAD_WORKBENCH_LAYOUT: CadWorkbenchLayout = {
  explorerWidth: CAD_WORKBENCH_BOUNDS.explorer.initial,
  photonWidth: CAD_WORKBENCH_BOUNDS.photon.initial,
  explorerCollapsed: false,
  photonCollapsed: false,
}

export function loadCadWorkbenchLayout(storage: CadWorkbenchLayoutStorage | null = typeof window === 'undefined' ? null : window.localStorage) {
  if (!storage) return DEFAULT_CAD_WORKBENCH_LAYOUT
  try {
    const parsed = JSON.parse(storage.getItem(CAD_WORKBENCH_LAYOUT_STORAGE_KEY) ?? 'null') as unknown
    if (!parsed || typeof parsed !== 'object') return DEFAULT_CAD_WORKBENCH_LAYOUT
    const candidate = parsed as Partial<CadWorkbenchLayout> & { version?: unknown }
    if (candidate.version !== 1
      || typeof candidate.explorerWidth !== 'number'
      || typeof candidate.photonWidth !== 'number'
      || typeof candidate.explorerCollapsed !== 'boolean'
      || typeof candidate.photonCollapsed !== 'boolean') return DEFAULT_CAD_WORKBENCH_LAYOUT
    return {
      explorerWidth: clampCadWorkbenchPaneWidth('explorer', candidate.explorerWidth),
      photonWidth: clampCadWorkbenchPaneWidth('photon', candidate.photonWidth),
      explorerCollapsed: candidate.explorerCollapsed,
      photonCollapsed: candidate.photonCollapsed,
    }
  } catch {
    return DEFAULT_CAD_WORKBENCH_LAYOUT
  }
}

export function saveCadWorkbenchLayout(
  layout: CadWorkbenchLayout,
  storage: CadWorkbenchLayoutStorage | null = typeof window === 'undefined' ? null : window.localStorage,
) {
  if (!storage) return false
  try {
    storage.setItem(CAD_WORKBENCH_LAYOUT_STORAGE_KEY, JSON.stringify({ version: 1, ...layout }))
    return true
  } catch {
    return false
  }
}

export function clampCadWorkbenchPaneWidth(pane: CadWorkbenchPane, value: number) {
  const bounds = CAD_WORKBENCH_BOUNDS[pane]
  if (!Number.isFinite(value)) return bounds.initial
  return Math.min(bounds.maximum, Math.max(bounds.minimum, Math.round(value)))
}

export function resizeCadWorkbenchPane(
  layout: CadWorkbenchLayout,
  pane: CadWorkbenchPane,
  value: number,
): CadWorkbenchLayout {
  const width = clampCadWorkbenchPaneWidth(pane, value)
  return pane === 'explorer'
    ? { ...layout, explorerWidth: width, explorerCollapsed: false }
    : { ...layout, photonWidth: width, photonCollapsed: false }
}

export function toggleCadWorkbenchPane(layout: CadWorkbenchLayout, pane: CadWorkbenchPane): CadWorkbenchLayout {
  return pane === 'explorer'
    ? { ...layout, explorerCollapsed: !layout.explorerCollapsed }
    : { ...layout, photonCollapsed: !layout.photonCollapsed }
}

export function enterCadWorkbenchLayout(layout: CadWorkbenchLayout): CadWorkbenchLayout {
  return layout.explorerCollapsed ? layout : { ...layout, explorerCollapsed: true }
}

export function keyboardCadWorkbenchPaneWidth(
  pane: CadWorkbenchPane,
  current: number,
  key: string,
  largeStep = false,
) {
  const bounds = CAD_WORKBENCH_BOUNDS[pane]
  const step = largeStep ? 48 : 16
  if (key === 'Home') return bounds.minimum
  if (key === 'End') return bounds.maximum
  if (pane === 'explorer') {
    if (key === 'ArrowLeft') return clampCadWorkbenchPaneWidth(pane, current - step)
    if (key === 'ArrowRight') return clampCadWorkbenchPaneWidth(pane, current + step)
  } else {
    if (key === 'ArrowLeft') return clampCadWorkbenchPaneWidth(pane, current + step)
    if (key === 'ArrowRight') return clampCadWorkbenchPaneWidth(pane, current - step)
  }
  return null
}
