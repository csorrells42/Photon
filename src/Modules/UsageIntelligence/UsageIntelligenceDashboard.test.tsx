import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { createSyntheticUsageSnapshot } from './MockUsageAdapter'
import { DashboardState } from './DashboardState'
import { UsageIntelligenceDashboard } from './UsageIntelligenceDashboard'

describe('UsageIntelligenceDashboard', () => {
  it('renders synthetic connected data without credential-like text', () => {
    const markup = renderToStaticMarkup(<UsageIntelligenceDashboard snapshot={createSyntheticUsageSnapshot()} />)

    expect(markup).toContain('Usage intelligence')
    expect(markup).toContain('Synthetic demo')
    expect(markup).toContain('No secrets in renderer')
    expect(markup).toContain('OpenAI API')
    expect(markup).not.toMatch(/sk-[a-z0-9]/i)
    expect(markup).not.toMatch(/authorization:\s*bearer/i)
    expect(markup).not.toMatch(/api[_-]?key\s*[=:]/i)
  })

  it('renders a retryable collection failure without exposing credential material', () => {
    const markup = renderToStaticMarkup(
      <DashboardState
        state="failure"
        failure={{ kind: 'failure', code: 'permission-denied', state: 'error', message: 'Host credential reference was denied.', retryable: true }}
        onRetry={() => undefined}
      />,
    )

    expect(markup).toContain('Usage collection needs attention')
    expect(markup).toContain('Try again')
    expect(markup).not.toMatch(/sk-[a-z0-9]/i)
  })

  it('renders the unavailable and not-configured recovery states', () => {
    const unavailable = renderToStaticMarkup(<DashboardState state="failure" failure={{ kind: 'failure', code: 'unsupported', state: 'unavailable', message: 'No supported integration.', retryable: false }} />)
    const notConfigured = renderToStaticMarkup(<DashboardState state="failure" failure={{ kind: 'failure', code: 'not-configured', state: 'not-configured', message: 'Host setup is required.', retryable: false }} />)

    expect(unavailable).toContain('Usage source unavailable')
    expect(notConfigured).toContain('No usage source is configured')
  })
})
