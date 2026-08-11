import { useEffect, useMemo, useSyncExternalStore } from 'react'
import { Check, KeyRound, Link2, RefreshCw, ShieldCheck, Trash2, X } from 'lucide-react'
import { DesktopHermesConnectionsClient, type HermesConnectionsClient } from './DesktopHermesConnectionsClient'
import { HermesConnectionsController } from './HermesConnectionsController'
import type { ConnectionCatalogEntry } from './contracts'
import './HermesConnectionsWorkspace.css'

export type HermesConnectionsWorkspaceProps = {
  profileId: string
  catalog: ConnectionCatalogEntry[]
  client?: HermesConnectionsClient
}

export function HermesConnectionsWorkspace({ profileId, catalog, client }: HermesConnectionsWorkspaceProps) {
  const resolvedClient = useMemo(() => client ?? new DesktopHermesConnectionsClient(), [client])
  const controller = useMemo(() => new HermesConnectionsController(resolvedClient, profileId), [resolvedClient, profileId])
  const snapshot = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot)

  useEffect(() => {
    void controller.load()
    return () => controller.dispose()
  }, [controller])

  const bySlot = useMemo(() => new Map(snapshot.entries.map((entry) => [`${entry.providerId}:${entry.slotId}`, entry])), [snapshot.entries])
  const review = snapshot.pendingReview

  return (
    <section className="hermes-connections" aria-label="Connections and credentials">
      <header className="hermes-connections__header">
        <div>
          <span className="hermes-connections__eyebrow"><ShieldCheck size={14} /> Native security boundary</span>
          <h2>Connections &amp; Credentials</h2>
          <p>Photon can use your connected providers without displaying, exporting, or placing credential values in this page.</p>
        </div>
        <button type="button" className="hermes-connections__refresh" onClick={() => void controller.load()} disabled={snapshot.working}>
          <RefreshCw size={15} className={snapshot.status === 'loading' ? 'is-spinning' : undefined} /> Refresh
        </button>
      </header>

      {snapshot.message && <div className={`hermes-connections__notice is-${snapshot.status}`} role="status">{snapshot.message}</div>}
      {snapshot.status === 'unavailable' && (
        <div className="hermes-connections__empty">
          <KeyRound size={28} />
          <strong>Native credential storage is not registered yet.</strong>
          <span>This surface stays read-only and unavailable instead of falling back to browser or portable plaintext storage.</span>
        </div>
      )}

      {snapshot.status !== 'unavailable' && (
        <div className="hermes-connections__grid">
          {catalog.map((provider) => {
            const key = `${provider.providerId}:${provider.slotId}`
            const connected = bySlot.get(key)
            return (
              <article className="hermes-connections__card" key={key}>
                <div className="hermes-connections__card-title">
                  <span className={`hermes-connections__status ${connected ? 'is-connected' : ''}`}><Link2 size={15} /></span>
                  <div><strong>{provider.displayName}</strong><span>{connected ? 'Connected' : 'Not connected'}</span></div>
                </div>
                {provider.description && <p>{provider.description}</p>}
                <dl>
                  <div><dt>Method</dt><dd>{provider.authKind.replace('-', ' ')}</dd></div>
                  <div><dt>Profile</dt><dd>{profileId}</dd></div>
                  {connected && <div><dt>Revision</dt><dd>{connected.revision}</dd></div>}
                </dl>
                <div className="hermes-connections__actions">
                  <button type="button" onClick={() => void controller.beginChange(provider, connected)} disabled={snapshot.working || !provider.supportsNativeChange}>
                    <KeyRound size={14} /> {connected ? 'Replace' : 'Connect'}
                  </button>
                  {connected && <button type="button" className="is-danger" onClick={() => void controller.beginRemove(connected)} disabled={snapshot.working}><Trash2 size={14} /> Remove</button>}
                </div>
                {!provider.supportsNativeChange && <small>This provider uses an external sign-in flow; Photon will preserve that flow.</small>}
              </article>
            )
          })}
          {catalog.length === 0 && snapshot.status === 'ready' && (
            <div className="hermes-connections__empty"><KeyRound size={28} /><strong>No provider catalog was supplied.</strong><span>The shared Hermes catalog registration is still pending.</span></div>
          )}
        </div>
      )}

      {review && (
        <div className="hermes-connections__review" role="dialog" aria-modal="true" aria-labelledby="connections-review-title">
          <div className="hermes-connections__review-card">
            <span className="hermes-connections__eyebrow"><ShieldCheck size={14} /> Native review</span>
            <h3 id="connections-review-title">{review.action === 'remove' ? 'Remove connection?' : 'Save this connection?'}</h3>
            <p>The native host collected the credential privately. Confirming binds this change to <b>{review.providerId}</b>, slot <b>{review.slotId}</b>, profile <b>{review.profileId}</b>, and revision <b>{review.expectedRevision}</b>.</p>
            <p className="hermes-connections__review-expiry">This one-use review expires {new Date(review.expiresAt).toLocaleTimeString()}.</p>
            <div className="hermes-connections__review-actions">
              <button type="button" onClick={() => void controller.cancelPending()} disabled={snapshot.working}><X size={15} /> Cancel</button>
              <button type="button" className={review.action === 'remove' ? 'is-danger' : 'is-primary'} onClick={() => void controller.commitPending()} disabled={snapshot.working}><Check size={15} /> Confirm</button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
