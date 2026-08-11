import { describe, expect, it } from 'vitest'
import { credentialDisplayName, credentialIdFromLabel, normalizeCredentialMetadata } from './DesktopCredentialBridge'

describe('DesktopCredentialBridge', () => {
  it('keeps only safe masked credential metadata', () => {
    expect(normalizeCredentialMetadata({
      provider: 'openrouter',
      credentialId: 'primary',
      configured: true,
      updatedAt: '2026-08-09T05:30:00.000Z',
      secret: 'must-not-survive',
      token: 'must-not-survive',
    })).toEqual({
      provider: 'openrouter',
      credentialId: 'primary',
      configured: true,
      updatedAt: '2026-08-09T05:30:00.000Z',
    })
  })

  it('rejects unsafe native credential identifiers', () => {
    expect(normalizeCredentialMetadata({ provider: '../../escape', credentialId: 'primary', configured: true })).toBeNull()
    expect(normalizeCredentialMetadata({ provider: 'openai-api', credentialId: 'secret profile', configured: true })).toBeNull()
  })

  it('turns human key labels into stable safe vault identifiers', () => {
    expect(credentialIdFromLabel('  Ali Search Key  ')).toBe('ali-search-key')
    expect(credentialIdFromLabel('Scarlett/API')).toBe('scarlett-api')
    expect(credentialIdFromLabel('...')).toBe('')
    expect(credentialDisplayName('deepseek-bob')).toBe('Deepseek Bob')
  })
})
