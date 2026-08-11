import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ExternalLink,
  Globe2,
  KeyRound,
  LoaderCircle,
  Package,
  Pencil,
  Plus,
  Power,
  RefreshCw,
  Search,
  Server,
  ShieldCheck,
  ShieldQuestion,
  TerminalSquare,
  Trash2,
  X,
  Zap,
} from 'lucide-react'
import {
  HERMES_MCP_ADAPTER_VERSION,
  hermesMcpAdapter,
} from './HermesMcpAdapter'
import { HermesMcpEditor } from '../HermesMcpEditor/HermesMcpEditor'
import { HermesMcpLiveEditorController } from '../HermesMcpEditor/HermesMcpLiveEditorController'
import { HERMES_MCP_EDITOR_CONTRACT_VERSION } from '../HermesMcpEditor/HermesMcpEditorContract'
import type {
  HermesMcpCatalogEntry,
  HermesMcpServer,
  HermesMcpSnapshot,
  HermesMcpTestResult,
} from './HermesMcpAdapter'
import './HermesMcpWorkspace.css'

type View = 'configured' | 'catalog'
type Dialog =
  | { kind: 'create' }
  | { kind: 'install'; entry: HermesMcpCatalogEntry }
  | { kind: 'remove'; server: HermesMcpServer }
  | null

type CreateDraft = {
  name: string
  transport: 'http' | 'stdio'
  url: string
  auth: 'none' | 'oauth'
  command: string
  args: string
}

const emptyDraft: CreateDraft = {
  name: '', transport: 'http', url: '', auth: 'none', command: '', args: '',
}

function isHttpUrl(value: string) {
  try {
    const parsed = new URL(value)
    return parsed.protocol === 'http:' || parsed.protocol === 'https:'
  } catch { return false }
}

function parseArgs(value: string) {
  return value.split(/[\s,]+/).map((item) => item.trim()).filter(Boolean)
}

function runtimeDescription(entry: HermesMcpCatalogEntry) {
  if (entry.transport === 'http') return entry.url || 'Remote endpoint supplied by the catalog'
  return [entry.command, ...entry.args].filter(Boolean).join(' ') || 'Local stdio command supplied by the catalog'
}

function serverDescription(server: HermesMcpServer) {
  return server.transport === 'http'
    ? server.url || 'HTTP endpoint unavailable'
    : [server.command, ...server.args].filter(Boolean).join(' ') || 'stdio command unavailable'
}

function delay(milliseconds: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds))
}

