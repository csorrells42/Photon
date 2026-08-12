import { AlertTriangle, CheckCircle2, FileSearch2, LoaderCircle, Play, RefreshCw, Square, Wrench } from 'lucide-react'
import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { buildLanguageToolingReports, createLanguageToolingRequestId, DesktopLanguageToolingHostAdapter, type DesktopLanguageToolingPublication, type DesktopLanguageToolingResult } from '../LanguageToolingProviders'
import { canRunDeveloperOperation } from './DeveloperOperationGate'
import type { DeveloperBuildController } from './useDeveloperBuild'
import { desktopDotNetDebuggerController } from './DesktopDotNetDebuggerClient'
import { configureRaspberryPiTarget, type RaspberryPiSetupResult } from './DesktopRaspberryPiSetupClient'
import './DeveloperServicesPanel.css'

const languageToolingHost = new DesktopLanguageToolingHostAdapter()
let languageToolingRevision = 0

export function DeveloperServicesPanel({ controller, selectedWorkspacePath, onLanguageToolingResult }: { controller: DeveloperBuildController; selectedWorkspacePath?: string | null; onLanguageToolingResult?: (publication: DesktopLanguageToolingPublication) => void }) {
  const buildProvider = controller.description?.providers.find((item) => item.build.supported)
  const lspProvider = controller.description?.providers.find((item) => item.lsp.supported)
  const dapProvider = controller.description?.providers.find((item) => item.dap.supported)
  const state = buildProvider?.availability.state ?? controller.description?.availability.state ?? 'unknown'
  const operation = controller.activeOperation === 'analyze' ? 'analysis' : 'build'
  const languageTooling = buildLanguageToolingReports(controller.description?.languageTooling)
  const dotnetTestsReady = Boolean(languageTooling.find((item) => item.id === 'dotnet')?.capabilities
    .some((capability) => capability.id === 'dotnet.tests' && capability.availability === 'available'))
  const [raspberryPiSetup, setRaspberryPiSetup] = useState<RaspberryPiSetupResult | null>(null)
  const [raspberryPiSetupBusy, setRaspberryPiSetupBusy] = useState(false)
  const configureRaspberryPi = async () => {
    if (raspberryPiSetupBusy) return
    setRaspberryPiSetupBusy(true)
    try {
      const result = await configureRaspberryPiTarget()
      setRaspberryPiSetup(result)
      if (result.succeeded) controller.refresh()
    } finally {
      setRaspberryPiSetupBusy(false)
    }
  }
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
      <DotNetTestPanel target={controller.selectedTarget} ready={dotnetTestsReady && controller.desktopAvailable} disabled={controller.busy || controller.describing} />
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
          const setupRequired = item.capabilities.some((capability) => capability.code === 'trusted-target-not-configured')
          const status = ready ? 'Host verified' : partial ? `${available}/${item.capabilities.length} host verified` : setupRequired ? 'Setup required' : known > 0 ? 'Not installed' : 'Adapter pending'
          return <div key={item.id}>
            <span>
              <b>{item.label}</b>
              <small>{item.capabilities.map((capability) => capability.label).join(' · ')}</small>
              {item.id === 'raspberry-pi' && <button type="button" className="developer-raspberry-setup" onClick={() => void configureRaspberryPi()} disabled={raspberryPiSetupBusy || controller.busy}>{raspberryPiSetupBusy ? 'Opening setup…' : 'Configure trusted target'}</button>}
              {item.id === 'raspberry-pi' && raspberryPiSetup && <small role="status" className={raspberryPiSetup.succeeded ? '' : 'error'}>{raspberryPiSetup.message}</small>}
            </span>
            <em className={ready || partial ? 'ready' : ''}>{status}</em>
          </div>
        })}
      </section>
      <CurrentFileTooling selectedWorkspacePath={selectedWorkspacePath} reports={languageTooling} onResult={onLanguageToolingResult} />
      <DotNetDebuggerPanel providerReady={dapProvider?.availability.state === 'available'} configuration={controller.configuration} />
    </div>
    <div className="outline-row"><span>One product · modular toolchains</span></div>
  </aside>
}

