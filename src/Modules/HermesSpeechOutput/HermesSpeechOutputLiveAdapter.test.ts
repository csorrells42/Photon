import { describe, expect, it, vi } from 'vitest'
import { createDefaultHermesSpeechOutputSettings } from './contracts'
import { HermesSpeechOutputLiveAdapter, normalizeHermesSpeechOutputSnapshot } from './HermesSpeechOutputLiveAdapter'

function response(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const snapshot = {
  profileId: 'default',
  revision: 'abc123',
  settings: createDefaultHermesSpeechOutputSettings(),
}

describe('HermesSpeechOutputLiveAdapter', () => {
  it('normalizes only an exact local Kokoro snapshot', () => {
    expect(normalizeHermesSpeechOutputSnapshot(snapshot)).toEqual(snapshot)
    expect(() => normalizeHermesSpeechOutputSnapshot({
      ...snapshot,
      settings: { ...snapshot.settings, provider: 'cloud' },
    })).toThrow('Voice settings are invalid.')
  })

  it('reads the exact profile-scoped settings route', async () => {
    const fetcher = vi.fn(async () => response(snapshot))
    const adapter = new HermesSpeechOutputLiveAdapter(fetcher)

    await expect(adapter.read('work:voice')).resolves.toEqual(snapshot)
    expect(fetcher).toHaveBeenCalledWith(
      '/api/audio/local-voice/settings?profile=work%3Avoice',
      expect.objectContaining({ credentials: 'include' }),
    )
  })

  it('previews only a validated local WAV using exact voice controls', async () => {
    const playAudio = vi.fn(async () => undefined)
    const fetcher = vi.fn(async () => response({
      ok: true,
      provider: 'kokoro',
      mime_type: 'audio/wav',
      data_url: 'data:audio/wav;base64,UklGRg==',
    }))
    const adapter = new HermesSpeechOutputLiveAdapter(fetcher, playAudio)
    const settings = { ...snapshot.settings, voiceId: 'am_michael' as const, speed: 1.03 }

    await expect(adapter.preview({
      contractVersion: 1,
      profileId: 'default',
      requestId: 'preview:1',
      settings,
    }, new AbortController().signal)).resolves.toEqual({ status: 'accepted', reason: 'preview_complete' })
    expect(fetcher).toHaveBeenCalledWith('/api/audio/speak-local?profile=default', expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({
        text: 'Hello Chris. This is Photon speaking locally.',
        voice: 'am_michael',
        speed: 1.03,
      }),
    }))
    expect(playAudio).toHaveBeenCalledWith('data:audio/wav;base64,UklGRg==')
  })

  it('saves exact revision-bound settings and projects stale revisions', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(response({
        ...snapshot,
        revision: 'def456',
        settings: { ...snapshot.settings, voiceId: 'am_michael', speed: 1.03 },
      }))
      .mockResolvedValueOnce(response({ detail: 'stale_revision' }, 409))
    const adapter = new HermesSpeechOutputLiveAdapter(fetcher)
    const request = {
      contractVersion: 1 as const,
      expectedRevision: 'abc123',
      profileId: 'default',
      requestId: 'save:1',
      settings: { ...snapshot.settings, voiceId: 'am_michael' as const, speed: 1.03 },
    }

    const saved = await adapter.save(request, new AbortController().signal)
    expect(saved.result).toEqual({ status: 'accepted', reason: 'saved' })
    expect(saved.snapshot?.revision).toBe('def456')
    expect(fetcher).toHaveBeenCalledWith('/api/audio/local-voice/settings', expect.objectContaining({
      method: 'PUT',
      body: JSON.stringify({
        contract_version: 1,
        expected_revision: 'abc123',
        profile_id: 'default',
        request_id: 'save:1',
        voice_id: 'am_michael',
        speed: 1.03,
      }),
    }))

    await expect(adapter.save(request, new AbortController().signal)).resolves.toEqual({
      result: { status: 'rejected', reason: 'stale_revision' },
    })
  })
})
