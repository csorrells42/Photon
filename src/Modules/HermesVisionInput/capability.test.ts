import { describe, expect, it } from 'vitest'
import { resolveSelectedHermesVisionCapability } from './capability'

describe('selected Hermes model vision capability', () => {
  it('remains explicitly unknown without exact authoritative evidence', () => {
    expect(resolveSelectedHermesVisionCapability({ provider: 'openrouter', model: 'vendor/vision-model' })).toBe('unknown')
    expect(resolveSelectedHermesVisionCapability(null)).toBe('unknown')
    expect(resolveSelectedHermesVisionCapability(
      { provider: 'openrouter', model: 'vendor/vision-model' },
      { source: 'hermes-model-info/v1', provider: 'openrouter', model: 'vendor/vision-model', supportsVision: 'yes' } as never,
    )).toBe('unknown')
  })

  it('accepts only exact selected-model evidence from the authoritative model-info contract', () => {
    expect(resolveSelectedHermesVisionCapability(
      { provider: 'openrouter', model: 'vendor/model' },
      { source: 'hermes-model-info/v1', provider: 'openrouter', model: 'vendor/model', supportsVision: true },
    )).toBe('supported')
    expect(resolveSelectedHermesVisionCapability(
      { provider: 'openrouter', model: 'vendor/model' },
      { source: 'hermes-model-info/v1', provider: 'openrouter', model: 'vendor/model', supportsVision: false },
    )).toBe('unsupported')
    expect(resolveSelectedHermesVisionCapability(
      { provider: 'openrouter', model: 'vendor/model' },
      { source: 'hermes-model-info/v1', provider: 'openrouter', model: 'other/model', supportsVision: true },
    )).toBe('unknown')
  })
})
