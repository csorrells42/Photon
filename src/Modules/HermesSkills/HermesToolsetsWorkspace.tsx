import { useCallback, useEffect, useMemo, useState } from 'react'
import { Check, CircleAlert, ExternalLink, LoaderCircle, RefreshCw, Search, ServerCog, ShieldCheck, Terminal, Wrench, X } from 'lucide-react'
import { HERMES_TOOLSETS_ADAPTER_VERSION, hermesToolsetsAdapter, type HermesToolset, type HermesToolsetConfig, type HermesToolsetProvider } from './HermesToolsetsAdapter'

const errorText = (reason: unknown) => reason instanceof Error ? reason.message : 'Hermes Toolsets request failed.'

export function HermesToolsetsWorkspace() {
  const [toolsets, setToolsets] = useState<HermesToolset[]>([])
  const [searchText, setSearchText] = useState('')
  const [loading, setLoading] = useState(true)
  const [working, setWorking] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [selected, setSelected] = useState<HermesToolset | null>(null)
  const [config, setConfig] = useState<HermesToolsetConfig | null>(null)
  const [configLoading, setConfigLoading] = useState(false)
  const [setupLog, setSetupLog] = useState<string[]>([])
  const [setupRunning, setSetupRunning] = useState(false)

  const load = useCallback(async () => {
    setLoading(true); setError(null)
    try { setToolsets(await hermesToolsetsAdapter.list()) }
    catch (reason) { setError(errorText(reason)) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { void load() }, [load])

  const filtered = useMemo(() => {
    const query = searchText.trim().toLowerCase()
    if (!query) return toolsets
    return toolsets.filter((row) => `${row.name} ${row.label} ${row.description} ${row.platformLabel} ${row.tools.join(' ')}`.toLowerCase().includes(query))
  }, [searchText, toolsets])

  async function open(toolset: HermesToolset) {
    setSelected(toolset); setConfig(null); setSetupLog([]); setConfigLoading(true); setError(null)
    try { setConfig(await hermesToolsetsAdapter.config(toolset.name)) }
    catch (reason) { setError(errorText(reason)) }
    finally { setConfigLoading(false) }
  }

  function close() {
    if (working || setupRunning) return
    setSelected(null); setConfig(null); setSetupLog([])
  }

  async function toggle(toolset: HermesToolset) {
    setWorking('toggle'); setError(null)
    try {
      await hermesToolsetsAdapter.toggle(toolset.name, !toolset.enabled)
      setSelected((current) => current ? { ...current, enabled: !current.enabled } : current)
      setToolsets((current) => current.map((row) => row.name === toolset.name ? { ...row, enabled: !row.enabled } : row))
      setNotice(`${toolset.label} ${toolset.enabled ? 'disabled' : 'enabled'}.`)
    } catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  async function selectProvider(provider: HermesToolsetProvider) {
    if (!selected) return
    setWorking(`provider:${provider.name}`); setError(null)
    try {
      const result = await hermesToolsetsAdapter.selectProvider(selected.name, provider.name)
      setConfig((current) => current ? { ...current, activeProvider: provider.name, providers: current.providers.map((row) => ({ ...row, active: row.name === provider.name })) } : current)
      setNotice(result.needs_nous_auth === true ? `${provider.name} selected; Nous Portal sign-in is still required.` : `${provider.name} selected.`)
      await load()
    } catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  async function pollSetup() {
    for (let attempt = 0; attempt < 300; attempt += 1) {
      await new Promise((resolve) => window.setTimeout(resolve, attempt ? 1_200 : 800))
      const status = await hermesToolsetsAdapter.postSetupStatus()
      setSetupLog(status.lines)
      if (!status.running) {
        setSetupRunning(false)
        setNotice(status.exitCode === 0 ? 'Provider setup completed.' : `Provider setup stopped with exit code ${status.exitCode ?? 'unknown'}.`)
        await load()
        if (selected) setConfig(await hermesToolsetsAdapter.config(selected.name))
        return
      }
    }
    setSetupRunning(false); setError('Provider setup is still running after five minutes. Refresh to inspect its current state.')
  }

  async function runSetup(provider: HermesToolsetProvider) {
    if (!selected || !provider.postSetup) return
    if (!window.confirm(`Run the upstream Hermes setup hook “${provider.postSetup}” for ${provider.name}? It may install packages or binaries inside the Hermes runtime.`)) return
    setSetupRunning(true); setSetupLog([]); setError(null)
    try {
      await hermesToolsetsAdapter.runPostSetup(selected.name, provider.postSetup, config?.providers.flatMap((row) => row.postSetup ? [row.postSetup] : []) ?? [])
      void pollSetup()
    } catch (reason) { setSetupRunning(false); setError(errorText(reason)) }
  }

  return <section className="toolsets-surface">
    <header className="toolsets-summary"><div><small>HERMES TOOLSETS</small><strong>Provider-neutral capability setup</strong><p>Enable capability groups, inspect provider readiness, select backends, and run reviewed upstream setup hooks.</p></div><button type="button" onClick={() => void load()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={14} /> Refresh</button></header>
    <div className="toolsets-secret-note"><ShieldCheck size={17} /><span><strong>Secrets never enter React</strong><small>Workbench shows provider readiness only. Configure missing keys in standalone Hermes until the native Connections broker can bind them without exposing values to the renderer.</small></span></div>
    {error && <div className="skills-error"><CircleAlert size={15} /><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
    {notice && <div className="skills-notice"><Check size={15} /><span>{notice}</span><button type="button" onClick={() => setNotice(null)}><X size={13} /></button></div>}
    <label className="skills-search"><Search size={15} /><input value={searchText} onChange={(event) => setSearchText(event.target.value)} placeholder="Search capability groups, platforms, and tools" /><span>{filtered.length} shown</span></label>
    {loading && !toolsets.length ? <div className="skills-empty"><LoaderCircle className="spin" size={24} /><strong>Reading Hermes toolsets…</strong></div> : <div className="toolsets-grid">{filtered.map((toolset) => <article key={toolset.name} className={toolset.enabled ? '' : 'disabled'}><header><span><Wrench size={16} /></span><div><strong>{toolset.label}</strong><small>{toolset.platformLabel} · {toolset.tools.length} tools</small></div><i className={toolset.configured ? 'ready' : ''}>{toolset.configured ? 'Configured' : 'Needs setup'}</i></header><p>{toolset.description || 'No upstream description supplied.'}</p><footer><small>{toolset.enabled ? 'Enabled' : 'Disabled'}</small><button type="button" onClick={() => void open(toolset)}>Configure</button></footer></article>)}{!filtered.length && <div className="skills-empty"><Search size={24} /><strong>No matching toolsets</strong><p>Try a broader capability or platform name.</p></div>}</div>}
    {selected && <div className="skills-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) close() }}><section className="toolset-dialog" role="dialog" aria-modal="true" aria-label={`Configure ${selected.label}`}><header><span><ServerCog size={20} /></span><div><small>TOOLSET CONFIGURATION</small><strong>{selected.label}</strong><p>{selected.platformLabel} · {selected.tools.length} advertised tools</p></div><button type="button" aria-label="Close" onClick={close} disabled={Boolean(working) || setupRunning}><X size={18} /></button></header><div className="toolset-toggle"><span><strong>{selected.enabled ? 'Enabled' : 'Disabled'}</strong><small>{selected.description}</small></span><button type="button" onClick={() => void toggle(selected)} disabled={Boolean(working)}>{working === 'toggle' ? <LoaderCircle className="spin" size={13} /> : null}{selected.enabled ? 'Disable toolset' : 'Enable toolset'}</button></div><div className="toolset-dialog-body">{configLoading ? <div className="skills-empty"><LoaderCircle className="spin" size={24} /><strong>Reading provider configuration…</strong></div> : !config?.hasCategory ? <div className="skills-empty"><Wrench size={24} /><strong>No provider configuration required</strong><p>This toolset only needs the enable switch above.</p></div> : config.providers.map((provider) => <article className={`toolset-provider ${provider.active ? 'active' : ''}`} key={provider.name}><header><div><strong>{provider.name}</strong><small>{provider.tag || provider.status}</small></div><span>{provider.badge && <i>{provider.badge}</i>}{provider.requiresNousAuth && <i>Nous Portal</i>}<b className={provider.status}>{provider.status}</b></span></header><div className="toolset-provider-actions">{provider.active ? <em><Check size={12} /> Selected</em> : <button type="button" onClick={() => void selectProvider(provider)} disabled={Boolean(working)}>{working === `provider:${provider.name}` ? <LoaderCircle className="spin" size={13} /> : null}Select provider</button>}</div>{provider.environment.length > 0 && <section className="toolset-secrets">{provider.environment.map((env) => <div key={env.key}><span><strong>{env.key}</strong>{env.isSet && <i>Saved</i>}</span>{env.helpUrl && <a href={env.helpUrl} target="_blank" rel="noreferrer">Provider instructions <ExternalLink size={11} /></a>}</div>)}<small>Missing values must be configured in standalone Hermes until the native Connections broker is available.</small></section>}{provider.postSetup && <section className="toolset-setup"><p><Terminal size={13} /> Upstream setup hook <code>{provider.postSetup}</code></p><button type="button" onClick={() => void runSetup(provider)} disabled={setupRunning || Boolean(working)}>{setupRunning ? <LoaderCircle className="spin" size={13} /> : <Terminal size={13} />}{setupRunning ? 'Setup running…' : 'Review & run setup'}</button></section>}</article>)}</div>{(setupRunning || setupLog.length > 0) && <section className="toolset-log"><header><Terminal size={13} /><strong>Upstream setup activity</strong>{setupRunning && <LoaderCircle className="spin" size={13} />}</header><pre>{setupLog.length ? setupLog.join('\n') : 'Starting…'}</pre></section>}<footer><ShieldCheck size={12} /> Toolsets adapter v{HERMES_TOOLSETS_ADAPTER_VERSION} · renderer receives status metadata only</footer></section></div>}
    <footer className="skills-contract"><ShieldCheck size={12} /> Hermes Toolsets adapter v{HERMES_TOOLSETS_ADAPTER_VERSION} · provider writes are constrained to advertised contracts</footer>
  </section>
}
