export const HERMES_WINDOWS_AUDIO_PROTOCOL = 1

export type HermesWindowsAudioEndpoint = Readonly<{ ref: string; name: string; isDefault: boolean }>
export type HermesWindowsAudioSnapshot = Readonly<{
  inputs: readonly HermesWindowsAudioEndpoint[]
  outputs: readonly HermesWindowsAudioEndpoint[]
  selectedInputRef: string
  selectedOutputRef: string
  missingInputRef?: string | null
  missingOutputRef?: string | null
}>

type WebView = { postMessage: (message: unknown) => void; addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void; removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void }
type HostWindow = Window & { chrome?: { webview?: WebView } }
export type HermesWindowsAudioMessage = Record<string, unknown> & { type: string; version?: number; requestId?: string; message?: string }

function endpoint(value: unknown): HermesWindowsAudioEndpoint | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  const ref = typeof raw.ref === 'string' ? raw.ref : typeof raw.Ref === 'string' ? raw.Ref : ''
  const name = typeof raw.name === 'string' ? raw.name : typeof raw.Name === 'string' ? raw.Name : ''
  const isDefault = typeof raw.isDefault === 'boolean' ? raw.isDefault : typeof raw.IsDefault === 'boolean' ? raw.IsDefault : false
  if (!ref.trim() || !name.trim()) return null
  return { ref, name, isDefault }
}

export function normalizeAudioSnapshot(message: HermesWindowsAudioMessage): (HermesWindowsAudioMessage & HermesWindowsAudioSnapshot) | null {
  if (message.type !== 'audio.devices.snapshot' || !Array.isArray(message.inputs) || !Array.isArray(message.outputs)) return null
  const inputs = message.inputs.map(endpoint)
  const outputs = message.outputs.map(endpoint)
  if (inputs.some((item) => item === null) || outputs.some((item) => item === null)) return null
  if (typeof message.selectedInputRef !== 'string' || typeof message.selectedOutputRef !== 'string') return null
  return { ...message, inputs: inputs as HermesWindowsAudioEndpoint[], outputs: outputs as HermesWindowsAudioEndpoint[], selectedInputRef: message.selectedInputRef, selectedOutputRef: message.selectedOutputRef }
}

function requestId(): string { return `audio-${Date.now()}-${crypto.randomUUID()}` }

export class HermesWindowsAudioHost {
  private readonly webview: WebView | undefined
  private readonly listeners = new Set<(message: HermesWindowsAudioMessage) => void>()
  private listening = false
  private readonly onMessage = (event: MessageEvent) => {
    const value = event.data
    if (!value || typeof value !== 'object' || Array.isArray(value) || typeof (value as HermesWindowsAudioMessage).type !== 'string') return
    const message = value as HermesWindowsAudioMessage
    if (!message.type.startsWith('audio.')) return
    this.listeners.forEach((listener) => listener(message))
  }

  constructor(webview: WebView | undefined = typeof window === 'undefined' ? undefined : (window as HostWindow).chrome?.webview) { this.webview = webview; this.attach() }
  get available(): boolean { return Boolean(this.webview) }
  subscribe(listener: (message: HermesWindowsAudioMessage) => void): () => void { this.attach(); this.listeners.add(listener); return () => this.listeners.delete(listener) }
  dispose(): void { if (this.listening) this.webview?.removeEventListener('message', this.onMessage); this.listening = false; this.listeners.clear() }
  list(): void { this.send('audio.devices.list') }
  save(kind: 'input' | 'output', endpointRef: string): void { this.send('audio.devices.save', { kind, endpointRef }) }
  startTest(endpointRef: string, durationMilliseconds = 5_000): void { this.send('audio.micTest.start', { endpointRef, durationMilliseconds }) }
  stopTest(): void { this.send('audio.micTest.stop') }
  cancelTest(): void { this.send('audio.micTest.cancel') }
  play(endpointRef: string): void { this.send('audio.micTest.play', { endpointRef }) }
  stopPlayback(): void { this.send('audio.micTest.stopPlayback') }
  deleteSample(): void { this.send('audio.micTest.delete') }
  startSpeech(): string { return this.send('audio.speech.start') }
  stopSpeech(id: string): void { this.send('audio.speech.stop', {}, id) }
  cancelSpeech(id: string): void { this.send('audio.speech.cancel', {}, id) }
  playOutput(dataUrl: string, signal?: AbortSignal, onStarted?: () => void): Promise<void> {
    const webview = this.webview
    if (!webview) return Promise.reject(new Error('windows-audio-host-unavailable'))
    const id = requestId()
    return new Promise((resolve, reject) => {
      let settled = false
      const finish = (reason?: Error) => {
        if (settled) return
        settled = true
        unsubscribe()
        signal?.removeEventListener('abort', abort)
        if (reason) reject(reason); else resolve()
      }
      const abort = () => {
        webview.postMessage({ type: 'audio.output.stop', version: HERMES_WINDOWS_AUDIO_PROTOCOL, requestId: id })
        finish(new DOMException('Playback cancelled.', 'AbortError'))
      }
      const unsubscribe = this.subscribe((message) => {
        if (message.requestId !== id) return
        if (message.type === 'audio.output.started') onStarted?.()
        else if (message.type === 'audio.output.completed') finish()
        else if (message.type === 'audio.output.stopped') finish(new DOMException('Playback stopped.', 'AbortError'))
        else if (message.type === 'audio.error') finish(new Error(message.message || 'Windows audio playback failed.'))
      })
      if (signal?.aborted) { abort(); return }
      signal?.addEventListener('abort', abort, { once: true })
      webview.postMessage({ type: 'audio.output.play', version: HERMES_WINDOWS_AUDIO_PROTOCOL, requestId: id, dataUrl })
    })
  }
  stopOutput(): void { this.send('audio.output.stop') }
  private send(type: string, body: Record<string, unknown> = {}, id = requestId()): string {
    if (!this.webview) throw new Error('windows-audio-host-unavailable')
    this.webview.postMessage({ type, version: HERMES_WINDOWS_AUDIO_PROTOCOL, requestId: id, ...body })
    return id
  }
  private attach(): void {
    if (!this.webview || this.listening) return
    this.webview.addEventListener('message', this.onMessage)
    this.listening = true
  }
}

export function isAudioSnapshot(message: HermesWindowsAudioMessage): message is HermesWindowsAudioMessage & HermesWindowsAudioSnapshot {
  return normalizeAudioSnapshot(message) !== null
}
