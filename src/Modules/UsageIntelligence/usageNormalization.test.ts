import { describe, expect, it } from 'vitest'
import { createSyntheticUsageSnapshot } from './MockUsageAdapter'
import { createSpendSummary, normalizeUsageSnapshot } from './usageNormalization'

describe('usage intelligence normalization', () => {
  it('normalizes the public contract tag and chronologically sorts provider trends', () => {
    const snapshot = createSyntheticUsageSnapshot()
    const first = snapshot.providers[0]
    const normalized = normalizeUsageSnapshot({
      ...snapshot,
      contractVersion: 'usage-intelligence/v1',
      providers: [{ ...first, trend: [...first.trend].reverse() }, ...snapshot.providers.slice(1)],
    })

    expect(normalized.contractVersion).toBe('usage-intelligence/v1')
    expect(normalized.providers[0].trend.map((point) => point.label)).toEqual(['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Today'])
  })

  it('combines only comparable values in one currency and preserves quality', () => {
    const snapshot = createSyntheticUsageSnapshot()
    const summary = createSpendSummary(snapshot.providers)

    expect(summary.total?.value).toEqual({ amount: 57.77, currency: 'USD' })
    expect(summary.total?.quality).toBe('estimated')
    expect(summary.excludedProviders).toEqual(['google-ai-studio', 'chatgpt-subscription'])
  })

  it('refuses to invent a total across currencies', () => {
    const snapshot = createSyntheticUsageSnapshot()
    const providers = snapshot.providers.map((provider) => provider.provider === 'openrouter' && provider.spend
      ? { ...provider, spend: { ...provider.spend, value: { ...provider.spend.value, currency: 'EUR' } } }
      : provider)

    const summary = createSpendSummary(providers)

    expect(summary.total).toBeUndefined()
    expect(summary.comparisonNote).toContain('currencies differ')
    expect(summary.includedProviders).toEqual([])
  })
})
