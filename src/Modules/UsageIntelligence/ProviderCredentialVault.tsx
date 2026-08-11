import { Check, KeyRound, LockKeyhole, Plus, RefreshCw, Trash2, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { providerDefinitions } from './contracts'
import {
  credentialDisplayName,
  credentialIdFromLabel,
  deleteCredential,
  desktopCredentialWebView,
  normalizeCredentialMetadata,
  openCredentialDialog,
  requestCredentialList,
} from './DesktopCredentialBridge'
import type { DesktopCredentialFrame, DesktopCredentialMetadata } from './DesktopCredentialBridge'

const credentialProviders = providerDefinitions.filter((provider) => provider.id !== 'chatgpt-subscription')

function formatUpdatedAt(value?: string) {
  if (!value) return 'Stored in Windows Credential Manager'
  const date = new Date(value)
  return `Updated ${new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(date)}`
}

export function ProviderCredentialVault() {
  const [available, setAvailable] = useState(() => desktopCredentialWebView() !== null)
  const [entries, setEntries] = useState<DesktopCredentialMetadata[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [addingProvider, setAddingProvider] = useState<string | null>(null)
  const [keyLabel, setKeyLabel] = useState('')

  useEffect(() => {
    let bridge = desktopCredentialWebView()
    const receive = (event: MessageEvent) => {
      const frame = event.data as DesktopCredentialFrame
      if (frame?.version !== 1 || typeof frame.type !== 'string') return
      if (frame.type === 'credentials.list.result') {
        const next = Array.isArray(frame.entries)
          ? frame.entries.map(normalizeCredentialMetadata).filter((entry): entry is DesktopCredentialMetadata => entry !== null)
          : []
        setEntries(next)
        setLoading(false)
        setError(null)
      } else if (frame.type === 'credentials.changed' || frame.type === 'credentials.deleted') {
        window.dispatchEvent(new CustomEvent('hermes-credentials-changed'))
        setAddingProvider(null)
        setKeyLabel('')
        setLoading(true)
        requestCredentialList()
      } else if (frame.type === 'credentials.error') {
        setLoading(false)
        setError(typeof frame.message === 'string' ? frame.message : 'The native credential vault needs attention.')
      }
    }
    const connect = () => {
      bridge?.removeEventListener('message', receive)
      bridge = desktopCredentialWebView()
      setAvailable(bridge !== null)
      if (bridge) {
        bridge.addEventListener('message', receive)
        setLoading(true)
        requestCredentialList()
      }
    }

    if (bridge) {
      bridge.addEventListener('message', receive)
      setLoading(true)
      requestCredentialList()
    }
    window.addEventListener('hermes-desktop-ready', connect)
    return () => {
      bridge?.removeEventListener('message', receive)
      window.removeEventListener('hermes-desktop-ready', connect)
    }
  }, [])

  const grouped = useMemo(() => {
    const result = new Map<string, DesktopCredentialMetadata[]>()
    for (const entry of entries) {
      const current = result.get(entry.provider) ?? []
      current.push(entry)
      result.set(entry.provider, current)
    }
    for (const current of result.values()) current.sort((left, right) => left.credentialId.localeCompare(right.credentialId))
    return result
  }, [entries])

  function beginAdd(provider: string) {
    setAddingProvider(provider)
    setKeyLabel('')
    setError(null)
  }

  function saveNamedKey(provider: string) {
    const credentialId = credentialIdFromLabel(keyLabel)
    if (!credentialId) {
      setError('Enter a short key profile name such as Ali, Scarlett, Bob, or Charlie.')
      return
    }
    if (entries.some((entry) => entry.provider === provider && entry.credentialId.toLowerCase() === credentialId)) {
      setError(`A ${provider} key profile named ${credentialDisplayName(credentialId)} already exists. Replace it from its row instead.`)
      return
    }
    if (!openCredentialDialog(provider, credentialId)) setError('The desktop credential dialog could not open.')
  }

  return (
    <section className="ui-panel ui-vault-panel" aria-label="Provider credential vault">
      <header className="ui-panel-heading">
        <div><span className="ui-eyebrow">Native trust boundary</span><h2>Named provider keys</h2></div>
        <span className="ui-panel-count"><LockKeyhole size={11} />{available ? `${entries.length} key profile${entries.length === 1 ? '' : 's'}` : 'Desktop required'}</span>
      </header>
      {!available ? <div className="ui-vault-unavailable"><LockKeyhole size={18} /><span><strong>Secure key management activates in the desktop app</strong><small>Browser mode never receives provider secrets or native credential access.</small></span></div> : <>
        {error && <div className="ui-vault-error">{error}<button type="button" onClick={() => { setError(null); setLoading(true); requestCredentialList() }}><RefreshCw size={12} />Refresh</button></div>}
        <div className="ui-vault-grid">
          {credentialProviders.map((provider) => {
            const providerEntries = grouped.get(provider.id) ?? []
            const adding = addingProvider === provider.id
            return <article key={provider.id} className={`ui-vault-provider ${providerEntries.length ? 'configured' : ''}`}>
              <header>
                <span className="ui-vault-provider-mark"><KeyRound size={14} /></span>
                <div><strong>{provider.displayName}</strong><small>{providerEntries.length ? `${providerEntries.length} independently managed key profile${providerEntries.length === 1 ? '' : 's'}` : 'No native credentials stored'}</small></div>
                {!adding && <button type="button" disabled={loading} onClick={() => beginAdd(provider.id)}><Plus size={12} />Add key</button>}
              </header>
              {providerEntries.length > 0 && <div className="ui-vault-entry-list">
                {providerEntries.map((entry) => <div className="ui-vault-entry" key={`${entry.provider}:${entry.credentialId}`}>
                  <span><strong>{credentialDisplayName(entry.credentialId)}</strong><small>{formatUpdatedAt(entry.updatedAt)}</small></span>
                  <button type="button" disabled={loading} onClick={() => openCredentialDialog(entry.provider, entry.credentialId)}>Replace</button>
                  <button type="button" className="remove" disabled={loading} aria-label={`Remove ${provider.displayName} key profile ${credentialDisplayName(entry.credentialId)}`} onClick={() => deleteCredential(entry.provider, entry.credentialId)}><Trash2 size={12} /></button>
                </div>)}
              </div>}
              {adding && <div className="ui-vault-add-row">
                <label><span>Key profile name</span><input autoFocus value={keyLabel} maxLength={128} placeholder="Ali, Scarlett, Bob…" onChange={(event) => setKeyLabel(event.target.value)} onKeyDown={(event) => {
                  if (event.key === 'Enter') saveNamedKey(provider.id)
                  if (event.key === 'Escape') setAddingProvider(null)
                }} /></label>
                <button type="button" className="confirm" aria-label={`Store named ${provider.displayName} key`} onClick={() => saveNamedKey(provider.id)}><Check size={13} /></button>
                <button type="button" aria-label="Cancel named key" onClick={() => setAddingProvider(null)}><X size={13} /></button>
              </div>}
            </article>
          })}
        </div>
      </>}
    </section>
  )
}
