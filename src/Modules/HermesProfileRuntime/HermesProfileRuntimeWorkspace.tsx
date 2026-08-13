import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type ConfigurationValue,
  type HermesProfileRuntimeAdapter,
  type OperationPreview,
  type PermissionGrantIntent,
  type ProfileExport,
  type ProfileMutation,
  type ProfileRuntimeSnapshot,
} from './contracts'
import { DeterministicHermesProfileRuntimeAdapter } from './DeterministicHermesProfileRuntimeAdapter'
import { HermesProfileRuntimeCoordinator } from './ProfileRuntimeCoordinator'
import { inspectProfileImport } from './runtimeSafety'
import './HermesProfileRuntimeWorkspace.css'

type PreviewAction =
  | { kind: 'profile'; mutation: ProfileMutation; preview: OperationPreview }
  | { kind: 'permission'; intent: PermissionGrantIntent; preview: OperationPreview }
  | { kind: 'import'; inspection: ReturnType<typeof inspectProfileImport>; preview: OperationPreview }

export type HermesProfileRuntimeWorkspaceProps = {
  adapter?: HermesProfileRuntimeAdapter
  initialProfileId?: string
  initialSnapshot?: ProfileRuntimeSnapshot
  onExport?: (profileExport: ProfileExport) => void
  interactionMode?: HermesProfileRuntimeWorkspaceInteractionMode
  surfaceMode?: HermesProfileRuntimeWorkspaceSurfaceMode
}

export type HermesProfileRuntimeWorkspaceInteractionMode = 'read-write' | 'read-only'
export type HermesProfileRuntimeWorkspaceSurfaceMode = 'full' | 'safe-live'

function message(reason: unknown) {
  if (reason instanceof DOMException && reason.name === 'AbortError') return 'Operation cancelled.'
  return reason instanceof Error ? reason.message : 'The profile-runtime operation failed.'
}

function statusClass(value: string) {
  return `hpr-status hpr-status-${value.replaceAll('_', '-')}`
}

