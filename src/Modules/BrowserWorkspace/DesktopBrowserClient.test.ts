import { describe, expect, it } from 'vitest'
import { normalizeBrowserAddress } from './DesktopBrowserClient'

describe('browser address normalization', () => {
  it('accepts HTTP addresses and upgrades hostnames to HTTPS', () => {
    expect(normalizeBrowserAddress('https://weather.gov/')).toBe('https://weather.gov/')
    expect(normalizeBrowserAddress('weather.gov')).toBe('https://weather.gov/')
  })
  it('turns ordinary text into a search without permitting script URLs', () => {
    expect(normalizeBrowserAddress('Andalusia Alabama weather')).toContain('google.com/search?q=Andalusia%20Alabama%20weather')
    expect(normalizeBrowserAddress('javascript:alert(1)')).toContain('google.com/search')
  })
  it('uses an inert blank page for an empty address', () => expect(normalizeBrowserAddress('  ')).toBe('about:blank'))
})
