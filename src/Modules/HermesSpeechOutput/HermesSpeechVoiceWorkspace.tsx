import { useEffect, useMemo, useState } from 'react'
import { CircleStop, LoaderCircle, Mic, Play, RefreshCw, Save, ShieldCheck, Trash2, Volume2 } from 'lucide-react'
import { HERMES_SPEECH_OUTPUT_VOICES, createDefaultHermesSpeechOutputSettings, type HermesSpeechOutputSettings, type HermesSpeechOutputVoice } from './contracts'
import { HermesSpeechOutputLiveAdapter } from './HermesSpeechOutputLiveAdapter'
import { HermesSpeechOutputSettingsSection } from './HermesSpeechOutputSettingsSection'
import { HermesWindowsAudioHost, normalizeAudioSnapshot, type HermesWindowsAudioSnapshot } from '../HermesSpeechVoice/HermesWindowsAudioHost'

type DeviceNotice = { kind: 'error' | 'ok'; text: string } | null

function HermesAudioDeviceSettings() {
  const host = useMemo(() => new HermesWindowsAudioHost(), [])
  const [snapshot, setSnapshot] = useState<HermesWindowsAudioSnapshot | null>(null)
  const [input, setInput] = useState('windows-default-input')
  const [output, setOutput] = useState('windows-default-output')
  const [notice, setNotice] = useState<DeviceNotice>(null)
  const [loading, setLoading] = useState(false)
  const [testPhase, setTestPhase] = useState<'idle' | 'recording' | 'ready' | 'playing'>('idle')
  const [elapsed, setElapsed] = useState(0)

  async function refresh() {
    if (!host.available) {
      setNotice({ kind: 'error', text: 'The native Windows audio bridge is unavailable. Restart Photon to enumerate real audio endpoints.' })
      return
    }
    setLoading(true)
    try { host.list() } catch { setLoading(false); setNotice({ kind: 'error', text: 'Windows audio devices could not be read.' }) }
  }

  useEffect(() => {
    const unsubscribe = host.subscribe((message) => {
      const audioSnapshot = normalizeAudioSnapshot(message)
      if (audioSnapshot) {
        setSnapshot(audioSnapshot); setInput(audioSnapshot.selectedInputRef); setOutput(audioSnapshot.selectedOutputRef); setLoading(false)
        if (audioSnapshot.missingInputRef || audioSnapshot.missingOutputRef) setNotice({ kind: 'error', text: 'A saved audio device was unplugged. Photon recovered to the current Windows default.' })
        else setNotice(null)
      } else if (message.type === 'audio.error') { setLoading(false); setTestPhase('idle'); setNotice({ kind: 'error', text: String(message.message || 'The Windows audio operation failed.') }) }
      else if (message.type === 'audio.devices.saved') setNotice({ kind: 'ok', text: 'Windows audio preference saved.' })
      else if (message.type === 'audio.micTest.started') { setElapsed(0); setTestPhase('recording'); setNotice({ kind: 'ok', text: 'Recording a private 5-second microphone sample…' }) }
      else if (message.type === 'audio.micTest.ready') { setTestPhase('ready'); setNotice({ kind: 'ok', text: String(message.warning || `Microphone captured ${message.durationMilliseconds} ms of real audio.`) }) }
      else if (message.type === 'audio.micTest.playing') setTestPhase('playing')
      else if (message.type === 'audio.micTest.playbackComplete' || message.type === 'audio.micTest.playbackStopped') setTestPhase('ready')
      else if (message.type === 'audio.micTest.deleted' || message.type === 'audio.micTest.cancelled') { setTestPhase('idle'); setElapsed(0) }
    })
    void refresh()
    return () => { unsubscribe(); host.cancelTest(); host.dispose() }
  }, [host])

  useEffect(() => {
    if (testPhase !== 'recording') return
    const timer = window.setInterval(() => setElapsed((value) => Math.min(5, value + .1)), 100)
    return () => window.clearInterval(timer)
  }, [testPhase])

  return <section className="speech-device-settings" aria-label="Windows audio devices">
    <header><div><small>VOICE / DEVICES</small><h3>Microphone and speaker</h3><p>Saved choices apply to Photon recording, previews, and read-aloud playback.</p></div><button type="button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={14} /> Refresh</button></header>
    <div className="speech-device-settings__grid">
      <label><span><Mic size={14} /> Microphone input</span><select value={input} onChange={(event) => setInput(event.target.value)} disabled={!snapshot}>{snapshot?.inputs.map((device) => <option key={device.ref} value={device.ref}>{device.name}{device.isDefault && !device.ref.startsWith('windows-default') ? ' · current default' : ''}</option>)}</select><button type="button" disabled={!snapshot} onClick={() => host.save('input', input)}><Save size={14} /> Save microphone</button></label>
      <label><span><Volume2 size={14} /> Speaker output</span><select value={output} onChange={(event) => setOutput(event.target.value)} disabled={!snapshot}>{snapshot?.outputs.map((device) => <option key={device.ref} value={device.ref}>{device.name}{device.isDefault && !device.ref.startsWith('windows-default') ? ' · current default' : ''}</option>)}</select><button type="button" disabled={!snapshot} onClick={() => host.save('output', output)}><Save size={14} /> Save output</button></label>
    </div>
    <div className="speech-device-settings__test" aria-label="Microphone test">
      {testPhase === 'idle' && <button type="button" disabled={!snapshot} onClick={() => host.startTest(input)}><Mic size={14} /> Test microphone</button>}
      {testPhase === 'recording' && <><strong aria-live="polite">Recording {elapsed.toFixed(1)} / 5.0 s</strong><button type="button" onClick={() => host.stopTest()}><CircleStop size={14} /> Stop</button><button type="button" onClick={() => host.cancelTest()}>Cancel</button></>}
      {testPhase === 'ready' && <><button type="button" onClick={() => host.play(output)}><Play size={14} /> Play sample</button><button type="button" onClick={() => host.startTest(input)}><Mic size={14} /> Re-record</button><button type="button" onClick={() => host.deleteSample()}><Trash2 size={14} /> Delete sample</button></>}
      {testPhase === 'playing' && <button type="button" onClick={() => host.stopPlayback()}><CircleStop size={14} /> Stop playback</button>}
      <small>Test audio stays in memory, is never sent to transcription, and is deleted on cancel, close, restart, or error.</small>
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
