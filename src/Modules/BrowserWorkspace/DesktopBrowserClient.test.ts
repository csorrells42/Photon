import { describe, expect, it } from 'vitest'
import { normalizeBrowserAddress, tryNormalizeBrowserAddress } from './DesktopBrowserClient'

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
})
