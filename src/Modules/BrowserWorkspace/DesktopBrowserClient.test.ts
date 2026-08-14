import { describe, expect, it } from 'vitest'
import { normalizeBrowserAddress, normalizeBrowserBookmarksSnapshot, tryNormalizeBrowserAddress } from './DesktopBrowserClient'

describe('browser address normalization', () => {
  it('accepts HTTP addresses and upgrades hostnames to HTTPS', () => {
    expect(normalizeBrowserAddress('https://weather.gov/')).toBe('https://weather.gov/')
    expect(normalizeBrowserAddress('weather.gov')).toBe('https://weather.gov/')
  })
  it('turns ordinary text into a search without permitting script URLs', () => {
    expect(normalizeBrowserAddress('Andalusia Alabama weather')).toContain('google.com/search?q=Andalusia%20Alabama%20weather')
    expect(tryNormalizeBrowserAddress('javascript:alert(1)')).toBeNull()
  })
  it('rejects embedded credentials, controls, and oversized addresses', () => {
    expect(tryNormalizeBrowserAddress('https://user:password@example.com/')).toBeNull()
    expect(tryNormalizeBrowserAddress('https://example.com/\u0000hidden')).toBeNull()
    expect(tryNormalizeBrowserAddress(`https://example.com/${'x'.repeat(4_096)}`)).toBeNull()
  })
  it('uses an inert blank page for an empty address', () => expect(normalizeBrowserAddress('  ')).toBe('about:blank'))

  it('accepts a bounded pathless Chrome bookmark snapshot', () => {
    expect(normalizeBrowserBookmarksSnapshot({
      type: 'browser.bookmarks.snapshot', version: 1, requestId: 'import-1', status: 'available', source: 'google-chrome',
      sourceRevision: 'a'.repeat(64), discoveredCount: 2, rejectedCount: 0, truncated: false, reason: '', message: 'Ready',
      bookmarks: [
        { title: 'Weather', url: 'https://weather.gov/', folder: 'Bookmarks bar' },
        { title: 'Hermes', url: 'https://example.com/hermes', folder: 'Other bookmarks / Work' },
      ],
    })?.bookmarks).toHaveLength(2)
  })

  it('rejects duplicate, credential-bearing, or path-leaking Chrome payloads', () => {
    const base = {
      type: 'browser.bookmarks.snapshot', version: 1, requestId: 'import-1', status: 'available', source: 'google-chrome',
      sourceRevision: 'b'.repeat(64), discoveredCount: 2, rejectedCount: 0, truncated: false, reason: '', message: 'Ready',
    }
    expect(normalizeBrowserBookmarksSnapshot({ ...base, bookmarks: [
      { title: 'One', url: 'https://example.com/', folder: 'Bookmarks bar' },
      { title: 'Two', url: 'https://example.com', folder: 'Other bookmarks' },
    ] })).toBeNull()
    expect(normalizeBrowserBookmarksSnapshot({ ...base, bookmarks: [
      { title: 'Secret', url: 'https://user:password@example.com/', folder: 'Bookmarks bar' },
    ] })).toBeNull()
    expect(normalizeBrowserBookmarksSnapshot({ ...base, profilePath: 'C:\\Users\\person\\Chrome', bookmarks: [] })).not.toHaveProperty('profilePath')
  })
})
