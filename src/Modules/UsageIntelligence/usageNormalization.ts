import {
  usageIntelligenceContractVersion,
  type MoneyMetric,
  type ProviderId,
  type ProviderUsage,
  type SpendSummary,
  type UsageDashboardSnapshot,
} from './contracts'

const comparableQualities = new Set(['reported', 'estimated', 'delayed'])

function isComparable(metric: MoneyMetric | undefined): metric is MoneyMetric {
  return metric !== undefined && comparableQualities.has(metric.quality)
}

/**
 * Calculates a total only when every included provider reports the same
 * currency. This deliberately refuses currency conversion or invented totals.
 */
export function createSpendSummary(providers: readonly ProviderUsage[]): SpendSummary {
  const reporting = reportingProviders(providers)
  const included = reporting.filter((provider) => isComparable(provider.spend))
  const excludedProviders = providers
    .filter((provider) => !included.includes(provider))
    .map((provider) => provider.provider)

  if (included.length === 0) {
    return {
      includedProviders: [],
      excludedProviders,
      comparisonNote: 'No comparable spend is available.',
    }
  }

  const currencies = new Set(included.map((provider) => provider.spend?.value.currency))
  if (currencies.size !== 1) {
    return {
      includedProviders: [],
      excludedProviders: providers.map((provider) => provider.provider),
      comparisonNote: 'Spend is not combined because reported currencies differ.',
    }
  }

  const currency = included[0].spend?.value.currency ?? 'USD'
  const quality = included.some((provider) => provider.spend?.quality === 'estimated')
    ? 'estimated'
    : included.some((provider) => provider.spend?.quality === 'delayed')
      ? 'delayed'
      : 'reported'

  return {
    total: {
      value: {
        amount: roundMoney(included.reduce((total, provider) => total + (provider.spend?.value.amount ?? 0), 0)),
        currency,
      },
      quality,
      label: 'Combined comparable spend',
    },
    includedProviders: included.map((provider) => provider.provider),
    excludedProviders,
    comparisonNote: reporting.length !== providers.length
      ? 'Live totals exclude synthetic demo providers.'
      : excludedProviders.length > 0
      ? 'Includes only providers with comparable reported, estimated, or delayed spend.'
      : 'All configured provider spend is comparable in this currency.',
  }
}

export function reportingProviders(providers: readonly ProviderUsage[]): readonly ProviderUsage[] {
  const native = providers.filter((provider) => provider.provenance.kind !== 'synthetic')
  return native.length > 0 ? native : providers
}

/** Ensures a host collector cannot accidentally change the public schema tag. */
export function normalizeUsageSnapshot(snapshot: UsageDashboardSnapshot): UsageDashboardSnapshot {
  return {
    ...snapshot,
    contractVersion: usageIntelligenceContractVersion,
    providers: snapshot.providers.map(normalizeProviderUsage),
    spendSummary: createSpendSummary(snapshot.providers),
  }
}

function normalizeProviderUsage(provider: ProviderUsage): ProviderUsage {
  return {
    ...provider,
    trend: [...provider.trend].sort((left, right) => left.at.localeCompare(right.at)),
    ...(provider.credentialBreakdown
      ? { credentialBreakdown: [...provider.credentialBreakdown].sort((left, right) => left.displayName.localeCompare(right.displayName)) }
      : {}),
  }
}

function roundMoney(value: number): number {
  return Math.round((value + Number.EPSILON) * 1_000_000) / 1_000_000
}

export function providerIds(snapshot: UsageDashboardSnapshot): ProviderId[] {
  return snapshot.providers.map((provider) => provider.provider)
}
