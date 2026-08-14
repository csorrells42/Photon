import { describe, expect, it, vi } from 'vitest'
import {
  HERMES_SPEECH_INPUT_ENDPOINT,
  HermesSpeechInputController,
  type HermesSpeechInputState,
} from './HermesSpeechInput'

function harness(transcript = 'Hello Photon') {
  const states: HermesSpeechInputState[] = []
  const track = { stop: vi.fn() }
  const stream = { getTracks: () => [track] } as unknown as MediaStream
  let recorder: {
    mimeType: string
    state: string
    ondataavailable: ((event: BlobEvent) => void) | null
    onerror: (() => void) | null
    onstop: (() => void) | null
    start: (timeslice?: number) => void
    stop: () => void
  }
  recorder = {
    mimeType: 'audio/webm',
    state: 'inactive',
    ondataavailable: null,
    onerror: null,
    onstop: null,
    start: () => { recorder.state = 'recording' },
    stop: () => {
      recorder.ondataavailable?.({ data: new Blob(['sound'], { type: 'audio/webm' }) } as BlobEvent)
      recorder.state = 'inactive'
      recorder.onstop?.()
    },
  }
  const fetchMock = vi.fn<typeof fetch>(async () => ({
    ok: true,
    json: async () => ({ ok: true, transcript, provider: 'local' }),
  } as Response))
  const controller = new HermesSpeechInputController({
    createRecorder: () => recorder,
    fetch: fetchMock,
    getUserMedia: async () => stream,
    readBlob: async () => 'data:audio/webm;base64,c291bmQ=',
    setTimer: (() => 1) as unknown as typeof setTimeout,
    clearTimer: vi.fn(),
  })
  return { controller, fetchMock, recorder, states, track }
}

describe('HermesSpeechInputController', () => {
  it('records explicitly, stops, transcribes locally, and returns a reviewable draft', async () => {
    const test = harness()
    await test.controller.start((state) => test.states.push(state))
    expect(test.states.at(-1)).toEqual({ phase: 'listening' })
    test.controller.stop()
    await vi.waitFor(() => expect(test.states.at(-1)).toEqual({ phase: 'ready', transcript: 'Hello Photon' }))
    expect(test.fetchMock).toHaveBeenCalledWith(HERMES_SPEECH_INPUT_ENDPOINT, expect.objectContaining({ method: 'POST' }))
    expect(test.fetchMock.mock.calls[0]?.[1]).toMatchObject({ credentials: 'include' })
    expect(test.track.stop).toHaveBeenCalledOnce()
  })

  it('uploads native Windows capture with the authenticated request contract', async () => {
    const listeners = new Set<(event: MessageEvent) => void>()
    const postMessage = vi.fn()
    vi.stubGlobal('window', {
      chrome: {
        webview: {
          postMessage,
          addEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.add(listener),
          removeEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.delete(listener),
        },
      },
    })
    const states: HermesSpeechInputState[] = []
    const fetchMock = vi.fn<typeof fetch>(async () => ({
      ok: true,
      json: async () => ({ ok: true, transcript: 'Native speech', provider: 'local' }),
    } as Response))
    const controller = new HermesSpeechInputController({ fetch: fetchMock })

    await controller.start((state) => states.push(state))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({ type: 'audio.speech.start' }))
    const startRequest = postMessage.mock.calls[0]?.[0] as { requestId: string }
    listeners.forEach((listener) => listener({ data: {
      type: 'audio.error',
      version: 1,
      requestId: 'unrelated-audio-request',
      message: 'Unrelated speaker error',
    } } as MessageEvent))
    expect(states.at(-1)).toEqual({ phase: 'requesting' })
    listeners.forEach((listener) => listener({ data: {
      type: 'audio.speech.started',
      version: 1,
      requestId: startRequest.requestId,
    } } as MessageEvent))
    controller.stop()
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'audio.speech.stop',
      requestId: startRequest.requestId,
    }))
    listeners.forEach((listener) => listener({ data: {
      type: 'audio.speech.captured',
      version: 1,
      requestId: startRequest.requestId,
      dataUrl: 'data:audio/wav;base64,c291bmQ=',
      mimeType: 'audio/wav',
    } } as MessageEvent))

    await vi.waitFor(() => expect(states.at(-1)).toEqual({ phase: 'ready', transcript: 'Native speech' }))
    expect(fetchMock).toHaveBeenCalledWith(HERMES_SPEECH_INPUT_ENDPOINT, expect.objectContaining({
      method: 'POST',
      credentials: 'include',
      body: JSON.stringify({ data_url: 'data:audio/wav;base64,c291bmQ=', mime_type: 'audio/wav' }),
    }))
    vi.unstubAllGlobals()
  })

  it('cancels capture, retires the microphone, and never transcribes', async () => {
    const test = harness()
    await test.controller.start((state) => test.states.push(state))
    test.controller.cancel()
    expect(test.states.at(-1)).toEqual({ phase: 'cancelled' })
    expect(test.track.stop).toHaveBeenCalledOnce()
    expect(test.fetchMock).not.toHaveBeenCalled()
  })

  it('reports unavailable when microphone permission fails', async () => {
    const states: HermesSpeechInputState[] = []
    const controller = new HermesSpeechInputController({ getUserMedia: async () => { throw new Error('denied') } })
    await controller.start((state) => states.push(state))
    expect(states.at(-1)).toEqual({ phase: 'unavailable', reason: 'Microphone permission or input is unavailable.' })
  })

  it('does not invent text when Whisper detects silence', async () => {
    const test = harness('   ')
    await test.controller.start((state) => test.states.push(state))
    test.controller.stop()
    await vi.waitFor(() => expect(test.states.at(-1)).toEqual({ phase: 'error', reason: 'No speech was detected.' }))
  })

  it('surfaces a bounded backend rejection instead of hiding it behind a generic toast', async () => {
    const test = harness()
    test.fetchMock.mockResolvedValueOnce({
      ok: false,
      status: 400,
      json: async () => ({ detail: 'Local Whisper model is unavailable.' }),
    } as Response)
    await test.controller.start((state) => test.states.push(state))
    test.controller.stop()
    await vi.waitFor(() => expect(test.states.at(-1)).toEqual({
      phase: 'error',
      reason: 'Local Whisper model is unavailable.',
    }))
  })
})
