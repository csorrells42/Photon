import { describe, expect, it } from 'vitest'
import { ASSISTANT_DISPLAY_NAME_STORAGE_KEY, DEFAULT_ASSISTANT_DISPLAY_NAME, loadAssistantDisplayName, normalizeAssistantDisplayName, saveAssistantDisplayName } from './AssistantIdentity'

describe('assistant display identity', () => {
  it('uses Photon when the configured name is absent or empty', () => {
    expect(normalizeAssistantDisplayName(undefined)).toBe(DEFAULT_ASSISTANT_DISPLAY_NAME)
    expect(normalizeAssistantDisplayName('   ')).toBe(DEFAULT_ASSISTANT_DISPLAY_NAME)
  })
  it('normalizes whitespace and removes unsafe display controls', () => {
    expect(normalizeAssistantDisplayName('  Sparky\n\u202e  Prime  ')).toBe('Sparky Prime')
  })
  it('bounds long names', () => expect(normalizeAssistantDisplayName('A'.repeat(80))).toHaveLength(40))
  it('loads and saves through the stable product-owned key', () => {
    const values = new Map<string, string>()
    const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) } }
    expect(saveAssistantDisplayName('  Aurora  ', storage)).toBe('Aurora')
    expect(values.get(ASSISTANT_DISPLAY_NAME_STORAGE_KEY)).toBe('Aurora')
    expect(loadAssistantDisplayName(storage)).toBe('Aurora')
  })
})
