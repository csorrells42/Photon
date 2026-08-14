import { hermesAudioConstraints } from './HermesAudioDevicePreferences'
import { HERMES_WINDOWS_AUDIO_PROTOCOL, HermesWindowsAudioHost } from './HermesWindowsAudioHost'

export const HERMES_SPEECH_INPUT_ENDPOINT = '/api/audio/transcribe'
export const HERMES_SPEECH_INPUT_MAX_BYTES = 25 * 1024 * 1024
export const HERMES_SPEECH_INPUT_MAX_DURATION_MS = 120_000

export type HermesSpeechInputPhase =
  | 'idle'
  | 'requesting'
  | 'listening'
  | 'transcribing'
  | 'ready'
  | 'cancelled'
  | 'unavailable'
  | 'error'

export type HermesSpeechInputState = Readonly<{
  phase: HermesSpeechInputPhase
  transcript?: string
  reason?: string
}>

export const HERMES_SPEECH_INPUT_IDLE: HermesSpeechInputState = { phase: 'idle' }

type MediaRecorderLike = {
  mimeType: string
  state: string
  ondataavailable: ((event: BlobEvent) => void) | null
  onerror: (() => void) | null
  onstop: (() => void) | null
  start: (timeslice?: number) => void
  stop: () => void
}

type SpeechInputDependencies = {
  createRecorder: (stream: MediaStream) => MediaRecorderLike
  fetch: typeof fetch
  getUserMedia: () => Promise<MediaStream>
  readBlob: (blob: Blob) => Promise<string>
  setTimer: (callback: () => void, milliseconds: number) => ReturnType<typeof setTimeout>
  clearTimer: (timer: ReturnType<typeof setTimeout>) => void
}

type TranscriptionResponse = {
  ok: true
  transcript: string
  provider?: string
}

function isTranscriptionResponse(value: unknown): value is TranscriptionResponse {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const candidate = value as Record<string, unknown>
  return candidate.ok === true
    && typeof candidate.transcript === 'string'
    && candidate.transcript.length <= 32_000
    && (candidate.provider === undefined || typeof candidate.provider === 'string')
}

function stopTracks(stream: MediaStream | null) {
  stream?.getTracks().forEach((track) => track.stop())
}

function defaultReadBlob(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onerror = () => reject(new Error('audio-read-failed'))
    reader.onload = () => typeof reader.result === 'string'
      ? resolve(reader.result)
      : reject(new Error('audio-read-failed'))
    reader.readAsDataURL(blob)
  })
}

async function transcriptionFailureReason(response: Response): Promise<string> {
  if (response.status === 401 || response.status === 403) {
    return 'Hermes authentication expired. Reopen the Workbench and try again.'
  }
  if (response.status === 413) return 'The recording exceeded the local transcription limit.'
  try {
    const payload: unknown = await response.json()
    if (payload && typeof payload === 'object' && !Array.isArray(payload)) {
      const detail = (payload as Record<string, unknown>).detail
      if (typeof detail === 'string') {
        const normalized = detail.replace(/\s+/g, ' ').trim()
        if (normalized && normalized.length <= 240) return normalized
      }
    }
  } catch {
    // The stable status fallback below is more useful than a JSON parsing error.
  }
  return `Local Whisper returned HTTP ${response.status || 'error'}.`
}

export class HermesSpeechInputController {
  private readonly dependencies: SpeechInputDependencies
  private generation = 0
  private recorder: MediaRecorderLike | null = null
  private stream: MediaStream | null = null
  private chunks: Blob[] = []
  private abort: AbortController | null = null
  private timer: ReturnType<typeof setTimeout> | null = null
  private publish: ((state: HermesSpeechInputState) => void) | null = null
  private readonly nativeHost: HermesWindowsAudioHost
  private readonly forceBrowserCapture: boolean
  private nativeListening = false
  private nativeRequestId: string | null = null
  private unsubscribeNative: (() => void) | null = null

