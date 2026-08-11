import { describe, expect, it } from 'vitest'
import { HERMES_PROFILE_RUNTIME_CONTRACT, type AdapterResponse } from './contracts'
import {
  PROFILE_RUNTIME_LIMITS,
  assertMatchingResponse,
  createRequestContext,
  inspectProfileImport,
  validateConfigurationValues,
} from './runtimeSafety'

function validImport(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    format: HERMES_PROFILE_RUNTIME_CONTRACT,
    name: 'Imported profile',
    documents: { persona: '<img src=x onerror=alert(1)>', soul: 'Careful', context: 'Plain text only' },
    intent: { modelId: 'model', projectPath: null, worktreePath: null, note: '' },
    configuration: { 'runtime.mode': 'balanced' },
    ...overrides,
  })
}

describe('profile-runtime untrusted data safety', () => {
  it('keeps markup as bounded text and never interprets it as HTML', () => {
    const inspected = inspectProfileImport(validImport())
    expect(inspected.data.documents.persona).toBe('<img src=x onerror=alert(1)>')
    expect(inspected.characterCount).toBeGreaterThan(0)
  })

  it.each(['apiKey', 'access_token', 'password', 'credentialValue', 'Authorization'])(
    'rejects secret-bearing import key %s',
    (key) => expect(() => inspectProfileImport(validImport({ [key]: 'do-not-round-trip' }))).toThrow(/forbidden secret-bearing/i),
  )

  it('rejects oversized, malformed, and incompatible imports', () => {
    expect(() => inspectProfileImport('x'.repeat(PROFILE_RUNTIME_LIMITS.importBytes + 1))).toThrow(/exceeds/i)
    expect(() => inspectProfileImport('{broken')).toThrow(/valid JSON/i)
    expect(() => inspectProfileImport(JSON.stringify({ format: 'future/v9' }))).toThrow(/incompatible/i)
    expect(() => inspectProfileImport(validImport({ configuration: { '<bad>': 'x' } }))).toThrow(/malformed/i)
  })

  it('validates schema-driven non-secret configuration bounds and disposition', () => {
    const schema = [
      { key: 'count', label: 'Count', description: '', kind: 'integer' as const, required: true, minimum: 1, maximum: 3, disposition: 'manageable' as const },
      { key: 'host', label: 'Host owned', description: '', kind: 'string' as const, required: false, disposition: 'delegated' as const },
    ]
    expect(validateConfigurationValues(schema, { count: 2 })).toEqual({ count: 2 })
    expect(() => validateConfigurationValues(schema, { count: 9 })).toThrow(/maximum/i)
    expect(() => validateConfigurationValues(schema, { count: 2, host: 'change' })).toThrow(/delegated/i)
    expect(() => validateConfigurationValues(schema, { count: 2, unknown: true })).toThrow(/not declared/i)
  })
})

describe('profile and correlation isolation', () => {
  it('rejects cross-profile and mismatched-correlation adapter results', () => {
    const request = createRequestContext('profile-main', 'test:1')
    const base: AdapterResponse<string> = { ...request, revision: 1, value: 'ok' }
    expect(assertMatchingResponse(base, request).value).toBe('ok')
    expect(() => assertMatchingResponse({ ...base, profileId: 'profile-lab' }, request)).toThrow(/cross-profile/i)
    expect(() => assertMatchingResponse({ ...base, correlationId: 'test:other' }, request)).toThrow(/correlation/i)
    expect(() => assertMatchingResponse({ ...base, contract: 'future/v2' as never }, request)).toThrow(/contract/i)
  })
})
