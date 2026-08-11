import type { ProviderUsageAdapter, UsageAdapterResult } from './ProviderUsageAdapter'
import {
  usageIntelligenceContractVersion,
  type ProviderUsage,
  type UsageCollectionRequest,
  type UsageDashboardSnapshot,
} from './contracts'
import { createSpendSummary, normalizeUsageSnapshot } from './usageNormalization'

const syntheticCollectedAt = '2026-08-09T14:30:00.000Z'

const trend = [
  { at: '2026-08-03T00:00:00.000Z', label: 'Sun', spend: 5.16, requests: 198, input: 118_000, output: 26_400 },
  { at: '2026-08-04T00:00:00.000Z', label: 'Mon', spend: 6.42, requests: 241, input: 154_000, output: 31_800 },
  { at: '2026-08-05T00:00:00.000Z', label: 'Tue', spend: 10.78, requests: 436, input: 246_000, output: 53_200 },
  { at: '2026-08-06T00:00:00.000Z', label: 'Wed', spend: 8.34, requests: 322, input: 188_000, output: 42_900 },
  { at: '2026-08-07T00:00:00.000Z', label: 'Thu', spend: 12.09, requests: 518, input: 296_000, output: 65_500 },
  { at: '2026-08-08T00:00:00.000Z', label: 'Fri', spend: 9.22, requests: 369, input: 219_000, output: 49_100 },
  { at: '2026-08-09T00:00:00.000Z', label: 'Today', spend: 7.61, requests: 284, input: 172_000, output: 38_600 },
]

