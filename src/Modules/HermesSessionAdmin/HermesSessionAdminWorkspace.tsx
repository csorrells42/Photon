import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from 'react'
import {
  HERMES_SESSION_ADMIN_CONTRACT_VERSION,
  sessionAdminBounds,
  type DeletePreviewData,
  type DescendantNode,
  type DescendantTree,
  type ExportSessionsData,
  type HermesSessionAdminAdapter,
  type ImportedSessionText,
  type ImportValidationData,
  type PrunePreviewData,
  type SessionAdminResult,
  type SessionAdminOperation,
  type SessionAdminSession,
  type SessionStatisticsData,
} from './contracts'
import { DuplicatePendingOperationError, SessionAdminOperationCoordinator, restoreFocus } from './OperationCoordinator'
import { SessionAdminValidationError, statusNotice, validateProfileId, validateResultProfile } from './validation'
import './HermesSessionAdminWorkspace.css'

export interface HermesSessionAdminWorkspaceProps {
  profileId: string
  adapter: HermesSessionAdminAdapter
}

type DialogState =
  | { kind: 'delete'; preview: DeletePreviewData }
  | { kind: 'prune'; preview: PrunePreviewData }
  | { kind: 'replace-model'; session: SessionAdminSession; nextModel: string }
  | { kind: 'clear-model'; session: SessionAdminSession }
  | null

function errorText(reason: unknown): string {
  if (reason instanceof SessionAdminValidationError) return reason.message
  if (reason instanceof Error) return reason.message
  return 'Session administration failed for an unknown reason.'
}

export function AccessibleDialog({ title, onClose, children, busy = false }: {
  title: string
  onClose(): void
  children: ReactNode
  busy?: boolean
}) {
  const dialogRef = useRef<HTMLElement>(null)
  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    dialogRef.current?.focus()
    return () => restoreFocus(previous)
  }, [])

  function handleKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.key === 'Escape' && !busy) { event.preventDefault(); onClose(); return }
    if (event.key !== 'Tab') return
    const focusable = [...(dialogRef.current?.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
    ) ?? [])]
    if (!focusable.length) { event.preventDefault(); dialogRef.current?.focus(); return }
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }

  return <div className="session-admin-backdrop" role="presentation" onMouseDown={(event) => {
    if (event.target === event.currentTarget && !busy) onClose()
  }}>
    <section ref={dialogRef} className="session-admin-dialog" role="dialog" aria-modal="true" aria-label={title} tabIndex={-1} onKeyDown={handleKeyDown}>
      <header><h2>{title}</h2><button type="button" onClick={onClose} disabled={busy} aria-label={`Close ${title}`}>Close</button></header>
      {children}
    </section>
  </div>
}

export function ImportedTextPreview({ sessions }: { sessions: ImportedSessionText[] }) {
  return <div className="session-admin-import-preview" aria-label="Validated import text preview">
    {sessions.map((session) => <article key={session.requestedSessionId}>
      <h4>{session.title}</h4>
      <p><code>{session.requestedSessionId}</code> · {session.messages.length} messages</p>
      {session.messages.slice(0, 3).map((message, index) => <pre key={`${session.requestedSessionId}:${index}`}>{message.role}: {message.text}</pre>)}
    </article>)}
  </div>
}

function DescendantList({ nodes }: { nodes: DescendantNode[] }) {
  if (!nodes.length) return <p>No descendants reported.</p>
  return <ul>{nodes.map((node) => <li key={node.sessionId}>
    <span>{node.title}</span> <code>{node.sessionId}</code>
    {node.children.length > 0 && <DescendantList nodes={node.children} />}
  </li>)}</ul>
}

function Statistic({ label, statistic }: { label: string; statistic: SessionStatisticsData[keyof SessionStatisticsData] }) {
  return <article><strong>{statistic.value === null ? 'Unavailable' : statistic.value.toLocaleString()}</strong><span>{label}</span><small>{statistic.quality} · {statistic.detail}</small></article>
}

