import { AlertTriangle, CheckCircle2, FileSearch2, LoaderCircle, Play, RefreshCw, Square, Wrench } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { buildLanguageToolingReports, createLanguageToolingRequestId, DesktopLanguageToolingHostAdapter, type DesktopLanguageToolingPublication, type DesktopLanguageToolingResult } from '../LanguageToolingProviders'
import { canRunDeveloperOperation } from './DeveloperOperationGate'
import type { DeveloperBuildController } from './useDeveloperBuild'

const languageToolingHost = new DesktopLanguageToolingHostAdapter()
let languageToolingRevision = 0

export function DeveloperServicesPanel({ controller, selectedWorkspacePath, onLanguageToolingResult }: { controller: DeveloperBuildController; selectedWorkspacePath?: string | null; onLanguageToolingResult?: (publication: DesktopLanguageToolingPublication) => void }) {
  const buildProvider = controller.description?.providers.find((item) => item.build.supported)
  const lspProvider = controller.description?.providers.find((item) => item.lsp.supported)
  const dapProvider = controller.description?.providers.find((item) => item.dap.supported)
  const state = buildProvider?.availability.state ?? controller.description?.availability.state ?? 'unknown'
  const operation = controller.activeOperation === 'analyze' ? 'analysis' : 'build'
  const languageTooling = buildLanguageToolingReports(controller.description?.languageTooling)
  return <aside className="explorer developer-services-panel" aria-label="Build and analyze">
    <div className="panel-label">BUILD &amp; ANALYZE <button type="button" aria-label="Refresh developer tools" onClick={controller.refresh} disabled={controller.describing || controller.busy}><RefreshCw className={controller.describing ? 'spin' : undefined} size={12} /></button></div>
    <div className="developer-services-content">
      <section className={`developer-provider-card ${state}`} aria-label="Developer services status">
        <span><Wrench size={15} /></span>
        <div><strong>{buildProvider?.displayName ?? '.NET developer tools'}</strong><small>{statusLabel(controller, state)}</small></div>
        {controller.describing ? <LoaderCircle className="spin" size={14} /> : state === 'available' ? <CheckCircle2 size={14} /> : <AlertTriangle size={14} />}
      </section>
      <label className="developer-field"><span>Target</span><select value={controller.selectedTarget} onChange={(event) => controller.setSelectedTarget(event.target.value)} disabled={controller.busy || !controller.description?.targets.length}>
        {!controller.description?.targets.length && <option value="">No .NET target discovered</option>}
        {controller.description?.targets.map((target) => <option key={target} value={target}>{target}</option>)}
      </select></label>
      <div className="developer-field"><span>Configuration</span><div className="developer-configuration" role="group" aria-label="Developer configuration">
        {(['Debug', 'Release'] as const).map((value) => <button type="button" key={value} className={controller.configuration === value ? 'active' : ''} onClick={() => controller.setConfiguration(value)} disabled={controller.busy}>{value}</button>)}
      </div></div>
      {controller.busy
        ? <button type="button" className="developer-build-button stop" onClick={controller.cancel} disabled={controller.cancelling} aria-label={controller.cancelling ? `Cancelling ${operation}` : `Stop ${operation}`}>
            {controller.cancelling ? <><LoaderCircle className="spin" size={13} /> Cancelling {operation}…</> : <><Square size={13} fill="currentColor" /> Stop {operation}</>}
          </button>
        : <div className="developer-operation-actions" role="group" aria-label="Developer operations">
            <button type="button" className="developer-build-button" onClick={() => void controller.build()} disabled={!controller.desktopAvailable || !canRunDeveloperOperation(controller.description, 'build', controller.selectedTarget)}><Play size={14} /> Build target</button>
            <button type="button" className="developer-build-button" onClick={() => void controller.analyze()} disabled={!controller.desktopAvailable || !canRunDeveloperOperation(controller.description, 'analyze', controller.selectedTarget)}><FileSearch2 size={14} /> Analyze target</button>
          </div>}
      {controller.error && <div className="developer-build-message error" role="alert"><AlertTriangle size={13} /><span>{controller.error}</span></div>}
      {controller.result && <ResultSummary result={controller.result} />}
      <section className="developer-capabilities">
        <header><Wrench size={13} /><strong>Toolchain capabilities</strong></header>
        <Capability label="Compiler / build" provider={buildProvider} />
        <Capability label="Language service" provider={lspProvider} />
        <Capability label="Debugger provider" provider={dapProvider} />
      </section>
      <section className="developer-provider-catalog" aria-label="Language tooling providers">
        <header><strong>Language tooling providers</strong><small>Trusted host availability only</small></header>
        {languageTooling.map((item) => {
          const available = item.capabilities.filter((capability) => capability.availability === 'available').length
          const known = item.capabilities.filter((capability) => capability.availability !== 'unknown').length
          const ready = available === item.capabilities.length
          const partial = available > 0 && !ready
          const status = ready ? 'Host verified' : partial ? `${available}/${item.capabilities.length} host verified` : known > 0 ? 'Not installed' : 'Adapter pending'
          return <div key={item.id}><span><b>{item.label}</b><small>{item.capabilities.map((capability) => capability.label).join(' · ')}</small></span><em className={ready || partial ? 'ready' : ''}>{status}</em></div>
        })}
      </section>
      <CurrentFileTooling selectedWorkspacePath={selectedWorkspacePath} reports={languageTooling} onResult={onLanguageToolingResult} />
    </div>
    <div className="outline-row"><span>One product · modular toolchains</span></div>
  </aside>
}