function syntheticProviderUsage(): ProviderUsage[] {
  return [
    {
      provider: 'openai-api', displayName: 'OpenAI API', readiness: 'ready', state: 'connected',
      statusMessage: 'Synthetic host collection succeeded.',
      spend: { value: { amount: 18.42, currency: 'USD' }, quality: 'reported', label: 'Current period spend' },
      remaining: { value: { amount: 56.58, currency: 'USD' }, quality: 'reported', label: 'Budget remaining' },
      requests: { value: 1_238, quality: 'reported', label: 'Requests this period' },
      tokens: { input: 712_000, output: 161_000, quality: 'reported' },
      billingPeriod: { label: 'Monthly budget', resetsAt: '2026-09-01T00:00:00.000Z' },
      trend: trend.map((point) => ({ at: point.at, label: point.label, spend: { value: { amount: point.spend * 0.38, currency: 'USD' }, quality: 'reported', label: 'Daily spend' }, requests: { value: Math.round(point.requests * 0.43), quality: 'reported', label: 'Daily requests' }, tokens: { input: Math.round(point.input * 0.44), output: Math.round(point.output * 0.44), quality: 'reported' } })),
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', collectedAt: syntheticCollectedAt, detail: 'Deterministic demo data; no provider request was made.' },
    },
    {
      provider: 'openrouter', displayName: 'OpenRouter', readiness: 'ready', state: 'connected',
      statusMessage: 'Synthetic managed-key credit snapshot.',
      spend: { value: { amount: 6.83, currency: 'USD' }, quality: 'reported', label: 'Usage against credits' },
      remaining: { value: { amount: 33.17, currency: 'USD' }, quality: 'reported', label: 'Credits remaining' },
      requests: { value: 604, quality: 'reported', label: 'Requests this period' },
      tokens: { input: 346_000, output: 81_000, quality: 'reported' },
      billingPeriod: { label: 'Credit balance', endsAt: '2026-09-30T00:00:00.000Z' },
      trend: trend.map((point) => ({ at: point.at, label: point.label, spend: { value: { amount: point.spend * 0.17, currency: 'USD' }, quality: 'reported', label: 'Daily credit usage' }, requests: { value: Math.round(point.requests * 0.2), quality: 'reported', label: 'Daily requests' } })),
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', collectedAt: syntheticCollectedAt, detail: 'Deterministic demo data; no provider request was made.' },
    },
    {
      provider: 'anthropic-api', displayName: 'Anthropic API / Claude', readiness: 'ready', state: 'warning',
      statusMessage: 'Synthetic delayed cost report.',
      spend: { value: { amount: 20.44, currency: 'USD' }, quality: 'delayed', label: 'Reported cost (delayed)' },
      requests: { value: 932, quality: 'reported', label: 'Requests this period' }, tokens: { input: 528_000, output: 94_000, quality: 'reported' },
      billingPeriod: { label: 'Current billing period', resetsAt: '2026-09-01T00:00:00.000Z' },
      trend: trend.map((point) => ({ at: point.at, label: point.label, spend: { value: { amount: point.spend * 0.31, currency: 'USD' }, quality: 'delayed', label: 'Daily reported cost (delayed)' }, requests: { value: Math.round(point.requests * 0.27), quality: 'reported', label: 'Daily requests' } })),
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', collectedAt: '2026-08-09T13:00:00.000Z', detail: 'Demo data models a delayed provider report; no provider request was made.' },
    },
    {
      provider: 'google-cloud', displayName: 'Google Cloud / APIs', readiness: 'partial', state: 'warning', statusMessage: 'Synthetic estimate based on a delayed export.',
      spend: { value: { amount: 12.08, currency: 'USD' }, quality: 'estimated', label: 'Estimated usage cost' }, remaining: { value: { amount: 37.92, currency: 'USD' }, quality: 'estimated', label: 'Budget remaining' },
      billingPeriod: { label: 'Monthly budget', resetsAt: '2026-09-01T00:00:00.000Z' },
      trend: trend.map((point) => ({ at: point.at, label: point.label, spend: { value: { amount: point.spend * 0.14, currency: 'USD' }, quality: 'estimated', label: 'Daily estimated cost' } })),
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', collectedAt: '2026-08-09T10:30:00.000Z', detail: 'Demo data models a delayed billing export; no provider request was made.' },
    },
    {
      provider: 'google-ai-studio', displayName: 'Google AI Studio / Gemini API', readiness: 'partial', state: 'not-configured', statusMessage: 'No synthetic host connection configured.',
      billingPeriod: { label: 'AI Studio usage is not collected in this demo.' }, trend: [],
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', detail: 'This is an intentionally unconfigured demo state; no provider request was made.' },
    },
    {
      provider: 'chatgpt-subscription', displayName: 'ChatGPT subscription', readiness: 'unsupported', state: 'unavailable', statusMessage: 'No supported programmatic consumer usage API is assumed.',
      billingPeriod: { label: 'Manual subscription surface only.' }, trend: [],
      provenance: { kind: 'synthetic', label: 'Synthetic demo collector', detail: 'Consumer dashboard scraping is intentionally not implemented.' },
    },
  ]
}

export function createSyntheticUsageSnapshot(): UsageDashboardSnapshot {
  const providers = syntheticProviderUsage()
  return normalizeUsageSnapshot({
    contractVersion: usageIntelligenceContractVersion, generatedAt: syntheticCollectedAt,
    period: { start: '2026-08-03T00:00:00.000Z', end: '2026-08-10T00:00:00.000Z', label: 'Last 7 days' }, providers,
    spendSummary: createSpendSummary(providers),
    notices: [
      'Synthetic demo data only. No credentials, browser storage, or provider network calls are used.',
      'Combined spend includes comparable USD values; estimated and delayed values remain labeled.',
    ],
  })
}

export class MockUsageAdapter implements ProviderUsageAdapter {
  async collect(request: UsageCollectionRequest): Promise<UsageAdapterResult> {
    const snapshot = createSyntheticUsageSnapshot()
    const providers = request.providers === undefined ? snapshot.providers : snapshot.providers.filter((provider) => request.providers?.includes(provider.provider))
    return { kind: 'success', snapshot: normalizeUsageSnapshot({ ...snapshot, period: request.period, providers, spendSummary: createSpendSummary(providers) }) }
  }
}

export const mockUsageAdapter = new MockUsageAdapter()
