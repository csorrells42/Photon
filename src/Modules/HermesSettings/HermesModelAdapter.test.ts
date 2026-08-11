import { afterEach, describe, expect, it, vi } from 'vitest'
import { hermesGateway } from '../HermesGateway/HermesGatewayClient'
import {
  defaultModelSwitchValue,
  effectiveHermesReasoningEffort,
  hermesModelAdapter,
  hermesReasoningControlForSelection,
  modelSwitchValue,
  normalizeHermesModelCatalog,
  normalizeHermesReasoningEffort,
} from './HermesModelAdapter'

afterEach(() => vi.restoreAllMocks())

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
      reasoning_control: {
        model: 'deepseek/deepseek-chat',
        provider: 'openrouter',
        label: 'Thinking',
        options: ['none', 'low', 'high', 'bogus'],
        effort_aliases: { minimal: 'low', ultra: 'bogus' },
        option_labels: { none: 'Off', low: 'Low', bogus: 'Bogus' },
        default_effort: 'low',
        default_enabled: true,
        mandatory: false,
        supports_effort: true,
        supports_toggle: true,
        source: 'server',
      },
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
    expect(result.reasoningControl).toEqual({
      defaultEffort: 'low',
      defaultEnabled: true,
      effortAliases: { minimal: 'low' },
      label: 'Thinking',
      mandatory: false,
      optionLabels: { none: 'Off', low: 'Low' },
      options: ['none', 'low', 'high'],
      source: 'server',
      supportsEffort: true,
      supportsToggle: true,
      targetModel: 'deepseek/deepseek-chat',
      targetProvider: 'openrouter',
    })
  })

  it('treats an explicitly unauthenticated provider as unavailable', () => {
    const result = normalizeHermesModelCatalog({
      providers: [{ name: 'Example', slug: 'example', authenticated: false, models: [] }],
    })
    expect(result.providers[0].authenticated).toBe(false)
  })

  it('does not invent reasoning levels when the server publishes none', () => {
    const result = normalizeHermesModelCatalog({
      model: 'deepseek/deepseek-v4-flash-0731',
      provider: 'openrouter',
      providers: [],
    })
    expect(result.reasoningControl).toEqual({
      effortAliases: {},
      label: 'Reasoning',
      mandatory: false,
      optionLabels: {},
      options: [],
      source: 'unverified',
      supportsEffort: false,
      supportsToggle: false,
      targetModel: 'deepseek/deepseek-v4-flash-0731',
      targetProvider: 'openrouter',
    })
  })

  it('preserves the explicit Hermes compatibility set for providers without native option metadata', () => {
    const result = normalizeHermesModelCatalog({
      model: 'deepseek/deepseek-v4-flash-0731',
      provider: 'openrouter',
      providers: [],
      reasoning_control: { label: 'Thinking', options: ['none', 'low', 'medium', 'high'], source: 'compatibility' },
    })
    expect(result.reasoningControl).toEqual({
      effortAliases: {},
      label: 'Thinking',
      mandatory: false,
      optionLabels: {},
      options: ['none', 'low', 'medium', 'high'],
      source: 'compatibility',
      supportsEffort: true,
      supportsToggle: true,
      targetModel: 'deepseek/deepseek-v4-flash-0731',
      targetProvider: 'openrouter',
    })
  })

  it('binds reasoning options to the exact selected model and projects adapter aliases', () => {
    const catalog = normalizeHermesModelCatalog({
      model: 'gpt-5.6-sol',
      provider: 'openai-codex',
      providers: [],
      reasoning_control: {
        model: 'gpt-5.6-sol',
        provider: 'openai-codex',
        label: 'Effort',
        options: ['low', 'medium', 'high', 'xhigh', 'max'],
        effort_aliases: { minimal: 'low', ultra: 'max' },
        source: 'compatibility',
      },
    })
    const exact = hermesReasoningControlForSelection(catalog, { provider: 'openai-codex', model: 'gpt-5.6-sol' })
    expect(exact).toBe(catalog.reasoningControl)
    expect(effectiveHermesReasoningEffort('ultra', exact)).toBe('max')
    expect(hermesReasoningControlForSelection(catalog, { provider: 'openrouter', model: 'gpt-5.6-sol' })).toBeNull()
    expect(hermesReasoningControlForSelection(catalog, { provider: 'openai-codex', model: 'gpt-5.5' })).toBeNull()
  })

  it('requests reasoning metadata for the exact selected provider and model', async () => {
    const request = vi.spyOn(hermesGateway, 'request').mockResolvedValue({
      model: 'old-model',
      provider: 'openrouter',
      providers: [],
      reasoning_control: {
        model: 'openai/gpt-5.6',
        provider: 'openrouter',
        label: 'Effort',
        options: ['low', 'medium', 'high', 'xhigh', 'max'],
        source: 'compatibility',
      },
    })
    const catalog = await hermesModelAdapter.options('session-1', true, {
      model: 'openai/gpt-5.6',
      provider: 'openrouter',
    })
    expect(request).toHaveBeenCalledWith('model.options', {
      explicit_only: true,
      session_id: 'session-1',
      refresh: true,
      reasoning_model: 'openai/gpt-5.6',
      reasoning_provider: 'openrouter',
    })
    expect(catalog.reasoningControl.targetModel).toBe('openai/gpt-5.6')
    expect(catalog.reasoningControl.targetProvider).toBe('openrouter')
  })

  it('builds the exact session-scoped model switch syntax', () => {
    expect(modelSwitchValue({ model: 'deepseek/deepseek-chat', provider: 'openrouter' }))
      .toBe('deepseek/deepseek-chat --provider openrouter --session')
  })

  it('builds the exact persistent model switch syntax', () => {
    expect(defaultModelSwitchValue({ model: 'gpt-oss:20b', provider: 'local-ollama' }))
      .toBe('gpt-oss:20b --provider local-ollama --global')
  })

  it('rejects an incomplete model selection', () => {
    expect(() => modelSwitchValue({ model: '', provider: 'openrouter' })).toThrow('model and provider')
  })

  it('accepts only bounded Hermes reasoning effort values', () => {
    expect(normalizeHermesReasoningEffort('none')).toBe('none')
    expect(normalizeHermesReasoningEffort('enabled')).toBe('enabled')
    expect(normalizeHermesReasoningEffort('xhigh')).toBe('xhigh')
    expect(normalizeHermesReasoningEffort('ultra')).toBe('ultra')
    expect(normalizeHermesReasoningEffort('')).toBeNull()
    expect(normalizeHermesReasoningEffort('unbounded')).toBeNull()
  })

  it('preserves an honest runner-managed state without exposing options', () => {
    const result = normalizeHermesModelCatalog({
      model: 'ai/qwen3',
      provider: 'custom:docker-model-runner',
      providers: [],
      reasoning_control: {
        label: 'Reasoning',
        options: ['high'],
        source: 'server-managed',
      },
    })
    expect(result.reasoningControl).toMatchObject({
      options: [],
      source: 'server-managed',
      supportsEffort: false,
      supportsToggle: false,
    })
  })

  it('distinguishes an unset model default from a configured toggle', async () => {
    vi.spyOn(hermesGateway, 'request')
      .mockResolvedValueOnce({ value: '', configured: false })
      .mockResolvedValueOnce({ value: 'enabled', configured: true })
    await expect(hermesModelAdapter.getReasoningEffort('session-1')).resolves.toBeNull()
    await expect(hermesModelAdapter.getReasoningEffort('session-1')).resolves.toBe('enabled')
  })

  it('requests exact server-side model-bound validation when saving', async () => {
    const request = vi.spyOn(hermesGateway, 'request').mockResolvedValue({ value: 'enabled' })
    await expect(hermesModelAdapter.setReasoningEffort('enabled', 'session-1')).resolves.toBe('enabled')
    expect(request).toHaveBeenCalledWith('config.set', {
      session_id: 'session-1',
      key: 'reasoning',
      value: 'enabled',
      validate_model_control: true,
    })
  })
})
