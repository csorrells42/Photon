import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import {
  BrowserMapsPanel,
  BrowserBookmarksBar,
  HERMES_HELP_BROWSER_REQUEST,
  applyBrowserBookmarkRequest,
  browserBookmarksStorageKey,
  browserRequestTabId,
  buildGoogleMapsDirectionsUrl,
  buildGoogleMapsSearchUrl,
  mergeImportedBrowserBookmarks,
  type BrowserTab,
  type BrowserBookmark,
  type GoogleMapsTravelMode,
} from './BrowserWorkspace'

describe('BrowserWorkspace Google Maps integration', () => {
  it('encodes places with punctuation, Unicode, query delimiters, and percent signs exactly once', () => {
    expect(buildGoogleMapsSearchUrl('München & café #1? 50%')).toBe(
      'https://www.google.com/maps/search/?api=1&query=M%C3%BCnchen+%26+caf%C3%A9+%231%3F+50%25',
    )
  })

  it('builds exact directions URLs and supports destination-only directions', () => {
    expect(buildGoogleMapsDirectionsUrl('Plant A & B', 'Dock #4? 50%', 'walking')).toBe(
      'https://www.google.com/maps/dir/?api=1&origin=Plant+A+%26+B&destination=Dock+%234%3F+50%25&travelmode=walking',
    )
    expect(buildGoogleMapsDirectionsUrl('', 'Nissan Smyrna', 'driving')).toBe(
      'https://www.google.com/maps/dir/?api=1&destination=Nissan+Smyrna&travelmode=driving',
    )
  })

  it('fails closed for missing or oversized values and unsupported travel modes', () => {
    expect(buildGoogleMapsSearchUrl('')).toBeNull()
    expect(buildGoogleMapsSearchUrl('x'.repeat(513))).toBeNull()
    expect(buildGoogleMapsDirectionsUrl('Plant A', '', 'transit')).toBeNull()
    expect(buildGoogleMapsDirectionsUrl('', 'Plant B', 'flying' as GoogleMapsTravelMode)).toBeNull()
  })

  it('renders accessible typed map controls and an honest Google-supplied-results notice', () => {
    const markup = renderToStaticMarkup(<BrowserMapsPanel onNavigate={vi.fn()} onError={vi.fn()} initiallyExpanded />)
    expect(markup).toContain('aria-label="Google Maps tools"')
    expect(markup).toContain('aria-expanded="true"')
    expect(markup).toContain('aria-label="Google Maps place or address"')
    expect(markup).toContain('aria-label="Directions origin"')
    expect(markup).toContain('aria-label="Directions destination"')
    expect(markup).toContain('aria-label="Travel mode"')
    expect(markup).toContain('Search places or plan a route in this browser')
    expect(markup).toContain('Hermes does not verify routes or traffic')
  })

  it('uses one stable host-owned Help tab key instead of duplicating the tab', () => {
    expect(HERMES_HELP_BROWSER_REQUEST).toEqual({
      key: 'hermes-help',
      label: 'Hermes Help',
      url: 'https://hermes-agent.nousresearch.com/docs/',
      bookmark: true,
    })
    const tabs: BrowserTab[] = [{ id: 'help-tab', url: HERMES_HELP_BROWSER_REQUEST.url, title: 'Hermes Help', requestKey: 'hermes-help' }]
    expect(browserRequestTabId(tabs, { nonce: 2, ...HERMES_HELP_BROWSER_REQUEST })).toBe('help-tab')
    expect(browserRequestTabId(tabs, { nonce: 3, url: 'https://example.com/' })).toBeNull()
  })

  it('routes Photon bookmark requests into the same persistent bookmarks-bar store', () => {
    const current: BrowserBookmark[] = [{ id: 'existing', title: 'Existing', url: 'https://example.com/' }]
    const stored = applyBrowserBookmarkRequest(current, {
      nonce: 7,
      url: 'https://developer.mozilla.org/en-US/',
      label: 'MDN',
      bookmark: true,
    })
    expect(browserBookmarksStorageKey).toBe('phos.browser.bookmarks.v1')
    expect(stored).toHaveLength(2)
    expect(stored[0]).toMatchObject({ title: 'MDN', url: 'https://developer.mozilla.org/en-US/' })
    expect(applyBrowserBookmarkRequest(stored, { nonce: 8, url: 'https://ignored.example/' })).toBe(stored)
  })

  it('renders the integrated bookmarks bar even before the first bookmark exists', () => {
    const source = renderToStaticMarkup(<BrowserBookmarksBar bookmarks={[]} onImport={vi.fn()} onOpen={vi.fn()} onRemove={vi.fn()} />)
    expect(source).toContain('aria-label="Bookmarks bar"')
    expect(source).toContain('data-bookmark-storage-key="phos.browser.bookmarks.v1"')
    expect(source).toContain('ask Photon to bookmark it')
    expect(source).toContain('Import Chrome')
  })

  it('merges Chrome bookmarks after existing entries without overwriting or duplicating them', () => {
    const existing = [{ id: 'existing', title: 'My title', url: 'https://example.com/' }]
    const merged = mergeImportedBrowserBookmarks(existing, [
      { title: 'Chrome title must not win', url: 'https://example.com', folder: 'Bookmarks bar' },
      { title: 'Weather', url: 'https://weather.gov/', folder: 'Other bookmarks / Reference' },
      { title: 'Duplicate weather', url: 'https://weather.gov', folder: 'Mobile bookmarks' },
    ])
    expect(merged.bookmarks).toHaveLength(2)
    expect(merged.bookmarks[0]).toEqual(existing[0])
    expect(merged.bookmarks[1]).toMatchObject({ title: 'Weather', url: 'https://weather.gov/', folder: 'Other bookmarks / Reference' })
    expect(merged.importedCount).toBe(1)
    expect(merged.duplicateCount).toBe(2)
    const repeated = mergeImportedBrowserBookmarks(merged.bookmarks, [
      { title: 'Weather again', url: 'https://weather.gov', folder: 'Bookmarks bar' },
    ])
    expect(repeated.bookmarks).toEqual(merged.bookmarks)
    expect(repeated.importedCount).toBe(0)
    expect(repeated.duplicateCount).toBe(1)
  })

  it('preserves existing bookmarks first when the bounded bar reaches capacity', () => {
    const existing = Array.from({ length: 256 }, (_, index) => ({ id: `existing-${index}`, title: `Existing ${index}`, url: `https://example.com/${index}` }))
    const merged = mergeImportedBrowserBookmarks(existing, [
      { title: 'New Chrome item', url: 'https://weather.gov/', folder: 'Bookmarks bar' },
    ])
    expect(merged.bookmarks).toEqual(existing)
    expect(merged.importedCount).toBe(0)
    expect(merged.capacitySkippedCount).toBe(1)
  })
})