function CurrentFileTooling({ selectedWorkspacePath, reports, onResult }: { selectedWorkspacePath?: string | null; reports: ReturnType<typeof buildLanguageToolingReports>; onResult?: (publication: DesktopLanguageToolingPublication) => void }) {
  const [boardFqbn, setBoardFqbn] = useState('arduino:avr:uno')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<DesktopLanguageToolingResult | null>(null)
  const active = useRef<AbortController | null>(null)
  useEffect(() => () => active.current?.abort(), [])
  const extension = selectedWorkspacePath?.match(/\.([^.\\/]+)$/)?.[1]?.toLowerCase()
  const providerId = extension === 'ino' ? 'arduino' : ['c', 'cc', 'cpp', 'cxx'].includes(extension ?? '') ? 'gcc' : ['py', 'pyi'].includes(extension ?? '') ? 'python' : null
  const capabilityId = providerId === 'arduino' ? 'arduino.compiler' : providerId === 'gcc' ? 'gcc.compiler' : providerId === 'python' ? 'python.project' : null
  const report = reports.find((item) => item.id === providerId)
  const verified = Boolean(capabilityId && report?.capabilities.some((capability) => capability.id === capabilityId && capability.availability === 'available'))
  const runnable = Boolean(selectedWorkspacePath && providerId && verified && languageToolingHost.available() && !busy && (providerId !== 'arduino' || boardFqbn.trim()))

  const runCurrentFileTool = async () => {
    if (!runnable || !selectedWorkspacePath || !providerId) return
    active.current?.abort()
    const controller = new AbortController()
    active.current = controller
    setBusy(true)
    setResult(null)
    const requestId = createLanguageToolingRequestId(providerId)
    try {
      const completed = providerId === 'python'
        ? await languageToolingHost.request({
            contract: 'language-tooling-providers/v1', operation: 'inspect-project', requestId,
            workspaceId: 'active-workspace', providerId, projectPath: selectedWorkspacePath,
          }, controller.signal)
        : await languageToolingHost.request({
            contract: 'language-tooling-providers/v1', operation: 'compile', requestId,
            workspaceId: 'active-workspace', providerId, targetPath: selectedWorkspacePath, mode: 'check',
            ...(providerId === 'arduino' ? { boardFqbn: boardFqbn.trim() } : {}),
          }, controller.signal)
      setResult(completed)
      if (providerId === 'gcc' || providerId === 'arduino') {
        onResult?.({ requestId, revision: ++languageToolingRevision, providerId, targetPath: selectedWorkspacePath, result: completed })
      }
    } finally {
      if (active.current === controller) {
        active.current = null
        setBusy(false)
      }
    }
  }

  const label = providerId === 'arduino' ? 'Arduino sketch' : providerId === 'gcc' ? 'GNU C/C++ source' : providerId === 'python' ? 'Python source' : 'Select a C, C++, Python, or Arduino source file'
  return <section className="developer-provider-catalog developer-current-file" aria-label="Current file tooling">
    <header><strong>Current file tooling</strong><small>{label}</small></header>
    {providerId === 'arduino' && <label className="developer-field"><span>Board FQBN</span><input value={boardFqbn} onChange={(event) => setBoardFqbn(event.target.value)} disabled={busy} aria-label="Arduino board FQBN" /></label>}
    <button type="button" className="developer-build-button" onClick={() => void runCurrentFileTool()} disabled={!runnable}>
      {busy ? <><LoaderCircle className="spin" size={13} /> Inspecting current file…</> : <><Play size={13} /> {providerId ? providerId === 'python' ? 'Inspect Python project' : `Check with ${providerId === 'gcc' ? 'GCC' : 'Arduino'}` : 'No supported source selected'}</>}
    </button>
    {providerId && !verified && <small role="status">The pinned {providerId === 'gcc' ? 'GCC' : providerId === 'arduino' ? 'Arduino' : 'Python'} toolchain is not installed in this portable build yet.</small>}
    {result && <div className={`developer-build-message ${result.succeeded ? '' : 'error'}`} role="status" aria-live="polite"><span>{result.message} {result.diagnostics.length ? `(${result.diagnostics.length} problem${result.diagnostics.length === 1 ? '' : 's'})` : ''}</span></div>}
  </section>
}

type Provider = NonNullable<DeveloperBuildController['description']>['providers'][number]
function statusLabel(controller: DeveloperBuildController, state: string) {
  if (!controller.desktopAvailable) return 'Desktop app required'
  return controller.description ? `${state} · protocol v${controller.description.protocolVersion}` : 'Inspecting desktop toolchain…'
}
function Capability({ label, provider }: { label: string; provider?: Provider }) {
  const ready = provider?.availability.state === 'available'
  return <div><span>{label}</span><b className={ready ? 'ready' : ''}>{ready ? 'Host verified' : provider ? 'Not installed' : 'Planned'}</b></div>
}
function ResultSummary({ result }: { result: NonNullable<DeveloperBuildController['result']> }) {
  const label = result.operation === 'analyze' ? 'Analysis' : 'Build'
  const status = result.wasCancelled ? 'cancelled' : result.succeeded ? 'succeeded' : 'failed'
  return <section className={`developer-build-summary ${status}`} role="status" aria-live="polite">
    <span>{result.succeeded ? <CheckCircle2 size={15} /> : result.wasCancelled ? <Square size={13} /> : <AlertTriangle size={15} />}</span>
    <div><strong>{label} {status}</strong><small>{result.diagnostics.length} problem{result.diagnostics.length === 1 ? '' : 's'}{typeof result.exitCode === 'number' ? ` · exit ${result.exitCode}` : ''}</small></div>
  </section>
}
