import { describe, expect, it } from 'vitest'
import { normalizeHermesDesktopUiAction, normalizeHermesPreviewUrl, normalizeHermesWorkspacePreviewPath, shouldAcceptHermesDesktopUiAction } from './HermesDesktopUiAdapter'

describe('HermesDesktopUiAdapter', () => {
  it('accepts ordinary and map URLs while removing fragments', () => {
    expect(normalizeHermesPreviewUrl('https://www.google.com/maps/dir/?api=1&destination=Drug+Store#route'))
      .toBe('https://www.google.com/maps/dir/?api=1&destination=Drug+Store')
    expect(normalizeHermesDesktopUiAction({ type: 'preview.open', session_id: 's', payload: { url: 'https://maps.apple.com/?q=hardware+store', label: 'Directions' } }))
      .toEqual({ kind: 'open-preview', url: 'https://maps.apple.com/?q=hardware+store', label: 'Directions' })
  })

  it('fails closed for non-web, credentialed, and secret-bearing URLs', () => {
    expect(normalizeHermesPreviewUrl('file:///C:/Users/name/secret.txt')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://user:pass@example.com/')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?access_token=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?apiKey=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?jwt=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?ticket=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?sig=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?client_secret=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?refresh_token=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?id_token=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/callback?private_key=secret')).toBeNull()
    expect(normalizeHermesPreviewUrl('https://example.com/?monkey=business')).toBe('https://example.com/?monkey=business')
    expect(normalizeHermesPreviewUrl('javascript:alert(1)')).toBeNull()
  })

  it('accepts only safe workspace-owned file previews', () => {
    expect(normalizeHermesWorkspacePreviewPath('/workspace/src/Program.cs')).toBe('src/Program.cs')
    expect(normalizeHermesWorkspacePreviewPath('file:///workspace/docs/readme.md')).toBe('docs/readme.md')
    expect(normalizeHermesDesktopUiAction({ type: 'preview.open', payload: { url: '/workspace/docs/readme.md', label: 'Readme' } }))
      .toEqual({ kind: 'open-workspace-file', path: 'docs/readme.md', label: 'Readme' })
    expect(normalizeHermesWorkspacePreviewPath('/etc/passwd')).toBeNull()
    expect(normalizeHermesWorkspacePreviewPath('/workspace/../data/.env')).toBeNull()
    expect(normalizeHermesWorkspacePreviewPath('/workspace/credentials.json')).toBeNull()
  })

  it('requires a nonempty exact active session for focus-changing actions', () => {
    expect(shouldAcceptHermesDesktopUiAction(false, 'session-a', 'session-a')).toBe(true)
    expect(shouldAcceptHermesDesktopUiAction(false, 'session-a')).toBe(false)
    expect(shouldAcceptHermesDesktopUiAction(false, 'session-a', 'session-b')).toBe(false)
    expect(shouldAcceptHermesDesktopUiAction(true, 'session-a', 'session-a')).toBe(false)
  })

  it('accepts only the fixed pane vocabulary', () => {
    expect(normalizeHermesDesktopUiAction({ type: 'pane.reveal', payload: { pane: 'terminal' } }))
      .toEqual({ kind: 'reveal-pane', pane: 'terminal' })
    expect(normalizeHermesDesktopUiAction({ type: 'pane.reveal', payload: { pane: 'credentials' } })).toBeNull()
    expect(normalizeHermesDesktopUiAction({ type: 'other', payload: { url: 'https://example.com' } })).toBeNull()
  })
})
