import {
  assertHermesSpeechOutputSettings,
  type HermesSpeechOutputActionResult,
  type HermesSpeechOutputPreviewRequest,
  type HermesSpeechOutputSaveRequest,
  type HermesSpeechOutputSettings,
} from './contracts'

export type HermesSpeechOutputSnapshot = Readonly<{
  profileId: string
  revision: string
  settings: HermesSpeechOutputSettings
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

export function normalizeHermesSpeechOutputSnapshot(value: unknown): HermesSpeechOutputSnapshot {
  const raw = object(value)
  return {
    profileId: opaque(raw.profileId, 'Profile ID'),
    revision: opaque(raw.revision, 'Settings revision'),
    settings: assertHermesSpeechOutputSettings(raw.settings),
  }
}

export class HermesSpeechOutputLiveAdapter {
  constructor(
    private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_),
    private readonly playAudio: (source: string) => Promise<void> = async (source) => {
      await new Audio(source).play()
    },
  ) {}

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
      const source = typeof raw.data_url === 'string' && raw.data_url.startsWith('data:audio/wav;base64,')
        ? raw.data_url : ''
      if (!source || raw.provider !== 'kokoro') return { status: 'rejected', reason: 'invalid_settings' }
      await this.playAudio(source)
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
