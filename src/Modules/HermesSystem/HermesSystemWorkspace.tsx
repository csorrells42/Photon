import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import {
  Activity,
  Bot,
  Cable,
  CheckCircle2,
  CircleAlert,
  Code2,
  Cpu,
  Gauge,
  HardDrive,
  KeyRound,
  Layers3,
  LibraryBig,
  LoaderCircle,
  LockKeyhole,
  LogIn,
  LogOut,
  Package,
  RefreshCw,
  Radio,
  Server,
  ShieldCheck,
  SlidersHorizontal,
  UserRound,
  Wifi,
  Wrench,
} from 'lucide-react'
import {
  HERMES_SYSTEM_ADAPTER_VERSION,
  hermesSystemAdapter,
} from './HermesSystemAdapter'
import type {
  HermesAuthProvider,
  HermesIdentity,
  HermesStatusSnapshot,
  HermesSystemStats,
} from './HermesSystemAdapter'
import {
  evaluateHermesCompatibility,
  hermesCompatibilityAdapter,
} from './HermesCompatibilityAdapter'
import type { HermesCompatibilitySnapshot } from './HermesCompatibilityAdapter'
import {
  SERENA_HEALTH_ADAPTER_VERSION,
  serenaHealthAdapter,
} from '../Serena/SerenaHealthAdapter'
import type { SerenaHealthSnapshot } from '../Serena/SerenaHealthAdapter'
import { announceHermesAuthChanged, subscribeHermesAuthChanged } from './HermesAuthEvents'
import { HermesMcpWorkspace } from '../HermesMcp/HermesMcpWorkspace'
import { HERMES_MCP_ADAPTER_VERSION } from '../HermesMcp/HermesMcpAdapter'
import { HermesSkillsWorkspace } from '../HermesSkills/HermesSkillsWorkspace'
import { HERMES_SKILLS_ADAPTER_VERSION } from '../HermesSkills/HermesSkillsAdapter'
import { HermesExtensionSettingsWorkspace } from '../HermesExtensionSettings'
import {
  HermesExtensionSettingsLiveAdapter,
  HermesExtensionSettingsLiveController,
} from '../HermesExtensionSettingsLive'
import { HermesProfileRuntimeWorkspace } from '../HermesProfileRuntime'
import { createProductionHermesProfileRuntimeBridge } from '../HermesProfileRuntimeIntegration'
import {
  HermesSessionAdminWorkspace,
  liveHermesSessionAdminAdapter,
} from '../HermesSessionAdmin'
import { HermesConnectionsWorkspace, type ConnectionCatalogEntry } from '../HermesConnections'
import { HermesRuntimeConfigurationWorkspace } from '../HermesRuntimeConfiguration'
import { HermesSpeechVoiceWorkspace } from '../HermesSpeechOutput'
import './HermesSystemWorkspace.css'

type Section = 'overview' | 'account' | 'connections' | 'runtime' | 'speech-voice' | 'integrations' | 'mcp' | 'skills' | 'extensions' | 'profiles' | 'session-admin'

