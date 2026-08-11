import { BarChart3, Database, Gauge } from 'lucide-react'
import type { UsageDashboardSnapshot, UsageTrendPoint } from './contracts'
import { formatCount, formatMoney, formatTokenPair, qualityLabel } from './formatters'
import { reportingProviders } from './usageNormalization'

interface UsageTrendsProps {
  snapshot: UsageDashboardSnapshot
}

interface CombinedPoint {
  at: string
  label: string
  spend: number
  quality: 'reported' | 'estimated' | 'delayed'
  requests: number
  input: number
  output: number
}

function combineTrends(snapshot: UsageDashboardSnapshot): CombinedPoint[] {
  const points = new Map<string, CombinedPoint>()
  for (const provider of reportingProviders(snapshot.providers)) {
    for (const point of provider.trend) {
      const existing = points.get(point.at) ?? { at: point.at, label: point.label, spend: 0, quality: 'reported', requests: 0, input: 0, output: 0 }
      if (point.spend?.value.currency === 'USD') {
        existing.spend += point.spend.value.amount
        if (point.spend.quality === 'estimated') existing.quality = 'estimated'
        else if (point.spend.quality === 'delayed' && existing.quality === 'reported') existing.quality = 'delayed'
      }
      existing.requests += point.requests?.value ?? 0
      existing.input += point.tokens?.input ?? 0
      existing.output += point.tokens?.output ?? 0
      points.set(point.at, existing)
    }
  }
  return [...points.values()].sort((left, right) => left.at.localeCompare(right.at))
}

function linePath(points: CombinedPoint[], width = 640, height = 156): string {
  if (points.length === 0) return ''
  const max = Math.max(...points.map((point) => point.spend), 1)
  const horizontal = points.length === 1 ? 0 : width / (points.length - 1)
  return points.map((point, index) => {
    const x = index * horizontal
    const y = height - (point.spend / max) * (height - 20) - 10
    return `${index === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`
  }).join(' ')
}

function areaPath(points: CombinedPoint[]): string {
  const line = linePath(points)
  return line ? `${line} L 640 156 L 0 156 Z` : ''
}

function asTrendPoint(point: CombinedPoint): UsageTrendPoint {
  return { at: point.at, label: point.label, spend: { value: { amount: point.spend, currency: 'USD' }, quality: point.quality, label: 'Combined daily spend' } }
}

export function UsageTrends({ snapshot }: UsageTrendsProps) {
  const points = combineTrends(snapshot)
  const live = reportingProviders(snapshot.providers).some((provider) => provider.provenance.kind === 'host-collector')
  const totalRequests = points.reduce((total, point) => total + point.requests, 0)
  const totalInput = points.reduce((total, point) => total + point.input, 0)
  const totalOutput = points.reduce((total, point) => total + point.output, 0)
  const hasEstimated = points.some((point) => point.quality !== 'reported')

  return (
    <section className="ui-panel ui-trends-panel" aria-labelledby="usage-trends-title">
      <div className="ui-panel-heading">
        <div>
          <span className="ui-eyebrow">{snapshot.period.label}</span>
          <h2 id="usage-trends-title">Usage trend</h2>
        </div>
        <span className={`ui-quality ${hasEstimated ? 'ui-quality--estimated' : 'ui-quality--reported'}`}>
          {qualityLabel(hasEstimated ? 'estimated' : 'reported')}
        </span>
      </div>
      <div className="ui-trend-layout">
        <div className="ui-chart-wrap">
          <svg className="ui-trend-chart" viewBox="0 0 640 156" role="img" aria-label="Combined daily spend trend">
            <defs>
              <linearGradient id="usage-intelligence-area" x1="0" x2="0" y1="0" y2="1">
                <stop offset="0%" stopColor="#8d77ff" stopOpacity="0.38" />
                <stop offset="100%" stopColor="#8d77ff" stopOpacity="0" />
              </linearGradient>
            </defs>
            <path className="ui-chart-grid" d="M 0 38 H 640 M 0 78 H 640 M 0 118 H 640" />
            <path d={areaPath(points)} fill="url(#usage-intelligence-area)" />
            <path d={linePath(points)} className="ui-chart-line" />
            {points.map((point, index) => {
              const max = Math.max(...points.map((candidate) => candidate.spend), 1)
              const x = points.length === 1 ? 0 : (640 / (points.length - 1)) * index
              const y = 156 - (point.spend / max) * 136 - 10
              return <circle key={point.at} cx={x} cy={y} r="3.5" className="ui-chart-dot"><title>{`${point.label}: ${formatMoney(asTrendPoint(point).spend)}`}</title></circle>
            })}
          </svg>
          <div className="ui-chart-labels">{points.map((point) => <span key={point.at}>{point.label}</span>)}</div>
          <p className="ui-chart-note">{points.length === 0 && live ? 'The current live collector does not expose a time series.' : `USD values only. ${hasEstimated ? 'Estimated or delayed source data is included and labeled.' : 'All values are reported.'}`}</p>
        </div>
        <div className="ui-trend-metrics">
          <div><span className="ui-trend-icon"><BarChart3 size={15} /></span><span><small>Peak day</small><strong>{points.length ? formatMoney(asTrendPoint(points.reduce((best, point) => point.spend > best.spend ? point : best)).spend) : '—'}</strong></span></div>
          <div><span className="ui-trend-icon"><Gauge size={15} /></span><span><small>Requests</small><strong>{formatCount(totalRequests)}</strong></span></div>
          <div><span className="ui-trend-icon"><Database size={15} /></span><span><small>Tokens</small><strong>{formatTokenPair(totalInput, totalOutput)}</strong></span></div>
        </div>
      </div>
    </section>
  )
}
