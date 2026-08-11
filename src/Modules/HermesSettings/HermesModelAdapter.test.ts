import { describe, expect, it } from 'vitest'
import { modelSwitchValue, normalizeHermesModelCatalog } from './HermesModelAdapter'

describe('Hermes model compatibility adapter', () => {
  it('normalizes the upstream model catalog and removes invalid entries', () => {
    const result = normalizeHermesModelCatalog({
      model: 'deepseek/deepseek-chat',
      provider: 'openrouter',
      providers: [
        {
          name: 'OpenRouter',
          slug: 'openrouter',
          authenticated: true,
          models: ['deepseek/deepseek-chat', 'deepseek/deepseek-chat', '', 42],
          featured_models: ['deepseek/deepseek-chat'],
          unavailable_models: ['paid/model'],
          capabilities: {
            'deepseek/deepseek-chat': { fast: false, reasoning: true },
          },
        },
        { name: 'Broken provider without a slug', models: ['ignored'] },
      ],
    })

    expect(result.currentModel).toBe('deepseek/deepseek-chat')
    expect(result.providers).toHaveLength(1)
    expect(result.providers[0]).toMatchObject({
      slug: 'openrouter',
      models: ['deepseek/deepseek-chat'],
      featuredModels: ['deepseek/deepseek-chat'],
      unavailableModels: ['paid/model'],
      capabilities: { 'deepseek/deepseek-chat': { fast: false, reasoning: true } },
    })
  })

  it('treats an explicitly unauthenticated provider as unavailable', () => {
    const result = normalizeHermesModelCatalog({
      providers: [{ name: 'Example', slug: 'example', authenticated: false, models: [] }],
    })
    expect(result.providers[0].authenticated).toBe(false)
  })

  it('builds the exact session-scoped model switch syntax', () => {
    expect(modelSwitchValue({ model: 'deepseek/deepseek-chat', provider: 'openrouter' }))
      .toBe('deepseek/deepseek-chat --provider openrouter --session')
  })

  it('rejects an incomplete model selection', () => {
    expect(() => modelSwitchValue({ model: '', provider: 'openrouter' })).toThrow('model and provider')
  })
})
