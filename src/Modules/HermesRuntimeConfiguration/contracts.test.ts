import { describe, expect, it } from 'vitest'
import { assertRuntimeEndpointDraft, normalizeRuntimeBaseUrl, normalizeRuntimeEndpointSnapshot, normalizeRuntimeEndpointValidation, runtimeProfilePayload } from './contracts'

describe('Hermes runtime endpoint contracts', () => {
  it('accepts bounded HTTP endpoints and rejects credential-bearing or active-content URLs', () => {
    expect(normalizeRuntimeBaseUrl('http://host.docker.internal:11434/v1/')).toBe('http://host.docker.internal:11434/v1')
    expect(normalizeRuntimeBaseUrl('https://10.0.0.8:8080/v1')).toBe('https://10.0.0.8:8080/v1')
    expect(normalizeRuntimeBaseUrl('http://user:pass@127.0.0.1/v1')).toBeNull()
    expect(normalizeRuntimeBaseUrl('file:///C:/models')).toBeNull()
    expect(normalizeRuntimeBaseUrl('https://example.test/v1?token=secret')).toBeNull()
  })

  it('validates model configuration bounds without a secret field', () => {
    expect(assertRuntimeEndpointDraft({
      id: 'local-gpt-oss', name: 'Local GPT-OSS', baseUrl: 'http://host.docker.internal:11434/v1',
      model: 'gpt-oss:20b', contextLength: 131_072, discoverModels: true, makeDefault: true,
    })).toMatchObject({ id: 'local-gpt-oss', model: 'gpt-oss:20b', contextLength: 131_072 })
    expect(() => assertRuntimeEndpointDraft({
      name: 'Bad', baseUrl: 'http://127.0.0.1/v1', model: '', discoverModels: true, makeDefault: false,
    })).toThrow('model ID')
  })

  it('normalizes a renderer-safe endpoint inventory and drops malformed rows', () => {
    const snapshot = normalizeRuntimeEndpointSnapshot({
      current: { provider: 'local-gpt-oss', model: 'gpt-oss:20b', base_url: 'http://host.docker.internal:11434/v1' },
      endpoints: [
        { id: 'local-gpt-oss', name: 'Local GPT-OSS', base_url: 'http://host.docker.internal:11434/v1', model: 'gpt-oss:20b', models: ['gpt-oss:20b'], discover_models: true, has_api_key: false, is_current: true, source: 'providers', profiles: [{ id: 'balanced', name: 'Balanced', model: 'gpt-oss:20b', overrides: { temperature: .4, top_k: 40 }, is_active: true }] },
        { id: '../bad', name: 'Bad', base_url: 'https://example.test/v1', model: 'bad' },
      ],
    })
    expect(snapshot.endpoints).toHaveLength(1)
    expect(snapshot.endpoints[0]).toMatchObject({ id: 'local-gpt-oss', isCurrent: true, hasCredential: false })
    expect(snapshot.endpoints[0].profiles[0]).toMatchObject({ id: 'balanced', isActive: true, overrides: { temperature: .4, topK: 40 } })
    expect(JSON.stringify(snapshot)).not.toMatch(/api_key|password|token|secret/i)
  })

  it('projects exact LM Studio native settings without inventing missing values', () => {
    const result = normalizeRuntimeEndpointValidation({ ok: true, reachable: true, runtime_kind: 'lm-studio', models: ['openai/gpt-oss-20b'], model_details: [{ id: 'openai/gpt-oss-20b', type: 'llm', loaded: true, max_context_length: 131072, loaded_instances: [{ id: 'openai/gpt-oss-20b', config: { context_length: 65536, flash_attention: true } }], capabilities: { reasoning: { allowed_options: ['low', 'medium', 'high'], default: 'low' } } }] })
    expect(result.runtimeKind).toBe('lm-studio')
    expect(result.modelDetails[0]).toMatchObject({ loaded: true, maxContextLength: 131072, loadedInstances: [{ config: { context_length: 65536, flash_attention: true } }], capabilities: { reasoning: { allowedOptions: ['low', 'medium', 'high'], default: 'low' } } })
    expect(JSON.stringify(result)).not.toContain('temperature')
  })

  it('serializes only explicit profile fields and never context', () => {
    const payload = runtimeProfilePayload({ id: 'balanced', name: 'Balanced', model: 'gpt-oss', makeActive: true, overrides: { temperature: .5, topK: 40 } })
    expect(payload).toMatchObject({ overrides: { temperature: .5, top_k: 40 }, make_active: true })
    expect(JSON.stringify(payload)).not.toMatch(/context/i)
  })
})
