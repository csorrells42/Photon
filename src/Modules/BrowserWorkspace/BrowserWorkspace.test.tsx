import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import {
  BrowserMapsPanel,
  HERMES_HELP_BROWSER_REQUEST,
  browserRequestTabId,
  buildGoogleMapsDirectionsUrl,
  buildGoogleMapsSearchUrl,
  type BrowserTab,
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
})
