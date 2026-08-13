export const HERMES_DEFAULT_AUDIO_DEVICE = 'default'

const inputKey = 'hermes.audio.input.v1'
const outputKey = 'hermes.audio.output.v1'

function read(key: string): string {
  try { return localStorage.getItem(key)?.trim() || HERMES_DEFAULT_AUDIO_DEVICE } catch { return HERMES_DEFAULT_AUDIO_DEVICE }
}

function write(key: string, deviceId: string): void {
  const exact = deviceId.trim() || HERMES_DEFAULT_AUDIO_DEVICE
  try { localStorage.setItem(key, exact) } catch { /* current session keeps the selected value */ }
}

export function readHermesAudioInputDevice(): string { return read(inputKey) }
export function readHermesAudioOutputDevice(): string { return read(outputKey) }
export function saveHermesAudioInputDevice(deviceId: string): void { write(inputKey, deviceId) }
export function saveHermesAudioOutputDevice(deviceId: string): void { write(outputKey, deviceId) }

export function hermesAudioConstraints(): MediaStreamConstraints {
  const selected = readHermesAudioInputDevice()
  return {
    audio: {
      autoGainControl: true,
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
      ...(selected === HERMES_DEFAULT_AUDIO_DEVICE ? {} : { deviceId: { exact: selected } }),
    },
    video: false,
  }
}

type SinkAudio = HTMLAudioElement & { setSinkId?: (deviceId: string) => Promise<void> }

export async function applyHermesAudioOutput(audio: HTMLAudioElement): Promise<void> {
  const selected = readHermesAudioOutputDevice()
  if (selected === HERMES_DEFAULT_AUDIO_DEVICE) return
  const sink = audio as SinkAudio
  if (!sink.setSinkId) throw new Error('audio-output-selection-unavailable')
  await sink.setSinkId(selected)
}
