import { useEffect, useMemo, useState } from 'react'
import { LoaderCircle, Mic, RefreshCw, Save, ShieldCheck, Volume2 } from 'lucide-react'
import { HERMES_SPEECH_OUTPUT_VOICES, createDefaultHermesSpeechOutputSettings, type HermesSpeechOutputSettings, type HermesSpeechOutputVoice } from './contracts'
import { HermesSpeechOutputLiveAdapter } from './HermesSpeechOutputLiveAdapter'
import { HermesSpeechOutputSettingsSection } from './HermesSpeechOutputSettingsSection'
import {
  HERMES_DEFAULT_AUDIO_DEVICE,
  readHermesAudioInputDevice,
  readHermesAudioOutputDevice,
  saveHermesAudioInputDevice,
  saveHermesAudioOutputDevice,
} from '../HermesSpeechVoice/HermesAudioDevicePreferences'

type DeviceNotice = { kind: 'error' | 'ok'; text: string } | null

function deviceLabel(device: MediaDeviceInfo, index: number, kind: 'Microphone' | 'Output'): string {
  return device.label.trim() || `${kind} ${index + 1}`
}

function HermesAudioDeviceSettings() {
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([])
  const [input, setInput] = useState(readHermesAudioInputDevice)
  const [output, setOutput] = useState(readHermesAudioOutputDevice)
  const [notice, setNotice] = useState<DeviceNotice>(null)
  const [loading, setLoading] = useState(false)

  async function refresh() {
    if (!navigator.mediaDevices?.enumerateDevices) {
      setNotice({ kind: 'error', text: 'Audio device discovery is unavailable in this desktop runtime.' })
      return
    }
    setLoading(true)
    try {
      const next = await navigator.mediaDevices.enumerateDevices()
      setDevices(next)
      setNotice(null)
    } catch {
      setNotice({ kind: 'error', text: 'Windows audio devices could not be read.' })
    } finally { setLoading(false) }
  }

  useEffect(() => {
    void refresh()
    const changed = () => void refresh()
    navigator.mediaDevices?.addEventListener?.('devicechange', changed)
    return () => navigator.mediaDevices?.removeEventListener?.('devicechange', changed)
  }, [])

  const inputs = devices.filter((device) => device.kind === 'audioinput')
  const outputs = devices.filter((device) => device.kind === 'audiooutput')

  return <section className="speech-device-settings" aria-label="Windows audio devices">
    <header><div><small>VOICE / DEVICES</small><h3>Microphone and speaker</h3><p>Saved choices apply to Photon recording, previews, and read-aloud playback.</p></div><button type="button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={14} /> Refresh</button></header>
    <div className="speech-device-settings__grid">
      <label><span><Mic size={14} /> Microphone input</span><select value={input} onChange={(event) => setInput(event.target.value)}><option value={HERMES_DEFAULT_AUDIO_DEVICE}>Windows default microphone</option>{inputs.filter((device) => device.deviceId !== HERMES_DEFAULT_AUDIO_DEVICE).map((device, index) => <option key={device.deviceId} value={device.deviceId}>{deviceLabel(device, index, 'Microphone')}</option>)}</select><button type="button" onClick={() => { saveHermesAudioInputDevice(input); setNotice({ kind: 'ok', text: 'Default microphone saved.' }) }}><Save size={14} /> Save microphone</button></label>
      <label><span><Volume2 size={14} /> Speaker output</span><select value={output} onChange={(event) => setOutput(event.target.value)}><option value={HERMES_DEFAULT_AUDIO_DEVICE}>Windows default output</option>{outputs.filter((device) => device.deviceId !== HERMES_DEFAULT_AUDIO_DEVICE).map((device, index) => <option key={device.deviceId} value={device.deviceId}>{deviceLabel(device, index, 'Output')}</option>)}</select><button type="button" onClick={() => { saveHermesAudioOutputDevice(output); setNotice({ kind: 'ok', text: 'Default speaker output saved.' }) }}><Save size={14} /> Save output</button></label>
    </div>
    {notice && <div className={`speech-device-settings__notice is-${notice.kind}`} role="status" aria-live="polite">{notice.text}</div>}
  </section>
}

export function HermesSpeechVoiceWorkspace({ profileId = 'default' }: { profileId?: string }) {
  const adapter = useMemo(() => new HermesSpeechOutputLiveAdapter(), [])
  const [settings, setSettings] = useState<HermesSpeechOutputSettings>(createDefaultHermesSpeechOutputSettings())
  const [voices, setVoices] = useState<readonly HermesSpeechOutputVoice[]>(HERMES_SPEECH_OUTPUT_VOICES)
  const [revision, setRevision] = useState('loading')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const abort = new AbortController()
    setLoading(true)
    void adapter.read(profileId, abort.signal).then((snapshot) => {
      if (abort.signal.aborted) return
      setSettings(snapshot.settings)
      setVoices(snapshot.voices)
      setRevision(snapshot.revision)
      setError(null)
    }).catch(() => {
      if (!abort.signal.aborted) setError('Local voice settings are unavailable.')
    }).finally(() => {
      if (!abort.signal.aborted) setLoading(false)
    })
    return () => abort.abort()
  }, [adapter, profileId])

  if (loading) return <div className="speech-voice-loading"><LoaderCircle className="spin" size={22} /> Reading local speech settings…</div>

  return <section className="speech-voice-workspace">
    <div className="speech-input-summary">
      <span><Mic size={20} /></span>
      <div><small>VOICE / HEAR</small><strong>Local microphone transcription</strong><p>The microphone button in Photon records on this computer and sends only the captured audio to the local Hermes Whisper endpoint. Its transcript is inserted into the composer for your review before sending.</p></div>
      <em><ShieldCheck size={13} /> Local</em>
    </div>
    <HermesAudioDeviceSettings />
    {error && <div className="speech-voice-error" role="alert">{error}</div>}
    <HermesSpeechOutputSettingsSection
      disabled={Boolean(error)}
      profileId={profileId}
      revision={revision}
      settings={settings}
      voices={voices}
      onSettingsChange={setSettings}
      onPreview={(request, signal) => adapter.preview(request, signal)}
      onSave={async (request, signal) => {
        const saved = await adapter.save(request, signal)
        if (saved.snapshot) {
          setSettings(saved.snapshot.settings)
          setRevision(saved.snapshot.revision)
          setVoices(saved.snapshot.voices)
        }
        return saved.result
      }}
    />
  </section>
}
