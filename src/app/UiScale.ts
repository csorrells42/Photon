export const UI_SCALE_STORAGE_KEY = 'hermes-workbench.ui-scale'
export const DEFAULT_UI_SCALE = 1.25
export const UI_SCALE_OPTIONS = [1, 1.1, 1.25, 1.4] as const

export function normalizeUiScale(value: unknown) {
  if (value === null || value === undefined || value === '') return DEFAULT_UI_SCALE
  const numeric = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(numeric)) return DEFAULT_UI_SCALE
  return UI_SCALE_OPTIONS.reduce((nearest, option) => (
    Math.abs(option - numeric) < Math.abs(nearest - numeric) ? option : nearest
  ), DEFAULT_UI_SCALE as number)
}

export function stepUiScale(current: number, direction: -1 | 1) {
  const normalized = normalizeUiScale(current)
  const index = UI_SCALE_OPTIONS.findIndex((option) => option === normalized)
  const nextIndex = Math.max(0, Math.min(UI_SCALE_OPTIONS.length - 1, index + direction))
  return UI_SCALE_OPTIONS[nextIndex]
}

export function effectiveUiWidth(viewportWidth: number, scale: number) {
  const width = Number.isFinite(viewportWidth) ? Math.max(0, viewportWidth) : 0
  return width / normalizeUiScale(scale)
}

export function uiLayoutBand(viewportWidth: number, scale: number): 'full' | 'condensed' | 'compact' {
  const width = effectiveUiWidth(viewportWidth, scale)
  if (width <= 900) return 'compact'
  if (width <= 1050) return 'condensed'
  return 'full'
}

export function loadUiScale() {
  try {
    return normalizeUiScale(window.localStorage.getItem(UI_SCALE_STORAGE_KEY))
  } catch {
    return DEFAULT_UI_SCALE
  }
}

export function saveUiScale(scale: number) {
  try {
    window.localStorage.setItem(UI_SCALE_STORAGE_KEY, String(normalizeUiScale(scale)))
  } catch {
    // A restricted browser can still use the setting for the current session.
  }
}

export function applyUiScale(scale: number) {
  const normalized = normalizeUiScale(scale)
  document.documentElement.style.setProperty('--ui-scale', String(normalized))
  document.documentElement.style.setProperty('--ui-scale-inverse', String(1 / normalized))
}