function DotNetTestPanel({ target, ready, disabled }: { target: string; ready: boolean; disabled: boolean }) {
  const [selection, setSelection] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<DesktopLanguageToolingResult | null>(null)
  const active = useRef<AbortController | null>(null)
  useEffect(() => () => active.current?.abort(), [])
  const runnable = ready && Boolean(target) && !disabled && !busy && languageToolingHost.available()

  const runTests = async () => {
    if (!runnable) return
    active.current?.abort()
    const cancellation = new AbortController()
    active.current = cancellation
    setBusy(true)
    setResult(null)
    try {
      const completed = await languageToolingHost.request({
        contract: 'language-tooling-providers/v1',
        operation: 'run-tests',
        requestId: createLanguageToolingRequestId('dotnet-tests'),
        workspaceId: 'active-workspace',
        providerId: 'dotnet',
        targetPath: target,
        ...(selection.trim() ? { selection: selection.trim() } : {}),
      }, cancellation.signal)
      setResult(completed)
    } finally {
      if (active.current === cancellation) {
        active.current = null
        setBusy(false)
      }
    }
  }

  return <section className="developer-provider-catalog developer-dotnet-tests" aria-label=".NET tests">
    <header><strong>.NET Tests</strong><small>{ready ? 'Fixed dotnet test operation' : 'Test provider unavailable'}</small></header>
    <label className="developer-field"><span>Optional test filter</span><input aria-label=".NET test filter" value={selection} onChange={(event) => setSelection(event.target.value.slice(0, 512))} disabled={!ready || busy || disabled} /></label>
    {busy
      ? <button type="button" className="developer-build-button stop" onClick={() => active.current?.abort()}><Square size={12} fill="currentColor" /> Stop tests</button>
      : <button type="button" className="developer-build-button" onClick={() => void runTests()} disabled={!runnable}><Play size={13} /> Run tests</button>}
    {result && <div className={`developer-build-message ${result.succeeded ? '' : 'error'}`} role="status" aria-live="polite"><span>{result.message} {result.diagnostics.length ? `(${result.diagnostics.length} problem${result.diagnostics.length === 1 ? '' : 's'})` : ''}</span></div>}
  </section>
}

