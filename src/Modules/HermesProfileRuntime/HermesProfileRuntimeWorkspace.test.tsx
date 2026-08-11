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

    expect(markup).toContain('Live read-only beta')
    expect(markup).toContain('INSPECTED PROFILE')
    expect(markup).toContain('Inspect profile facts')
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
})
