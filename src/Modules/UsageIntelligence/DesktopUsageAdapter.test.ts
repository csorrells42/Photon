import { describe, expect, it, vi } from 'vitest'
import { DesktopUsageAdapter, openRouterProviderFromResult, openRouterProviderFromResults } from './DesktopUsageAdapter'
import { mockUsageAdapter } from './MockUsageAdapter'

const request = { period: { start: '2026-08-03T00:00:00.000Z', end: '2026-08-10T00:00:00.000Z', label: 'Last 7 days' } }

describe('desktop usage adapter', () => {
  it('maps live key data without inventing requests, tokens, or time-series points', () => {
    const provider = openRouterProviderFromResult({ kind: 'success', data: { usage: 25.5, usageDaily: 1.5, usageWeekly: 4.25, usageMonthly: 12, limitRemaining: 74.5, limitReset: 'monthly', isFreeTier: false, collectedAt: '2026-08-09T12:00:00.000Z' } })
    expect(provider.state).toBe('connected')
    expect(provider.spend?.value.amount).toBe(4.25)
    expect(provider.remaining?.value.amount).toBe(74.5)
    expect(provider.requests).toBeUndefined()
    expect(provider.tokens).toBeUndefined()
    expect(provider.trend).toEqual([])
    expect(provider.provenance.kind).toBe('host-collector')
  })

  it('replaces only OpenRouter and excludes synthetic dollars from the live total', async () => {
    const collector = vi.fn(async (credentialId: string) => ({ kind: 'success' as const, data: { usage: credentialId === 'ali' ? 9 : 6, usageWeekly: credentialId === 'ali' ? 3 : 2, isFreeTier: false, collectedAt: '2026-08-09T12:00:00.000Z' } }))
    const adapter = new DesktopUsageAdapter(
      collector,
      mockUsageAdapter,
      async () => ({ kind: 'success', entries: [
        { provider: 'openrouter', credentialId: 'ali', configured: true },
        { provider: 'openrouter', credentialId: 'scarlett', configured: true },
      ] }),
    )
    const result = await adapter.collect(request)
    expect(result.kind).toBe('success')
    if (result.kind !== 'success') return
    const openRouter = result.snapshot.providers.find((provider) => provider.provider === 'openrouter')
    expect(openRouter?.provenance.kind).toBe('host-collector')
    expect(openRouter?.credentialBreakdown?.map((entry) => entry.displayName)).toEqual(['Ali', 'Scarlett'])
    expect(openRouter?.spend?.value.amount).toBe(5)
    expect(result.snapshot.spendSummary.total?.value.amount).toBe(5)
    expect(result.snapshot.spendSummary.includedProviders).toEqual(['openrouter'])
    expect(collector).toHaveBeenCalledWith('ali', undefined)
    expect(collector).toHaveBeenCalledWith('scarlett', undefined)
  })

  it('keeps the synthetic adapter in browser mode', async () => {
    const adapter = new DesktopUsageAdapter(async () => ({ kind: 'unavailable' }), mockUsageAdapter, async () => ({ kind: 'unavailable' }))
    const result = await adapter.collect(request)
    expect(result.kind).toBe('success')
    if (result.kind !== 'success') return
    expect(result.snapshot.providers.every((provider) => provider.provenance.kind === 'synthetic')).toBe(true)
  })

  it('keeps every named key visible when one collector needs attention', () => {
    const provider = openRouterProviderFromResults([
      { credentialId: 'ali', result: { kind: 'success', data: { usage: 9, usageWeekly: 3, limitRemaining: 7, isFreeTier: false, collectedAt: '2026-08-09T12:00:00.000Z' } } },
      { credentialId: 'bob', result: { kind: 'failure', code: 'permission-denied', message: 'Replace this stored key.', retryable: false } },
    ])

    expect(provider.state).toBe('warning')
    expect(provider.spend?.value.amount).toBe(3)
    expect(provider.credentialBreakdown).toHaveLength(2)
    expect(provider.credentialBreakdown?.find((entry) => entry.credentialId === 'bob')).toMatchObject({ state: 'error', statusMessage: 'Replace this stored key.' })
  })

  it('shows named Gemini keys as secure inventory without inventing usage', async () => {
    const adapter = new DesktopUsageAdapter(
      async () => ({ kind: 'unavailable' }),
      mockUsageAdapter,
      async () => ({ kind: 'success', entries: [
        { provider: 'google-ai-studio', credentialId: 'ali', configured: true },
        { provider: 'google-ai-studio', credentialId: 'charlie', configured: true },
      ] }),
    )
    const result = await adapter.collect(request)
    expect(result.kind).toBe('success')
    if (result.kind !== 'success') return
    const gemini = result.snapshot.providers.find((provider) => provider.provider === 'google-ai-studio')
    expect(gemini?.state).toBe('warning')
    expect(gemini?.spend).toBeUndefined()
    expect(gemini?.credentialBreakdown?.map((entry) => entry.displayName)).toEqual(['Ali', 'Charlie'])
    expect(gemini?.statusMessage).toContain('stored securely')
  })

  it('maps official OpenAI and Anthropic organization observations without exposing keys', async () => {
    const adapter = new DesktopUsageAdapter(
      async () => ({ kind: 'unavailable' }),
      mockUsageAdapter,
      async () => ({ kind: 'success', entries: [
        { provider: 'openai-api', credentialId: 'engineering', configured: true },
        { provider: 'anthropic-api', credentialId: 'research', configured: true },
      ] }),
      async (provider) => provider === 'google-ai-studio' ? ({
        kind: 'failure' as const,
        code: 'not-configured',
        message: 'Google Cloud CLI is not configured.',
        retryable: false,
      }) : ({
        kind: 'success',
        data: {
          capabilities: [{ capability: 'organization-usage', state: 'Supported', reason: 'Available.' }],
          observations: provider === 'openai-api'
            ? [
                { kind: 'Usage', metric: 'input-tokens', value: 1000, unit: 'tokens', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Usage', metric: 'output-tokens', value: 200, unit: 'tokens', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Usage', metric: 'model-requests', value: 12, unit: 'requests', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Cost', metric: 'actual-cost', value: 7, unit: 'currency', currency: 'USD', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
              ]
            : [
                { kind: 'Usage', metric: 'uncached-input-tokens', value: 500, unit: 'tokens', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Usage', metric: 'cache-read-input-tokens', value: 300, unit: 'tokens', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Usage', metric: 'output-tokens', value: 100, unit: 'tokens', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
                { kind: 'Cost', metric: 'actual-cost', value: 5, unit: 'currency', currency: 'USD', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' } },
              ],
          errors: [],
          collectedAt: '2026-08-09T12:00:00.000Z',
        },
      }),
    )
    const result = await adapter.collect(request)
    expect(result.kind).toBe('success')
    if (result.kind !== 'success') return
    const openai = result.snapshot.providers.find((provider) => provider.provider === 'openai-api')
    const anthropic = result.snapshot.providers.find((provider) => provider.provider === 'anthropic-api')
    expect(openai).toMatchObject({ state: 'connected', spend: { value: { amount: 7, currency: 'USD' } }, requests: { value: 12 }, tokens: { input: 1000, output: 200 } })
    expect(anthropic).toMatchObject({ state: 'connected', spend: { value: { amount: 5, currency: 'USD' } }, tokens: { input: 800, output: 100 } })
    expect(result.snapshot.spendSummary.total?.value.amount).toBe(12)
    const serialized = JSON.stringify(result.snapshot)
    expect(serialized).not.toMatch(/"(?:apiKey|authorization)"\s*:/i)
    expect(serialized).not.toMatch(/bearer\s+[a-z0-9._~-]+/i)
  })

  it('keeps Gemini project aggregates separate from named API key inventory', async () => {
    const adapter = new DesktopUsageAdapter(
      async () => ({ kind: 'unavailable' }),
      mockUsageAdapter,
      async () => ({ kind: 'success', entries: [
        { provider: 'google-ai-studio', credentialId: 'ali', configured: true },
        { provider: 'google-ai-studio', credentialId: 'scarlett', configured: true },
      ] }),
      async (provider) => provider === 'google-ai-studio' ? ({
        kind: 'success',
        data: {
          capabilities: [
            { capability: 'project-usage', state: 'Partial', reason: 'Project aggregate only.' },
            { capability: 'gemini-per-key-usage', state: 'Unavailable', reason: 'No API-key attribution.' },
          ],
          observations: [
            { kind: 'Usage', metric: 'input-tokens', value: 1500, unit: 'tokens', freshness: { collectedAt: '2026-08-10T00:00:00.000Z', state: 'Delayed', note: 'Monitoring lag.' } },
            { kind: 'Usage', metric: 'output-tokens', value: 300, unit: 'tokens', freshness: { collectedAt: '2026-08-10T00:00:00.000Z', state: 'Delayed', note: 'Monitoring lag.' } },
            { kind: 'Usage', metric: 'requests', value: 18, unit: 'requests', freshness: { collectedAt: '2026-08-10T00:00:00.000Z', state: 'Delayed', note: 'Monitoring lag.' } },
          ],
          errors: [],
          collectedAt: '2026-08-10T00:00:00.000Z',
        },
      } as const) : ({ kind: 'failure', code: 'not-configured', message: 'Not configured.', retryable: false } as const),
    )
    const result = await adapter.collect(request)
    expect(result.kind).toBe('success')
    if (result.kind !== 'success') return
    const gemini = result.snapshot.providers.find((provider) => provider.provider === 'google-ai-studio')
    expect(gemini).toMatchObject({
      state: 'warning',
      requests: { value: 18 },
      tokens: { input: 1500, output: 300 },
    })
    expect(gemini?.spend).toBeUndefined()
    expect(gemini?.credentialBreakdown?.map((entry) => entry.displayName)).toEqual(['Ali', 'Scarlett'])
    expect(gemini?.credentialBreakdown?.every((entry) => entry.state === 'unavailable')).toBe(true)
    expect(gemini?.statusMessage.toLowerCase()).toContain('project-level')
    expect(JSON.stringify(gemini)).not.toMatch(/bearer|authorization/i)
  })
})
