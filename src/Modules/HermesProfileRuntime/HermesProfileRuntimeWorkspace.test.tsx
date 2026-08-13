import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { DeterministicHermesProfileRuntimeAdapter } from './DeterministicHermesProfileRuntimeAdapter'
import { HermesProfileRuntimeWorkspace } from './HermesProfileRuntimeWorkspace'
import { createRequestContext } from './runtimeSafety'

describe('HermesProfileRuntimeWorkspace labels and safe rendering', () => {
  it('keeps Workbench ConPTY distinct from the Hermes agent terminal backend', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const snapshot = (await adapter.load(createRequestContext('profile-main', 'render:1'), new AbortController().signal)).value
    const markup = renderToStaticMarkup(<HermesProfileRuntimeWorkspace adapter={adapter} initialSnapshot={snapshot} />)
    expect(markup).toContain('Native Workbench terminal:')
    expect(markup).toContain('ConPTY-backed developer terminal, outside this module.')
    expect(markup).toContain('Hermes agent terminal backend:')
    expect(markup).toContain('Not Workbench ConPTY')
    expect(markup).toContain('Preview permission request')
    expect(markup).not.toContain('Grant permission now')
  })

  it('renders unsafe document text escaped and exposes honest delegated labels', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const signal = new AbortController().signal
    const first = await adapter.load(createRequestContext('profile-main', 'render:load'), signal)
    const snapshot = (await adapter.saveDocument({ ...createRequestContext('profile-main', 'render:save', first.revision), document: 'persona', text: '<script>alert(1)</script>' }, signal)).value
    const markup = renderToStaticMarkup(<HermesProfileRuntimeWorkspace adapter={adapter} initialSnapshot={snapshot} />)
    expect(markup).toContain('&lt;script&gt;alert(1)&lt;/script&gt;')
    expect(markup).not.toContain('<script>alert(1)</script>')
    expect(markup).toContain('delegated')
    expect(markup).toContain('renderer receives no credential values')
  })

  it('keeps live read-only facts visible while removing every unsupported action', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const snapshot = (await adapter.load(createRequestContext('profile-main', 'render:readonly'), new AbortController().signal)).value
    const markup = renderToStaticMarkup(
      <HermesProfileRuntimeWorkspace
        adapter={adapter}
        initialSnapshot={snapshot}
        interactionMode="read-only"
      />,
    )

    expect(markup).toContain('Verified live profile facts. Change, grant, import, and export controls are intentionally unavailable.')
    expect(markup).not.toContain('Live read-only beta')
    expect(markup).toContain('INSPECTED PROFILE')
    expect(markup).toContain('Inspect profile facts')
    expect(markup).toContain('aria-labelledby="hpr-workspace-title"')
    expect(markup).toContain('aria-describedby="hpr-workspace-summary"')
    expect(markup).toContain('aria-label="Terminal boundary"')
    expect(markup).toContain('Workbench and Hermes agent execution remain separate.')
    expect(markup).toContain('aria-labelledby="hpr-documents-heading"')
    expect(markup).toContain('aria-describedby="hpr-read-only-profile-help"')
    expect(markup).not.toContain('disabled="" aria-describedby="hpr-read-only-profile-help"')
    expect(markup).toContain('It does not change the active Hermes profile.')
    expect(markup).toContain('readOnly=""')
    expect(markup).toContain('You are the Main profile.')
    expect(markup).toContain('hermes-default')
    for (const unsupported of [
      '>Preview<',
      'Preview delete',
      'Save persona',
      'Save soul',
      'Save context',
      'Save profile intents',
      'Select Hermes backend',
      'Preview permission request',
      'Save non-secret configuration',
      'Validate and preview import',
      'Prepare secret-free export',
      'Confirm typed intent',
    ]) expect(markup).not.toContain(unsupported)
  })

  it('renders only genuinely mounted capabilities in safe-live mode', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const snapshot = (await adapter.load(createRequestContext('profile-main', 'render:safe-live'), new AbortController().signal)).value
    const markup = renderToStaticMarkup(
      <HermesProfileRuntimeWorkspace
        adapter={adapter}
        initialSnapshot={snapshot}
        interactionMode="read-write"
        surfaceMode="safe-live"
      />,
    )

    expect(markup).toContain('SOUL identity document')
    expect(markup).toContain('Default model for future sessions')
    expect(markup).toContain('Save model assignment')
    expect(markup).toContain('Backend selection and status')
    expect(markup).toContain('Preview delete')
    for (const unsupported of [
      'Persona, soul, and context',
      'Save persona',
      'Save context',
      'Project path intent',
      'Worktree path intent',
      'Intent note',
      'Status and permission intents',
      'OAuth, credential pool, and endpoint status',
      'Non-secret configuration',
      'Import and export',
    ]) expect(markup).not.toContain(unsupported)
  })
})
