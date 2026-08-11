import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { createDeterministicFakeSessionAdminAdapter } from './FakeHermesSessionAdminAdapter'
import { AccessibleDialog, HermesSessionAdminWorkspace, ImportedTextPreview } from './HermesSessionAdminWorkspace'
import { liveHermesSessionAdminAdapter } from './LiveHermesSessionAdminAdapter'

describe('HermesSessionAdminWorkspace', () => {
  it('exports an isolated profile-scoped administration surface without search or pin duplication', () => {
    const html = renderToStaticMarkup(<HermesSessionAdminWorkspace profileId="default" adapter={createDeterministicFakeSessionAdminAdapter()} />)
    expect(html).toContain('Session administration')
    expect(html).toContain('Every request and result is checked against this identity')
    expect(html).toContain('Search and pinning remain in Hermes Sessions')
    expect(html).toContain('Preview delete')
    expect(html).toContain('Preview prune')
    expect(html).toContain('Validate import')
    expect(html).toContain('Request export')
    expect(html).toContain('Per-session model lock')
    expect(html).not.toContain('Full-text search')
    expect(html).not.toContain('Pin session')
  })

  it('renders unsafe imported content as escaped text and never as HTML', () => {
    const unsafe = '<img src=x onerror=alert(1)><script>bad()</script>'
    const html = renderToStaticMarkup(<ImportedTextPreview sessions={[{
      requestedSessionId: 'unsafe', title: '<b>Unsafe title</b>',
      messages: [{ role: 'user', text: unsafe }],
    }]} />)
    expect(html).toContain('&lt;b&gt;Unsafe title&lt;/b&gt;')
    expect(html).toContain('&lt;img src=x onerror=alert(1)&gt;')
    expect(html).toContain('&lt;script&gt;bad()&lt;/script&gt;')
    expect(html).not.toContain('<script>')
    expect(html).not.toContain('<img src=x')
  })

  it('marks confirmation dialogs modal, keyboard focusable, and explicitly closeable', () => {
    const html = renderToStaticMarkup(<AccessibleDialog title="Confirm destructive operation" onClose={() => undefined}>
      <label>Type DELETE<input /></label><button type="button">Confirm</button>
    </AccessibleDialog>)
    expect(html).toContain('role="dialog"')
    expect(html).toContain('aria-modal="true"')
    expect(html).toContain('tabindex="-1"')
    expect(html).toContain('aria-label="Close Confirm destructive operation"')
  })

  it('labels the live bounded adapter and disables every unverified mutation', () => {
    const html = renderToStaticMarkup(<HermesSessionAdminWorkspace profileId="default" adapter={liveHermesSessionAdminAdapter} />)

    expect(html).toContain('LIVE BOUNDED ADAPTER')
    expect(html).toContain('Verified live operations')
    expect(html).toContain('list, descendants, export, statistics')
    expect(html).toContain('Controls without a verified upstream route remain visible but disabled.')
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Preview delete<\/button>/)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Create fork<\/button>/)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Preview prune<\/button>/)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Validate import<\/button>/)
  })
})
