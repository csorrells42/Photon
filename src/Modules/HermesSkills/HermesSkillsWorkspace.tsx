import { useCallback, useEffect, useMemo, useState } from 'react'
import { BookOpen, Check, CircleAlert, ExternalLink, LoaderCircle, PackageCheck, RefreshCw, Search, ShieldAlert, ShieldCheck, Sparkles, Trash2, Wrench, X } from 'lucide-react'
import { HERMES_SKILLS_ADAPTER_VERSION, hermesSkillsAdapter, type HermesSkill, type HermesSkillHubInstalled, type HermesSkillHubResult, type HermesSkillHubScan, type HermesSkillHubSources } from './HermesSkillsAdapter'
import './HermesSkillsWorkspace.css'
import { HermesToolsetsWorkspace } from './HermesToolsetsWorkspace'

type View = 'installed' | 'hub' | 'toolsets'
const errorText = (reason: unknown) => reason instanceof Error ? reason.message : 'Hermes Skills request failed.'

function installedIdentifier(installed: Record<string, HermesSkillHubInstalled>, skill: HermesSkill) {
  return Object.entries(installed).find(([, value]) => value.name === skill.name)?.[0] ?? null
}

function sourceLink(repository: string | null) {
  if (!repository) return null
  try {
    const url = repository.startsWith('http') ? new URL(repository) : new URL(`https://github.com/${repository}`)
    return url.protocol === 'https:' ? url.toString() : null
  } catch { return null }
}

