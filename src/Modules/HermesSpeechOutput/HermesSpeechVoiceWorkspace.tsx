import { useEffect, useMemo, useState } from 'react'
import { LoaderCircle, Mic, ShieldCheck } from 'lucide-react'
import { createDefaultHermesSpeechOutputSettings, type HermesSpeechOutputSettings } from './contracts'
import { HermesSpeechOutputLiveAdapter } from './HermesSpeechOutputLiveAdapter'
import { HermesSpeechOutputSettingsSection } from './HermesSpeechOutputSettingsSection'

export function HermesSpeechVoiceWorkspace({ profileId = 'default' }: { profileId?: string }) {
  const adapter = useMemo(() => new HermesSpeechOutputLiveAdapter(), [])
  const [settings, setSettings] = useState<HermesSpeechOutputSettings>(createDefaultHermesSpeechOutputSettings())
  const [revision, setRevision] = useState('loading')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const abort = new AbortController()
    setLoading(true)
    void adapter.read(profileId, abort.signal).then((snapshot) => {
      if (abort.signal.aborted) return
      setSettings(snapshot.settings)
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
    {error && <div className="speech-voice-error" role="alert">{error}</div>}
    <HermesSpeechOutputSettingsSection
      disabled={Boolean(error)}
      profileId={profileId}
      revision={revision}
      settings={settings}
      onSettingsChange={setSettings}
      onPreview={(request, signal) => adapter.preview(request, signal)}
      onSave={async (request, signal) => {
        const saved = await adapter.save(request, signal)
        if (saved.snapshot) {
          setSettings(saved.snapshot.settings)
          setRevision(saved.snapshot.revision)
        }
        return saved.result
      }}
    />
  </section>
}
