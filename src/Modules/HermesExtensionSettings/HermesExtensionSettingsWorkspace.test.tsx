import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { createDeterministicExtensionSettingsSnapshot } from './FakeHermesExtensionSettingsController'
import { HermesExtensionSettingsWorkspace } from './HermesExtensionSettingsWorkspace'
import { ReviewDialog } from './ReviewDialog'

describe('HermesExtensionSettingsWorkspace', () => {
  it('renders provenance, secret-boundary, partial-state, and keyboard tab semantics', () => {
    const snapshot = createDeterministicExtensionSettingsSnapshot()
    const markup = renderToStaticMarkup(<HermesExtensionSettingsWorkspace initialSnapshot={snapshot} />)

    expect(markup).toContain('HERMES EXTENSION SETTINGS')
    expect(markup).toContain('Partial data')
    expect(markup).toContain('Stored secret values never enter props, results, logs, copy actions, persistence, or rendering.')
    expect(markup).toContain('nous-approved')
    expect(markup).toContain('workbench-reviewed')
    expect(markup).toContain('external-unreviewed')
    expect(markup).toContain('user-created')
    expect(markup).toContain('never implies approval by Chris or Codex')
    expect(markup).toContain('role="tablist"')
    expect(markup).toContain('role="tab"')
    expect(markup).toContain('aria-selected="true"')
    expect(markup).toContain('tabindex="-1"')
  })

  it('renders untrusted skill material as escaped text rather than markup', () => {
    const snapshot = createDeterministicExtensionSettingsSnapshot()
    snapshot.skills[0] = {
      ...snapshot.skills[0],
      content: '<script>window.compromised = true</script><img src=x onerror=alert(1)>',
    }

    const markup = renderToStaticMarkup(<HermesExtensionSettingsWorkspace initialSnapshot={snapshot} />)

    expect(markup).toContain('&lt;script&gt;window.compromised = true&lt;/script&gt;')
    expect(markup).toContain('&lt;img src=x onerror=alert(1)&gt;')
    expect(markup).not.toContain('<script>window.compromised')
    expect(markup).not.toContain('<img src=x')
  })

  it('presents only source-confirmed assignment controls in a compact, explained live mode', () => {
    const snapshot = createDeterministicExtensionSettingsSnapshot()
    const markup = renderToStaticMarkup(<HermesExtensionSettingsWorkspace initialSnapshot={snapshot} mode="live-model-assignments" />)

    expect(markup).toContain('Model assignments')
    expect(markup).toContain('WHAT YOU ARE LOOKING AT')
    expect(markup).toContain('A deliberately small live control')
    expect(markup).toContain('Default model')
    expect(markup).toContain('Intentionally absent')
    expect(markup).toContain('Current and next model assignments')
    expect(markup).toContain('Review assignment change')
    expect(markup).toContain('compression auxiliary model')
    expect(markup).toContain('Reported but not editable in this lab')
    expect(markup).toContain('summarization. Hermes reported these tasks, but this page has no verified write route for them.')
    expect(markup).not.toContain('Skill Studio')
    expect(markup).not.toContain('Mixture of Agents')
    expect(markup).not.toContain('Provider settings and validation intents')
  })

  it('renders explicit commit and expensive-model confirmations in the review dialog', () => {
    const markup = renderToStaticMarkup(
      <ReviewDialog
        review={{
          reviewId: 'review-0001',
          kind: 'models',
          title: 'Update advanced model settings',
          before: ['Default model: sol-high'],
          after: ['Default model: aggregate-pro'],
          warnings: ['One selected model is expensive.'],
          requiresExpensiveModelConfirmation: true,
        }}
        busy={false}
        confirmed={false}
        expensiveConfirmed={false}
        onConfirmedChange={() => undefined}
        onExpensiveConfirmedChange={() => undefined}
        onCancel={() => undefined}
        onCommit={async () => undefined}
      />,
    )

    expect(markup).toContain('REVIEW BEFORE COMMIT')
    expect(markup).toContain('I reviewed the before/after values')
    expect(markup).toContain('I explicitly approve the selected expensive model configuration.')
    expect(markup).toContain('aria-modal="true"')
    expect(markup).toContain('disabled=""')
  })
})
