import { useEffect, useRef, useState } from 'react'
import { Check, CircleAlert, LoaderCircle, Play, Save, ShieldCheck, Volume2 } from 'lucide-react'
import {
  HERMES_SPEECH_OUTPUT_MAX_SPEED,
  HERMES_SPEECH_OUTPUT_MIN_SPEED,
  HERMES_SPEECH_OUTPUT_VOICES,
  assertHermesSpeechOutputSettings,
  createHermesSpeechOutputPreviewRequest,
  createHermesSpeechOutputSaveRequest,
  hermesSpeechOutputReasonText,
  type HermesSpeechOutputActionResult,
  type HermesSpeechOutputPreviewRequest,
  type HermesSpeechOutputSaveRequest,
  type HermesSpeechOutputSettings,
  type HermesSpeechOutputVoiceId,
} from './contracts'
import './HermesSpeechOutputSettingsSection.css'

export type HermesSpeechOutputSettingsSectionProps = {
  disabled?: boolean
  onPreview?: (
    request: HermesSpeechOutputPreviewRequest,
    signal: AbortSignal,
  ) => Promise<HermesSpeechOutputActionResult>
  onSave: (
    request: HermesSpeechOutputSaveRequest,
    signal: AbortSignal,
  ) => Promise<HermesSpeechOutputActionResult>
  onSettingsChange: (settings: HermesSpeechOutputSettings) => void
  profileId: string
  revision: string
  settings: HermesSpeechOutputSettings
}

type WorkingAction = 'preview' | 'save'
type Notice = Readonly<{ reason: HermesSpeechOutputActionResult['reason']; tone: 'bad' | 'good' | 'warn' }>

function requestId(prefix: WorkingAction): string {
  return `${prefix}:${crypto.randomUUID()}`
}

function toneFor(result: HermesSpeechOutputActionResult): Notice['tone'] {
  if (result.status === 'accepted') return 'good'
  if (result.status === 'cancelled' || result.reason === 'stale_revision') return 'warn'
  return 'bad'
}

export function HermesSpeechOutputSettingsSection({
  disabled = false,
  onPreview,
  onSave,
  onSettingsChange,
  profileId,
  revision,
  settings,
}: HermesSpeechOutputSettingsSectionProps) {
  const [working, setWorking] = useState<WorkingAction | null>(null)
  const [notice, setNotice] = useState<Notice | null>(null)
  const operationRef = useRef<{ abort: AbortController; generation: number } | null>(null)
  const generationRef = useRef(0)
  const exactSettings = assertHermesSpeechOutputSettings(settings)
  const selectedVoice = HERMES_SPEECH_OUTPUT_VOICES.find((voice) => voice.id === exactSettings.voiceId)

  function cancelCurrent() {
    generationRef.current += 1
    operationRef.current?.abort.abort()
    operationRef.current = null
    setWorking(null)
  }

  useEffect(() => {
    cancelCurrent()
    setNotice(null)
    return () => {
      generationRef.current += 1
      operationRef.current?.abort.abort()
      operationRef.current = null
    }
  }, [disabled, exactSettings.speed, exactSettings.voiceId, profileId, revision])

  function update(next: HermesSpeechOutputSettings) {
    cancelCurrent()
    setNotice(null)
    onSettingsChange(assertHermesSpeechOutputSettings(next))
  }

  async function run(action: WorkingAction) {
    if (disabled || working !== null || action === 'preview' && !onPreview) return
    cancelCurrent()
    const generation = generationRef.current
    const abort = new AbortController()
    operationRef.current = { abort, generation }
    setWorking(action)
    setNotice(null)
    try {
      const id = requestId(action)
      const result = action === 'preview'
        ? await onPreview!(createHermesSpeechOutputPreviewRequest(profileId, id, exactSettings), abort.signal)
        : await onSave(createHermesSpeechOutputSaveRequest(profileId, revision, id, exactSettings), abort.signal)
      if (generation === generationRef.current && !abort.signal.aborted) {
        setNotice({ reason: result.reason, tone: toneFor(result) })
      }
    } catch {
      if (generation === generationRef.current && !abort.signal.aborted) {
        setNotice({ reason: 'operation_failed', tone: 'bad' })
      }
    } finally {
      if (generation === generationRef.current) {
        operationRef.current = null
        setWorking(null)
      }
    }
  }

  return (
    <section className="speech-output-settings" aria-label="Voice output settings">
      <header className="speech-output-settings__header">
        <span><Volume2 size={17} /></span>
        <div>
          <small>VOICE / SPEAK</small>
          <h3>Photon speaking voice</h3>
          <p>Choose how completed Photon responses sound when read aloud.</p>
        </div>
        <strong><ShieldCheck size={13} /> On-device</strong>
      </header>

      <div className="speech-output-settings__privacy">
        <ShieldCheck size={15} />
        <span><strong>Local-only speech</strong><small>Response text is rendered by Kokoro on this computer and is never sent to a cloud voice provider.</small></span>
      </div>

      <div className="speech-output-settings__fields">
        <label>
          <span>Speaking voice</span>
          <select
            value={exactSettings.voiceId}
            disabled={disabled || working !== null}
            onChange={(event) => update({ ...exactSettings, voiceId: event.target.value as HermesSpeechOutputVoiceId })}
          >
            {HERMES_SPEECH_OUTPUT_VOICES.map((voice) => (
              <option key={voice.id} value={voice.id}>{voice.label} — {voice.description}</option>
            ))}
          </select>
          <small>{selectedVoice?.description}</small>
        </label>

        <label>
          <span>Speaking pace <output>{exactSettings.speed.toFixed(2)}×</output></span>
          <input
            aria-label="Speaking pace"
            type="range"
            min={HERMES_SPEECH_OUTPUT_MIN_SPEED}
            max={HERMES_SPEECH_OUTPUT_MAX_SPEED}
            step="0.01"
            value={exactSettings.speed}
            disabled={disabled || working !== null}
            onChange={(event) => update({ ...exactSettings, speed: Number(event.target.value) })}
          />
          <small>0.98× is the tuned natural default.</small>
        </label>
      </div>

      <div className="speech-output-settings__actions">
        <button type="button" disabled={disabled || working !== null || !onPreview} onClick={() => void run('preview')}>
          {working === 'preview' ? <LoaderCircle className="spin" size={14} /> : <Play size={14} />}
          Preview voice
        </button>
        <button className="is-primary" type="button" disabled={disabled || working !== null} onClick={() => void run('save')}>
          {working === 'save' ? <LoaderCircle className="spin" size={14} /> : <Save size={14} />}
          Save for this profile
        </button>
      </div>

      <footer>
        <span>Profile <code>{profileId}</code></span>
        <span>Revision <code>{revision}</code></span>
      </footer>

      {notice && (
        <div className={`speech-output-settings__notice is-${notice.tone}`} role="status" aria-live="polite">
          {notice.tone === 'good' ? <Check size={14} /> : <CircleAlert size={14} />}
          {hermesSpeechOutputReasonText(notice.reason)}
        </div>
      )}
    </section>
  )
}
