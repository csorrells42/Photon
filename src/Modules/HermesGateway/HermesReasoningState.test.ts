import { describe, expect, it } from 'vitest'
import { reasoningCatalogSelection, reasoningEffortFromSessionInfo } from './HermesReasoningState'

describe('Hermes reasoning session state', () => {
  it('preserves state only when the backend omitted the field', () => {
    expect(reasoningEffortFromSessionInfo({ model: 'gpt-5.6' }, 'high')).toBe('high')
  })

  it('clears a stale model override when the backend reports model default', () => {
    expect(reasoningEffortFromSessionInfo({
      reasoning_configured: false,
      reasoning_effort: '',
    }, 'high')).toBeNull()
  })

  it('treats a present legacy empty value as inherit rather than retaining stale state', () => {
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: '' }, 'high')).toBeNull()
  })

  it('preserves exact toggle and effort tokens', () => {
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: 'enabled' }, null)).toBe('enabled')
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: 'xhigh' }, null)).toBe('xhigh')
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: 'none' }, 'high')).toBe('none')
  })

  it('fails closed on malformed provider state', () => {
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: '<script>' }, 'high')).toBeNull()
    expect(reasoningEffortFromSessionInfo({ reasoning_effort: { value: 'high' } }, 'high')).toBeNull()
  })

  it('keeps an explicitly requested pending model over the still-live old catalog row', () => {
    const oldModel = { model: 'old', provider: 'openrouter' }
    const pending = { model: 'new', provider: 'openrouter' }
    expect(reasoningCatalogSelection(oldModel, oldModel, pending, true)).toEqual(pending)
  })

  it('adopts the active catalog when no explicit model transition is pending', () => {
    const oldModel = { model: 'old', provider: 'openrouter' }
    const current = { model: 'current', provider: 'openai-api' }
    expect(reasoningCatalogSelection(oldModel, current, undefined, true)).toEqual(current)
    expect(reasoningCatalogSelection(oldModel, current, undefined, false)).toEqual(oldModel)
  })
})
