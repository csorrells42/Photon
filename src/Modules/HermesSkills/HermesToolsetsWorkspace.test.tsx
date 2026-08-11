import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesToolsetsWorkspace } from './HermesToolsetsWorkspace'

describe('HermesToolsetsWorkspace', () => {
  it('renders the secret-isolation and advertised-contract boundary', () => {
    const html = renderToStaticMarkup(<HermesToolsetsWorkspace />)
    expect(html).toContain('Provider-neutral capability setup')
    expect(html).toContain('Secrets never enter React')
    expect(html).toContain('native Connections broker')
    expect(html).toContain('provider writes are constrained to advertised contracts')
  })
})
