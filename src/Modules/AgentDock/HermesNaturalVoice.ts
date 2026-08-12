export const HERMES_NATURAL_VOICE_ENDPOINT = '/api/audio/speak-local'
export const HERMES_NATURAL_VOICE_MAX_TEXT = 8_000
const MAX_AUDIO_DATA_URL_LENGTH = 24 * 1024 * 1024

export type HermesNaturalVoicePhase = 'idle' | 'loading' | 'playing' | 'error'

export type HermesNaturalVoiceState = {
  messageId: string | null
  phase: HermesNaturalVoicePhase
}

export const HERMES_NATURAL_VOICE_IDLE: HermesNaturalVoiceState = {
  messageId: null,
  phase: 'idle',
}

type NaturalVoiceAudio = {
  currentTime: number
  onended: (() => void) | null
  onerror: (() => void) | null
  pause: () => void
  play: () => Promise<void>
}

type NaturalVoiceDependencies = {
  fetch: typeof fetch
  createAudio: (source: string) => NaturalVoiceAudio
}

type NaturalVoiceResponse = {
  ok: true
  data_url: string
  mime_type: 'audio/wav'
  provider: 'kokoro'
}

function isNaturalVoiceResponse(value: unknown): value is NaturalVoiceResponse {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const candidate = value as Record<string, unknown>
  return candidate.ok === true
    && candidate.provider === 'kokoro'
    && candidate.mime_type === 'audio/wav'
    && typeof candidate.data_url === 'string'
    && candidate.data_url.startsWith('data:audio/wav;base64,')
    && candidate.data_url.length <= MAX_AUDIO_DATA_URL_LENGTH
}

export function prepareHermesNaturalVoiceText(markdown: string): string {
  const spoken = markdown
    .replace(/```[\s\S]*?```/g, ' Code block omitted. ')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/https?:\/\/\S+/gi, ' link ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/^\s{0,3}(?:#{1,6}|[-*+] |\d+[.)] )/gm, '')
    .replace(/[*_~]+/g, '')
    .replace(/[|>]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()

  if (spoken.length <= HERMES_NATURAL_VOICE_MAX_TEXT) return spoken
  const clipped = spoken.slice(0, HERMES_NATURAL_VOICE_MAX_TEXT - 1)
  const lastBreak = clipped.lastIndexOf(' ')
  return `${clipped.slice(0, Math.max(lastBreak, HERMES_NATURAL_VOICE_MAX_TEXT - 160)).trimEnd()}…`
}

export class HermesNaturalVoicePlayer {
  private readonly dependencies: NaturalVoiceDependencies
  private generation = 0
  private active: { messageId: string; audio: NaturalVoiceAudio | null; abort: AbortController } | null = null
  private publish: ((state: HermesNaturalVoiceState) => void) | null = null
  private profileId = 'default'

  setProfileId(profileId: string): void {
    const next = profileId.trim()
    this.profileId = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(next) ? next : 'default'
  }

  constructor(dependencies?: Partial<NaturalVoiceDependencies>) {
    this.dependencies = {
      fetch: dependencies?.fetch ?? fetch,
      createAudio: dependencies?.createAudio ?? ((source) => new Audio(source) as unknown as NaturalVoiceAudio),
    }
  }

  async toggle(
    messageId: string,
    markdown: string,
    publish: (state: HermesNaturalVoiceState) => void,
  ): Promise<void> {
    if (this.active?.messageId === messageId) {
      this.stop()
      return
    }
    this.stop(false)

    const text = prepareHermesNaturalVoiceText(markdown)
    if (!text) {
      publish({ messageId, phase: 'error' })
      return
    }

    const generation = ++this.generation
    const abort = new AbortController()
    this.active = { messageId, audio: null, abort }
    this.publish = publish
    publish({ messageId, phase: 'loading' })

    try {
      const response = await this.dependencies.fetch(`${HERMES_NATURAL_VOICE_ENDPOINT}?profile=${encodeURIComponent(this.profileId)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text }),
        signal: abort.signal,
      })
      if (!response.ok) throw new Error('local voice request failed')
      const payload: unknown = await response.json()
      if (!isNaturalVoiceResponse(payload)) throw new Error('invalid local voice response')
      if (generation !== this.generation || abort.signal.aborted) return

      const audio = this.dependencies.createAudio(payload.data_url)
      this.active.audio = audio
      audio.onended = () => this.finish(generation)
      audio.onerror = () => this.fail(generation)
      await audio.play()
      if (generation === this.generation && !abort.signal.aborted) {
        publish({ messageId, phase: 'playing' })
      }
    } catch {
      if (generation === this.generation && !abort.signal.aborted) this.fail(generation)
    }
  }

  stop(publishIdle = true): void {
    this.generation += 1
    const active = this.active
    this.active = null
    active?.abort.abort()
    if (active?.audio) {
      active.audio.onended = null
      active.audio.onerror = null
      active.audio.pause()
      active.audio.currentTime = 0
    }
    if (publishIdle) this.publish?.(HERMES_NATURAL_VOICE_IDLE)
    this.publish = null
  }

  private finish(generation: number): void {
    if (generation !== this.generation) return
    const publish = this.publish
    this.active = null
    this.publish = null
    publish?.(HERMES_NATURAL_VOICE_IDLE)
  }

  private fail(generation: number): void {
    if (generation !== this.generation) return
    const messageId = this.active?.messageId ?? null
    const publish = this.publish
    this.active = null
    this.publish = null
    publish?.({ messageId, phase: 'error' })
  }
}