function DotNetDebuggerPanel({ providerReady, configuration }: { providerReady: boolean; configuration: 'Debug' | 'Release' }) {
  const state = useSyncExternalStore(desktopDotNetDebuggerController.subscribe, desktopDotNetDebuggerController.getSnapshot, desktopDotNetDebuggerController.getSnapshot)
  const [attachPid, setAttachPid] = useState('')
  const [argumentsText, setArgumentsText] = useState('')
  const [stopAtEntry, setStopAtEntry] = useState(false)
  const [expression, setExpression] = useState('')
  useEffect(() => {
    if (providerReady && state.available) void desktopDotNetDebuggerController.refreshTargets(configuration)
  }, [providerReady, state.available, configuration])
  const stopped = state.state === 'stopped'
  const active = !['inactive', 'exited', 'disconnected', 'faulted', 'unknown'].includes(state.state)
  const argumentsList = argumentsText.split('\n').map((item) => item.trim()).filter(Boolean).slice(0, 128)
  return <section className="developer-debugger" aria-label=".NET debugger">
    <header><strong>.NET Debugger</strong><em className={providerReady ? 'ready' : ''}>{providerReady ? state.state : 'Not installed'}</em></header>
    <label className="developer-field"><span>Program</span><select aria-label="Debug program" value={state.selectedProgram} onChange={(event) => desktopDotNetDebuggerController.setSelectedProgram(event.target.value)} disabled={!providerReady || state.busy || active}>
      {!state.targets.length && <option value="">Build a runnable Debug target first</option>}
      {state.targets.map((target) => <option value={target} key={target}>{target}</option>)}
    </select></label>
    <label className="developer-field"><span>Arguments (one per line)</span><textarea aria-label="Debug arguments" rows={2} value={argumentsText} onChange={(event) => setArgumentsText(event.target.value.slice(0, 32 * 1024))} disabled={state.busy || active} /></label>
    <label className="developer-debug-check"><input type="checkbox" checked={stopAtEntry} onChange={(event) => setStopAtEntry(event.target.checked)} disabled={state.busy || active} /> Stop at entry</label>
    <div className="developer-debug-actions">
      <button type="button" onClick={() => void desktopDotNetDebuggerController.refreshTargets(configuration)} disabled={!providerReady || state.busy || active}>Refresh targets</button>
      <button type="button" onClick={() => void desktopDotNetDebuggerController.launch(stopAtEntry, argumentsList)} disabled={!providerReady || state.busy || active || !state.selectedProgram}><Play size={12} /> Launch</button>
    </div>
    <div className="developer-debug-attach">
      <input aria-label=".NET process ID" inputMode="numeric" placeholder="Process ID" value={attachPid} onChange={(event) => setAttachPid(event.target.value.replace(/\D/g, '').slice(0, 10))} disabled={state.busy || active} />
      <button type="button" onClick={() => void desktopDotNetDebuggerController.attach(Number(attachPid))} disabled={!providerReady || state.busy || active || !attachPid}>Attach</button>
    </div>
    {active && <div className="developer-debug-toolbar" role="group" aria-label="Debug controls">
      <button type="button" title="Continue" onClick={() => void desktopDotNetDebuggerController.continue()} disabled={!stopped || state.busy}>Continue</button>
      <button type="button" title="Step over" onClick={() => void desktopDotNetDebuggerController.stepOver()} disabled={!stopped || state.busy}>Over</button>
      <button type="button" title="Step into" onClick={() => void desktopDotNetDebuggerController.stepInto()} disabled={!stopped || state.busy}>Into</button>
      <button type="button" title="Step out" onClick={() => void desktopDotNetDebuggerController.stepOut()} disabled={!stopped || state.busy}>Out</button>
      <button type="button" title="Disconnect" onClick={() => void desktopDotNetDebuggerController.disconnect()} disabled={state.busy}><Square size={10} fill="currentColor" /> Stop</button>
    </div>}
    {state.threads.length > 0 && <div className="developer-debug-list"><strong>Threads</strong>{state.threads.map((thread) => <span key={thread.id} className={thread.id === state.activeThreadId ? 'active' : ''}>{thread.name} <small>#{thread.id}</small></span>)}</div>}
    {state.frames.length > 0 && <div className="developer-debug-list"><strong>Call stack</strong>{state.frames.map((frame) => <button type="button" key={frame.id} className={frame.id === state.activeFrameId ? 'active' : ''} onClick={() => void desktopDotNetDebuggerController.selectFrame(frame)}><span>{frame.name}</span><small>{frame.path ? `${frame.path}:${frame.line}` : `frame ${frame.id}`}</small></button>)}</div>}
    {stopped && <div className="developer-debug-evaluate"><input aria-label="Debug expression" placeholder="Evaluate expression" value={expression} onChange={(event) => setExpression(event.target.value.slice(0, 16 * 1024))} onKeyDown={(event) => { if (event.key === 'Enter') void desktopDotNetDebuggerController.evaluate(expression) }} /><button type="button" onClick={() => void desktopDotNetDebuggerController.evaluate(expression)} disabled={!expression.trim()}>Evaluate</button></div>}
    {state.evaluation && <pre className="developer-debug-output" aria-label="Evaluation result">{state.evaluation}</pre>}
    {state.variables.length > 0 && <div className="developer-debug-variables"><strong>Variables</strong>{state.variables.map((variable, index) => <div key={`${variable.name}-${index}`}><span>{variable.name}</span><code>{variable.value}</code><small>{variable.type}</small></div>)}</div>}
    {state.output && <pre className="developer-debug-output" aria-label="Debug console output">{state.output}</pre>}
    {state.error && <div className="developer-build-message error" role="alert"><AlertTriangle size={13} /><span>{state.error}</span></div>}
    <small>Click the editor gutter to add or remove source breakpoints.</small>
  </section>
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

  if (providerId === 'python' && selectedWorkspacePath) {
    return <PythonToolingActions selectedWorkspacePath={selectedWorkspacePath} report={report} />
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

function PythonToolingActions({ selectedWorkspacePath, report }: { selectedWorkspacePath: string; report?: ReturnType<typeof buildLanguageToolingReports>[number] }) {
  type PythonOperation = 'inspect' | 'syntax' | 'tests' | 'language'
  const [busy, setBusy] = useState<PythonOperation | null>(null)
  const [selection, setSelection] = useState('')
  const [result, setResult] = useState<DesktopLanguageToolingResult | null>(null)
  const active = useRef<AbortController | null>(null)
  useEffect(() => () => active.current?.abort(), [])
  const available = (capabilityId: string) => Boolean(report?.capabilities.some((capability) =>
    capability.id === capabilityId && capability.availability === 'available'))
  const hostReady = languageToolingHost.available()

  const run = async (operation: PythonOperation) => {
    if (busy || !hostReady) return
    const required = operation === 'inspect' ? 'python.project'
      : operation === 'syntax' ? 'python.compiler'
        : operation === 'tests' ? 'python.tests'
          : 'python.lsp'
    if (!available(required)) return
    active.current?.abort()
    const cancellation = new AbortController()
    active.current = cancellation
    setBusy(operation)
    setResult(null)
    try {
      if (operation === 'language') {
        const started = await languageToolingHost.request({
          contract: 'language-tooling-providers/v1', operation: 'start-language-session',
          requestId: createLanguageToolingRequestId('python-language-start'), workspaceId: 'active-workspace',
          providerId: 'python', documentPath: selectedWorkspacePath,
        }, cancellation.signal)
        if (!started.succeeded || !started.sessionId) {
          setResult(started)
          return
        }
        let stopped = await languageToolingHost.request({
          contract: 'language-tooling-providers/v1', operation: 'stop-language-session',
          requestId: createLanguageToolingRequestId('python-language-stop'), workspaceId: 'active-workspace',
          providerId: 'python', sessionId: started.sessionId,
        }, cancellation.signal)
        if (!stopped.succeeded && stopped.code === 'cancelled') {
          const cleanup = new AbortController()
          const cleanupTimeout = window.setTimeout(() => cleanup.abort(), 10_000)
          try {
            const recovered = await languageToolingHost.request({
              contract: 'language-tooling-providers/v1', operation: 'stop-language-session',
              requestId: createLanguageToolingRequestId('python-language-cleanup'), workspaceId: 'active-workspace',
              providerId: 'python', sessionId: started.sessionId,
            }, cleanup.signal)
            if (recovered.succeeded) stopped = { ...recovered, message: 'The Python language check was cancelled and its host session was released.' }
          } finally {
            window.clearTimeout(cleanupTimeout)
          }
        }
        setResult(stopped.succeeded
          ? { ...stopped, message: 'Serena verified the selected Python document and completed a bounded language session.' }
          : stopped)
        return
      }
      const requestId = createLanguageToolingRequestId(`python-${operation}`)
      const completed = operation === 'inspect'
        ? await languageToolingHost.request({
            contract: 'language-tooling-providers/v1', operation: 'inspect-project', requestId,
            workspaceId: 'active-workspace', providerId: 'python', projectPath: selectedWorkspacePath,
          }, cancellation.signal)
        : operation === 'syntax'
          ? await languageToolingHost.request({
              contract: 'language-tooling-providers/v1', operation: 'compile', requestId,
              workspaceId: 'active-workspace', providerId: 'python', targetPath: selectedWorkspacePath, mode: 'check',
            }, cancellation.signal)
          : await languageToolingHost.request({
              contract: 'language-tooling-providers/v1', operation: 'run-tests', requestId,
              workspaceId: 'active-workspace', providerId: 'python', targetPath: selectedWorkspacePath,
              ...(selection.trim() ? { selection: selection.trim() } : {}),
            }, cancellation.signal)
      setResult(completed)
    } finally {
      if (active.current === cancellation) {
        active.current = null
        setBusy(null)
      }
    }
  }

  return <section className="developer-provider-catalog developer-current-file" aria-label="Python tooling">
    <header><strong>Python tooling</strong><small>{selectedWorkspacePath}</small></header>
    <label className="developer-field"><span>Optional unittest filter</span><input aria-label="Python test filter" value={selection} onChange={(event) => setSelection(event.target.value.slice(0, 512))} disabled={busy !== null || !available('python.tests')} /></label>
    {busy
      ? <button type="button" className="developer-build-button stop" onClick={() => active.current?.abort()}><Square size={12} fill="currentColor" /> Stop Python {busy}</button>
      : <div className="developer-operation-actions" role="group" aria-label="Python operations">
          <button type="button" className="developer-build-button" onClick={() => void run('inspect')} disabled={!hostReady || !available('python.project')}><FileSearch2 size={13} /> Inspect project</button>
          <button type="button" className="developer-build-button" onClick={() => void run('syntax')} disabled={!hostReady || !available('python.compiler')}><Play size={13} /> Check syntax</button>
          <button type="button" className="developer-build-button" onClick={() => void run('tests')} disabled={!hostReady || !available('python.tests')}><Play size={13} /> Run unittest</button>
          <button type="button" className="developer-build-button" onClick={() => void run('language')} disabled={!hostReady || !available('python.lsp')}><Play size={13} /> Verify Serena language service</button>
        </div>}
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
