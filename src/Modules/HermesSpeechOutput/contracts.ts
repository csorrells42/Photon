export const HERMES_SPEECH_OUTPUT_CONTRACT_VERSION = 1 as const
export const HERMES_SPEECH_OUTPUT_PROVIDER = 'kokoro' as const
export const HERMES_SPEECH_OUTPUT_DEFAULT_VOICE = 'af_heart' as const
export const HERMES_SPEECH_OUTPUT_DEFAULT_SPEED = 0.98
export const HERMES_SPEECH_OUTPUT_MIN_SPEED = 0.75
export const HERMES_SPEECH_OUTPUT_MAX_SPEED = 1.25

export const HERMES_SPEECH_OUTPUT_VOICES = [
  {
    id: 'af_heart',
    label: 'Heart',
    description: 'Natural American female voice',
  },
  {
    id: 'am_michael',
    label: 'Michael',
    description: 'Natural American male voice',
  },
] as const

export type HermesSpeechOutputVoiceId = typeof HERMES_SPEECH_OUTPUT_VOICES[number]['id']

export type HermesSpeechOutputSettings = Readonly<{
  contractVersion: typeof HERMES_SPEECH_OUTPUT_CONTRACT_VERSION
  localOnly: true
  provider: typeof HERMES_SPEECH_OUTPUT_PROVIDER
  speed: number
  voiceId: HermesSpeechOutputVoiceId
}>

export type HermesSpeechOutputActionReason =
  | 'cancelled'
  | 'invalid_settings'
  | 'local_voice_unavailable'
  | 'operation_failed'
  | 'preview_complete'
  | 'saved'
  | 'stale_revision'

export type HermesSpeechOutputActionResult = Readonly<
  | { reason: 'preview_complete' | 'saved'; status: 'accepted' }
  | { reason: 'cancelled'; status: 'cancelled' }
  | {
      reason: Exclude<HermesSpeechOutputActionReason, 'cancelled' | 'preview_complete' | 'saved'>
      status: 'failed' | 'rejected' | 'unavailable'
    }
>

export type HermesSpeechOutputPreviewRequest = Readonly<{
  contractVersion: typeof HERMES_SPEECH_OUTPUT_CONTRACT_VERSION
  profileId: string
  requestId: string
  settings: HermesSpeechOutputSettings
}>

export type HermesSpeechOutputSaveRequest = Readonly<{
  contractVersion: typeof HERMES_SPEECH_OUTPUT_CONTRACT_VERSION
  expectedRevision: string
  profileId: string
  requestId: string
  settings: HermesSpeechOutputSettings
}>

const opaqueIdPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const voiceIds = new Set<string>(HERMES_SPEECH_OUTPUT_VOICES.map((voice) => voice.id))

function exactOpaqueId(value: unknown, label: string): string {
  const text = typeof value === 'string' ? value.trim() : ''
  if (!opaqueIdPattern.test(text)) throw new Error(`${label} is invalid.`)
  return text
}

function exactSpeed(value: unknown): number {
  if (
    typeof value !== 'number'
    || !Number.isFinite(value)
    || value < HERMES_SPEECH_OUTPUT_MIN_SPEED
    || value > HERMES_SPEECH_OUTPUT_MAX_SPEED
  ) {
    throw new Error('Voice speed is invalid.')
  }
  return Math.round(value * 100) / 100
}

export function createDefaultHermesSpeechOutputSettings(): HermesSpeechOutputSettings {
  return {
    contractVersion: HERMES_SPEECH_OUTPUT_CONTRACT_VERSION,
    localOnly: true,
    provider: HERMES_SPEECH_OUTPUT_PROVIDER,
    speed: HERMES_SPEECH_OUTPUT_DEFAULT_SPEED,
    voiceId: HERMES_SPEECH_OUTPUT_DEFAULT_VOICE,
  }
}

export function assertHermesSpeechOutputSettings(value: unknown): HermesSpeechOutputSettings {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('Voice settings are invalid.')
  }
  const candidate = value as Record<string, unknown>
  if (
    candidate.contractVersion !== HERMES_SPEECH_OUTPUT_CONTRACT_VERSION
    || candidate.provider !== HERMES_SPEECH_OUTPUT_PROVIDER
    || candidate.localOnly !== true
    || typeof candidate.voiceId !== 'string'
    || !voiceIds.has(candidate.voiceId)
  ) {
    throw new Error('Voice settings are invalid.')
  }
  return {
    contractVersion: HERMES_SPEECH_OUTPUT_CONTRACT_VERSION,
    localOnly: true,
    provider: HERMES_SPEECH_OUTPUT_PROVIDER,
    speed: exactSpeed(candidate.speed),
    voiceId: candidate.voiceId as HermesSpeechOutputVoiceId,
  }
}

export function createHermesSpeechOutputPreviewRequest(
  profileId: string,
  requestId: string,
  settings: HermesSpeechOutputSettings,
): HermesSpeechOutputPreviewRequest {
  return {
    contractVersion: HERMES_SPEECH_OUTPUT_CONTRACT_VERSION,
    profileId: exactOpaqueId(profileId, 'Profile ID'),
    requestId: exactOpaqueId(requestId, 'Request ID'),
    settings: assertHermesSpeechOutputSettings(settings),
  }
}

export function createHermesSpeechOutputSaveRequest(
  profileId: string,
  expectedRevision: string,
  requestId: string,
  settings: HermesSpeechOutputSettings,
): HermesSpeechOutputSaveRequest {
  return {
    contractVersion: HERMES_SPEECH_OUTPUT_CONTRACT_VERSION,
    expectedRevision: exactOpaqueId(expectedRevision, 'Settings revision'),
    profileId: exactOpaqueId(profileId, 'Profile ID'),
    requestId: exactOpaqueId(requestId, 'Request ID'),
    settings: assertHermesSpeechOutputSettings(settings),
  }
}

export function hermesSpeechOutputReasonText(reason: HermesSpeechOutputActionReason): string {
  switch (reason) {
    case 'preview_complete': return 'The local voice preview finished.'
    case 'saved': return 'Speaking voice saved for this profile.'
    case 'cancelled': return 'The voice request was cancelled.'
    case 'stale_revision': return 'Voice settings changed elsewhere. Reload before saving.'
    case 'local_voice_unavailable': return 'The on-device voice engine is unavailable.'
    case 'invalid_settings': return 'Choose a supported local voice and speed.'
    default: return 'The local voice request failed.'
  }
}