const operationLabels: Partial<Record<SessionAdminOperation, string>> = {
  list: 'Session inventory',
  descendants: 'Latest descendant trail',
  export: 'Text-only export',
  statistics: 'Storage statistics',
}

export function HermesSessionAdminWorkspace({ profileId, adapter }: HermesSessionAdminWorkspaceProps) {
  const [sessions, setSessions] = useState<SessionAdminSession[]>([])
  const [statistics, setStatistics] = useState<SessionStatisticsData | null>(null)
  const [selected, setSelected] = useState<Set<string>>(() => new Set())
  const [trees, setTrees] = useState<Record<string, DescendantTree>>({})
  const [pending, setPending] = useState<Record<string, string>>({})
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dialog, setDialog] = useState<DialogState>(null)
  const [confirmationText, setConfirmationText] = useState('')
  const [branchSource, setBranchSource] = useState('')
  const [branchId, setBranchId] = useState('')
  const [branchTitle, setBranchTitle] = useState('')
  const [branchMode, setBranchMode] = useState<'branch' | 'fork'>('fork')
  const [pruneBefore, setPruneBefore] = useState('0')
  const [includeArchived, setIncludeArchived] = useState(false)
  const [importText, setImportText] = useState('')
  const [importValidation, setImportValidation] = useState<ImportValidationData | null>(null)
  const [exportResult, setExportResult] = useState<ExportSessionsData | null>(null)
  const [modelSessionId, setModelSessionId] = useState('')
  const [modelText, setModelText] = useState('')
  const coordinator = useRef(new SessionAdminOperationCoordinator())
  const sequence = useRef(0)

  const activeProfile = useMemo(() => {
    try { return validateProfileId(profileId) }
    catch { return '' }
  }, [profileId])
  const supportsOperation = useCallback((operation: SessionAdminOperation) => (
    !adapter.supportedOperations || adapter.supportedOperations.includes(operation)
  ), [adapter])
  const hasBoundedCapabilities = Boolean(adapter.supportedOperations)
  const supportedOperationLabels = adapter.supportedOperations?.map((operation) => operationLabels[operation] ?? operation).join(' · ')

  const runOperation = useCallback(<T,>(
    key: string,
    invoke: (scope: { profileId: string; correlationId: string }, signal: AbortSignal) => Promise<SessionAdminResult<T>>,
    accept?: (data: T, result: SessionAdminResult<T>) => void,
  ) => {
    if (!activeProfile) { setError('A valid profile identity is required.'); return }
    if (adapter.contractVersion !== HERMES_SESSION_ADMIN_CONTRACT_VERSION) { setError('Session administration adapter version mismatch.'); return }
    sequence.current += 1
    const correlationId = `session-admin:${activeProfile}:${sequence.current}`
    let operation
    try { operation = coordinator.current.begin(key, correlationId) }
    catch (reason) {
      setError(reason instanceof DuplicatePendingOperationError ? reason.message : errorText(reason)); return
    }
    setPending((current) => ({ ...current, [key]: correlationId }))
    setError(null)
    const release = () => {
      if (!coordinator.current.finish(key, correlationId)) return false
      setPending((current) => {
        if (current[key] !== correlationId) return current
        const next = { ...current }
        delete next[key]
        return next
      })
      return true
    }
    void invoke({ profileId: activeProfile, correlationId }, operation.controller.signal).then((rawResult) => {
      if (!coordinator.current.isCurrent(key, correlationId)) return
      const result = validateResultProfile({ profileId: activeProfile, correlationId }, rawResult)
      if (!release()) return
      const genericNotice = statusNotice(result.status)
      if (result.status === 'error') setError(result.notices.join(' ') || genericNotice)
      else if (result.status !== 'success') setNotice([...result.notices, genericNotice].filter(Boolean).join(' '))
      else if (result.notices.length) setNotice(result.notices.join(' '))
      if (result.data !== null && (result.status === 'success' || result.status === 'partial')) accept?.(result.data, result)
    }).catch((reason) => { if (release()) setError(errorText(reason)) })
  }, [activeProfile, adapter])

  const refresh = useCallback(() => {
    runOperation('list', (scope, signal) => adapter.listSessions({ ...scope, limit: sessionAdminBounds.maxSessions }, signal), (data) => {
      setSessions(data.sessions); if (data.truncated) setNotice(`Showing ${data.sessions.length} of ${data.total ?? 'an unknown number of'} sessions.`)
    })
    runOperation('statistics', (scope, signal) => adapter.statistics(scope, signal), setStatistics)
  }, [adapter, runOperation])

  useEffect(() => { refresh(); return () => coordinator.current.cancelAll() }, [refresh])

  const modelSession = sessions.find((session) => session.sessionId === modelSessionId) ?? null

  function toggleSelection(sessionId: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(sessionId)) next.delete(sessionId)
      else if (next.size < sessionAdminBounds.maxSelection) next.add(sessionId)
      else setError(`Selection is limited to ${sessionAdminBounds.maxSelection} sessions.`)
      return next
    })
  }

  function loadDescendants(sessionId: string) {
    runOperation(`descendants:${sessionId}`, (scope, signal) => adapter.descendants({
      ...scope, rootSessionId: sessionId, maxDepth: sessionAdminBounds.maxDescendantDepth, maxNodes: sessionAdminBounds.maxDescendants,
    }, signal), (data) => setTrees((current) => ({ ...current, [sessionId]: data })))
  }

  function createBranch() {
    runOperation('branch', (scope, signal) => adapter.createBranch({
      ...scope, sourceSessionId: branchSource, newSessionId: branchId, title: branchTitle, mode: branchMode,
    }, signal), (data) => {
      setSessions((current) => [data.created, ...current]); setNotice(`${branchMode} ${data.created.sessionId} created.`)
      setBranchId(''); setBranchTitle('')
    })
  }

  function previewDelete() {
    runOperation('delete-preview', (scope, signal) => adapter.previewDelete({
      ...scope, sessionIds: [...selected], includeDescendants: true,
    }, signal), (data) => { setConfirmationText(''); setDialog({ kind: 'delete', preview: data }) })
  }

  function commitDelete(preview: DeletePreviewData) {
    runOperation('delete-commit', (scope, signal) => adapter.commitDelete({ ...scope, previewToken: preview.previewToken, confirmation: 'delete-reviewed' }, signal), (data) => {
      setSessions((current) => current.filter((session) => !data.deletedIds.includes(session.sessionId)))
      setSelected(new Set()); setDialog(null); setConfirmationText('')
      setNotice(`${data.deletedIds.length} sessions deleted${data.failed.length ? `; ${data.failed.length} failed` : ''}.`)
    })
  }

  function previewPrune() {
    runOperation('prune-preview', (scope, signal) => adapter.previewPrune({
      ...scope, criteria: { inactiveBefore: Number(pruneBefore), includeArchived, maxDelete: sessionAdminBounds.maxDelete },
    }, signal), (data) => { setConfirmationText(''); setDialog({ kind: 'prune', preview: data }) })
  }

  function commitPrune(preview: PrunePreviewData) {
    runOperation('prune-commit', (scope, signal) => adapter.commitPrune({ ...scope, previewToken: preview.previewToken, confirmation: 'prune-reviewed' }, signal), (data) => {
      setSessions((current) => current.filter((session) => !data.deletedIds.includes(session.sessionId)))
      setSelected((current) => new Set([...current].filter((id) => !data.deletedIds.includes(id))))
      setDialog(null); setConfirmationText(''); setNotice(`${data.deletedIds.length} prune candidates deleted.`)
    })
  }

  function validateImport() {
    setImportValidation(null)
    runOperation('import-validate', (scope, signal) => adapter.validateImport({ ...scope, untrustedJsonText: importText }, signal), setImportValidation)
  }

  function commitImport(validation: ImportValidationData) {
    if (!validation.validationToken) return
    runOperation('import-commit', (scope, signal) => adapter.commitImport({ ...scope, validationToken: validation.validationToken! }, signal), (data) => {
      setNotice(`${data.importedIds.length} sessions imported${data.skipped.length ? `; ${data.skipped.length} skipped` : ''}.`)
      setImportText(''); setImportValidation(null); refresh()
    })
  }

  function exportSelected() {
    setExportResult(null)
    runOperation('export', (scope, signal) => adapter.exportSessions({ ...scope, sessionIds: [...selected] }, signal), setExportResult)
  }

  function requestModelSet(session: SessionAdminSession, nextModel: string) {
    const model = nextModel.trim()
    if (session.modelLock && session.modelLock !== model) { setDialog({ kind: 'replace-model', session, nextModel: model }); return }
    commitModelSet(session, model, false)
  }

  function commitModelSet(session: SessionAdminSession, model: string, confirmReplace: boolean) {
    runOperation(`model-set:${session.sessionId}`, (scope, signal) => adapter.setModelLock({
      ...scope, sessionId: session.sessionId, model, expectedCurrentModel: session.modelLock, confirmReplace,
    }, signal), (data) => {
      setSessions((current) => current.map((row) => row.sessionId === data.sessionId ? { ...row, modelLock: data.model } : row))
      setDialog(null); setNotice(`Model lock ${data.model ? 'set' : 'cleared'} for ${data.sessionId}.`)
    })
  }

  function commitModelClear(session: SessionAdminSession) {
    if (!session.modelLock) return
    runOperation(`model-clear:${session.sessionId}`, (scope, signal) => adapter.clearModelLock({
      ...scope, sessionId: session.sessionId, expectedCurrentModel: session.modelLock!, confirmation: 'clear-model-lock',
    }, signal), (data) => {
      setSessions((current) => current.map((row) => row.sessionId === data.sessionId ? { ...row, modelLock: null } : row))
      setDialog(null); setNotice(`Model lock cleared for ${data.sessionId}.`)
    })
  }

  return <section className="session-admin-workspace" aria-labelledby="session-admin-heading">
    <header className="session-admin-heading session-admin-hero">
      <div><small>WORKBENCH LAB · {HERMES_SESSION_ADMIN_CONTRACT_VERSION}{hasBoundedCapabilities ? ' · LIVE READ-ONLY CONNECTION' : ' · FULL ADMIN ADAPTER'}</small><h1 id="session-admin-heading">Session administration</h1><p>Inspect the conversation records belonging to one profile, follow the newest continuation, export selected text, and understand what Hermes can report. Search and pinning remain in Hermes Sessions.</p></div>
      <button type="button" onClick={refresh} disabled={Boolean(pending.list)}>Refresh</button>
    </header>

    <section className="session-admin-boundary" aria-label="Session administration scope">
      <div><small>ACTIVE PROFILE</small><strong><code>{activeProfile || 'Invalid profile'}</code></strong><span>Every request and result is checked against this identity.</span></div>
      <div><small>WHAT THIS LAB DOES</small><strong>{hasBoundedCapabilities ? 'Observe and export verified Hermes data' : 'Preview and administer isolated session data'}</strong><span>{hasBoundedCapabilities ? 'This connection intentionally does not claim that Hermes can delete, import, branch, prune, or set model locks.' : 'Destructive work is previewed and separately confirmed before it changes a session.'}</span></div>
    </section>
    {hasBoundedCapabilities && <div className="session-admin-capabilities" role="note"><strong>Connected now</strong><span>{supportedOperationLabels}</span><small>Unavailable write controls are not shown. They will appear only when Hermes exposes a verified route.</small></div>}
    {error && <div className="session-admin-error" role="alert"><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
    {notice && <div className="session-admin-notice" role="status"><span>{notice}</span><button type="button" onClick={() => setNotice(null)}>Dismiss</button></div>}
    {Object.keys(pending).length > 0 && <aside className="session-admin-pending" aria-label="Pending session operations"><strong>Pending</strong>{Object.entries(pending).map(([key, correlation]) => <span key={key}><code>{key}</code><small>{correlation}</small><button type="button" onClick={() => coordinator.current.cancel(key)}>Cancel</button></span>)}</aside>}

    <section className="session-admin-panel"><header><div><small>SESSION INVENTORY</small><h2>Sessions</h2><p>Select up to {sessionAdminBounds.maxSelection} records to export. {hasBoundedCapabilities ? 'Nothing on this connected surface changes a session.' : 'Destructive actions always show a preview first.'}</p></div><span>{selected.size} selected</span></header>
      <div className="session-admin-actions">{supportsOperation('delete-preview') && <button type="button" disabled={!selected.size || Boolean(pending['delete-preview'])} onClick={previewDelete}>Preview delete</button>}{supportsOperation('export') && <button type="button" disabled={!selected.size || Boolean(pending.export)} onClick={exportSelected}>Request export</button>}<button type="button" disabled={!selected.size} onClick={() => setSelected(new Set())}>Clear selection</button></div>
      <div className="session-admin-session-list">{sessions.map((session) => <article key={session.sessionId}>
        <label><input type="checkbox" checked={selected.has(session.sessionId)} onChange={() => toggleSelection(session.sessionId)} /><span><strong>{session.title}</strong><code>{session.sessionId}</code></span></label>
        <dl><div><dt>State</dt><dd>{session.lifecycle}</dd></div><div><dt>Messages</dt><dd>{session.messageCount ?? 'unavailable'}</dd></div><div><dt>Model lock</dt><dd>{session.modelLock ?? 'none'}</dd></div></dl>
        <div className="session-admin-row-actions">{supportsOperation('descendants') && <button type="button" onClick={() => loadDescendants(session.sessionId)}>Newest continuation</button>}{supportsOperation('branch') && <button type="button" onClick={() => { setBranchSource(session.sessionId); setBranchTitle(`${session.title} fork`) }}>Branch/fork</button>}{supportsOperation('model-lock-set') && <button type="button" onClick={() => { setModelSessionId(session.sessionId); setModelText(session.modelLock ?? '') }}>Model lock</button>}</div>
        {trees[session.sessionId] && <div className="session-admin-tree"><strong>Descendants · {trees[session.sessionId].returned}{trees[session.sessionId].truncated ? ' (truncated)' : ''}</strong><DescendantList nodes={trees[session.sessionId].nodes} /></div>}
      </article>)}</div>
      {!sessions.length && !pending.list && <p className="session-admin-empty">No sessions were reported for this profile.</p>}
    </section>

    {!hasBoundedCapabilities && <div className="session-admin-grid">
      <section className="session-admin-panel"><header><div><h2>Create branch or fork</h2><p>Creates one child and preserves the owning profile.</p></div></header>
        <label>Source session<input value={branchSource} onChange={(event) => setBranchSource(event.target.value)} /></label>
        <label>New session ID<input value={branchId} onChange={(event) => setBranchId(event.target.value)} /></label>
        <label>Title<input value={branchTitle} onChange={(event) => setBranchTitle(event.target.value)} /></label>
        <label>Mode<select value={branchMode} onChange={(event) => setBranchMode(event.target.value as 'branch' | 'fork')}><option value="fork">Fork</option><option value="branch">Branch</option></select></label>
        <button type="button" title={!supportsOperation('branch') ? 'Branch and fork mutations are not exposed by the verified live adapter.' : undefined} onClick={createBranch} disabled={!supportsOperation('branch') || !branchSource || !branchId || !branchTitle || Boolean(pending.branch)}>Create {branchMode}</button>
      </section>

      <section className="session-admin-panel"><header><div><h2>Prune ended sessions</h2><p>Active sessions are excluded. Preview and a separate typed confirmation are required.</p></div></header>
        <label>Inactive before epoch milliseconds<input inputMode="numeric" value={pruneBefore} onChange={(event) => setPruneBefore(event.target.value)} /></label>
        <label className="session-admin-check"><input type="checkbox" checked={includeArchived} onChange={(event) => setIncludeArchived(event.target.checked)} /> Include archived sessions</label>
        <button type="button" title={!supportsOperation('prune-preview') ? 'Prune preview is not exposed by the verified live adapter.' : undefined} onClick={previewPrune} disabled={!supportsOperation('prune-preview') || !Number.isFinite(Number(pruneBefore)) || Boolean(pending['prune-preview'])}>Preview prune</button>
      </section>

      <section className="session-admin-panel"><header><div><h2>Per-session model lock</h2><p>Replacement and clearing require confirmation. Stale current locks are rejected.</p></div></header>
        <label>Session<select value={modelSessionId} onChange={(event) => { const session = sessions.find((row) => row.sessionId === event.target.value); setModelSessionId(event.target.value); setModelText(session?.modelLock ?? '') }}><option value="">Choose a session</option>{sessions.map((session) => <option key={session.sessionId} value={session.sessionId}>{session.title}</option>)}</select></label>
        <label>Provider/model lock<input value={modelText} onChange={(event) => setModelText(event.target.value)} placeholder="provider/model" /></label>
        <div className="session-admin-actions"><button type="button" title={!supportsOperation('model-lock-set') ? 'Model-lock mutation is not exposed by the verified live adapter.' : undefined} disabled={!supportsOperation('model-lock-set') || !modelSession || !modelText.trim()} onClick={() => modelSession && requestModelSet(modelSession, modelText)}>Set lock</button><button type="button" title={!supportsOperation('model-lock-clear') ? 'Model-lock clearing is not exposed by the verified live adapter.' : undefined} disabled={!supportsOperation('model-lock-clear') || !modelSession?.modelLock} onClick={() => modelSession && setDialog({ kind: 'clear-model', session: modelSession })}>Clear lock</button></div>
      </section>
    </div>}

    {supportsOperation('import-validate') && <section className="session-admin-panel"><header><div><small>VALIDATED IMPORT</small><h2>Import untrusted JSON</h2><p>Bounded to {sessionAdminBounds.maxImportBytes.toLocaleString()} bytes, {sessionAdminBounds.maxImportSessions} sessions, and {sessionAdminBounds.maxMessagesPerImportedSession} messages per session. Content is rendered as text, never HTML.</p></div></header>
      <label>Import payload<textarea rows={8} value={importText} onChange={(event) => setImportText(event.target.value)} spellCheck={false} /></label>
      <div className="session-admin-actions"><button type="button" title={!supportsOperation('import-validate') ? 'Import is not exposed by the verified live adapter.' : undefined} onClick={validateImport} disabled={!supportsOperation('import-validate') || !importText || Boolean(pending['import-validate'])}>Validate import</button>{importValidation?.valid && importValidation.validationToken && <button type="button" onClick={() => commitImport(importValidation)} disabled={!supportsOperation('import-commit') || Boolean(pending['import-commit'])}>Import validated sessions</button>}</div>
      {importValidation && <div className={importValidation.valid ? 'session-admin-validation valid' : 'session-admin-validation invalid'}><strong>{importValidation.valid ? 'Validation passed' : 'Validation failed'}</strong><span>{importValidation.byteCount.toLocaleString()} bytes · {importValidation.sessions.length} bounded sessions</span>{importValidation.errors.length > 0 && <ul>{importValidation.errors.map((message, index) => <li key={index}>{message}</li>)}</ul>}<ImportedTextPreview sessions={importValidation.sessions} /></div>}
    </section>}

    {exportResult && <section className="session-admin-panel"><header><div><small>EXPORT READY</small><h2>Export result</h2><p>The adapter returned text only. Copy or save it through the owning Workbench surface; no file was automatically written.</p></div><code>{exportResult.fileName}</code></header><label>Exported JSON<textarea rows={8} readOnly value={exportResult.contentText} /></label></section>}

    <section className="session-admin-panel"><header><div><small>REPORTED TOTALS</small><h2>Honest statistics</h2><p>Unavailable values stay unavailable; partial values retain their quality label. These are storage records, not a claim about active runtime agents.</p></div></header>{statistics ? <div className="session-admin-statistics"><Statistic label="Total" statistic={statistics.total} /><Statistic label="Active" statistic={statistics.active} /><Statistic label="Ended" statistic={statistics.ended} /><Statistic label="Archived" statistic={statistics.archived} /><Statistic label="Messages" statistic={statistics.messages} /><Statistic label="Storage bytes" statistic={statistics.storageBytes} /></div> : <p className="session-admin-empty">Statistics are unavailable or still pending.</p>}</section>

    {dialog?.kind === 'delete' && <AccessibleDialog title="Confirm session deletion" onClose={() => setDialog(null)} busy={Boolean(pending['delete-commit'])}><p>{dialog.preview.resolvedIds.length} sessions will be deleted, including {dialog.preview.descendantCount} descendants. {dialog.preview.activeIds.length} are active. {dialog.preview.truncated && 'The preview was truncated; commit is blocked.'}</p><ul>{dialog.preview.resolvedIds.map((id) => <li key={id}><code>{id}</code></li>)}</ul><label>Type DELETE to confirm<input value={confirmationText} onChange={(event) => setConfirmationText(event.target.value)} /></label><footer><button type="button" onClick={() => setDialog(null)}>Cancel</button><button type="button" disabled={confirmationText !== 'DELETE' || dialog.preview.truncated} onClick={() => commitDelete(dialog.preview)}>Delete reviewed sessions</button></footer></AccessibleDialog>}
    {dialog?.kind === 'prune' && <AccessibleDialog title="Confirm session prune" onClose={() => setDialog(null)} busy={Boolean(pending['prune-commit'])}><p>{dialog.preview.candidateIds.length} of {dialog.preview.totalMatching} matching sessions are in this bounded preview. {dialog.preview.activeExcluded} active sessions were excluded.</p><ul>{dialog.preview.candidateIds.map((id) => <li key={id}><code>{id}</code></li>)}</ul><label>Type PRUNE to confirm<input value={confirmationText} onChange={(event) => setConfirmationText(event.target.value)} /></label><footer><button type="button" onClick={() => setDialog(null)}>Cancel</button><button type="button" disabled={confirmationText !== 'PRUNE'} onClick={() => commitPrune(dialog.preview)}>Prune reviewed sessions</button></footer></AccessibleDialog>}
    {dialog?.kind === 'replace-model' && <AccessibleDialog title="Confirm model-lock replacement" onClose={() => setDialog(null)} busy={Boolean(pending[`model-set:${dialog.session.sessionId}`])}><p>Replace <code>{dialog.session.modelLock}</code> with <code>{dialog.nextModel}</code> for <code>{dialog.session.sessionId}</code>?</p><footer><button type="button" onClick={() => setDialog(null)}>Cancel</button><button type="button" onClick={() => commitModelSet(dialog.session, dialog.nextModel, true)}>Replace model lock</button></footer></AccessibleDialog>}
    {dialog?.kind === 'clear-model' && <AccessibleDialog title="Confirm model-lock removal" onClose={() => setDialog(null)} busy={Boolean(pending[`model-clear:${dialog.session.sessionId}`])}><p>Clear <code>{dialog.session.modelLock}</code> from <code>{dialog.session.sessionId}</code>?</p><footer><button type="button" onClick={() => setDialog(null)}>Cancel</button><button type="button" onClick={() => commitModelClear(dialog.session)}>Clear model lock</button></footer></AccessibleDialog>}
  </section>
}