export function HermesSkillsWorkspace() {
  const [view, setView] = useState<View>('installed')
  const [skills, setSkills] = useState<HermesSkill[]>([])
  const [hub, setHub] = useState<HermesSkillHubSources | null>(null)
  const [results, setResults] = useState<HermesSkillHubResult[]>([])
  const [searchText, setSearchText] = useState('')
  const [loading, setLoading] = useState(true)
  const [searching, setSearching] = useState(false)
  const [working, setWorking] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [selected, setSelected] = useState<HermesSkillHubResult | null>(null)
  const [scan, setScan] = useState<HermesSkillHubScan | null>(null)
  const [preview, setPreview] = useState('')
  const [previewFiles, setPreviewFiles] = useState<string[]>([])
  const [reviewLoading, setReviewLoading] = useState(false)
  const [cautionAccepted, setCautionAccepted] = useState(false)

  const load = useCallback(async () => {
    setLoading(true); setError(null)
    try {
      const [nextSkills, nextHub] = await Promise.all([hermesSkillsAdapter.skills(), hermesSkillsAdapter.sources()])
      setSkills(nextSkills); setHub(nextHub)
      setResults(nextHub.featured)
    } catch (reason) { setError(errorText(reason)) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { void load() }, [load])
  useEffect(() => {
    if (view !== 'hub') return
    const query = searchText.trim()
    if (!query) { setResults(hub?.featured ?? []); setSearching(false); return }
    setSearching(true)
    const timer = window.setTimeout(() => {
      void hermesSkillsAdapter.search(query).then((response) => {
        setResults(response.results)
        setHub((current) => current ? { ...current, installed: { ...current.installed, ...response.installed } } : current)
        if (response.timedOut.length) setNotice(`Some upstream hubs timed out: ${response.timedOut.join(', ')}`)
      }).catch((reason) => setError(errorText(reason))).finally(() => setSearching(false))
    }, 350)
    return () => window.clearTimeout(timer)
  }, [hub?.featured, searchText, view])

  const enabledCount = useMemo(() => skills.filter((skill) => skill.enabled).length, [skills])
  const installed = hub?.installed ?? {}

  async function toggle(skill: HermesSkill) {
    setWorking(`toggle:${skill.name}`); setError(null)
    try {
      await hermesSkillsAdapter.toggle(skill.name, !skill.enabled)
      setSkills((current) => current.map((row) => row.name === skill.name ? { ...row, enabled: !row.enabled } : row))
      setNotice(`${skill.name} ${skill.enabled ? 'disabled' : 'enabled'}.`)
    } catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  async function review(result: HermesSkillHubResult) {
    setSelected(result); setScan(null); setPreview(''); setPreviewFiles([]); setCautionAccepted(false); setReviewLoading(true); setError(null)
    try {
      const [nextPreview, nextScan] = await Promise.all([hermesSkillsAdapter.preview(result.identifier), hermesSkillsAdapter.scan(result.identifier)])
      setPreview(nextPreview.skillMarkdown); setPreviewFiles(nextPreview.files); setScan(nextScan)
    } catch (reason) { setError(errorText(reason)) }
    finally { setReviewLoading(false) }
  }

  function closeReview() {
    if (working === 'install') return
    setSelected(null); setScan(null); setPreview(''); setPreviewFiles([]); setCautionAccepted(false)
  }

  async function install() {
    if (!selected || !scan) return
    setWorking('install'); setError(null)
    try {
      const response = await hermesSkillsAdapter.install(selected.identifier, scan, cautionAccepted)
      setNotice(`${selected.name} installation started${typeof response.pid === 'number' ? ` (process ${response.pid})` : ''}.`)
      setSelected(null); setScan(null); setPreview(''); setPreviewFiles([]); setCautionAccepted(false)
      window.setTimeout(() => void load(), 1_500)
    } catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  async function uninstall(skill: HermesSkill) {
    if (!window.confirm(`Uninstall the hub skill “${skill.name}”? This removes its installed files from the current Hermes profile.`)) return
    setWorking(`remove:${skill.name}`); setError(null)
    try { await hermesSkillsAdapter.uninstall(skill.name); setNotice(`${skill.name} uninstall started.`); window.setTimeout(() => void load(), 1_500) }
    catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  async function updateAll() {
    if (!window.confirm('Update every hub-installed skill in the current Hermes profile?')) return
    setWorking('update'); setError(null)
    try {
      const response = await hermesSkillsAdapter.update()
      setNotice(`Skills update started${typeof response.pid === 'number' ? ` (process ${response.pid})` : ''}.`)
      window.setTimeout(() => void load(), 1_500)
    } catch (reason) { setError(errorText(reason)) }
    finally { setWorking('') }
  }

  return <section className="skills-workspace">
    <header className="skills-heading"><div><small>HERMES SKILLS</small><strong>Capabilities with a visible trust trail</strong><p>Manage installed skills and inspect upstream source plus the real SKILL.md and security scan before installing anything.</p></div><button type="button" onClick={() => void load()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={14} /> Refresh</button></header>
    <div className="skills-trust-note"><ShieldCheck size={18} /><span><strong>Trust labels belong to their source</strong><small>Bundled, hub trust, and scan verdicts come from upstream Hermes/Nous. They do not claim independent Chris or Codex review.</small></span><i>Workbench reviewed · none yet</i></div>
    {error && <div className="skills-error"><CircleAlert size={15} /><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
    {notice && <div className="skills-notice"><Check size={15} /><span>{notice}</span><button type="button" onClick={() => setNotice(null)}><X size={13} /></button></div>}
    <nav className="skills-tabs" aria-label="Skills settings views">
      <button type="button" className={view === 'installed' ? 'active' : ''} onClick={() => setView('installed')}><PackageCheck size={16} /><span><strong>Installed</strong><small>{enabledCount} enabled · {skills.length} total</small></span></button>
      <button type="button" className={view === 'hub' ? 'active' : ''} onClick={() => setView('hub')}><Sparkles size={16} /><span><strong>Browse hubs</strong><small>{hub?.sources.length ?? 0} connected sources</small></span></button>
      <button type="button" className={view === 'toolsets' ? 'active' : ''} onClick={() => setView('toolsets')}><Wrench size={16} /><span><strong>Toolsets</strong><small>Providers and capability groups</small></span></button>
      {view === 'hub' && <button type="button" className="skills-update" onClick={() => void updateAll()} disabled={Boolean(working)}><RefreshCw className={working === 'update' ? 'spin' : ''} size={14} /> Update hub skills</button>}
    </nav>
    {view === 'toolsets' && <HermesToolsetsWorkspace />}
    {loading && !skills.length ? <div className="skills-empty"><LoaderCircle className="spin" size={24} /><strong>Reading Hermes skills…</strong></div> : null}
    {view === 'installed' && !loading && <div className="skills-grid">{skills.map((skill) => {
      const identifier = installedIdentifier(installed, skill)
      return <article key={skill.name} className={skill.enabled ? '' : 'disabled'}><header><span><BookOpen size={16} /></span><div><strong>{skill.name}</strong><small>{skill.category}</small></div><i className={`provenance ${skill.provenance}`}>{skill.provenance}</i></header><p>{skill.description || 'No upstream description supplied.'}</p><footer><small>{skill.usage} recorded use{skill.usage === 1 ? '' : 's'}</small><div>{identifier && <button type="button" className="danger" title={`Uninstall ${skill.name}`} onClick={() => void uninstall(skill)} disabled={Boolean(working)}><Trash2 size={13} /></button>}<button type="button" onClick={() => void toggle(skill)} disabled={Boolean(working)}>{working === `toggle:${skill.name}` ? <LoaderCircle className="spin" size={13} /> : null}{skill.enabled ? 'Disable' : 'Enable'}</button></div></footer></article>
    })}{!skills.length && <div className="skills-empty"><BookOpen size={24} /><strong>No skills reported</strong><p>Hermes did not return any installed skills for this profile.</p></div>}</div>}
    {view === 'hub' && <><div className="skills-source-strip"><span>Connected hubs</span>{hub?.sources.map((source) => <i key={source.id} className={source.rateLimited || source.available === false ? 'attention' : ''}>{source.label}{source.rateLimited ? ' · rate limited' : source.available === false ? ' · unavailable' : ''}</i>)}</div><label className="skills-search"><Search size={15} /><input value={searchText} onChange={(event) => setSearchText(event.target.value)} placeholder="Search official, trusted, and community skill hubs" /><span>{searching ? <LoaderCircle className="spin" size={14} /> : `${results.length} shown`}</span></label><div className="skills-hub-grid">{results.map((result) => {
      const isInstalled = Boolean(installed[result.identifier]); const link = sourceLink(result.repository)
      return <article key={result.identifier}><header><div><strong>{result.name}</strong><small>{result.source || result.identifier}</small></div><span className={`trust ${result.trustLevel}`}>Upstream trust · {result.trustLevel}</span></header><p>{result.description || 'No upstream description supplied.'}</p><div className="skills-tags">{result.tags.slice(0, 5).map((tag) => <i key={tag}>{tag}</i>)}</div><footer>{link ? <a href={link} target="_blank" rel="noreferrer">Source <ExternalLink size={12} /></a> : <small>{result.identifier}</small>}<button type="button" onClick={() => void review(result)}>{isInstalled ? 'Review installed' : 'Preview & scan'}</button></footer></article>
    })}{!searching && !results.length && <div className="skills-empty"><Search size={24} /><strong>No matching hub skills</strong><p>Try a broader search or refresh the connected sources.</p></div>}</div></>}
    {selected && <div className="skills-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) closeReview() }}><section className="skills-review-dialog" role="dialog" aria-modal="true" aria-label={`Review ${selected.name}`}><header><div><small>PRE-INSTALL REVIEW</small><strong>{selected.name}</strong><p>{selected.identifier}</p></div><button type="button" aria-label="Close" onClick={closeReview} disabled={working === 'install'}><X size={18} /></button></header><div className="skills-review-warning"><ShieldAlert size={18} /><span><strong>Upstream trust is not an independent endorsement</strong><small>Read the actual instructions and scan result below. Workbench never installs directly from a search card.</small></span></div>{reviewLoading ? <div className="skills-empty"><LoaderCircle className="spin" size={24} /><strong>Fetching preview and running Hermes security scan…</strong></div> : <div className="skills-review-body"><section className={`skills-scan ${scan?.policy ?? 'block'}`}><header><span><ShieldCheck size={17} /><strong>{scan ? `${scan.verdict} · policy ${scan.policy}` : 'Scan unavailable'}</strong></span><i>{scan?.findings.length ?? 0} findings</i></header><p>{scan?.summary || scan?.policyReason || 'A successful matching scan is required before installation.'}</p>{scan?.findings.length ? <div>{scan.findings.slice(0, 20).map((finding, index) => <article key={`${finding.file}:${finding.line}:${index}`}><strong>{finding.severity} · {finding.category}</strong><small>{finding.file}{finding.line !== null ? `:${finding.line}` : ''}</small><p>{finding.description}</p></article>)}</div> : null}</section><section className="skills-preview"><header><strong>SKILL.md</strong><small>{previewFiles.length} file{previewFiles.length === 1 ? '' : 's'} in bundle</small></header><pre>{preview || '(SKILL.md is empty)'}</pre></section></div>}<footer><span>{scan?.policy === 'ask' && <label><input type="checkbox" checked={cautionAccepted} onChange={(event) => setCautionAccepted(event.target.checked)} /> I reviewed the caution findings and accept them</label>}{scan?.policy === 'block' && <strong>Installation blocked by Hermes policy.</strong>}</span><div><button type="button" onClick={closeReview} disabled={working === 'install'}>Cancel</button><button type="button" className="primary" onClick={() => void install()} disabled={!scan || scan.policy === 'block' || (scan.policy === 'ask' && !cautionAccepted) || working === 'install'}>{working === 'install' ? <LoaderCircle className="spin" size={14} /> : <PackageCheck size={14} />} Install skill</button></div></footer></section></div>}
    <footer className="skills-contract"><ShieldCheck size={12} /> Hermes Skills adapter v{HERMES_SKILLS_ADAPTER_VERSION} · preview and matching scan required before install</footer>
  </section>
}
