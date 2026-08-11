import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  assertHermesSpeechOutputSettings,
  createDefaultHermesSpeechOutputSettings,
  createHermesSpeechOutputPreviewRequest,
  createHermesSpeechOutputSaveRequest,
  hermesSpeechOutputReasonText,
} from './contracts'
import { HermesSpeechOutputSettingsSection } from './HermesSpeechOutputSettingsSection'

describe('HermesSpeechOutput settings contract', () => {
  it('defaults to the tuned local Heart voice', () => {
    expect(createDefaultHermesSpeechOutputSettings()).toEqual({
      contractVersion: 1,
      localOnly: true,
      provider: 'kokoro',
      speed: 0.98,
      voiceId: 'af_heart',
    })
  })

  it('accepts the male Michael voice and exact per-profile persistence binding', () => {
    const settings = assertHermesSpeechOutputSettings({
      contractVersion: 1,
      localOnly: true,
      provider: 'kokoro',
      speed: 1.02,
      voiceId: 'am_michael',
    })
    expect(createHermesSpeechOutputPreviewRequest('profile.alpha', 'preview:1', settings)).toMatchObject({
      profileId: 'profile.alpha',
      requestId: 'preview:1',
      settings,
    })
    expect(createHermesSpeechOutputSaveRequest('profile.alpha', 'revision:7', 'save:1', settings)).toMatchObject({
      expectedRevision: 'revision:7',
      profileId: 'profile.alpha',
      requestId: 'save:1',
      settings,
    })
  })

  it('fails closed for cloud, foreign, unbounded, or stale-shaped settings', () => {
    expect(() => assertHermesSpeechOutputSettings({
      contractVersion: 1,
      localOnly: true,
      provider: 'openai',
      speed: 1,
      voiceId: 'am_michael',
    })).toThrow('Voice settings are invalid.')
    expect(() => assertHermesSpeechOutputSettings({
      contractVersion: 1,
      localOnly: true,
      provider: 'kokoro',
      speed: 8,
      voiceId: 'unknown',
    })).toThrow()
    expect(() => createHermesSpeechOutputSaveRequest('bad profile', 'revision:1', 'save:1', createDefaultHermesSpeechOutputSettings())).toThrow('Profile ID is invalid.')
  })

  it('maps only typed safe reasons to user-facing status', () => {
    expect(hermesSpeechOutputReasonText('saved')).toBe('Speaking voice saved for this profile.')
    expect(hermesSpeechOutputReasonText('local_voice_unavailable')).toBe('The on-device voice engine is unavailable.')
  })
})

describe('HermesSpeechOutputSettingsSection', () => {
  it('renders adjacent local voice configuration with female and male choices', () => {
    const html = renderToStaticMarkup(
      <HermesSpeechOutputSettingsSection
        profileId="profile.alpha"
        revision="revision:7"
        settings={createDefaultHermesSpeechOutputSettings()}
        onSettingsChange={() => undefined}
        onPreview={async () => ({ reason: 'preview_complete', status: 'accepted' })}
        onSave={async () => ({ reason: 'saved', status: 'accepted' })}
      />,
    )
    expect(html).toContain('VOICE / SPEAK')
    expect(html).toContain('Local-only speech')
    expect(html).toContain('af_heart')
    expect(html).toContain('am_michael')
    expect(html).toContain('Natural American male voice')
    expect(html).toContain('Save for this profile')
  })

  it('honestly disables preview when no preview authority is mounted', () => {
    const html = renderToStaticMarkup(
      <HermesSpeechOutputSettingsSection
        profileId="profile.alpha"
        revision="revision:7"
        settings={createDefaultHermesSpeechOutputSettings()}
        onSettingsChange={() => undefined}
        onSave={async () => ({ reason: 'saved', status: 'accepted' })}
      />,
    )
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>.*Preview voice/s)
  })
})
