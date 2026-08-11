import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesMcpWorkspace } from './HermesMcpWorkspace'

describe('Hermes MCP settings provenance surface', () => {
  it('distinguishes upstream Nous approval from independent Workbench review', () => {
    const markup = renderToStaticMarkup(<HermesMcpWorkspace />)

    expect(markup).toContain('Nous-approved')
    expect(markup).toContain('It does not mean Chris or Codex independently reviewed')
    expect(markup).toContain('Workbench reviewed · none yet')
    expect(markup).toContain('External · not reviewed')
    expect(markup).toContain('environment values are never returned to React')
  })
})