const nativeConnectionCatalog: ConnectionCatalogEntry[] = [
  { providerId: 'openrouter', displayName: 'OpenRouter', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:openrouter', 'usage'], supportsNativeChange: true, description: 'Model access and usage collection through a machine-bound native credential.' },
  { providerId: 'openai-api', displayName: 'OpenAI API', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:openai', 'usage'], supportsNativeChange: true, description: 'Optional direct OpenAI model access and usage collection.' },
  { providerId: 'anthropic-api', displayName: 'Anthropic API / Claude', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:anthropic', 'usage'], supportsNativeChange: true, description: 'Optional direct Anthropic model access and usage collection.' },
  { providerId: 'google-ai-studio', displayName: 'Google AI Studio / Gemini', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:google', 'usage'], supportsNativeChange: true, description: 'Optional direct Google model access and usage collection.' },
  { providerId: 'deepseek-api', displayName: 'DeepSeek API', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:deepseek'], supportsNativeChange: true, description: 'Optional direct DeepSeek model access through the authenticated runtime channel.' },
  { providerId: 'xai-api', displayName: 'xAI API / Grok', slotId: 'default', authKind: 'api-key', sourceKind: 'native', purposes: ['model:xai'], supportsNativeChange: true, description: 'Optional direct xAI model access through the authenticated runtime channel.' },
]

type DesktopAuthFrame = { type?: string; message?: string }
const liveExtensionSettingsAdapter = new HermesExtensionSettingsLiveAdapter()
const liveExtensionSettingsController = new HermesExtensionSettingsLiveController({
  readAdapter: liveExtensionSettingsAdapter,
  writeFetch: (...arguments_) => globalThis.fetch(...arguments_),
  profileId: 'default',
})
const liveProfileRuntimeBridge = createProductionHermesProfileRuntimeBridge({
  fetch: (...arguments_) => globalThis.fetch(...arguments_),
})


function desktopWebView() {
  return (window as Window & { chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void; removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void } } }).chrome?.webview
}

function formatBytes(value?: number) {
  if (!value || value < 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let amount = value
  let unit = 0
  while (amount >= 1024 && unit < units.length - 1) { amount /= 1024; unit += 1 }
  return `${amount >= 10 || unit === 0 ? amount.toFixed(0) : amount.toFixed(1)} ${units[unit]}`
}

function formatUptime(seconds?: number) {
  if (seconds === undefined) return '—'
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  return days ? `${days}d ${hours}h` : `${hours}h ${Math.floor((seconds % 3_600) / 60)}m`
}

function percent(value?: number) {
  return Math.max(0, Math.min(100, value ?? 0))
}

function serenaTitle(snapshot: SerenaHealthSnapshot | null) {
  if (!snapshot) return 'Checking Serena'
  if (snapshot.state === 'ready') return `${snapshot.toolCount} tool${snapshot.toolCount === 1 ? '' : 's'} ready`
  if (snapshot.state === 'disabled') return 'Configured but disabled'
  if (snapshot.state === 'missing') return 'Configuration missing'
  if (snapshot.state === 'authentication-required') return 'Sign-in required'
  return 'Connection needs attention'
}

type Props = { accountRequest?: number }

export function HermesSystemWorkspace({ accountRequest = 0 }: Props) {
  const [section, setSection] = useState<Section>('overview')
  const [status, setStatus] = useState<HermesStatusSnapshot | null>(null)
  const [stats, setStats] = useState<HermesSystemStats | null>(null)
  const [providers, setProviders] = useState<HermesAuthProvider[]>([])
  const [identity, setIdentity] = useState<HermesIdentity | null>(null)
  const [compatibility, setCompatibility] = useState<HermesCompatibilitySnapshot | null>(null)
  const [serena, setSerena] = useState<SerenaHealthSnapshot | null>(null)
  const [serenaLoading, setSerenaLoading] = useState(true)
  const [loading, setLoading] = useState(true)
  const [working, setWorking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedProvider, setSelectedProvider] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')

  const load = useCallback(async (quiet = false) => {
    if (!quiet) setLoading(true)
    setError(null)
    try {
      const [nextStatus, runtimeIdentity] = await Promise.all([
        hermesSystemAdapter.status(),
        hermesCompatibilityAdapter.identity(),
      ])
      setStatus(nextStatus)
      setCompatibility(evaluateHermesCompatibility(nextStatus.version, runtimeIdentity))

      let nextIdentity: HermesIdentity | null = null
      let nextProviders: HermesAuthProvider[] = []
      if (nextStatus.auth.required) {
        ;[nextIdentity, nextProviders] = await Promise.all([
          hermesSystemAdapter.identity(),
          hermesSystemAdapter.providers(),
        ])
      }
      setIdentity(nextIdentity)
      setProviders(nextProviders)
      setSelectedProvider((current) => current || nextProviders.find((provider) => provider.supportsPassword)?.name || nextProviders[0]?.name || '')

      if (!nextStatus.auth.required || nextIdentity) setStats(await hermesSystemAdapter.stats())
      else setStats(null)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not load Hermes system status.')
    } finally {
      setLoading(false)
    }
  }, [])

  const loadSerena = useCallback(async () => {
    setSerenaLoading(true)
    try { setSerena(await serenaHealthAdapter.inspect()) }
    finally { setSerenaLoading(false) }
  }, [])

  useEffect(() => {
    void load()
    const timer = window.setInterval(() => void load(true), 30_000)
    return () => window.clearInterval(timer)
  }, [load])

  useEffect(() => {
    if (accountRequest > 0) setSection('account')
  }, [accountRequest])

  useEffect(() => subscribeHermesAuthChanged(() => void load()), [load])

  useEffect(() => {
    void loadSerena()
    const timer = window.setInterval(() => void loadSerena(), 120_000)
    return () => window.clearInterval(timer)
  }, [loadSerena])

  useEffect(() => {
    const host = desktopWebView()
    if (!host) return
    const receive = (event: MessageEvent) => {
      const frame = event.data as DesktopAuthFrame
      if (frame?.type === 'auth.completed') announceHermesAuthChanged('signed-in')
      if (frame?.type === 'auth.error') setError(frame.message || 'Hermes sign-in failed.')
    }
    host.addEventListener('message', receive)
    return () => host.removeEventListener('message', receive)
  }, [])

  const passwordProviders = useMemo(() => providers.filter((provider) => provider.supportsPassword), [providers])
  const oauthProviders = useMemo(() => providers.filter((provider) => !provider.supportsPassword), [providers])

  async function login(event: FormEvent) {
    event.preventDefault()
    setWorking(true)
    setError(null)
    try {
      await hermesSystemAdapter.passwordLogin(selectedProvider, username, password)
      setPassword('')
      announceHermesAuthChanged('signed-in')
    } catch (reason) {
      setPassword('')
      setError(reason instanceof Error ? reason.message : 'Hermes sign-in failed.')
    } finally {
      setWorking(false)
    }
  }

  function oauthLogin(provider: HermesAuthProvider) {
    setError(null)
    const host = desktopWebView()
    if (host) host.postMessage({ type: 'auth.open', version: 1, provider: provider.name })
    else window.location.assign(`/auth/login?provider=${encodeURIComponent(provider.name)}&next=${encodeURIComponent('/workbench-auth-complete')}`)
  }

  async function logout() {
    setWorking(true)
    setError(null)
    try {
      await hermesSystemAdapter.logout()
      announceHermesAuthChanged('signed-out')
      setWorking(false)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not sign out of Hermes.')
      setWorking(false)
    }
  }

  const accountLabel = !status?.auth.required
    ? 'Local trusted session'
    : identity?.displayName || identity?.email || identity?.userId || 'Sign-in required'
  const sectionTitle = section === 'overview'
    ? 'Hermes system overview'
    : section === 'account'
      ? 'Hermes account'
      : section === 'connections'
        ? 'Connections & credentials'
        : section === 'runtime'
        ? 'Runtime & model providers'
        : section === 'speech-voice'
          ? 'Speech & Voice'
        : section === 'integrations'
          ? 'Serena integration'
          : section === 'mcp'
            ? 'MCP & tools'
            : section === 'skills'
              ? 'Hermes skills'
              : section === 'extensions'
                ? 'Advanced extensions'
                : section === 'profiles'
                  ? 'Profiles & agent runtime'
                  : 'Advanced session administration'
  const sectionSubtitle = section === 'connections'
    ? 'Machine-bound provider access with native review'
    : section === 'speech-voice'
      ? 'Local microphone input and Kokoro speech output'
    : section === 'integrations'
    ? 'Hermes container → host-local Serena'
    : section === 'mcp'
      ? 'Upstream Hermes MCP management with explicit provenance'
      : section === 'skills'
        ? 'Preview, scan, and manage Hermes capabilities'
        : section === 'extensions'
          ? 'Verified live catalog with one reviewed model assignment per write'
          : section === 'profiles'
            ? 'Verified live profile facts with every unsupported action removed'
            : section === 'session-admin'
              ? 'Branches, imports, exports, statistics, and model locks'
              : status ? `Hermes ${status.version} · ${status.gateway.state}` : 'Connecting to Hermes…'
  const sectionIcon = section === 'overview'
    ? <Activity size={17} />
    : section === 'account'
      ? <UserRound size={17} />
      : section === 'connections'
        ? <KeyRound size={17} />
        : section === 'runtime'
        ? <Gauge size={17} />
        : section === 'speech-voice'
          ? <Radio size={17} />
        : section === 'integrations'
          ? <Cable size={17} />
          : section === 'mcp'
            ? <Package size={17} />
            : section === 'skills'
              ? <LibraryBig size={17} />
              : section === 'extensions'
                ? <SlidersHorizontal size={17} />
                : section === 'profiles'
                  ? <Layers3 size={17} />
                  : <Bot size={17} />
  const isLiveProfileRuntime = section === 'profiles'
  const isLiveExtensionSettings = section === 'extensions'
  const isLiveSessionAdmin = section === 'session-admin'
  const headerHealth = section === 'integrations'
    ? serena?.state === 'ready' ? 'ok' : serena?.state === 'error' ? 'degraded' : 'unknown'
    : status?.overall ?? 'unknown'
  const headerHealthLabel = isLiveProfileRuntime ? 'live read-only beta' : isLiveExtensionSettings ? 'live model beta' : isLiveSessionAdmin ? 'live read-only beta' : section === 'integrations' ? serena?.state ?? 'checking' : status?.overall ?? 'checking'

  return (
    <>
      <aside className="system-sidebar">
        <header><span>CONTROL CENTER</span><button type="button" title="Refresh status" onClick={() => void Promise.all([load(), loadSerena()])} disabled={loading || serenaLoading}><RefreshCw className={loading || serenaLoading ? 'spin' : ''} size={14} /></button></header>
        <nav>
          <button type="button" className={section === 'overview' ? 'active' : ''} onClick={() => setSection('overview')}><Activity size={15} /><span><strong>Overview</strong><small>Health and services</small></span></button>
          <button type="button" className={section === 'account' ? 'active' : ''} onClick={() => setSection('account')}><UserRound size={15} /><span><strong>Account</strong><small>{accountLabel}</small></span></button>
          <button type="button" className={section === 'connections' ? 'active' : ''} onClick={() => setSection('connections')}><KeyRound size={15} /><span><strong>Connections</strong><small>Native credential vault</small></span></button>
          <button type="button" className={section === 'runtime' ? 'active' : ''} onClick={() => setSection('runtime')}><Gauge size={15} /><span><strong>Runtime</strong><small>Models and endpoints</small></span></button>
          <button type="button" className={section === 'speech-voice' ? 'active' : ''} onClick={() => setSection('speech-voice')}><Radio size={15} /><span><strong>Speech &amp; Voice</strong><small>Local mic and speaking voice</small></span></button>
          <button type="button" className={section === 'integrations' ? 'active' : ''} onClick={() => setSection('integrations')}><Cable size={15} /><span><strong>Integrations</strong><small>{serenaTitle(serena)}</small></span></button>
          <button type="button" className={section === 'mcp' ? 'active' : ''} onClick={() => setSection('mcp')}><Package size={15} /><span><strong>MCP & tools</strong><small>Nous catalog and servers</small></span></button>
          <button type="button" className={section === 'skills' ? 'active' : ''} onClick={() => setSection('skills')}><LibraryBig size={15} /><span><strong>Skills</strong><small>Preview and security scan</small></span></button>
          <div className="system-nav-divider"><span>WORKBENCH LABS</span></div>
          <button type="button" className={section === 'extensions' ? 'active' : ''} onClick={() => setSection('extensions')}><SlidersHorizontal size={15} /><span><strong>Advanced extensions</strong><small>Live model assignments</small></span></button>
          <button type="button" className={section === 'profiles' ? 'active' : ''} onClick={() => setSection('profiles')}><Layers3 size={15} /><span><strong>Profiles & runtime</strong><small>Verified read-only beta</small></span></button>
          <button type="button" className={section === 'session-admin' ? 'active' : ''} onClick={() => setSection('session-admin')}><Bot size={15} /><span><strong>Session administration</strong><small>Verified read-only beta</small></span></button>
        </nav>
        <footer><ShieldCheck size={12} /> system v{HERMES_SYSTEM_ADAPTER_VERSION} · Serena v{SERENA_HEALTH_ADAPTER_VERSION} · MCP v{HERMES_MCP_ADAPTER_VERSION} · Skills v{HERMES_SKILLS_ADAPTER_VERSION}</footer>
      </aside>

      <main className="system-workspace">
        <header className="system-titlebar">
          <span className="system-title-icon">{sectionIcon}</span>
          <div><strong>{sectionTitle}</strong><small>{sectionSubtitle}</small></div>
          <span className={`system-health ${headerHealth}`}>{headerHealth === 'ok' ? <CheckCircle2 size={13} /> : <CircleAlert size={13} />}{headerHealthLabel}</span>
        </header>

        <div className="system-content">
          {loading && !status ? <div className="system-loading"><LoaderCircle className="spin" size={24} /><strong>Reading Hermes status…</strong></div> : null}
          {error && <div className="system-error"><CircleAlert size={16} /><span><strong>Control Center needs attention</strong><small>{error}</small></span><button type="button" onClick={() => void load()}>Retry</button></div>}

          {status && section === 'overview' && <>
            <div className="system-hero">
              <span className={`system-orb ${status.gateway.running ? 'online' : 'offline'}`}><Server size={25} /></span>
              <div><small>HERMES GATEWAY</small><strong>{status.gateway.running ? 'Online and ready' : 'Gateway is stopped'}</strong><p>{status.gateway.busy ? `${status.gateway.activeAgents} active agent${status.gateway.activeAgents === 1 ? '' : 's'} working now` : `${status.activeSessions} recent session${status.activeSessions === 1 ? '' : 's'} · idle`}</p></div>
              <i>{status.gateway.running ? <Wifi size={18} /> : <CircleAlert size={18} />}</i>
            </div>
            <section className="system-card-grid">
              <article><span><Activity size={15} /></span><small>Gateway state</small><strong>{status.gateway.state}</strong><p>{status.gateway.drainable ? 'Safe-drain supported' : 'Not currently drainable'}</p></article>
              <article><span><UserRound size={15} /></span><small>Active sessions</small><strong>{status.activeSessions}</strong><p>{status.gateway.activeAgents} agent turns running</p></article>
              <article><span><LockKeyhole size={15} /></span><small>Dashboard security</small><strong>{status.auth.required ? 'Signed gate' : 'Loopback only'}</strong><p>{status.auth.providers.length ? status.auth.providers.join(', ') : 'Local trust boundary'}</p></article>
              <article><span><Server size={15} /></span><small>Hermes release</small><strong>{status.version}</strong><p>{status.releaseDate || 'Current container image'}</p></article>
              <article className={`compatibility-card ${compatibility?.state ?? 'unverified'}`} title={compatibility?.detail}><span><ShieldCheck size={15} /></span><small>Adapter contract</small><strong>{compatibility?.title ?? 'Checking identity'}</strong><p>{compatibility?.imageId ? `image ${compatibility.imageId.slice(7, 19)}…` : compatibility?.detail || 'Reading the runtime identity'}</p></article>
              <article className={`serena-status-card ${serena?.state ?? 'checking'}`} title={serena?.detail}><span><Cable size={15} /></span><small>Serena code tools</small><strong>{serenaTitle(serena)}</strong><p>{serena?.detail || 'Verifying the Hermes-to-Serena tool path.'}</p></article>
            </section>
            <section className="component-health"><header><strong>Component health</strong><small>Auto-refreshes every 30 seconds</small></header>{status.components.map((component) => <div key={component.name}><span className={component.status} /><strong>{component.name}</strong><small>{component.state || component.status}</small></div>)}</section>
          </>}

          {status && section === 'account' && <section className="account-surface">
            {!status.auth.required ? <div className="account-current"><span><ShieldCheck size={24} /></span><div><small>LOCAL TRUST BOUNDARY</small><strong>No dashboard sign-in required</strong><p>Hermes is bound to loopback and only available on this computer.</p></div></div>
              : identity ? <>
                <div className="account-current"><span><UserRound size={24} /></span><div><small>CONNECTED HERMES ACCOUNT</small><strong>{accountLabel}</strong><p>Provider {identity.provider || 'current'}{identity.organizationId ? ` · organization ${identity.organizationId}` : ''}</p></div><button type="button" onClick={() => void logout()} disabled={working}><LogOut size={14} /> Sign out</button></div>
                <div className="security-note"><ShieldCheck size={16} /><span><strong>Session-cookie authentication</strong><small>The Workbench receives identity metadata only. Tokens and passwords are never stored by React.</small></span></div>
              </> : <>
                <div className="account-intro"><span><LockKeyhole size={23} /></span><div><small>PROTECTED HERMES DASHBOARD</small><strong>Connect the Workbench</strong><p>Use the account created during installation. Your password is sent once to the local Hermes container and immediately cleared from this form.</p></div></div>
                {passwordProviders.length > 0 && <form className="account-login" onSubmit={login}>
                  {passwordProviders.length > 1 && <label><span>Provider</span><select value={selectedProvider} onChange={(event) => setSelectedProvider(event.target.value)}>{passwordProviders.map((provider) => <option value={provider.name} key={provider.name}>{provider.displayName}</option>)}</select></label>}
                  <label><span>Username</span><input autoComplete="username" value={username} onChange={(event) => setUsername(event.target.value)} /></label>
                  <label><span>Password</span><input type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} /></label>
                  <button type="submit" disabled={working || !selectedProvider || !username.trim() || !password}>{working ? <LoaderCircle className="spin" size={14} /> : <LogIn size={14} />} Connect Hermes</button>
                </form>}
                {oauthProviders.length > 0 && <div className="oauth-options"><header><span>Or use a connected provider</span></header>{oauthProviders.map((provider) => <button type="button" key={provider.name} onClick={() => oauthLogin(provider)}><KeyRound size={14} /><span><strong>{provider.displayName}</strong><small>Open secure provider sign-in</small></span><LogIn size={13} /></button>)}</div>}
              </>}
          </section>}
          {section === 'connections' && <HermesConnectionsWorkspace profileId="default" catalog={nativeConnectionCatalog} />}
          {section === 'speech-voice' && <HermesSpeechVoiceWorkspace profileId="default" />}

          {status && section === 'runtime' && <section className="runtime-surface">
            {!stats ? <div className="system-locked"><LockKeyhole size={23} /><strong>Sign in to view runtime telemetry</strong><p>Host details remain behind the Hermes authentication gate.</p><button type="button" onClick={() => setSection('account')}>Open account</button></div> : <>
              <div className="runtime-identity"><span><Cpu size={21} /></span><div><small>HERMES RUNTIME</small><strong>{stats.hostname || 'Hermes container'}</strong><p>{stats.operatingSystem} · {stats.architecture || 'architecture unknown'} · Python {stats.pythonVersion || 'unknown'}</p></div><i>PID {stats.process?.pid ?? '—'}</i></div>
              <div className="runtime-meters">
                <article><header><span><Cpu size={14} /> CPU</span><strong>{stats.cpuPercent === undefined ? '—' : `${Math.round(stats.cpuPercent)}%`}</strong></header><div><i style={{ width: `${percent(stats.cpuPercent)}%` }} /></div><small>{stats.cpuCount ?? '—'} logical cores</small></article>
                <article><header><span><Gauge size={14} /> Memory</span><strong>{stats.memory ? `${Math.round(stats.memory.percent)}%` : '—'}</strong></header><div><i style={{ width: `${percent(stats.memory?.percent)}%` }} /></div><small>{formatBytes(stats.memory?.used)} of {formatBytes(stats.memory?.total)}</small></article>
                <article><header><span><HardDrive size={14} /> Storage</span><strong>{stats.disk ? `${Math.round(stats.disk.percent)}%` : '—'}</strong></header><div><i style={{ width: `${percent(stats.disk?.percent)}%` }} /></div><small>{formatBytes(stats.disk?.free)} free</small></article>
              </div>
              <div className="runtime-details"><div><small>Uptime</small><strong>{formatUptime(stats.uptimeSeconds)}</strong></div><div><small>Process memory</small><strong>{formatBytes(stats.process?.rss)}</strong></div><div><small>Process threads</small><strong>{stats.process?.threads ?? '—'}</strong></div><div><small>Hermes runtime</small><strong>{stats.hermesVersion || status.version}</strong></div></div>
              <HermesRuntimeConfigurationWorkspace onOpenConnections={() => setSection('connections')} />
            </>}
          </section>}

          {status && section === 'integrations' && <section className="integration-surface">
            <div className={`integration-hero ${serena?.state ?? 'checking'}`}>
              <span><Cable size={25} /></span>
              <div><small>HOST-LOCAL CODE INTELLIGENCE</small><strong>{serenaTitle(serena)}</strong><p>{serena?.detail || 'Hermes is checking its Serena MCP connection.'}</p></div>
              <button type="button" onClick={() => void loadSerena()} disabled={serenaLoading}><RefreshCw className={serenaLoading ? 'spin' : ''} size={14} /> Verify now</button>
            </div>

            {serena && <div className="integration-route">
              <div><small>Hermes MCP entry</small><strong>{serena.state === 'authentication-required' ? 'Protected by sign-in' : serena.configured ? 'Configured' : 'Not configured'}</strong></div>
              <div><small>Transport</small><strong>{serena.state === 'authentication-required' ? 'Available after sign-in' : serena.transport}</strong></div>
              <div><small>Endpoint</small><strong>{serena.state === 'authentication-required' ? 'Protected' : serena.endpoint || 'Not exposed'}</strong></div>
              <div><small>Last checked</small><strong>{new Date(serena.observedAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</strong></div>
            </div>}

            <section className="serena-tools">
              <header><span><Wrench size={14} /><strong>Discovered tools</strong></span><small>{serena?.state === 'ready' ? `${serena.toolCount} available · ${serena.prompts} prompts · ${serena.resources} resources` : 'Available after a successful probe'}</small></header>
              {serena?.tools.length ? <div className="serena-tool-grid">{serena.tools.map((tool) => <article key={tool.name}><span><Code2 size={13} /></span><div><strong>{tool.name}</strong><p>{tool.description || 'Serena code-intelligence tool'}</p></div></article>)}</div>
                : <div className="serena-tools-empty"><Cable size={22} /><strong>{serenaLoading ? 'Discovering Serena tools…' : serenaTitle(serena)}</strong><p>{serena?.detail || 'Tool metadata will appear here after verification.'}</p>{serena?.state === 'authentication-required' && <button type="button" onClick={() => setSection('account')}>Open Hermes account</button>}</div>}
            </section>
          </section>}

          {status && section === 'mcp' && <HermesMcpWorkspace />}
          {status && section === 'skills' && <HermesSkillsWorkspace />}
          {isLiveProfileRuntime && <aside className="workbench-labs-banner live" role="note"><ShieldCheck size={16} /><span><strong>Live read-only beta</strong><small>Profile inventory, active-profile attribution, SOUL, account disposition, and safe configuration metadata come from verified Hermes routes. Provider connection status stays unavailable until Hermes supplies a renderer-safe projection. Change and export controls are not rendered.</small></span></aside>}
          {isLiveExtensionSettings && <aside className="workbench-labs-banner live" role="note"><ShieldCheck size={16} /><span><strong>Live model assignments beta</strong><small>Catalog and assignment state come from verified Hermes routes. Only one main or auxiliary assignment can be reviewed and written at a time; unsupported extension writes are not shown.</small></span></aside>}
          {isLiveSessionAdmin && <aside className="workbench-labs-banner live" role="note"><ShieldCheck size={16} /><span><strong>Live read-only beta</strong><small>Session list, honest statistics, latest-descendant paths, and bounded text export use verified Hermes routes. Unsupported mutations remain visible but disabled.</small></span></aside>}
          {section === 'extensions' && <HermesExtensionSettingsWorkspace controller={liveExtensionSettingsController} mode="live-model-assignments" />}
          {section === 'profiles' && <HermesProfileRuntimeWorkspace adapter={liveProfileRuntimeBridge} initialProfileId="default" interactionMode="read-only" />}
          {section === 'session-admin' && <HermesSessionAdminWorkspace profileId="default" adapter={liveHermesSessionAdminAdapter} />}
        </div>
      </main>
    </>
  )
}