  constructor(dependencies?: Partial<SpeechInputDependencies>) {
    this.nativeHost = new HermesWindowsAudioHost()
    this.forceBrowserCapture = Boolean(dependencies?.getUserMedia || dependencies?.createRecorder)
    this.dependencies = {
      createRecorder: dependencies?.createRecorder ?? ((stream) => new MediaRecorder(stream) as MediaRecorderLike),
      fetch: dependencies?.fetch ?? fetch,
      getUserMedia: dependencies?.getUserMedia ?? defaultGetUserMedia,
      readBlob: dependencies?.readBlob ?? defaultReadBlob,
      setTimer: dependencies?.setTimer ?? setTimeout,
      clearTimer: dependencies?.clearTimer ?? clearTimeout,
    }
  }

  get isListening(): boolean {
    return this.nativeListening || this.recorder?.state === 'recording'
  }

  async start(publish: (state: HermesSpeechInputState) => void): Promise<void> {
    this.cancel(false)
    const generation = ++this.generation
    this.publish = publish
    publish({ phase: 'requesting' })
    if (this.nativeHost.available && !this.forceBrowserCapture) {
      this.unsubscribeNative?.()
      this.unsubscribeNative = this.nativeHost.subscribe((message) => {
        if (generation !== this.generation) return
        if (message.version !== HERMES_WINDOWS_AUDIO_PROTOCOL || message.requestId !== this.nativeRequestId) return
        if (message.type === 'audio.speech.started') { this.nativeListening = true; publish({ phase: 'listening' }) }
        else if (message.type === 'audio.speech.captured' && typeof message.dataUrl === 'string') {
          this.nativeListening = false
          this.nativeRequestId = null
          this.unsubscribeNative?.(); this.unsubscribeNative = null
          publish({ phase: 'transcribing' })
          void this.transcribeDataUrl(generation, message.dataUrl, typeof message.mimeType === 'string' ? message.mimeType : 'audio/wav')
        }
        else if (message.type === 'audio.speech.ready' && typeof message.transcript === 'string') {
          this.nativeListening = false
          this.nativeRequestId = null
          const transcript = message.transcript.trim()
          const publishReady = this.publish
          this.publish = null
          this.unsubscribeNative?.(); this.unsubscribeNative = null
          if (transcript) publishReady?.({ phase: 'ready', transcript })
          else this.fail(generation, 'No speech was detected.')
        } else if (message.type === 'audio.error') {
          this.nativeListening = false
          this.nativeRequestId = null
          this.fail(generation, typeof message.message === 'string' ? message.message : 'Windows microphone capture failed.')
        }
      })
      try {
        this.nativeRequestId = this.nativeHost.startSpeech()
      } catch {
        this.fail(generation, 'Windows microphone capture could not start.', 'unavailable')
      }
      return
    }
    try {
      const stream = await this.dependencies.getUserMedia()
      if (generation !== this.generation) {
        stopTracks(stream)
        return
      }
      const recorder = this.dependencies.createRecorder(stream)
      this.stream = stream
      this.recorder = recorder
      this.chunks = []
      recorder.ondataavailable = ({ data }) => {
        if (generation === this.generation && data.size > 0) this.chunks.push(data)
      }
      recorder.onerror = () => this.fail(generation, 'Microphone recording failed.')
      recorder.onstop = () => { if (generation === this.generation) void this.transcribe(generation, recorder.mimeType) }
      recorder.start(1_000)
      this.timer = this.dependencies.setTimer(() => this.stop(), HERMES_SPEECH_INPUT_MAX_DURATION_MS)
      publish({ phase: 'listening' })
    } catch {
      if (generation === this.generation) this.fail(generation, 'Microphone permission or input is unavailable.', 'unavailable')
    }
  }

