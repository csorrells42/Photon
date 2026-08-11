import type { MetricQuality, MoneyMetric } from './contracts'

export function formatMoney(metric: MoneyMetric | undefined): string {
  if (metric === undefined) return 'Unavailable'
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: metric.value.currency,
    maximumFractionDigits: 2,
  }).format(metric.value.amount)
}

export function formatCount(value: number | undefined): string {
  if (value === undefined) return '—'
  return new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}

export function formatTokenPair(input: number, output: number): string {
  return `${formatCount(input)} in / ${formatCount(output)} out`
}

export function qualityLabel(quality: MetricQuality): string {
  return {
    reported: 'Reported',
    estimated: 'Estimated',
    delayed: 'Delayed',
    unavailable: 'Unavailable',
    'not-comparable': 'Not comparable',
  }[quality]
}

export function formatDay(value: string | undefined): string | undefined {
  if (value === undefined) return undefined
  return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric' }).format(new Date(value))
}

export function freshnessLabel(collectedAt: string | undefined): string {
  if (collectedAt === undefined) return 'No collection timestamp'
  return `Collected ${new Intl.DateTimeFormat('en-US', {
    month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short',
  }).format(new Date(collectedAt))}`
}