export function HermesMcpWorkspace() {
  const [view, setView] = useState<View>('configured')
  const [snapshot, setSnapshot] = useState<HermesMcpSnapshot | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [working, setWorking] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [draft, setDraft] = useState<CreateDraft>(emptyDraft)
  const [testResults, setTestResults] = useState<Record<string, HermesMcpTestResult>>({})
  const [editingServerName, setEditingServerName] = useState<string | null>(null)

  const load = useCallback(async (quiet = false) => {
    if (!quiet) setLoading(true)
    setError(null)
    try { setSnapshot(await hermesMcpAdapter.snapshot()) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not load Hermes MCP settings.') }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { void load() }, [load])

  const catalogByName = useMemo(() => new Map((snapshot?.catalog ?? []).map((entry) => [entry.name, entry])), [snapshot])
  const visibleCatalog = useMemo(() => {
    const needle = query.trim().toLowerCase()
    if (!needle) return snapshot?.catalog ?? []
    return (snapshot?.catalog ?? []).filter((entry) => [entry.name, entry.description, entry.source, entry.transport, entry.authType]
      .some((value) => value.toLowerCase().includes(needle)))
  }, [query, snapshot])
  const diagnosticsByName = useMemo(() => {
    const result = new Map<string, string[]>()
    for (const diagnostic of snapshot?.diagnostics ?? []) result.set(diagnostic.name, [...(result.get(diagnostic.name) ?? []), diagnostic.message])
    return result
  }, [snapshot])
  const editingServer = snapshot?.servers.find((server) => server.name === editingServerName) ?? null
  const editorController = useMemo(
    () => editingServer ? new HermesMcpLiveEditorController(editingServer.name) : undefined,
    [editingServer],
  )

  function closeDialog() {
    setDialog(null)
    setDialogError(null)
    setDraft(emptyDraft)
  }

  function openInstall(entry: HermesMcpCatalogEntry) {
    setDialogError(null)
    setDialog({ kind: 'install', entry })
  }

  async function createServer(event: FormEvent) {
    event.preventDefault()
    setDialogError(null)
    setWorking('create')
    try {
      await hermesMcpAdapter.create({
        name: draft.name,
        transport: draft.transport,
        url: draft.url,
        auth: draft.auth,
        command: draft.command,
        args: parseArgs(draft.args),
      })
      closeDialog()
      setNotice(`Added ${draft.name.trim()}. External servers are not Nous-approved or Workbench-reviewed.`)
      await load(true)
    } catch (reason) {
      setDialogError(reason instanceof Error ? reason.message : 'Could not add the MCP server.')
    } finally { setWorking(null) }
  }

  async function installEntry(event: FormEvent) {
    event.preventDefault()
    if (dialog?.kind !== 'install') return
    const entry = dialog.entry
    if (entry.requiredEnvironment.length) {
      setDialogError('This entry requires credentials. Configure it in standalone Hermes until the native Connections broker is available.')
      return
    }
    setDialogError(null)
    setWorking(`install:${entry.name}`)
    try {
      const result = await hermesMcpAdapter.install(entry.name)
      closeDialog()
      setNotice(result.background
        ? `${entry.name} installation started in the background${result.action ? ` as ${result.action}` : ''}.`
        : `${entry.name} was installed and enabled.`)
      await load(true)
    } catch (reason) {
      setDialogError(reason instanceof Error ? reason.message : 'Could not install the catalog entry.')
    } finally { setWorking(null) }
  }

  async function testServer(server: HermesMcpServer) {
    setWorking(`test:${server.name}`)
    setError(null)
    try {
      const result = await hermesMcpAdapter.test(server.name)
      setTestResults((current) => ({ ...current, [server.name]: result }))
    } catch (reason) { setError(reason instanceof Error ? reason.message : `Could not test ${server.name}.`) }
    finally { setWorking(null) }
  }

  async function toggleServer(server: HermesMcpServer) {
    setWorking(`toggle:${server.name}`)
    setError(null)
    try {
      await hermesMcpAdapter.setEnabled(server.name, !server.enabled)
      setNotice(`${server.name} will be ${server.enabled ? 'disabled' : 'enabled'} for new Hermes sessions.`)
      await load(true)
    } catch (reason) { setError(reason instanceof Error ? reason.message : `Could not update ${server.name}.`) }
    finally { setWorking(null) }
  }

  async function removeServer() {
    if (dialog?.kind !== 'remove') return
    const server = dialog.server
    setWorking(`remove:${server.name}`)
    setDialogError(null)
    try {
      await hermesMcpAdapter.remove(server.name)
      closeDialog()
      setTestResults((current) => { const next = { ...current }; delete next[server.name]; return next })
      setNotice(`${server.name} was removed from Hermes.`)
      await load(true)
    } catch (reason) { setDialogError(reason instanceof Error ? reason.message : `Could not remove ${server.name}.`) }
    finally { setWorking(null) }
  }

  async function authenticateServer(server: HermesMcpServer) {
    const popup = window.open('about:blank', '_blank')
    if (!popup) { setError('The OAuth window was blocked. Allow popups for Hermes Workbench and try again.'); return }
    popup.opener = null
    setWorking(`auth:${server.name}`)
    setError(null)
    try {
      const started = await hermesMcpAdapter.startOAuth(server.name)
      if (started.status === 'error') throw new Error(started.error || 'OAuth failed to start.')
      if (!started.flowId || !started.authorizationUrl || !isHttpUrl(started.authorizationUrl)) throw new Error('Hermes did not return a valid OAuth authorization URL.')
      popup.location.href = started.authorizationUrl
      for (let attempt = 0; attempt < 300; attempt += 1) {
        if (popup.closed) throw new Error('The OAuth window was closed before authorization completed.')
        await delay(1_000)
        const current = await hermesMcpAdapter.oauthStatus(started.flowId)
        if (current.status === 'approved') {
          popup.close()
          setTestResults((results) => ({ ...results, [server.name]: { ok: true, error: '', tools: current.tools } }))
          setNotice(`${server.name} OAuth authorization completed.`)
          return
        }
        if (current.status === 'error') throw new Error(current.error || 'OAuth authorization failed.')
      }
      throw new Error('OAuth authorization timed out after five minutes.')
    } catch (reason) {
      popup.close()
      setError(reason instanceof Error ? reason.message : `Could not authenticate ${server.name}.`)
    } finally { setWorking(null) }
  }

  return (
    <section className="mcp-workspace">
      <header className="mcp-hero">
        <span><Package size={24} /></span>
        <div><small>HERMES MCP MANAGEMENT</small><strong>Tools, servers, and upstream catalog</strong><p>Complete Hermes MCP controls with explicit provenance before anything is installed.</p></div>
        <button type="button" onClick={() => void load()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={14} /> Refresh</button>
      </header>

      <div className="mcp-provenance">
        <ShieldCheck size={17} />
        <div><strong>Approval is source-specific</strong><p><b>Nous-approved</b> means the entry is curated by upstream Nous Research. It does not mean Chris or Codex independently reviewed its source, bootstrap commands, permissions, or service.</p></div>
        <span><i className="nous">Nous-approved</i><i className="reviewed">Workbench reviewed · none yet</i><i className="external">External · not reviewed</i></span>
      </div>

      <nav className="mcp-tabs" aria-label="MCP settings views">
        <button type="button" className={view === 'configured' ? 'active' : ''} onClick={() => setView('configured')}><Server size={14} /><span><strong>Configured</strong><small>{snapshot?.servers.length ?? 0} server{snapshot?.servers.length === 1 ? '' : 's'}</small></span></button>
        <button type="button" className={view === 'catalog' ? 'active' : ''} onClick={() => setView('catalog')}><Package size={14} /><span><strong>Nous catalog</strong><small>{snapshot?.catalog.length ?? 0} entries</small></span></button>
        <button type="button" className="mcp-add" onClick={() => { setDialogError(null); setDialog({ kind: 'create' }) }}><Plus size={14} /> Add external server</button>
      </nav>

      {error && <div className="mcp-alert error"><AlertTriangle size={15} /><span><strong>MCP settings need attention</strong><small>{error}</small></span><button type="button" onClick={() => setError(null)} aria-label="Dismiss error"><X size={13} /></button></div>}
      {notice && <div className="mcp-alert notice"><CheckCircle2 size={15} /><span><strong>Hermes MCP updated</strong><small>{notice}</small></span><button type="button" onClick={() => setNotice(null)} aria-label="Dismiss notice"><X size={13} /></button></div>}

      {loading && !snapshot ? <div className="mcp-loading"><LoaderCircle className="spin" size={22} /><strong>Reading Hermes MCP configuration…</strong></div> : null}

      {snapshot && view === 'configured' && <div className="mcp-list configured">
        {snapshot.servers.length === 0 ? <div className="mcp-empty"><Server size={24} /><strong>No MCP servers configured</strong><p>Browse the Nous catalog or add an external server with its provenance clearly marked.</p></div> : null}
        {snapshot.servers.map((server) => {
          const catalogEntry = catalogByName.get(server.name)
          const result = testResults[server.name]
          return <article className={!server.enabled ? 'disabled' : ''} key={server.name}>
            <header>
              <span className="mcp-type-icon">{server.transport === 'http' ? <Globe2 size={15} /> : <TerminalSquare size={15} />}</span>
              <div><strong>{server.name}</strong><small>{serverDescription(server)}</small></div>
              <span className="mcp-badges"><i>{server.transport}</i>{server.auth && <i>auth: {server.auth === 'header' ? 'bearer' : server.auth}</i>}<i className={catalogEntry ? 'nous' : 'external'}>{catalogEntry ? 'Nous-approved origin' : 'External · not reviewed'}</i>{!server.enabled && <i>disabled</i>}</span>
            </header>
            {server.environmentVariableNames.length > 0 && <p className="mcp-env-names"><KeyRound size={12} /> {server.environmentVariableNames.length} environment variable{server.environmentVariableNames.length === 1 ? '' : 's'} configured: {server.environmentVariableNames.join(', ')}. Values are not exposed to Workbench.</p>}
            {result && <div className={`mcp-test-result ${result.ok ? 'ok' : 'failed'}`}>{result.ok ? <CheckCircle2 size={13} /> : <AlertTriangle size={13} />}<span><strong>{result.ok ? 'Connection succeeded' : 'Connection failed'}</strong><small>{result.ok ? result.tools.length ? `${result.tools.length} tools: ${result.tools.map((tool) => tool.name).join(', ')}` : 'Connected; no tools advertised.' : result.error || 'Hermes did not provide an error detail.'}</small></span></div>}
            <footer>
              {server.auth === 'oauth' && <button type="button" onClick={() => void authenticateServer(server)} disabled={working !== null}>{working === `auth:${server.name}` ? <LoaderCircle className="spin" size={13} /> : <KeyRound size={13} />} Authenticate</button>}
              <button type="button" onClick={() => void toggleServer(server)} disabled={working !== null}>{working === `toggle:${server.name}` ? <LoaderCircle className="spin" size={13} /> : <Power size={13} />} {server.enabled ? 'Disable' : 'Enable'}</button>
              <button type="button" onClick={() => void testServer(server)} disabled={working !== null}>{working === `test:${server.name}` ? <LoaderCircle className="spin" size={13} /> : <Zap size={13} />} Test</button>
              <button type="button" onClick={() => setEditingServerName(server.name)} disabled={working !== null || !server.editable} title={server.editable ? 'Edit safe non-secret configuration' : 'Advanced credential material requires the native Connections vault'}><Pencil size={13} /> Edit</button>
              <button type="button" className="danger" onClick={() => { setDialogError(null); setDialog({ kind: 'remove', server }) }} disabled={working !== null}><Trash2 size={13} /> Remove</button>
            </footer>
          </article>
        })}
        {editingServer && editingServer.editable && editorController ? <div className="mcp-editor-host">
          <button type="button" className="mcp-editor-host__close" onClick={() => setEditingServerName(null)}><X size={13} /> Close editor</button>
          <HermesMcpEditor snapshot={{
            contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
            serverId: editingServer.name,
            revision: editingServer.revision,
            name: editingServer.name,
            transport: editingServer.transport,
            url: editingServer.url ?? '',
            command: editingServer.command ?? '',
            args: editingServer.args,
            environmentVariableNames: editingServer.environmentVariableNames,
            auth: editingServer.auth,
            enabled: editingServer.enabled,
          }} controller={editorController} onCommitted={async () => { await load(true); setEditingServerName(null) }} />
        </div> : null}
      </div>}

      {snapshot && view === 'catalog' && <>
        <label className="mcp-search"><Search size={14} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search the Nous-approved catalog" /><span>{visibleCatalog.length} shown</span></label>
        <div className="mcp-list catalog">
          {visibleCatalog.length === 0 ? <div className="mcp-empty"><Search size={24} /><strong>No catalog matches</strong><p>Try a different name, transport, authentication type, or description.</p></div> : null}
          {visibleCatalog.map((entry) => <article key={entry.name}>
            <header>
              <span className="mcp-type-icon">{entry.transport === 'http' ? <Globe2 size={15} /> : <TerminalSquare size={15} />}</span>
              <div><strong>{entry.name}</strong><small>{entry.description || 'Nous-approved MCP catalog entry'}</small></div>
              <span className="mcp-badges"><i className="nous">Nous-approved</i><i className="unreviewed">Not Chris + Codex reviewed</i><i>{entry.transport}</i><i>auth: {entry.authType}</i>{entry.installed && <i className="installed">Installed</i>}</span>
            </header>
            <div className="mcp-runtime"><small>{entry.transport === 'http' ? 'ENDPOINT' : 'RUNS'}</small><code>{runtimeDescription(entry)}</code></div>
            {entry.installUrl && <div className="mcp-runtime"><small>INSTALLS FROM</small>{isHttpUrl(entry.installUrl) ? <a href={entry.installUrl} target="_blank" rel="noreferrer">{entry.installUrl}<ExternalLink size={11} /></a> : <code>{entry.installUrl}</code>}{entry.installRef && <code>@ {entry.installRef}</code>}</div>}
            {entry.source && <div className="mcp-runtime"><small>UPSTREAM SOURCE</small>{isHttpUrl(entry.source) ? <a href={entry.source} target="_blank" rel="noreferrer">{entry.source}<ExternalLink size={11} /></a> : <code>{entry.source}</code>}</div>}
            {entry.bootstrap.length > 0 && <details><summary>Bootstrap commands ({entry.bootstrap.length})</summary>{entry.bootstrap.map((command, index) => <code key={`${entry.name}-${index}`}>{command}</code>)}</details>}
            {entry.postInstall && <details><summary>Setup notes</summary><p>{entry.postInstall}</p></details>}
            {(diagnosticsByName.get(entry.name) ?? []).map((message) => <p className="mcp-diagnostic" key={message}><AlertTriangle size={12} /> {message}</p>)}
            <footer><span><ShieldQuestion size={13} /> Review exact commands and source before installing.</span>{entry.installed ? <b><CheckCircle2 size={13} /> Installed{entry.enabled ? ' and enabled' : ' · disabled'}</b> : <button type="button" className="primary" onClick={() => openInstall(entry)} disabled={working !== null}>Review & install</button>}</footer>
          </article>)}
        </div>
      </>}

      {dialog && <div className="mcp-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && working === null) closeDialog() }}>
        <section className="mcp-dialog" role="dialog" aria-modal="true" aria-labelledby="mcp-dialog-title">
          <header><span>{dialog.kind === 'create' ? <Plus size={17} /> : dialog.kind === 'install' ? <Package size={17} /> : <Trash2 size={17} />}</span><div><strong id="mcp-dialog-title">{dialog.kind === 'create' ? 'Add external MCP server' : dialog.kind === 'install' ? `Review ${dialog.entry.name}` : `Remove ${dialog.server.name}`}</strong><small>{dialog.kind === 'install' ? 'Nous-approved upstream entry · not independently reviewed' : dialog.kind === 'create' ? 'External configuration · no approval implied' : 'This removes the server from Hermes configuration'}</small></div><button type="button" onClick={closeDialog} disabled={working !== null} aria-label="Close"><X size={15} /></button></header>
          {dialogError && <div className="mcp-dialog-error"><AlertTriangle size={14} /> {dialogError}</div>}

          {dialog.kind === 'create' && <form onSubmit={createServer}>
            <div className="mcp-dialog-warning"><ShieldQuestion size={16} /><span><strong>External · not reviewed</strong><small>Neither Nous approval nor Chris + Codex review is implied. Verify the operator, URL or command, requested credentials, and permissions yourself.</small></span></div>
            <label><span>Name</span><input autoFocus value={draft.name} onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))} placeholder="my-server" /></label>
            <label><span>Transport</span><select value={draft.transport} onChange={(event) => setDraft((current) => ({ ...current, transport: event.target.value as 'http' | 'stdio' }))}><option value="http">HTTP / SSE</option><option value="stdio">Local stdio process</option></select></label>
            {draft.transport === 'http' ? <>
              <label><span>URL</span><input value={draft.url} onChange={(event) => setDraft((current) => ({ ...current, url: event.target.value }))} placeholder="https://example.com/mcp" /></label>
              <label><span>Authentication</span><select value={draft.auth} onChange={(event) => setDraft((current) => ({ ...current, auth: event.target.value as CreateDraft['auth'] }))}><option value="none">None</option><option value="oauth">OAuth</option></select></label>
              {draft.auth === 'oauth' && <p className="mcp-form-note">Add the server, then use <b>Authenticate</b> from its configured-server card.</p>}
            </> : <>
              <label><span>Command</span><input value={draft.command} onChange={(event) => setDraft((current) => ({ ...current, command: event.target.value }))} placeholder="npx" /></label>
              <label><span>Arguments</span><input value={draft.args} onChange={(event) => setDraft((current) => ({ ...current, args: event.target.value }))} placeholder="-y @modelcontextprotocol/server-name" /></label>
              <p className="mcp-form-note"><b>Credential-bearing environment variables are intentionally not accepted in the renderer.</b> Configure them in standalone Hermes until the native Connections broker is available.</p>
            </>}
            <footer><button type="button" onClick={closeDialog} disabled={working !== null}>Cancel</button><button type="submit" className="primary" disabled={working !== null}>{working === 'create' ? <LoaderCircle className="spin" size={13} /> : <Plus size={13} />} Add external server</button></footer>
          </form>}

          {dialog.kind === 'install' && <form onSubmit={installEntry}>
            <div className="mcp-dialog-warning nous"><ShieldCheck size={16} /><span><strong>Nous-approved upstream catalog</strong><small>Nous Research curates this entry. Chris and Codex have not independently reviewed this exact source, revision, bootstrap, permissions, or service.</small></span></div>
            <div className="mcp-review-grid"><div><small>Transport</small><strong>{dialog.entry.transport}</strong></div><div><small>Authentication</small><strong>{dialog.entry.authType}</strong></div><div className="wide"><small>{dialog.entry.transport === 'http' ? 'Endpoint' : 'Runtime command'}</small><code>{runtimeDescription(dialog.entry)}</code></div>{dialog.entry.installUrl && <div className="wide"><small>Install source</small><code>{dialog.entry.installUrl}{dialog.entry.installRef ? ` @ ${dialog.entry.installRef}` : ''}</code></div>}</div>
            {dialog.entry.bootstrap.length > 0 && <div className="mcp-review-commands"><strong>Commands Hermes will run</strong>{dialog.entry.bootstrap.map((command, index) => <code key={index}>{command}</code>)}</div>}
            {dialog.entry.requiredEnvironment.length > 0 && <div className="mcp-form-note"><b>Native credential broker required</b><br />This entry needs {dialog.entry.requiredEnvironment.length} credential slot{dialog.entry.requiredEnvironment.length === 1 ? '' : 's'}. Workbench will not collect them in React; use standalone Hermes until Connections can bind them through the trusted desktop host.</div>}
            {dialog.entry.postInstall && <div className="mcp-form-note"><b>Setup notes</b><br />{dialog.entry.postInstall}</div>}
            <footer><button type="button" onClick={closeDialog} disabled={working !== null}>Cancel</button><button type="submit" className="primary" disabled={working !== null || dialog.entry.requiredEnvironment.length > 0}>{working === `install:${dialog.entry.name}` ? <LoaderCircle className="spin" size={13} /> : <Package size={13} />} Install Nous entry</button></footer>
          </form>}

          {dialog.kind === 'remove' && <div className="mcp-remove-confirm"><AlertTriangle size={23} /><strong>Remove {dialog.server.name}?</strong><p>This removes the server from Hermes configuration. It does not claim or revoke approval, and external provider data may remain with that provider.</p><footer><button type="button" onClick={closeDialog} disabled={working !== null}>Cancel</button><button type="button" className="danger" onClick={() => void removeServer()} disabled={working !== null}>{working === `remove:${dialog.server.name}` ? <LoaderCircle className="spin" size={13} /> : <Trash2 size={13} />} Remove server</button></footer></div>}
        </section>
      </div>}

      <footer className="mcp-protocol"><ShieldCheck size={12} /> Hermes MCP adapter v{HERMES_MCP_ADAPTER_VERSION} · environment values are never returned to React</footer>
    </section>
  )
}
