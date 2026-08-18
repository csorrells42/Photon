import { lazy, Suspense, useEffect, useState } from 'react'
import { AlertCircle, Bot, CheckCircle2, FileWarning, MonitorUp, ScrollText, TerminalSquare } from 'lucide-react'
import { HermesConsolePanel } from '../HermesConsole/HermesConsolePanel'
import type { DeveloperBuildResult } from '../DeveloperServices/DesktopDeveloperServicesClient'
import { workspaceRelativePath } from '../DeveloperServices/DesktopDeveloperServicesClient'
import { isNativeTerminalAvailable } from './DesktopHostTerminalClient'
import type { NativeTerminalCommandRequest } from './DesktopHostTerminalClient'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'

const NativeTerminalSurface = lazy(() => import('./NativeTerminalSurface').then((module) => ({ default: module.NativeTerminalSurface })))

type TerminalTab = 'powershell' | 'hermes' | 'output' | 'problems'

export function TerminalDock({ buildResult, commandRequest, onCommandHandled, onOpenWorkspacePath }: { buildResult?: DeveloperBuildResult | null; commandRequest?: NativeTerminalCommandRequest | null; onCommandHandled?: (nonce: number) => void; onOpenWorkspacePath?: (path: string) => void }) {
  const [assistantName] = useAssistantDisplayName()
  const [active, setActive] = useState<TerminalTab>('powershell')
  const [nativeAvailable, setNativeAvailable] = useState(isNativeTerminalAvailable())

  useEffect(() => {
    const ready = () => setNativeAvailable(isNativeTerminalAvailable())
    window.addEventListener('hermes-desktop-ready', ready)
    return () => window.removeEventListener('hermes-desktop-ready', ready)
  }, [])

  useEffect(() => {
    if (!buildResult) return
    setActive(buildResult.diagnostics.length > 0 ? 'problems' : 'output')
  }, [buildResult?.requestId])

  useEffect(() => {
    if (commandRequest) setActive('powershell')
  }, [commandRequest?.nonce])

  const output = buildResult ? [buildResult.output.standardOutput, buildResult.output.standardError].filter(Boolean).join('\n') : ''
  const operationLabel = buildResult?.operation === 'analyze' ? 'Analysis' : 'Build'
  const operationStatus = buildResult
    ? buildResult.wasCancelled ? 'cancelled' : buildResult.succeeded ? 'succeeded' : 'failed'
    : ''

  return (
    <section className="terminal-panel terminal-dock">
      <div className="terminal-tabs terminal-dock-tabs">
        <button type="button" className={active === 'powershell' ? 'active' : ''} onClick={() => setActive('powershell')}><TerminalSquare size={12} /> POWERSHELL</button>
        <button type="button" className={active === 'hermes' ? 'active' : ''} onClick={() => setActive('hermes')}><Bot size={12} /> {assistantName.toLocaleUpperCase()} CONSOLE</button>
        <button type="button" className={active === 'output' ? 'active' : ''} onClick={() => setActive('output')}><ScrollText size={12} /> OUTPUT</button>
        <button type="button" className={active === 'problems' ? 'active' : ''} onClick={() => setActive('problems')}><FileWarning size={12} /> PROBLEMS <b>{buildResult?.diagnostics.length ?? 0}</b></button><span className="terminal-spacer" />
      </div>
      <div className={`terminal-dock-surface ${active === 'powershell' ? 'active' : ''}`}>
        {nativeAvailable
          ? <Suspense fallback={<div className="native-terminal-unavailable"><TerminalSquare size={18} /><span><strong>Loading terminal renderer…</strong></span></div>}><NativeTerminalSurface commandRequest={commandRequest} onCommandHandled={onCommandHandled} /></Suspense>
          : <div className="native-terminal-unavailable"><MonitorUp size={20} /><span><strong>PowerShell is available in the desktop app</strong><small>The browser keeps the same Workbench, but native command execution stays disabled for security. Use the {assistantName} Console tab here.</small></span></div>}
      </div>
      <div className={`terminal-dock-surface ${active === 'hermes' ? 'active' : ''}`}><HermesConsolePanel embedded /></div>
      <div className={`terminal-dock-surface ${active === 'output' ? 'active' : ''}`}>
        {buildResult ? <div className="developer-output">
          <header className={buildResult.succeeded ? 'success' : buildResult.wasCancelled ? 'cancelled' : 'failed'}>{buildResult.succeeded ? <CheckCircle2 size={13} /> : <AlertCircle size={13} />}<strong>{operationLabel} {operationStatus}</strong>{buildResult.output.truncated && <span>Output truncated · {buildResult.output.droppedCharacters} characters omitted</span>}</header>
          <pre>{output || `${operationLabel} completed without console output.`}</pre>
        </div> : <div className="developer-terminal-empty"><ScrollText size={18} /><strong>No developer output yet</strong><small>Choose Run, then build or analyze a discovered .NET target.</small></div>}
      </div>
      <div className={`terminal-dock-surface ${active === 'problems' ? 'active' : ''}`}>
        {buildResult?.diagnostics.length ? <div className="developer-problems" role="list" aria-label="Build problems">
          {buildResult.diagnostics.map((diagnostic, index) => {
            const relative = workspaceRelativePath(diagnostic.filePath, buildResult.workspaceRoot)
            return <button type="button" role="listitem" key={`${diagnostic.filePath}:${diagnostic.range.start.line}:${diagnostic.code}:${index}`} disabled={!relative} onClick={() => relative && onOpenWorkspacePath?.(relative)}>
              <span className={diagnostic.severity}><AlertCircle size={13} /></span>
              <span><strong>{diagnostic.message}</strong><small>{relative ?? 'Outside workspace'}:{diagnostic.range.start.line}:{diagnostic.range.start.column} · {diagnostic.code || diagnostic.source}</small></span>
            </button>
          })}
        </div> : <div className="developer-terminal-empty"><CheckCircle2 size={18} /><strong>No developer problems</strong><small>{buildResult ? `The latest ${buildResult.operation} produced no compiler diagnostics.` : 'Build and analysis diagnostics will appear here.'}</small></div>}
      </div>
    </section>
  )
}
