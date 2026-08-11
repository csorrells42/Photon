import { describe, expect, it, vi } from 'vitest'
import {
  HERMES_NATURAL_VOICE_ENDPOINT,
  HERMES_NATURAL_VOICE_MAX_TEXT,
  HermesNaturalVoicePlayer,
  prepareHermesNaturalVoiceText,
} from './HermesNaturalVoice'

class FakeAudio {
  currentTime = 0
  onended: (() => void) | null = null
  onerror: (() => void) | null = null
  pause = vi.fn()
  play = vi.fn(async () => undefined)
}

describe('HermesNaturalVoice', () => {
  it('turns markdown into bounded conversational speech text', () => {
    const source = `# Result\nUse [the guide](https://example.test) and \`build\`.\n\`\`\`ts\nsecret()\n\`\`\`\n${'word '.repeat(2_000)}`
    const spoken = prepareHermesNaturalVoiceText(source)

    expect(spoken).toContain('Use the guide and build.')
    expect(spoken).toContain('Code block omitted.')
    expect(spoken).not.toContain('https://')
    expect(spoken).not.toContain('secret()')
    expect(spoken.length).toBeLessThanOrEqual(HERMES_NATURAL_VOICE_MAX_TEXT)
  })

  it('plays only a strict on-device Kokoro response', async () => {
    const audio = new FakeAudio()
    const fetchVoice = vi.fn(async () => ({
      ok: true,
      json: async () => ({
        ok: true,
        provider: 'kokoro',
        mime_type: 'audio/wav',
        data_url: 'data:audio/wav;base64,UklGRg==',
      }),
    } as Response))
    const states: string[] = []
    const player = new HermesNaturalVoicePlayer({
      fetch: fetchVoice,
      createAudio: () => audio,
    })

    await player.toggle('message-1', 'Hello **Chris**.', (state) => states.push(state.phase))

    expect(fetchVoice).toHaveBeenCalledWith(HERMES_NATURAL_VOICE_ENDPOINT, expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({ text: 'Hello Chris.' }),
    }))
    expect(states).toEqual(['loading', 'playing'])
    expect(audio.play).toHaveBeenCalledOnce()

    audio.onended?.()
    expect(states).toEqual(['loading', 'playing', 'idle'])
  })

  it('stops the exact active message and rejects a cloud-provider response', async () => {
    const audio = new FakeAudio()
    const fetchVoice = vi.fn(async () => ({
      ok: true,
      json: async () => ({
        ok: true,
        provider: 'openai',
        mime_type: 'audio/wav',
        data_url: 'data:audio/wav;base64,UklGRg==',
      }),
    } as Response))
    const states: string[] = []
    const player = new HermesNaturalVoicePlayer({ fetch: fetchVoice, createAudio: () => audio })

    await player.toggle('message-2', 'Local only.', (state) => states.push(state.phase))
    expect(states).toEqual(['loading', 'error'])
    expect(audio.play).not.toHaveBeenCalled()

    const pending = new Promise<Response>(() => undefined)
    const waiting = new HermesNaturalVoicePlayer({ fetch: vi.fn(() => pending) })
    const waitingStates: string[] = []
    void waiting.toggle('message-3', 'Please speak.', (state) => waitingStates.push(state.phase))
    await Promise.resolve()
    await waiting.toggle('message-3', 'Please speak.', (state) => waitingStates.push(state.phase))
    expect(waitingStates).toEqual(['loading', 'idle'])
  })
})