export function HermesProfileRuntimeWorkspace({
  adapter: suppliedAdapter,
  initialProfileId = 'profile-main',
  initialSnapshot,
  onExport,
  interactionMode = 'read-write',
  surfaceMode = 'full',
}: HermesProfileRuntimeWorkspaceProps) {
  const readOnly = interactionMode === 'read-only'
  const safeLive = surfaceMode === 'safe-live'
  const adapter = useMemo(() => suppliedAdapter ?? new DeterministicHermesProfileRuntimeAdapter(), [suppliedAdapter])
  const coordinator = useMemo(() => new HermesProfileRuntimeCoordinator(), [adapter])
  const [profileId, setProfileId] = useState(initialSnapshot?.profileId ?? initialProfileId)
  const [snapshot, setSnapshot] = useState<ProfileRuntimeSnapshot | null>(initialSnapshot ?? null)
  const [documents, setDocuments] = useState<Record<string, string>>(() => Object.fromEntries((initialSnapshot?.documents ?? []).map((item) => [item.kind, item.text])))
  const [intent, setIntent] = useState(initialSnapshot?.intent ?? { modelId: null, projectPath: null, worktreePath: null, note: '' })
  const [configuration, setConfiguration] = useState<Record<string, ConfigurationValue>>(initialSnapshot?.configuration ?? {})
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [previewAction, setPreviewAction] = useState<PreviewAction | null>(null)
  const [confirmation, setConfirmation] = useState('')
  const [mutationKind, setMutationKind] = useState<Exclude<ProfileMutation['kind'], 'delete'>>('create')
  const [profileName, setProfileName] = useState('')
  const [importText, setImportText] = useState('')
  const [exported, setExported] = useState<ProfileExport | null>(null)
  const alive = useRef(true)
  const loadGeneration = useRef(0)

  const sync = useCallback((next: ProfileRuntimeSnapshot) => {
    setProfileId(next.profileId)
    setSnapshot(next)
    setDocuments(Object.fromEntries(next.documents.map((item) => [item.kind, item.text])))
    setIntent(next.intent)
    setConfiguration(next.configuration)
  }, [])

  const run = useCallback(async <T,>(key: string, operation: () => Promise<T>): Promise<T | undefined> => {
    setBusy(key); setError(null); setNotice(null)
    try { return await operation() }
    catch (reason) { if (alive.current) setError(message(reason)); return undefined }
    finally { if (alive.current) setBusy((current) => current === key ? null : current) }
  }, [])

  const load = useCallback(async (targetProfileId: string, quiet = false) => {
    const generation = ++loadGeneration.current
    if (!quiet) setBusy('load')
    setError(null)
    try {
      const next = await coordinator.execute('load', targetProfileId, undefined, (context, signal) => adapter.load(context, signal))
      if (next.profileId !== targetProfileId) throw new Error('The adapter returned a cross-profile snapshot.')
      if (alive.current && loadGeneration.current === generation) sync(next)
    } catch (reason) { if (alive.current && loadGeneration.current === generation) setError(message(reason)) }
    finally { if (alive.current && loadGeneration.current === generation) setBusy((current) => current === 'load' ? null : current) }
  }, [adapter, coordinator, sync])

  useEffect(() => {
    alive.current = true
    if (!initialSnapshot) void load(initialProfileId)
    return () => { alive.current = false; loadGeneration.current += 1; coordinator.cancelAll() }
  }, [coordinator, initialProfileId, initialSnapshot, load])

  useEffect(() => {
    if (!previewAction) return
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) setPreviewAction(null) }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [busy, previewAction])

  const visibleDocuments = snapshot?.documents.filter((item) => !safeLive || item.kind === 'soul') ?? []
  const dirtyDocuments = visibleDocuments.some((item) => documents[item.kind] !== item.text)
  const dirtyIntent = snapshot ? JSON.stringify(intent) !== JSON.stringify(snapshot.intent) : false
  const dirtyConfiguration = snapshot ? JSON.stringify(configuration) !== JSON.stringify(snapshot.configuration) : false
  const isDirty = dirtyDocuments || dirtyIntent || dirtyConfiguration

  async function changeActive(targetProfileId: string) {
    if (!snapshot || targetProfileId === profileId) return
    if (isDirty) { setError('Save or discard profile edits before switching profiles.'); return }
    const result = await run('select-profile', () => coordinator.execute('select-profile', profileId, snapshot.revision, (context, signal) => adapter.selectActiveProfile({ ...context, targetProfileId }, signal)))
    if (result) await load(result.activeProfileId, true)
  }

  async function inspectProfile(targetProfileId: string) {
    if (targetProfileId === profileId) return
    await load(targetProfileId)
  }

  async function saveDocument(kind: 'persona' | 'soul' | 'context') {
    if (!snapshot) return
    const next = await run(`save-${kind}`, () => coordinator.execute(`save-${kind}`, profileId, snapshot.revision, (context, signal) => adapter.saveDocument({ ...context, document: kind, text: documents[kind] ?? '' }, signal)))
    if (next) { sync(next); setNotice(`${kind} saved by explicit caller action.`) }
  }

  async function saveIntent() {
    if (!snapshot) return
    const next = await run('save-intent', () => coordinator.execute('save-intent', profileId, snapshot.revision, (context, signal) => adapter.saveIntent({ ...context, intent }, signal)))
    if (next) { sync(next); setNotice(safeLive ? 'Profile model assignment saved.' : 'Profile model, project, and worktree intents saved.') }
  }

  async function saveConfiguration() {
    if (!snapshot) return
    const values = Object.fromEntries(snapshot.configurationSchema.filter((field) => field.disposition === 'manageable').map((field) => [field.key, configuration[field.key]]))
    const next = await run('save-configuration', () => coordinator.execute('save-configuration', profileId, snapshot.revision, (context, signal) => adapter.saveConfiguration({ ...context, values }, signal)))
    if (next) { sync(next); setNotice('Non-secret configuration saved.') }
  }

  async function previewMutation(event?: FormEvent) {
    event?.preventDefault(); if (!snapshot) return
    const mutation: ProfileMutation = mutationKind === 'clone'
      ? { kind: 'clone', sourceProfileId: profileId, name: profileName }
      : mutationKind === 'rename' ? { kind: 'rename', name: profileName } : { kind: 'create', name: profileName }
    const preview = await run('preview-profile', () => coordinator.execute('preview-profile', profileId, snapshot.revision, (context, signal) => adapter.previewProfileMutation({ ...context, mutation }, signal)))
    if (preview) { setConfirmation(''); setPreviewAction({ kind: 'profile', mutation, preview }) }
  }

  async function previewDelete() {
    if (!snapshot) return
    const mutation: ProfileMutation = { kind: 'delete' }
    const preview = await run('preview-profile', () => coordinator.execute('preview-profile', profileId, snapshot.revision, (context, signal) => adapter.previewProfileMutation({ ...context, mutation }, signal)))
    if (preview) { setConfirmation(''); setPreviewAction({ kind: 'profile', mutation, preview }) }
  }

  async function previewPermission(permissionId: string) {
    if (!snapshot) return
    const intent: PermissionGrantIntent = { permissionId, duration: 'session', rationale: 'Requested explicitly from the profile-runtime settings surface.' }
    const preview = await run('preview-permission', () => coordinator.execute('preview-permission', profileId, snapshot.revision, (context, signal) => adapter.previewPermissionGrant({ ...context, intent }, signal)))
    if (preview) { setConfirmation(''); setPreviewAction({ kind: 'permission', intent, preview }) }
  }

  async function previewImport() {
    if (!snapshot) return
    let inspection: ReturnType<typeof inspectProfileImport>
    try { inspection = inspectProfileImport(importText) }
    catch (reason) { setError(message(reason)); return }
    const preview = await run('preview-import', () => coordinator.execute('preview-import', profileId, snapshot.revision, (context, signal) => adapter.previewImport({ ...context, inspection }, signal)))
    if (preview) { setConfirmation(''); setPreviewAction({ kind: 'import', inspection, preview }) }
  }

  async function confirmPreview() {
    if (!snapshot || !previewAction) return
    const action = previewAction
    const key = `confirm-${action.kind}`
    if (action.preview.confirmationPhrase && confirmation !== action.preview.confirmationPhrase) { setError('Type the exact confirmation phrase shown in the preview.'); return }
    const next = await run(key, async () => {
      if (action.kind === 'profile') {
        const result = await coordinator.execute(key, profileId, snapshot.revision, (context, signal) => adapter.applyProfileMutation({ ...context, mutation: action.mutation, previewId: action.preview.previewId, destructiveConfirmation: action.preview.destructive ? { phrase: confirmation } : undefined }, signal))
        const target = action.mutation.kind === 'delete' ? result.activeProfileId : action.mutation.kind === 'rename' ? profileId : result.affectedProfileId
        await load(target, true)
        return null
      }
      if (action.kind === 'permission') return coordinator.execute(key, profileId, snapshot.revision, (context, signal) => adapter.confirmPermissionGrant({ ...context, intent: action.intent, previewId: action.preview.previewId, confirmationPhrase: confirmation }, signal))
      return coordinator.execute(key, profileId, snapshot.revision, (context, signal) => adapter.applyImport({ ...context, inspection: action.inspection, previewId: action.preview.previewId, confirmationPhrase: confirmation }, signal))
    })
    if (next) sync(next)
    if (next !== undefined) { setPreviewAction(null); setProfileName(''); setNotice(`${action.preview.title} completed.`) }
  }

  async function exportProfile() {
    if (!snapshot) return
    const result = await run('export', () => coordinator.execute('export', profileId, snapshot.revision, (context, signal) => adapter.exportProfile(context, signal)))
    if (result) { setExported(result); onExport?.(result); setNotice('Secret-free profile export prepared for the caller.') }
  }

  function cancelPending() {
    if (busy) coordinator.cancel(busy)
  }

  return (
    <section className="hpr-workspace" aria-labelledby="hpr-workspace-title" aria-describedby="hpr-workspace-summary" aria-busy={busy ? 'true' : 'false'}>
      <header className="hpr-hero">
        <div><small>HERMES PROFILE RUNTIME</small><h2 id="hpr-workspace-title">Profile-scoped settings and tool runtime</h2><p id="hpr-workspace-summary">{readOnly ? 'Verified live profile facts. Change, grant, import, and export controls are intentionally unavailable.' : safeLive ? 'Versioned controls for real profile lifecycle, SOUL, model assignment, and terminal selection. Unsupported surfaces stay hidden.' : 'Versioned, host-neutral controls. No live Hermes operations, credential values, or implicit grants.'}</p></div>
        <span className={statusClass(snapshot?.availability ?? 'unavailable')}>{snapshot?.availability ?? 'loading'}</span>
      </header>

      <aside className="hpr-boundary" aria-label="Terminal boundary">
        <div><strong>Terminal boundary</strong><span>Workbench and Hermes agent execution remain separate.</span></div>
        <dl>
          <div><dt>Native Workbench terminal:</dt><dd>ConPTY-backed developer terminal, outside this module.</dd></div>
          <div><dt>Hermes agent terminal backend:</dt><dd>{readOnly ? 'Live status only; selection is unavailable here.' : 'Tool-execution backend selected below.'}</dd></div>
        </dl>
      </aside>

      {error && <div className="hpr-alert hpr-error" role="alert"><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
      {notice && <div className="hpr-alert hpr-notice" role="status"><span>{notice}</span><button type="button" onClick={() => setNotice(null)}>Dismiss</button></div>}
      {busy && <div className="hpr-pending" role="status"><span>Pending: {busy}</span><button type="button" onClick={cancelPending}>Cancel operation</button></div>}

      {!snapshot ? <div className="hpr-empty"><strong>Profile runtime unavailable</strong><p>{busy ? 'Loading a profile-scoped snapshot…' : 'The adapter did not provide a snapshot.'}</p></div> : <>
        <section className="hpr-panel hpr-profile-panel" aria-labelledby="hpr-profile-heading">
          <header><div><small>{readOnly ? 'INSPECTED PROFILE' : 'ACTIVE PROFILE'}</small><h3 id="hpr-profile-heading">{snapshot.profiles.find((item) => item.id === profileId)?.name ?? profileId}</h3></div><span>revision {snapshot.revision}</span></header>
          <label><span>{readOnly ? 'Inspect profile facts' : 'Select active profile'}</span><select value={profileId} onChange={(event) => void (readOnly ? inspectProfile(event.target.value) : changeActive(event.target.value))} disabled={busy !== null} aria-describedby={readOnly ? 'hpr-read-only-profile-help' : undefined}>{snapshot.profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.name}{profile.isActive ? ' · active' : ''}</option>)}</select></label>
          {readOnly ? <small id="hpr-read-only-profile-help">Selecting a profile reloads read-only facts. It does not change the active Hermes profile.</small> : <form className="hpr-profile-actions" onSubmit={(event) => void previewMutation(event)}>
            <label><span>Operation</span><select value={mutationKind} onChange={(event) => setMutationKind(event.target.value as typeof mutationKind)}><option value="create">Create</option><option value="clone">Clone current</option><option value="rename">Rename current</option></select></label>
            <label><span>Profile name</span><input value={profileName} maxLength={128} onChange={(event) => setProfileName(event.target.value)} /></label>
            <button type="submit" disabled={busy !== null || !profileName.trim()}>Preview</button>
            <button type="button" className="danger" onClick={() => void previewDelete()} disabled={busy !== null || snapshot.profiles.find((item) => item.id === profileId)?.isDeleteProtected}>Preview delete</button>
          </form>}
        </section>

        <section className="hpr-panel" aria-labelledby="hpr-documents-heading">
          <header><div><small>{safeLive ? 'PROFILE IDENTITY' : 'CALLER-OWNED SAVE'}</small><h3 id="hpr-documents-heading">{safeLive ? 'SOUL identity document' : 'Persona, soul, and context'}</h3></div>{dirtyDocuments && <span className="dirty">Unsaved</span>}</header>
          <div className="hpr-document-grid">{visibleDocuments.map((document) => <label key={document.kind}><span>{document.kind}</span><textarea readOnly={readOnly} value={documents[document.kind] ?? ''} maxLength={document.maximumCharacters} onChange={(event) => setDocuments((current) => ({ ...current, [document.kind]: event.target.value }))} /><small>{(documents[document.kind] ?? '').length.toLocaleString()} / {document.maximumCharacters.toLocaleString()}</small>{readOnly ? null : <button type="button" onClick={() => void saveDocument(document.kind)} disabled={busy !== null || documents[document.kind] === document.text}>Save {document.kind}</button>}</label>)}</div>
        </section>

        <section className="hpr-panel" aria-labelledby="hpr-intents-heading">
          <header><div><small>{safeLive ? 'MODEL ASSIGNMENT' : 'PROFILE INTENTS'}</small><h3 id="hpr-intents-heading">{safeLive ? 'Default model for future sessions' : 'Model and workspace targets'}</h3></div>{dirtyIntent && <span className="dirty">Unsaved</span>}</header>
          <div className="hpr-field-grid">
            <label><span>{safeLive ? 'Provider and model ID' : 'Model ID intent'}</span><input readOnly={readOnly} value={intent.modelId ?? ''} maxLength={256} onChange={(event) => setIntent((current) => ({ ...current, modelId: event.target.value || null }))} /></label>
            {safeLive ? null : <><label><span>Project path intent</span><input readOnly={readOnly} value={intent.projectPath ?? ''} maxLength={2048} onChange={(event) => setIntent((current) => ({ ...current, projectPath: event.target.value || null }))} /></label>
              <label><span>Worktree path intent</span><input readOnly={readOnly} value={intent.worktreePath ?? ''} maxLength={2048} onChange={(event) => setIntent((current) => ({ ...current, worktreePath: event.target.value || null }))} /></label>
              <label><span>Intent note</span><input readOnly={readOnly} value={intent.note} maxLength={2048} onChange={(event) => setIntent((current) => ({ ...current, note: event.target.value }))} /></label></>}
          </div>{readOnly ? null : <button type="button" onClick={() => void saveIntent()} disabled={busy !== null || !dirtyIntent}>{safeLive ? 'Save model assignment' : 'Save profile intents'}</button>}
        </section>

        <section className="hpr-panel" aria-labelledby="hpr-terminal-heading">
          <header><div><small>HERMES AGENT TERMINAL</small><h3 id="hpr-terminal-heading">Backend selection and status</h3></div><span>Not Workbench ConPTY</span></header>
          <div className="hpr-card-grid">{snapshot.terminalBackends.map((backend) => <article key={backend.id}><header><strong>{backend.label}</strong><span className={statusClass(backend.availability)}>{backend.availability}</span></header><p>{backend.description}</p><small>{backend.detail}</small>{readOnly ? null : <button type="button" disabled={busy !== null || backend.selected || backend.availability === 'unavailable'} onClick={async () => { const next = await run('terminal-backend', () => coordinator.execute('terminal-backend', profileId, snapshot.revision, (context, signal) => adapter.selectTerminalBackend({ ...context, backendId: backend.id }, signal))); if (next) sync(next) }}>{backend.selected ? 'Selected Hermes backend' : 'Select Hermes backend'}</button>}</article>)}</div>
        </section>

        {safeLive ? null : <section className="hpr-panel" aria-labelledby="hpr-computer-use-heading">
          <header><div><small>COMPUTER USE</small><h3 id="hpr-computer-use-heading">Status and permission intents</h3></div><span className={statusClass(snapshot.computerUse.availability)}>{snapshot.computerUse.availability}</span></header>
          <p>{snapshot.computerUse.detail}</p><div className="hpr-card-grid">{snapshot.computerUse.permissions.map((permission) => <article key={permission.id}><header><strong>{permission.label}</strong><span className={statusClass(permission.state)}>{permission.state}</span></header><p>{permission.description}</p><small>{permission.disposition}: {permission.detail}</small>{!readOnly && permission.disposition === 'manageable' && permission.state !== 'granted' && <button type="button" onClick={() => void previewPermission(permission.id)} disabled={busy !== null}>Preview permission request</button>}</article>)}</div>
        </section>}

        {safeLive ? null : <section className="hpr-panel" aria-labelledby="hpr-providers-heading">
          <header><div><small>PROVIDERS</small><h3 id="hpr-providers-heading">OAuth, credential pool, and endpoint status</h3></div><span>No secret values</span></header>
          <div className="hpr-card-grid">{snapshot.providers.map((provider) => <article key={provider.id}><header><strong>{provider.label}</strong><span className={statusClass(provider.disposition)}>{provider.disposition}</span></header><dl><dt>OAuth</dt><dd>{provider.oauth}</dd><dt>Credential pool</dt><dd>{provider.credentialPool} · {provider.configuredCredentialSlots} slot(s)</dd><dt>Custom endpoint</dt><dd>{provider.customEndpoint.state}{provider.customEndpoint.origin ? ` · ${provider.customEndpoint.origin}` : ''}</dd></dl><small>{provider.detail}</small></article>)}</div>
        </section>}

        {safeLive ? null : <section className="hpr-panel" aria-labelledby="hpr-configuration-heading">
          <header><div><small>SCHEMA-DRIVEN</small><h3 id="hpr-configuration-heading">Non-secret configuration</h3></div>{dirtyConfiguration && <span className="dirty">Unsaved</span>}</header>
          <div className="hpr-field-grid">{snapshot.configurationSchema.map((field) => <label key={field.key}><span>{field.label} <i>{field.disposition}</i></span>{field.kind === 'boolean' ? <input type="checkbox" checked={configuration[field.key] === true} disabled={readOnly || field.disposition !== 'manageable'} onChange={(event) => setConfiguration((current) => ({ ...current, [field.key]: event.target.checked }))} /> : field.kind === 'enum' ? <select value={String(configuration[field.key] ?? '')} disabled={readOnly || field.disposition !== 'manageable'} onChange={(event) => setConfiguration((current) => ({ ...current, [field.key]: event.target.value }))}>{field.options?.map((option) => <option key={option}>{option}</option>)}</select> : <input readOnly={readOnly} type={field.kind === 'integer' ? 'number' : 'text'} value={String(configuration[field.key] ?? '')} min={field.minimum} max={field.maximum} maxLength={field.maximumLength} disabled={field.disposition !== 'manageable'} onChange={(event) => setConfiguration((current) => ({ ...current, [field.key]: field.kind === 'integer' ? Number(event.target.value) : event.target.value }))} />}<small>{field.description}</small></label>)}</div>{readOnly ? null : <button type="button" onClick={() => void saveConfiguration()} disabled={busy !== null || !dirtyConfiguration}>Save non-secret configuration</button>}
        </section>}

        {readOnly || safeLive ? null : <section className="hpr-panel" aria-labelledby="hpr-transfer-heading">
          <header><div><small>BOUNDED TRANSFER</small><h3 id="hpr-transfer-heading">Import and export</h3></div><span>JSON · 256 KiB max</span></header>
          <label><span>Untrusted profile JSON</span><textarea className="hpr-import" value={importText} maxLength={262144} onChange={(event) => setImportText(event.target.value)} placeholder="Paste hermes-profile-runtime/v1 JSON. Text is never rendered as HTML." /></label>
          <div className="hpr-button-row"><button type="button" onClick={() => void previewImport()} disabled={busy !== null || !importText}>Validate and preview import</button><button type="button" onClick={() => void exportProfile()} disabled={busy !== null}>Prepare secret-free export</button></div>
          {exported && <label><span>{exported.fileName} · {exported.byteCount.toLocaleString()} bytes</span><textarea readOnly value={exported.text} /></label>}
        </section>}
      </>}

      {!readOnly && previewAction && <div className="hpr-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !busy) setPreviewAction(null) }}><section className="hpr-dialog" role="dialog" aria-modal="true" aria-labelledby="hpr-preview-title">
        <header><div><small>EXPLICIT PREVIEW</small><h3 id="hpr-preview-title">{previewAction.preview.title}</h3></div><button type="button" onClick={() => setPreviewAction(null)} disabled={busy !== null} aria-label="Close preview">Close</button></header>
        <p>{previewAction.preview.summary}</p><ul>{previewAction.preview.consequences.map((item) => <li key={item}>{item}</li>)}</ul>
        <dl><dt>Profile</dt><dd>{previewAction.preview.profileId}</dd><dt>Correlation preview</dt><dd>{previewAction.preview.previewId}</dd><dt>Destructive</dt><dd>{previewAction.preview.destructive ? 'yes' : 'no'}</dd></dl>
        {previewAction.preview.confirmationPhrase && <label><span>Type <code>{previewAction.preview.confirmationPhrase}</code> to confirm</span><input autoFocus value={confirmation} onChange={(event) => setConfirmation(event.target.value)} /></label>}
        <footer><button type="button" onClick={() => setPreviewAction(null)} disabled={busy !== null}>Cancel</button><button type="button" className={previewAction.preview.destructive ? 'danger' : ''} onClick={() => void confirmPreview()} disabled={busy !== null || Boolean(previewAction.preview.confirmationPhrase && confirmation !== previewAction.preview.confirmationPhrase)}>Confirm typed intent</button></footer>
      </section></div>}

      <footer className="hpr-contract">{HERMES_PROFILE_RUNTIME_CONTRACT} · profile {profileId} · renderer receives no credential values</footer>
    </section>
  )
}
