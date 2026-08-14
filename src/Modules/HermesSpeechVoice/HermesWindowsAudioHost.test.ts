import { describe, expect, it, vi } from 'vitest'
import { HermesWindowsAudioHost, isAudioSnapshot, normalizeAudioSnapshot } from './HermesWindowsAudioHost'

describe('HermesWindowsAudioHost', () => {
  it('sends narrow versioned host requests and never projects native device ids', () => {
    const sent: unknown[] = []
    let listener: ((event: MessageEvent) => void) | undefined
    const webview = {
      postMessage: (message: unknown) => sent.push(message),
      addEventListener: (_type: string, next: (event: MessageEvent) => void) => { listener = next },
      removeEventListener: vi.fn(),
    }
    const host = new HermesWindowsAudioHost(webview)
    const received: unknown[] = []
    host.subscribe((message) => received.push(message))
    host.list()
    host.save('input', 'win-in-opaque')
    host.startTest('win-in-opaque', 5_000)
    host.play('win-out-opaque')
    expect(sent).toEqual(expect.arrayContaining([
      expect.objectContaining({ type: 'audio.devices.list', version: 1 }),
      expect.objectContaining({ type: 'audio.devices.save', kind: 'input', endpointRef: 'win-in-opaque' }),
      expect.objectContaining({ type: 'audio.micTest.start', durationMilliseconds: 5_000 }),
      expect.objectContaining({ type: 'audio.micTest.play', endpointRef: 'win-out-opaque' }),
    ]))
    listener?.(new MessageEvent('message', { data: { type: 'audio.devices.snapshot', inputs: [], outputs: [], selectedInputRef: 'windows-default-input', selectedOutputRef: 'windows-default-output' } }))
    expect(received).toHaveLength(1)
    expect(isAudioSnapshot(received[0] as never)).toBe(true)
    host.dispose()
  })

  it('fails honestly outside the native desktop host', () => {
    const host = new HermesWindowsAudioHost(undefined)
    expect(host.available).toBe(false)
    expect(() => host.list()).toThrow('windows-audio-host-unavailable')
  })

  it('normalizes the exact PascalCase endpoint shape produced by the native serializer', () => {
    const raw = {
      type: 'audio.devices.snapshot',
      inputs: [{ Ref: 'windows-default-input', Name: 'Windows default microphone', IsDefault: true }, { Ref: 'win-in-opaque', Name: 'Studio microphone', IsDefault: false }],
      outputs: [{ Ref: 'windows-default-output', Name: 'Windows default output', IsDefault: true }],
      selectedInputRef: 'win-in-opaque',
      selectedOutputRef: 'windows-default-output',
    }
    const snapshot = normalizeAudioSnapshot(raw)
    expect(snapshot?.inputs[1]).toEqual({ ref: 'win-in-opaque', name: 'Studio microphone', isDefault: false })
    expect(snapshot?.outputs[0].name).toBe('Windows default output')
    expect(isAudioSnapshot(raw)).toBe(true)
  })

  it('rejects endpoint arrays whose entries cannot render a real name and reference', () => {
    expect(normalizeAudioSnapshot({ type: 'audio.devices.snapshot', inputs: [{}], outputs: [], selectedInputRef: '', selectedOutputRef: '' })).toBeNull()
  })

  it('reattaches after React development cleanup disposes the first subscription', () => {
    const listeners = new Set<(event: MessageEvent) => void>()
    const webview = {
      postMessage: vi.fn(),
      addEventListener: (_type: string, listener: (event: MessageEvent) => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: (event: MessageEvent) => void) => listeners.delete(listener),
    }
    const host = new HermesWindowsAudioHost(webview)
    host.subscribe(vi.fn())
    host.dispose()
    const received = vi.fn()
    host.subscribe(received)
    listeners.forEach((listener) => listener(new MessageEvent('message', { data: { type: 'audio.devices.snapshot', inputs: [], outputs: [], selectedInputRef: 'windows-default-input', selectedOutputRef: 'windows-default-output' } })))
    expect(received).toHaveBeenCalledOnce()
  })

  it('reuses the speech start request id for stop and cancel correlation', () => {
    const postMessage = vi.fn()
    const host = new HermesWindowsAudioHost({
      postMessage,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })
    const id = host.startSpeech()
    host.stopSpeech(id)
    host.cancelSpeech(id)
    expect(postMessage.mock.calls.map(([message]) => message)).toEqual([
      expect.objectContaining({ type: 'audio.speech.start', requestId: id }),
      expect.objectContaining({ type: 'audio.speech.stop', requestId: id }),
      expect.objectContaining({ type: 'audio.speech.cancel', requestId: id }),
    ])
  })
})