  stop(): void {
    if (this.nativeListening && this.nativeRequestId) {
      this.publish?.({ phase: 'transcribing' })
      this.nativeHost.stopSpeech(this.nativeRequestId)
      return
    }
    if (!this.recorder || this.recorder.state !== 'recording') return
    if (this.timer) this.dependencies.clearTimer(this.timer)
    this.timer = null
    this.publish?.({ phase: 'transcribing' })
    this.recorder.stop()
    stopTracks(this.stream)
    this.stream = null
  }

  cancel(publishCancelled = true): void {
    this.generation += 1
    if (this.nativeRequestId) this.nativeHost.cancelSpeech(this.nativeRequestId)
    this.nativeListening = false
    this.nativeRequestId = null
    this.unsubscribeNative?.(); this.unsubscribeNative = null
    if (this.timer) this.dependencies.clearTimer(this.timer)
    this.timer = null
    this.abort?.abort()
    this.abort = null
    if (this.recorder?.state === 'recording') this.recorder.stop()
    this.recorder = null
    this.chunks = []
    stopTracks(this.stream)
    this.stream = null
    if (publishCancelled) this.publish?.({ phase: 'cancelled' })
    this.publish = null
  }

  private async transcribe(generation: number, mimeType: string): Promise<void> {
    const chunks = this.chunks
    this.chunks = []
    this.recorder = null
    const blob = new Blob(chunks, { type: mimeType || 'audio/webm' })
    if (blob.size <= 0 || blob.size > HERMES_SPEECH_INPUT_MAX_BYTES) {
      this.fail(generation, blob.size <= 0 ? 'No microphone audio was captured.' : 'The recording exceeded the local transcription limit.')
      return
    }
    const abort = new AbortController()
    this.abort = abort
    try {
      const dataUrl = await this.dependencies.readBlob(blob)
      if (generation !== this.generation || abort.signal.aborted) return
      await this.transcribeDataUrl(generation, dataUrl, blob.type || 'audio/webm', abort)
    } catch {
      if (generation === this.generation && !abort.signal.aborted) this.fail(generation, 'Local Whisper could not transcribe this recording.')
    }
  }

  private async transcribeDataUrl(generation: number, dataUrl: string, mimeType: string, existingAbort?: AbortController): Promise<void> {
    const abort = existingAbort ?? new AbortController()
    this.abort = abort
    try {
      const response = await this.dependencies.fetch(HERMES_SPEECH_INPUT_ENDPOINT, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ data_url: dataUrl, mime_type: mimeType }),
        signal: abort.signal,
      })
      if (!response.ok) {
        this.fail(generation, await transcriptionFailureReason(response))
        return
      }
      const payload: unknown = await response.json()
      if (!isTranscriptionResponse(payload)) {
        this.fail(generation, 'Local Whisper returned an invalid response.')
        return
      }
      if (generation !== this.generation || abort.signal.aborted) return
      const transcript = payload.transcript.trim()
      if (!transcript) {
        this.fail(generation, 'No speech was detected.')
        return
      }
      const publish = this.publish
      this.abort = null
      this.publish = null
      publish?.({ phase: 'ready', transcript })
    } catch (error) {
      if (generation === this.generation && !abort.signal.aborted) {
        this.fail(
          generation,
          error instanceof TypeError
            ? 'Local Whisper could not reach the Hermes runtime.'
            : 'Local Whisper could not transcribe this recording.',
        )
      }
    }
  }

  private fail(generation: number, reason: string, phase: 'error' | 'unavailable' = 'error'): void {
    if (generation !== this.generation) return
    if (this.timer) this.dependencies.clearTimer(this.timer)
    this.timer = null
    this.abort?.abort()
    this.abort = null
    this.nativeListening = false
    this.nativeRequestId = null
    this.unsubscribeNative?.(); this.unsubscribeNative = null
    this.recorder = null
    this.chunks = []
    stopTracks(this.stream)
    this.stream = null
    const publish = this.publish
    this.publish = null
    publish?.({ phase, reason })
  }
}

function defaultGetUserMedia(): Promise<MediaStream> {
  return navigator.mediaDevices.getUserMedia(hermesAudioConstraints())
}
