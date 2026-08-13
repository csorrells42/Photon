import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { HermesMcpServer } from './HermesMcpAdapter'
import { HermesMcpWorkspace, serverDescription } from './HermesMcpWorkspace'

describe('Hermes MCP settings provenance surface', () => {
  it('distinguishes upstream Nous approval from independent Workbench review', () => {
    const markup = renderToStaticMarkup(<HermesMcpWorkspace />)

    expect(markup).toContain('Nous-approved')
    expect(markup).toContain('It does not mean Chris or Codex independently reviewed')
    expect(markup).toContain('Workbench reviewed · none yet')
    expect(markup).toContain('External · not reviewed')
    expect(markup).toContain('environment values are never returned to React')
  })

  it('states that redacted HTTP endpoint details are protected rather than unavailable', () => {
    const server: HermesMcpServer = {
      name: 'photon_docker_gateway',
      transport: 'http',
      url: null,
      command: null,
      args: [],
      environmentVariableNames: [],
      auth: 'header',
      enabled: true,
      tools: null,
      revision: 'receipt',
      editable: false,
      editorBlockReason: 'Protected headers are managed by the launcher.',
    }

    expect(serverDescription(server)).toBe('HTTP endpoint details protected')
  })
})
