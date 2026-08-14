import {
  HERMES_SPEECH_OUTPUT_VOICES,
  assertHermesSpeechOutputSettings,
  type HermesSpeechOutputActionResult,
  type HermesSpeechOutputPreviewRequest,
  type HermesSpeechOutputSaveRequest,
  type HermesSpeechOutputSettings,
  type HermesSpeechOutputVoice,
} from './contracts'
import { applyHermesAudioOutput } from '../HermesSpeechVoice/HermesAudioDevicePreferences'
import { HermesWindowsAudioHost } from '../HermesSpeechVoice/HermesWindowsAudioHost'

export type HermesSpeechOutputSnapshot = Readonly<{
  profileId: string
  revision: string
  settings: HermesSpeechOutputSettings
  voices: readonly HermesSpeechOutputVoice[]
}>

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

function object(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function opaque(value: unknown, label: string): string {
  const text = typeof value === 'string' ? value.trim() : ''
  if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(text)) throw new Error(`${label} is invalid.`)
  return text
}

const localAudioDataUrlPattern = /^data:audio\/(?:wav|wave|x-wav|mpeg|mp3|ogg|opus|flac);base64,[A-Za-z0-9+/]+={0,2}$/

function localAudioDataUrl(value: unknown): string {
  const source = typeof value === 'string' ? value.trim() : ''
  return source.length <= 16_000_000 && localAudioDataUrlPattern.test(source) ? source : ''
}

const languageNames: Record<string, string> = {
  a: 'American English', b: 'British English', e: 'Spanish', f: 'French', h: 'Hindi',
  i: 'Italian', j: 'Japanese', p: 'Portuguese', z: 'Mandarin Chinese',
}

function voiceLabel(id: string): HermesSpeechOutputVoice {
  const name = id.slice(3).split('_').map((part) => part ? part[0].toUpperCase() + part.slice(1) : '').join(' ')
  const language = languageNames[id[0]] ?? 'Local'
  const gender = id[1] === 'f' ? 'female' : id[1] === 'm' ? 'male' : ''
  return { id, label: name || id, description: `${language}${gender ? ` ${gender}` : ''} voice` }
}

function normalizeVoices(value: unknown): readonly HermesSpeechOutputVoice[] {
  if (!Array.isArray(value)) return HERMES_SPEECH_OUTPUT_VOICES
  const ids = [...new Set(value.filter((item): item is string => typeof item === 'string' && /^[a-z]{2}_[a-z0-9_]{1,63}$/.test(item)))]
  return ids.length ? ids.map(voiceLabel) : HERMES_SPEECH_OUTPUT_VOICES
}

export function normalizeHermesSpeechOutputSnapshot(value: unknown): HermesSpeechOutputSnapshot {
  const raw = object(value)
  return {
    profileId: opaque(raw.profileId, 'Profile ID'),
    revision: opaque(raw.revision, 'Settings revision'),
    settings: assertHermesSpeechOutputSettings(raw.settings),
    voices: normalizeVoices(raw.voices),
  }
}

export class HermesSpeechOutputLiveAdapter {
  private readonly nativeAudio = new HermesWindowsAudioHost()
  constructor(
    private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_),
    private readonly playAudio?: (source: string) => Promise<void>,
  ) {}

  private async play(source: string, signal: AbortSignal): Promise<void> {
    if (this.playAudio) { await this.playAudio(source); return }
    if (this.nativeAudio.available) { await this.nativeAudio.playOutput(source, signal); return }
    const audio = new Audio(source)
    await applyHermesAudioOutput(audio)
    await audio.play()
  }

  private async response(response: Response): Promise<unknown> {
    if (!response.ok) {
      let reason = 'operation_failed'
      try { reason = String(object(await response.clone().json()).detail || reason) } catch { /* bounded reason */ }
      throw new Error(reason)
    }
    return response.json()
  }

  async read(profileId = 'default', signal?: AbortSignal): Promise<HermesSpeechOutputSnapshot> {
    const profile = opaque(profileId, 'Profile ID')
    const response = await this.fetcher(`/api/audio/local-voice/settings?profile=${encodeURIComponent(profile)}`, {
      credentials: 'include', signal,
    })
    return normalizeHermesSpeechOutputSnapshot(await this.response(response))
  }

  async preview(request: HermesSpeechOutputPreviewRequest, signal: AbortSignal): Promise<HermesSpeechOutputActionResult> {
    try {
      const response = await this.fetcher(`/api/audio/speak-local?profile=${encodeURIComponent(request.profileId)}`, {
        method: 'POST', credentials: 'include', signal,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          text: 'Hello Chris. This is Photon speaking locally.',
          voice: request.settings.voiceId,
          speed: request.settings.speed,
        }),
      })
      const raw = object(await this.response(response))
      const source = localAudioDataUrl(raw.data_url)
      if (!source || raw.provider !== 'kokoro') return { status: 'rejected', reason: 'invalid_settings' }
      await this.play(source, signal)
      return { status: 'accepted', reason: 'preview_complete' }
    } catch (reason) {
      if (signal.aborted) return { status: 'cancelled', reason: 'cancelled' }
      return { status: 'unavailable', reason: 'local_voice_unavailable' }
    }
  }

  async save(request: HermesSpeechOutputSaveRequest, signal: AbortSignal): Promise<{
    result: HermesSpeechOutputActionResult
    snapshot?: HermesSpeechOutputSnapshot
  }> {
    try {
      const response = await this.fetcher('/api/audio/local-voice/settings', {
        method: 'PUT', credentials: 'include', signal,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          contract_version: request.contractVersion,
          expected_revision: request.expectedRevision,
          profile_id: request.profileId,
          request_id: request.requestId,
          voice_id: request.settings.voiceId,
          speed: request.settings.speed,
        }),
      })
      const snapshot = normalizeHermesSpeechOutputSnapshot(await this.response(response))
      return { result: { status: 'accepted', reason: 'saved' }, snapshot }
    } catch (reason) {
      if (signal.aborted) return { result: { status: 'cancelled', reason: 'cancelled' } }
      if (reason instanceof Error && reason.message === 'stale_revision') {
        return { result: { status: 'rejected', reason: 'stale_revision' } }
      }
      return { result: { status: 'unavailable', reason: 'local_voice_unavailable' } }
    }
  }
}
