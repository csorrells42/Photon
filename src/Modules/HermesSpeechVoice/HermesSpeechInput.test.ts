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
  const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ ok: true, transcript, provider: 'local' }) }))
  const controller = new HermesSpeechInputController({
    createRecorder: () => recorder,
    fetch: fetchMock as unknown as typeof fetch,
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
    expect(test.track.stop).toHaveBeenCalledOnce()
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
})
