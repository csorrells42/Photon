import { RefreshCw, ShieldCheck, Sparkles } from 'lucide-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ProviderUsageAdapter, UsageAdapterFailure } from './ProviderUsageAdapter'
import { isUsageAdapterSuccess } from './ProviderUsageAdapter'
import { desktopUsageAdapter } from './DesktopUsageAdapter'
import type { UsageCollectionRequest, UsageDashboardSnapshot } from './contracts'
import { DashboardState } from './DashboardState'
import { ProviderCards } from './ProviderCards'
import { ProviderCredentialVault } from './ProviderCredentialVault'
import { AccountLinkPanel } from './AccountLinkPanel'
import { UsageSummary } from './UsageSummary'
import { UsageTrends } from './UsageTrends'
import './UsageIntelligenceDashboard.css'

function createDefaultRequest(): UsageCollectionRequest {
  const end = new Date()
  const start = new Date(end.getTime() - (7 * 24 * 60 * 60 * 1000))
  return { period: { start: start.toISOString(), end: end.toISOString(), label: 'Last 7 days' } }
}

type DashboardView =
  | { kind: 'loading' }
  | { kind: 'ready'; snapshot: UsageDashboardSnapshot }
  | { kind: 'failure'; failure: UsageAdapterFailure }

export interface UsageIntelligenceDashboardProps {
  /** Inject a collected snapshot when the parent already owns the refresh cycle. */
  snapshot?: UsageDashboardSnapshot
  /** Defaults to the deterministic, clearly marked synthetic adapter. */
  adapter?: ProviderUsageAdapter
  request?: UsageCollectionRequest
  className?: string
}

/**
 * A standalone, renderer-safe dashboard. It owns no credentials and never
 * sends provider requests; its adapter is the only collection boundary.
 */
export function UsageIntelligenceDashboard({
  snapshot,
  adapter = desktopUsageAdapter,
  request,
  className = '',
}: UsageIntelligenceDashboardProps) {
  const [view, setView] = useState<DashboardView>(() => snapshot ? { kind: 'ready', snapshot } : { kind: 'loading' })
  const [refreshCount, setRefreshCount] = useState(0)
  const resolvedRequest = useMemo(() => request ?? createDefaultRequest(), [request])

  const refresh = useCallback(() => setRefreshCount((count) => count + 1), [])

  useEffect(() => {
    window.addEventListener('hermes-credentials-changed', refresh)
    window.addEventListener('hermes-usage-account-changed', refresh)
    return () => {
      window.removeEventListener('hermes-credentials-changed', refresh)
      window.removeEventListener('hermes-usage-account-changed', refresh)
    }
  }, [refresh])

  useEffect(() => {
    if (snapshot) {
      setView({ kind: 'ready', snapshot })
      return undefined
    }

    const controller = new AbortController()
    setView({ kind: 'loading' })
    void adapter.collect(resolvedRequest, controller.signal).then((result) => {
      if (controller.signal.aborted) return
      setView(isUsageAdapterSuccess(result) ? { kind: 'ready', snapshot: result.snapshot } : { kind: 'failure', failure: result })
    }).catch(() => {
      if (!controller.signal.aborted) {
        setView({
          kind: 'failure',
          failure: { kind: 'failure', code: 'unexpected', state: 'error', message: 'The adapter did not return a usage result.', retryable: true },
        })
      }
    })
    return () => controller.abort()
  }, [adapter, refreshCount, resolvedRequest, snapshot])

  const sourceBadge = view.kind !== 'ready'
    ? null
    : view.snapshot.providers.every((provider) => provider.provenance.kind === 'synthetic')
      ? 'Synthetic demo'
      : view.snapshot.providers.some((provider) => provider.provenance.kind === 'host-collector' && provider.state === 'connected')
        ? view.snapshot.providers.some((provider) => provider.provenance.kind === 'synthetic') ? 'Mixed live + demo' : 'Live sources'
        : 'Desktop collector ready'

  return (
    <section className={`usage-intelligence ${className}`.trim()} data-state={view.kind} aria-label="Usage intelligence">
      <header className="ui-dashboard-header">
        <div className="ui-dashboard-title">
          <span className="ui-dashboard-mark"><Sparkles size={17} /></span>
          <div>
            <span className="ui-eyebrow">Hermes Workbench</span>
            <h1>Usage intelligence</h1>
            <p>Provider-neutral usage and cost signals, kept behind a trusted collection boundary.</p>
          </div>
        </div>
        <div className="ui-dashboard-actions">
          {sourceBadge && <span className="ui-demo-badge"><Sparkles size={12} />{sourceBadge}</span>}
          <span className="ui-privacy-badge"><ShieldCheck size={13} />No secrets in renderer</span>
          <button type="button" onClick={refresh} disabled={view.kind === 'loading'} aria-label="Refresh usage data">
            <RefreshCw size={14} className={view.kind === 'loading' ? 'ui-spin' : undefined} />Refresh
          </button>
        </div>
      </header>

      {view.kind === 'loading' && <DashboardState state="loading" />}
      {view.kind === 'failure' && <DashboardState state="failure" failure={view.failure} onRetry={refresh} />}
      {view.kind === 'ready' && <DashboardContent snapshot={view.snapshot} />}
    </section>
  )
}

function DashboardContent({ snapshot }: { snapshot: UsageDashboardSnapshot }) {
  return (
    <div className="ui-dashboard-content">
      <UsageSummary snapshot={snapshot} />
      <AccountLinkPanel />
      <ProviderCredentialVault />
      <div className="ui-note-row" role="note">
        <span>{snapshot.spendSummary.comparisonNote}</span>
        <span>{snapshot.notices[0]}</span>
      </div>
      <UsageTrends snapshot={snapshot} />
      <ProviderCards providers={snapshot.providers} />
    </div>
  )
}
