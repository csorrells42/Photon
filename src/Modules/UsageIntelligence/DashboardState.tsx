import { AlertTriangle, CircleSlash2, LoaderCircle, Settings2 } from 'lucide-react'
import type { UsageAdapterFailure } from './ProviderUsageAdapter'

interface DashboardStateProps {
  state: 'loading' | 'failure'
  failure?: UsageAdapterFailure
  onRetry?: () => void
}

export function DashboardState({ state, failure, onRetry }: DashboardStateProps) {
  if (state === 'loading') {
    return (
      <section className="ui-empty-state ui-empty-state--loading" aria-live="polite">
        <LoaderCircle className="ui-spin" size={22} />
        <div><strong>Collecting usage summary</strong><p>Waiting for the configured usage adapter. Provider calls belong in the trusted host or backend.</p></div>
      </section>
    )
  }

  const isNotConfigured = failure?.state === 'not-configured'
  const isUnavailable = failure?.state === 'unavailable'
  const Icon = isNotConfigured ? Settings2 : isUnavailable ? CircleSlash2 : AlertTriangle
  const title = isNotConfigured ? 'No usage source is configured' : isUnavailable ? 'Usage source unavailable' : 'Usage collection needs attention'

  return (
    <section className="ui-empty-state ui-empty-state--failure" aria-live="polite">
      <Icon size={22} />
      <div>
        <strong>{title}</strong>
        <p>{failure?.message ?? 'No adapter result is available.'}</p>
        <small>Credentials are resolved only by a future trusted host integration; this module does not store or display them.</small>
      </div>
      {failure?.retryable && onRetry && <button type="button" onClick={onRetry}>Try again</button>}
    </section>
  )
}
