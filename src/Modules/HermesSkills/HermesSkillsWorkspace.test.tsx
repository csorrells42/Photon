import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesSkillsWorkspace } from './HermesSkillsWorkspace'

describe('HermesSkillsWorkspace', () => {
  it('renders the explicit upstream trust and scan-before-install boundary', () => {
    const html = renderToStaticMarkup(<HermesSkillsWorkspace />)
    expect(html).toContain('Capabilities with a visible trust trail')
    expect(html).toContain('They do not claim independent Chris or Codex review')
    expect(html).toContain('Workbench reviewed · none yet')
    expect(html).toContain('matching scan required before install')
  })
})
