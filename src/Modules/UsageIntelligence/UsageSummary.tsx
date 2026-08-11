import { Activity, Banknote, Database, Layers3 } from 'lucide-react'
import type { UsageDashboardSnapshot } from './contracts'
import { formatCount, formatMoney, formatTokenPair, qualityLabel } from './formatters'
import { reportingProviders } from './usageNormalization'

interface UsageSummaryProps {
  snapshot: UsageDashboardSnapshot
}

export function UsageSummary({ snapshot }: UsageSummaryProps) {
  const providers = reportingProviders(snapshot.providers)
  const live = providers.some((provider) => provider.provenance.kind === 'host-collector')
  const requests = providers.reduce((total, provider) => total + (provider.requests?.value ?? 0), 0)
  const inputTokens = providers.reduce((total, provider) => total + (provider.tokens?.input ?? 0), 0)
  const outputTokens = providers.reduce((total, provider) => total + (provider.tokens?.output ?? 0), 0)
  const balances = providers.filter((provider) => provider.remaining !== undefined)
  const total = snapshot.spendSummary.total

  return (
    <section className="ui-summary-grid" aria-label="Usage summary">
      <article className="ui-summary-card ui-summary-card--primary">
        <span className="ui-summary-icon"><Banknote size={17} /></span>
        <div>
          <span className="ui-eyebrow">Combined spend</span>
          <strong>{formatMoney(total)}</strong>
          <small>{total ? `${qualityLabel(total.quality)} · ${snapshot.period.label}` : 'No comparable currency total'}</small>
        </div>
      </article>
      <article className="ui-summary-card">
        <span className="ui-summary-icon"><Layers3 size={17} /></span>
        <div>
          <span className="ui-eyebrow">Budget & credit</span>
          <strong>{balances.length} sources</strong>
          <small>Shown per provider; balances are never combined.</small>
        </div>
      </article>
      <article className="ui-summary-card">
        <span className="ui-summary-icon"><Activity size={17} /></span>
        <div>
          <span className="ui-eyebrow">Requests</span>
          <strong>{formatCount(requests)}</strong>
          <small>{live ? 'Reported requests from live collectors.' : 'Reported requests across connected demo providers.'}</small>
        </div>
      </article>
      <article className="ui-summary-card">
        <span className="ui-summary-icon"><Database size={17} /></span>
        <div>
          <span className="ui-eyebrow">Tokens</span>
          <strong>{formatTokenPair(inputTokens, outputTokens)}</strong>
          <small>Reported input and output totals where available.</small>
        </div>
      </article>
    </section>
  )
}
