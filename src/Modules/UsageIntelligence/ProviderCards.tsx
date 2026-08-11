import { AlertTriangle, Check, CircleSlash2, CloudOff, Settings2 } from 'lucide-react'
import type { ProviderConnectionState, ProviderUsage } from './contracts'
import { formatCount, formatDay, formatMoney, formatTokenPair, freshnessLabel, qualityLabel } from './formatters'

interface ProviderCardsProps {
  providers: ProviderUsage[]
}

const stateMeta: Record<ProviderConnectionState, { label: string; icon: typeof Check }> = {
  connected: { label: 'Connected', icon: Check },
  warning: { label: 'Attention', icon: AlertTriangle },
  error: { label: 'Error', icon: CloudOff },
  unavailable: { label: 'Unavailable', icon: CircleSlash2 },
  'not-configured': { label: 'Not configured', icon: Settings2 },
}

function ProviderCard({ provider }: { provider: ProviderUsage }) {
  const state = stateMeta[provider.state]
  const StateIcon = state.icon
  const periodEnd = formatDay(provider.billingPeriod?.resetsAt ?? provider.billingPeriod?.endsAt)

  return (
    <article className={`ui-provider-card ui-provider-card--${provider.state}`} aria-label={`${provider.displayName}: ${state.label}`}>
      <header>
        <div>
          <span className="ui-provider-mark" aria-hidden="true">{provider.displayName.slice(0, 1)}</span>
          <span>
            <strong>{provider.displayName}</strong>
            <small>{provider.readiness.replace('-', ' ')} integration</small>
          </span>
        </div>
        <span className="ui-state-badge"><StateIcon size={12} />{state.label}</span>
      </header>

      <div className="ui-provider-metrics">
        <div>
          <span>Spend</span>
          <strong>{formatMoney(provider.spend)}</strong>
          {provider.spend && <small className={`ui-quality ui-quality--${provider.spend.quality}`}>{qualityLabel(provider.spend.quality)}</small>}
        </div>
        <div>
          <span>{provider.remaining?.label ?? 'Period'}</span>
          <strong>{provider.remaining ? formatMoney(provider.remaining) : provider.billingPeriod?.label ?? 'Unavailable'}</strong>
          {provider.remaining && <small className={`ui-quality ui-quality--${provider.remaining.quality}`}>{qualityLabel(provider.remaining.quality)}</small>}
        </div>
      </div>

      <div className="ui-provider-usage">
        <span>{provider.requests ? `${formatCount(provider.requests.value)} requests` : 'Requests unavailable'}</span>
        <span>{provider.tokens ? formatTokenPair(provider.tokens.input, provider.tokens.output) : 'Tokens unavailable'}</span>
      </div>

      {provider.credentialBreakdown && provider.credentialBreakdown.length > 0 && <div className="ui-key-breakdown" aria-label={`${provider.displayName} named key usage`}>
        {provider.credentialBreakdown.map((entry) => <div className={`ui-key-row ui-key-row--${entry.state}`} key={entry.credentialId} title={entry.statusMessage}>
          <span><strong>{entry.displayName}</strong></span>
          <b>{entry.spend ? formatMoney(entry.spend) : stateMeta[entry.state].label}</b>
          <small>{entry.remaining ? `${formatMoney(entry.remaining)} remaining · ` : ''}{entry.statusMessage}</small>
        </div>)}
      </div>}

      <footer>
        <p>{provider.statusMessage}</p>
        <small>{periodEnd ? `${provider.billingPeriod?.resetsAt ? 'Resets' : 'Ends'} ${periodEnd} · ` : ''}{freshnessLabel(provider.provenance.collectedAt)}</small>
        <small className="ui-provenance" title={provider.provenance.detail}>{provider.provenance.label}</small>
      </footer>
    </article>
  )
}

export function ProviderCards({ providers }: ProviderCardsProps) {
  return (
    <section className="ui-panel ui-provider-panel" aria-labelledby="usage-providers-title">
      <div className="ui-panel-heading">
        <div>
          <span className="ui-eyebrow">Provider status</span>
          <h2 id="usage-providers-title">Every source, honestly labeled</h2>
        </div>
        <span className="ui-panel-count">{providers.length} providers</span>
      </div>
      <div className="ui-provider-grid">
        {providers.map((provider) => <ProviderCard key={provider.provider} provider={provider} />)}
      </div>
    </section>
  )
}
