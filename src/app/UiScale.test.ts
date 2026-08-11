import { describe, expect, it } from 'vitest'
import { DEFAULT_UI_SCALE, effectiveUiWidth, normalizeUiScale, stepUiScale, uiLayoutBand } from './UiScale'

describe('UI scale', () => {
  it('uses the readable first-run default for missing or invalid values', () => {
    expect(normalizeUiScale(null)).toBe(DEFAULT_UI_SCALE)
    expect(normalizeUiScale('not-a-number')).toBe(DEFAULT_UI_SCALE)
  })

  it('restores the nearest supported scale', () => {
    expect(normalizeUiScale('1.37')).toBe(1.4)
    expect(normalizeUiScale(1.08)).toBe(1.1)
  })

  it('steps through presets without moving beyond the limits', () => {
    expect(stepUiScale(1.25, 1)).toBe(1.4)
    expect(stepUiScale(1.25, -1)).toBe(1.1)
    expect(stepUiScale(1.4, 1)).toBe(1.4)
    expect(stepUiScale(1, -1)).toBe(1)
  })

  it('selects layout bands from the effective scaled width', () => {
    expect(effectiveUiWidth(1000, 1.4)).toBeCloseTo(714.29, 2)
    expect(uiLayoutBand(1000, 1.4)).toBe('compact')
    expect(uiLayoutBand(1280, 1.25)).toBe('condensed')
    expect(uiLayoutBand(1600, 1.4)).toBe('full')
  })
})
