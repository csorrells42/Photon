import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Bot,
  Box,
  Braces,
  ChartNoAxesCombined,
  DraftingCompass,
  ChevronDown,
  ChevronRight,
  Files,
  GitBranch,
  Globe2,
  LayoutPanelLeft,
  Maximize2,
  MessageSquare,
  PanelLeftClose,
  PanelRightClose,
  Play,
  Search,
  Settings,
  Sparkles,
  SplitSquareHorizontal,
  TerminalSquare,
  X,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { AgentDock } from '../Modules/AgentDock/AgentDock'
import type { SessionOpenRequest } from '../Modules/AgentDock/AgentDock'
import { CodexPanel as CodexAgentPanel } from '../Modules/CodexAgent/CodexPanel'
import { SessionSidebar } from '../Modules/HermesSessions/SessionSidebar'
import type { HermesSession } from '../Modules/HermesSessions/HermesSessionApi'
import { HermesSystemWorkspace } from '../Modules/HermesSystem/HermesSystemWorkspace'
import { UsageIntelligenceDashboard } from '../Modules/UsageIntelligence'
import { WorkspaceEditor } from '../Modules/Workspace/WorkspaceEditor'
import { WorkspaceExplorer } from '../Modules/Workspace/WorkspaceExplorer'
import { DeveloperServicesPanel } from '../Modules/DeveloperServices/DeveloperServicesPanel'
import type { DesktopLanguageToolingPublication } from '../Modules/LanguageToolingProviders'
import { useDeveloperBuild } from '../Modules/DeveloperServices/useDeveloperBuild'
import { SourceControlWorkspace } from '../Modules/SourceControl/SourceControlWorkspace'
import { DEFAULT_ASSISTANT_DISPLAY_NAME, useAssistantDisplayName } from '../Modules/AssistantIdentity/AssistantIdentity'
import { desktopDocumentClient } from '../Modules/Workspace/DesktopDocumentClient'
import { BrowserWorkspace } from '../Modules/BrowserWorkspace/BrowserWorkspace'
import type { BrowserOpenRequest } from '../Modules/BrowserWorkspace/BrowserWorkspace'
import type { HermesDesktopUiAction } from '../Modules/HermesGateway/HermesDesktopUiAdapter'
import { workbenchFileStatus } from './WorkbenchFileStatus'
import { WorkspaceSearchController, WorkspaceSearchPanel, createDesktopWorkspaceSearchProviders } from '../Modules/WorkspaceSearch'
import { DesktopDockerControlAdapter } from '../Modules/DockerControlCenter/DesktopDockerControlAdapter'
import { DockerControlCenter, DockerControlController } from '../Modules/DockerControlCenter'
import { PhotonCadDesktopWorkspace } from '../Modules/PhotonCad/PhotonCadDesktopWorkspace'
import { PhotonCadDesktopPreviewSurface } from '../Modules/PhotonCad/PhotonCadDesktopPreviewSurface'
import { DockGroup } from '../Modules/WorkbenchDocking/DockGroup'
import type { DockPanelRegistration } from '../Modules/WorkbenchDocking/DockGroup'
import { loadDockGroupLayout, saveDockGroupLayout } from '../Modules/WorkbenchDocking/DockGroupLayout'
import type { DockGroupLayout } from '../Modules/WorkbenchDocking/DockGroupLayout'
import { announceHermesAuthChanged } from '../Modules/HermesSystem/HermesAuthEvents'
import {
  applyUiScale,
  loadUiScale,
  normalizeUiScale,
  saveUiScale,
  stepUiScale,
  UI_SCALE_OPTIONS,
} from './UiScale'
import {
  canExecuteWorkbenchEditCommand,
  executeWorkbenchEditCommand,
  isWorkbenchEditTarget,
  requestWorkbenchExit,
  type WorkbenchEditCommand,
  type WorkbenchExitTarget,
} from './WorkbenchMenuCommands'

type Layout = 'code' | 'chat' | 'split'
type LeftPanel = 'explorer' | 'search' | 'containers' | 'cad' | 'sessions' | 'sourceControl' | 'run' | 'usage' | 'browser' | 'system'
type AgentPanelId = 'hermes' | 'codex'
type ShellMenu = 'file' | 'edit' | 'view'

type PhotonCadDesktopHost = Window & {
  __HERMES_DESKTOP_HOST__?: {
    capabilities?: {
      photonCadProjects?: boolean
      photonCadProjectsVersion?: number
    }
  }
}

const AGENT_PANEL_IDS: readonly AgentPanelId[] = ['hermes', 'codex']
const AGENT_DOCK_STORAGE_KEY = 'hermes-workbench.agent-dock-layout.v1'
const DEFAULT_AGENT_DOCK_LAYOUT: DockGroupLayout<AgentPanelId> = {
  mode: 'vertical',
  order: AGENT_PANEL_IDS,
  activeTab: 'hermes',
}

function desktopPhotonCadProjectsAvailable() {
  if (typeof window === 'undefined') return false
  const capability = (window as PhotonCadDesktopHost).__HERMES_DESKTOP_HOST__?.capabilities
  return capability?.photonCadProjects === true && capability.photonCadProjectsVersion === 1
}

function postDesktopHostMessage(type: 'window.minimize' | 'window.maximize' | 'window.close') {
  const host = (window as Window & { chrome?: { webview?: { postMessage: (message: unknown) => void } } }).chrome?.webview
  host?.postMessage({ type })
}

const codeLines = [
  ['muted', '// Hermes workbench orchestration'],
  ['keyword', 'export async function connectAgent(workspace: Workspace) {'],
  ['plain', '  const gateway = await HermesGateway.connect({'],
  ['property', "    endpoint: 'ws://127.0.0.1:9119/api/ws',"],
  ['property', "    tools: ['serena'],"],
  ['plain', '  })'],
  ['plain', ''],
  ['keyword', '  return gateway.createSession({'],
  ['property', '    workspace,'],
  ['property', "    surface: 'agent-dock',"],
  ['property', '    streamToolActivity: true,'],
  ['plain', '  })'],
  ['keyword', '}'],
]

function Explorer() {
  return (
    <aside className="explorer">
      <div className="panel-label">EXPLORER <span>•••</span></div>
      <div className="tree-heading"><ChevronDown size={14} /> HERMESAGENT</div>
      <div className="tree">
        <div><ChevronDown size={14} /><span className="folder">src</span></div>
        <div className="indent-1"><ChevronDown size={14} /><span className="folder">Modules</span></div>
        <div className="indent-2 selected"><ChevronDown size={14} /><span className="folder">AgentDock</span></div>
        <div className="indent-3"><span className="tsx">TS</span> AgentDock.tsx</div>
        <div className="indent-1"><ChevronRight size={14} /><span className="folder">app</span></div>
        <div className="indent-1"><ChevronRight size={14} /><span className="folder">backend</span></div>
        <div><ChevronRight size={14} /><span className="folder">remote-install</span></div>
        <div><ChevronRight size={14} /><span className="folder">source</span></div>
        <div><span className="json">{'{}'}</span> docker-compose.yml</div>
        <div><span className="md">M↓</span> README.md</div>
      </div>
      <div className="outline-row"><ChevronRight size={14} /> OUTLINE</div>
      <div className="outline-row"><ChevronRight size={14} /> TIMELINE</div>
    </aside>
  )
}

function Editor() {
  return (
    <main className="editor-area">
      <div className="editor-tabs">
        <div className="editor-tab active"><span className="tsx">TS</span> agentGateway.ts <X size={14} /></div>
        <div className="editor-tab"><span className="tsx">TS</span> AgentDock.tsx <X size={14} /></div>
        <span className="tab-spacer" />
        <button><SplitSquareHorizontal size={16} /></button>
        <button>•••</button>
      </div>
      <div className="breadcrumbs"><span>src</span><ChevronRight size={13} /><span>services</span><ChevronRight size={13} /><Braces size={13} /><strong>agentGateway.ts</strong></div>
      <div className="code-editor">
        <div className="code-content">
          {codeLines.map(([kind, text], index) => (
            <div className="code-line" key={index}>
              <span className="line-number">{index + 1}</span>
              <code className={kind}>{text || '\u00A0'}</code>
            </div>
          ))}
          <div className="inline-suggestion">
            <div className="suggestion-head"><Sparkles size={14} /> Hermes suggests <span>⌘ ↵ to apply</span></div>
            <p>Add reconnection with bounded exponential backoff before this reaches the live gateway.</p>
            <div><button>Apply change</button><button className="ghost">Dismiss</button></div>
          </div>
          {Array.from({ length: 10 }, (_, index) => (
            <div className="code-line faint" key={`faint-${index}`}>
              <span className="line-number">{index + 14}</span><code> </code>
            </div>
          ))}
        </div>
        <div className="minimap" aria-hidden="true">
          {Array.from({ length: 34 }, (_, index) => <i key={index} style={{ width: `${34 + ((index * 19) % 54)}%` }} />)}
        </div>
      </div>
      <div className="terminal-panel">
        <div className="terminal-tabs"><span className="active">TERMINAL</span><span>OUTPUT</span><span>PROBLEMS <b>0</b></span><span className="terminal-spacer" /><button>＋</button><button>⌄</button><button>×</button></div>
        <div className="terminal-body"><span className="prompt">PS</span> C:\Users\clsor\Documents\Codex\HermesAgent&gt; <strong>docker compose ps</strong><br /><span className="success">✓ hermes-gateway running</span> <span className="dim">127.0.0.1:9119</span><br /><span className="success">✓ serena connected</span> <span className="dim">25 tools available</span><br /><span className="prompt">PS</span> C:\Users\clsor\Documents\Codex\HermesAgent&gt; <span className="cursor">▋</span></div>
      </div>
    </main>
  )
}

export function App() {
  const [assistantName, setAssistantName] = useAssistantDisplayName()
  const [layout, setLayout] = useState<Layout>('code')
  const [leftPanel, setLeftPanel] = useState<LeftPanel>('explorer')
  const [terminalOpen, setTerminalOpen] = useState(true)
  const [sessionRequest, setSessionRequest] = useState<SessionOpenRequest | null>(null)
  const [activeSession, setActiveSession] = useState<HermesSession | null>(null)
  const [selectedWorkspacePath, setSelectedWorkspacePath] = useState<string | null>(null)
  const [languageToolingResult, setLanguageToolingResult] = useState<DesktopLanguageToolingPublication | null>(null)
  const [workspaceSelection, setWorkspaceSelection] = useState<{ path: string; line: number; column: number; nonce: number } | null>(null)
  const [sourceControlPath, setSourceControlPath] = useState<string | null>('.')
  const [accountRequest, setAccountRequest] = useState(0)
  const [browserOpenRequests, setBrowserOpenRequests] = useState<BrowserOpenRequest[]>([])
  const [uiScale, setUiScale] = useState(loadUiScale)
  const [openMenu, setOpenMenu] = useState<ShellMenu | null>(null)
  const [menuStatus, setMenuStatus] = useState('')
  const [activeEditTarget, setActiveEditTarget] = useState<HTMLElement | null>(null)
  const [commandCenterOpen, setCommandCenterOpen] = useState(false)
  const [commandQuery, setCommandQuery] = useState('')
  const [agentDockLayout, setAgentDockLayout] = useState(() => loadDockGroupLayout(AGENT_DOCK_STORAGE_KEY, AGENT_PANEL_IDS, DEFAULT_AGENT_DOCK_LAYOUT))
  const [photonCadProjectsAvailable, setPhotonCadProjectsAvailable] = useState(desktopPhotonCadProjectsAvailable)
  const menuBarRef = useRef<HTMLElement>(null)
  const activeEditTargetRef = useRef<HTMLElement | null>(null)
  const browserOpenNonceRef = useRef(0)
  const showBuildOutput = useCallback(() => setTerminalOpen(true), [])
  const developerBuild = useDeveloperBuild(showBuildOutput)
  const workspaceSearchProviders = useMemo(() => createDesktopWorkspaceSearchProviders(), [])
  const workspaceSearchController = useMemo(() => new WorkspaceSearchController({
    literalProvider: workspaceSearchProviders.literalProvider,
    semanticProvider: workspaceSearchProviders.semanticProvider,
    trustedSemanticProvider: 'serena',
    onActivate: ({ path, line, column }) => {
      setSelectedWorkspacePath(path)
      setWorkspaceSelection({ path, line, column, nonce: Date.now() })
      setLayout('code')
      setLeftPanel('explorer')
    },
  }), [workspaceSearchProviders])
  const dockerControlAdapter = useMemo(() => new DesktopDockerControlAdapter(), [])
  const dockerControlController = useMemo(() => new DockerControlController({ adapter: dockerControlAdapter }), [dockerControlAdapter])
  const buildErrors = developerBuild.result ? developerBuild.result.diagnostics.filter((diagnostic) => diagnostic.severity === 'error').length : null
  const buildWarnings = developerBuild.result ? developerBuild.result.diagnostics.filter((diagnostic) => diagnostic.severity === 'warning').length : null
  const selectedFileStatus = workbenchFileStatus(selectedWorkspacePath)
  const editorSurfaceVisible = layout !== 'chat' && (leftPanel === 'explorer' || leftPanel === 'run' || leftPanel === 'sessions')
  const fileCommandsAvailable = editorSurfaceVisible && Boolean(selectedWorkspacePath)
  const terminalVisible = editorSurfaceVisible && terminalOpen

  useEffect(() => {
    const refresh = () => setPhotonCadProjectsAvailable(desktopPhotonCadProjectsAvailable())
    window.addEventListener('hermes-desktop-ready', refresh)
    refresh()
    return () => window.removeEventListener('hermes-desktop-ready', refresh)
  }, [])

  function chooseUiScale(scale: number) {
    setUiScale(normalizeUiScale(scale))
    setOpenMenu(null)
  }

  function runEditCommand(command: WorkbenchEditCommand) {
    const result = executeWorkbenchEditCommand(command, activeEditTargetRef.current, document)
    setMenuStatus(result.message)
    setOpenMenu(null)
  }

  function exitWorkbench() {
    const destination = requestWorkbenchExit(window as unknown as WorkbenchExitTarget)
    if (destination === 'browser') setMenuStatus('The browser host did not expose a desktop Exit command.')
    setOpenMenu(null)
  }

  async function openFile() {
    try {
      const result = await desktopDocumentClient.pickFile()
      if (!result.cancelled && result.path) { setWorkspaceSelection(null); setSelectedWorkspacePath(result.path); setLayout('code'); setLeftPanel('explorer') }
    } catch (reason) { setMenuStatus(reason instanceof Error ? reason.message : 'The file could not be opened.') }
    setOpenMenu(null)
  }

  async function openGitRepository() {
    try {
      const result = await desktopDocumentClient.pickRepository()
      if (!result.cancelled && result.path) { setSourceControlPath(result.path); setLayout('code'); setLeftPanel('sourceControl') }
    } catch (reason) { setMenuStatus(reason instanceof Error ? reason.message : 'The repository could not be opened.') }
    setOpenMenu(null)
  }

  function runFileCommand(command: 'save' | 'saveAs' | 'close') {
    if (!fileCommandsAvailable) {
      setMenuStatus('Open an editor file before using Save, Save As, or Close.')
      setOpenMenu(null)
      return
    }
    window.dispatchEvent(new CustomEvent('photos-file-command', { detail: command }))
    setOpenMenu(null)
  }

  function toggleTerminal() {
    if (terminalVisible) {
      setTerminalOpen(false)
      return
    }
    setLayout('code')
    setLeftPanel('explorer')
    setTerminalOpen(true)
  }

  const commandItems = useMemo(() => [
    { label: 'Open File…', detail: 'File', run: () => void openFile() },
    { label: 'Save File', detail: 'File', run: () => runFileCommand('save') },
    { label: 'Save File As…', detail: 'File', run: () => runFileCommand('saveAs') },
    { label: 'Close File', detail: 'File', run: () => runFileCommand('close') },
    { label: 'Open Git Repository…', detail: 'Source Control', run: () => void openGitRepository() },
    { label: 'Close Git Repository', detail: 'Source Control', run: () => { setSourceControlPath(null); setLeftPanel('sourceControl') } },
    { label: 'Show Explorer', detail: 'View', run: () => { setLayout('code'); setLeftPanel('explorer') } },
    { label: 'Show Workspace Search', detail: 'View', run: () => { setLayout('code'); setLeftPanel('search') } },
    { label: 'Show Docker Control Center', detail: 'View', run: () => { setLayout('code'); setLeftPanel('containers') } },
    { label: 'Open Photon CAD', detail: 'View', run: () => { setLayout('code'); setLeftPanel('cad') } },
    { label: 'Show Source Control', detail: 'View', run: () => { setLayout('code'); setLeftPanel('sourceControl') } },
    { label: 'Show Run and Debug', detail: 'View', run: () => { setLayout('code'); setLeftPanel('run') } },
    { label: 'Show Usage Intelligence', detail: 'View', run: () => { setLayout('code'); setLeftPanel('usage') } },
    { label: 'Open Browser', detail: 'View', run: () => { setLayout('code'); setLeftPanel('browser') } },
    { label: 'Show Settings', detail: 'View', run: () => { setLayout('code'); setLeftPanel('system') } },
    { label: `Focus ${assistantName}`, detail: 'Layout', run: () => setLayout('chat') },
    { label: `Show ${assistantName} and Codex`, detail: 'Layout', run: () => setLayout('split') },
    { label: terminalVisible ? 'Hide Terminal' : 'Show Terminal', detail: 'View', run: toggleTerminal },
  ], [assistantName, fileCommandsAvailable, terminalVisible])
  const filteredCommandItems = commandItems.filter((command) => (
    (fileCommandsAvailable || command.detail !== 'File' || command.label.startsWith('Open File'))
    && `${command.label} ${command.detail}`.toLocaleLowerCase().includes(commandQuery.trim().toLocaleLowerCase())
  ))

  function executeCommandCenterAction(action: () => void) {
    setCommandCenterOpen(false)
    setCommandQuery('')
    action()
  }

  function openWorkbenchSignIn() {
    setLayout('code')
    setLeftPanel('system')
    setAccountRequest((value) => value + 1)
  }

  const handleDesktopUiAction = useCallback((action: HermesDesktopUiAction) => {
    if (action.kind === 'open-preview') {
      setLayout('code')
      setLeftPanel('browser')
      setBrowserOpenRequests((current) => [...current, { nonce: ++browserOpenNonceRef.current, url: action.url, label: action.label }].slice(-32))
      return
    }
    if (action.kind === 'open-workspace-file') {
      setWorkspaceSelection(null)
      setSelectedWorkspacePath(action.path)
      setLayout('code')
      setLeftPanel('explorer')
      return
    }
    if (action.pane === 'chat') setLayout('chat')
    else if (action.pane === 'files') { setLayout('code'); setLeftPanel('explorer') }
    else if (action.pane === 'terminal') { setLayout('code'); setLeftPanel('explorer'); setTerminalOpen(true) }
    else if (action.pane === 'review') { setLayout('code'); setLeftPanel('sourceControl') }
    else if (action.pane === 'sessions') { setLayout('code'); setLeftPanel('sessions') }
  }, [])

  useEffect(() => {
    return () => {
      workspaceSearchController.dispose()
      workspaceSearchProviders.close()
      dockerControlController.dispose()
      dockerControlAdapter.close()
    }
  }, [dockerControlAdapter, dockerControlController, workspaceSearchController, workspaceSearchProviders])

  useEffect(() => {
    if (window.location.pathname === '/workbench-auth-complete') {
      window.history.replaceState(null, '', '/')
      openWorkbenchSignIn()
      announceHermesAuthChanged('signed-in')
    }
  }, [])

  useEffect(() => {
    const showCodex = () => setLayout('split')
    window.addEventListener('hermes-show-codex', showCodex)
    return () => window.removeEventListener('hermes-show-codex', showCodex)
  }, [])

  useEffect(() => {
    applyUiScale(uiScale)
    saveUiScale(uiScale)
  }, [uiScale])

  useEffect(() => {
    saveDockGroupLayout(AGENT_DOCK_STORAGE_KEY, agentDockLayout)
  }, [agentDockLayout])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return
      if (event.key === '+' || event.key === '=') {
        event.preventDefault()
        setUiScale((current) => stepUiScale(current, 1))
      } else if (event.key === '-') {
        event.preventDefault()
        setUiScale((current) => stepUiScale(current, -1))
      } else if (event.key === '0') {
        event.preventDefault()
        setUiScale(1)
      } else if (event.key.toLocaleLowerCase() === 'o' && !event.shiftKey) {
        event.preventDefault(); void openFile()
      } else if (event.key.toLocaleLowerCase() === 's' && fileCommandsAvailable) {
        event.preventDefault(); runFileCommand(event.shiftKey ? 'saveAs' : 'save')
      } else if (event.key.toLocaleLowerCase() === 'w' && !event.shiftKey && fileCommandsAvailable) {
        event.preventDefault(); runFileCommand('close')
      } else if (event.key.toLocaleLowerCase() === 'k' && !event.shiftKey) {
        event.preventDefault(); setCommandCenterOpen(true); setCommandQuery('')
      } else if (event.key.toLocaleLowerCase() === 'f' && event.shiftKey) {
        event.preventDefault(); setLayout('code'); setLeftPanel('search')
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [fileCommandsAvailable])

  useEffect(() => {
    const captureEditTarget = (event: FocusEvent) => {
      const target = event.target
      if (target instanceof HTMLElement && isWorkbenchEditTarget(target)) {
        activeEditTargetRef.current = target
        setActiveEditTarget(target)
      }
    }
    document.addEventListener('focusin', captureEditTarget)
    return () => document.removeEventListener('focusin', captureEditTarget)
  }, [])

  useEffect(() => {
    if (!openMenu) return
    const closeOnPointerDown = (event: PointerEvent) => {
      if (!menuBarRef.current?.contains(event.target as Node)) setOpenMenu(null)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpenMenu(null)
    }
    document.addEventListener('pointerdown', closeOnPointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnPointerDown)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [openMenu])

  return (
    <div className={`workbench layout-${layout} panel-${leftPanel} ${terminalOpen ? '' : 'terminal-closed'}`}>
      <header className="titlebar">
        <div className="brand" title="Photos Agape Aphthartos"><span className="brand-mark"><img src="/app/assets/photon-mark.png" alt="" /></span><strong>Photos Agape Aphthartos</strong></div>
        <nav ref={menuBarRef} aria-label="Application menu">
          <div className="workbench-menu">
            <button aria-expanded={openMenu === 'file'} aria-haspopup="menu" onClick={() => setOpenMenu((open) => open === 'file' ? null : 'file')}>File</button>
            {openMenu === 'file' && (
              <div className="workbench-menu-popover compact" role="menu" aria-label="File menu">
                <button role="menuitem" onClick={() => void openFile()}><span>Open…</span><small>Ctrl O</small></button>
                <button role="menuitem" disabled={!fileCommandsAvailable} onClick={() => runFileCommand('save')}><span>Save</span><small>Ctrl S</small></button>
                <button role="menuitem" disabled={!fileCommandsAvailable} onClick={() => runFileCommand('saveAs')}><span>Save As…</span><small>Ctrl Shift S</small></button>
                <button role="menuitem" disabled={!fileCommandsAvailable} onClick={() => runFileCommand('close')}><span>Close</span><small>Ctrl W</small></button>
                <hr />
                <button role="menuitem" onClick={() => void openGitRepository()}><span>Open Git Repository…</span></button>
                <button role="menuitem" disabled={!sourceControlPath} onClick={() => { setSourceControlPath(null); setOpenMenu(null) }}><span>Close Git Repository</span></button>
                <hr />
                <button role="menuitem" onClick={exitWorkbench}><span>Exit</span><small>Alt F4</small></button>
              </div>
            )}
          </div>
          <div className="workbench-menu">
            <button aria-expanded={openMenu === 'edit'} aria-haspopup="menu" onClick={() => setOpenMenu((open) => open === 'edit' ? null : 'edit')}>Edit</button>
            {openMenu === 'edit' && (
              <div className="workbench-menu-popover compact" role="menu" aria-label="Edit menu">
                <button role="menuitem" disabled={!canExecuteWorkbenchEditCommand('cut', activeEditTarget, document)} onClick={() => runEditCommand('cut')}><span>Cut</span><small>Ctrl X</small></button>
                <button role="menuitem" disabled={!canExecuteWorkbenchEditCommand('copy', activeEditTarget, document)} onClick={() => runEditCommand('copy')}><span>Copy</span><small>Ctrl C</small></button>
                <button role="menuitem" disabled={!canExecuteWorkbenchEditCommand('paste', activeEditTarget, document)} onClick={() => runEditCommand('paste')}><span>Paste</span><small>Ctrl V</small></button>
              </div>
            )}
          </div>
          <div className="workbench-menu">
            <button aria-expanded={openMenu === 'view'} aria-haspopup="menu" onClick={() => setOpenMenu((open) => open === 'view' ? null : 'view')}>View</button>
            {openMenu === 'view' && (
              <div className="view-menu-popover" role="menu" aria-label="View options">
                <header><strong>Interface size</strong><small>Enlarge the entire Workbench</small></header>
                <div className="view-scale-stepper">
                  <button aria-label="Decrease interface size" onClick={() => setUiScale((current) => stepUiScale(current, -1))}><ZoomOut size={15} /></button>
                  <strong>{Math.round(uiScale * 100)}%</strong>
                  <button aria-label="Increase interface size" onClick={() => setUiScale((current) => stepUiScale(current, 1))}><ZoomIn size={15} /></button>
                </div>
                <div className="view-scale-presets">
                  {UI_SCALE_OPTIONS.map((scale) => (
                    <button role="menuitemradio" aria-checked={uiScale === scale} key={scale} onClick={() => chooseUiScale(scale)}>
                      <span>{Math.round(scale * 100)}%</span><small>{scale === 1 ? 'Compact' : scale === 1.1 ? 'Comfortable' : scale === 1.25 ? 'Large' : 'Extra large'}</small>
                    </button>
                  ))}
                </div>
                <label className="assistant-name-setting">
                  <span><strong>Assistant name</strong><small>Changes the visible name only</small></span>
                  <input aria-label="Assistant display name" maxLength={40} value={assistantName} onChange={(event) => setAssistantName(event.target.value)} />
                </label>
                <button className="assistant-name-reset" type="button" onClick={() => setAssistantName(DEFAULT_ASSISTANT_DISPLAY_NAME)}>Reset to Photon</button>
                <footer><span>Ctrl + / -</span><span>Ctrl 0 resets</span></footer>
              </div>
            )}
          </div>
          <button onClick={() => { setLayout('code'); setLeftPanel('run') }}>Run</button><button onClick={toggleTerminal}>Terminal</button>
        </nav>
        <div className="command-center-shell">
          <button className="command-center" aria-expanded={commandCenterOpen} aria-haspopup="dialog" onClick={() => { setCommandCenterOpen(true); setCommandQuery('') }} title="Open Command Center"><Search size={14} /><span>Photos Agape Aphthartos</span><small>Ctrl K</small></button>
          {commandCenterOpen && <section className="command-palette" role="dialog" aria-label="Command Center">
            <div className="command-palette-input"><Search size={15} /><input autoFocus aria-label="Search commands" placeholder="Type a command…" value={commandQuery} onChange={(event) => setCommandQuery(event.target.value)} onKeyDown={(event) => {
              if (event.key === 'Escape') { event.preventDefault(); setCommandCenterOpen(false) }
              else if (event.key === 'Enter' && filteredCommandItems[0]) { event.preventDefault(); executeCommandCenterAction(filteredCommandItems[0].run) }
            }} /><button aria-label="Close Command Center" onClick={() => setCommandCenterOpen(false)}><X size={14} /></button></div>
            <div className="command-palette-results" role="listbox">
              {filteredCommandItems.map((command) => <button key={`${command.detail}:${command.label}`} role="option" onClick={() => executeCommandCenterAction(command.run)}><Search size={13} /><span><strong>{command.label}</strong><small>{command.detail}</small></span></button>)}
              {!filteredCommandItems.length && <p>No matching commands</p>}
            </div>
          </section>}
        </div>
        <div className="layout-switcher">
          <button className={layout === 'code' ? 'active' : ''} onClick={() => setLayout('code')} title="Code workspace"><LayoutPanelLeft size={16} /></button>
          <button className={layout === 'chat' ? 'active' : ''} onClick={() => setLayout('chat')} title="Focus chat"><MessageSquare size={16} /></button>
          <button className={layout === 'split' ? 'active' : ''} onClick={() => setLayout('split')} title="Two agents"><SplitSquareHorizontal size={16} /></button>
        </div>
        <div className="window-controls">
          <button aria-label="Minimize window" onClick={() => postDesktopHostMessage('window.minimize')}>—</button>
          <button aria-label="Maximize or restore window" onClick={() => postDesktopHostMessage('window.maximize')}>□</button>
          <button aria-label="Close window" onClick={() => postDesktopHostMessage('window.close')}>×</button>
        </div>
      </header>

      <aside className="activity-rail">
        <div className="activity-top">
          <button aria-label="Files" className={leftPanel === 'explorer' ? 'active' : ''} onClick={() => setLeftPanel('explorer')}><Files size={22} /></button>
          <button aria-label="Workspace search" className={leftPanel === 'search' ? 'active' : ''} title="Workspace search (Ctrl Shift F)" onClick={() => { setLayout('code'); setLeftPanel('search') }}><Search size={22} /></button>
          <button aria-label="Source control" className={leftPanel === 'sourceControl' ? 'active' : ''} onClick={() => setLeftPanel('sourceControl')}><GitBranch size={22} /></button>
          <button aria-label="Run" className={leftPanel === 'run' ? 'active' : ''} onClick={() => { setLayout('code'); setLeftPanel('run') }}><Play size={22} /></button>
          <button aria-label="Docker Control Center" className={leftPanel === 'containers' ? 'active' : ''} onClick={() => { setLayout('code'); setLeftPanel('containers') }}><Box size={22} /></button>
          <button aria-label="Photon CAD" className={leftPanel === 'cad' ? 'active' : ''} onClick={() => { setLayout('code'); setLeftPanel('cad') }}><DraftingCompass size={22} /></button>
          <button aria-label={`${assistantName} sessions`} className={leftPanel === 'sessions' ? 'active' : ''} onClick={() => setLeftPanel('sessions')}><Bot size={22} /></button>
          <button aria-label="Usage intelligence" className={leftPanel === 'usage' ? 'active' : ''} onClick={() => setLeftPanel('usage')}><ChartNoAxesCombined size={22} /></button>
          <button aria-label="Browser" className={leftPanel === 'browser' ? 'active' : ''} onClick={() => { setLayout('code'); setLeftPanel('browser') }}><Globe2 size={22} /></button>
        </div>
        <div><button aria-label="Settings" className={leftPanel === 'system' ? 'active' : ''} onClick={() => setLeftPanel('system')}><Settings size={22} /></button></div>
      </aside>

      {layout !== 'chat' && (leftPanel === 'system' ? <HermesSystemWorkspace accountRequest={accountRequest} /> : leftPanel === 'usage' ? <UsageIntelligenceDashboard /> : leftPanel === 'browser' ? <BrowserWorkspace openRequest={browserOpenRequests[0] ?? null} onOpenRequestHandled={(nonce) => setBrowserOpenRequests((current) => current.filter((request) => request.nonce !== nonce))} /> : leftPanel === 'sourceControl' ? sourceControlPath ? <SourceControlWorkspace workspaceRelativePath={sourceControlPath} /> : <aside className="source-control-closed"><GitBranch size={28} /><strong>No Git repository open</strong><button onClick={() => void openGitRepository()}>Open Git Repository…</button></aside> : leftPanel === 'search' ? <>
        <WorkspaceExplorer selectedPath={selectedWorkspacePath} onOpen={(entry) => { setWorkspaceSelection(null); setSelectedWorkspacePath(entry.path) }} />
        <WorkspaceSearchPanel controller={workspaceSearchController} />
      </> : leftPanel === 'containers' ? <>
        <WorkspaceExplorer selectedPath={selectedWorkspacePath} onOpen={(entry) => { setWorkspaceSelection(null); setSelectedWorkspacePath(entry.path) }} />
        <DockerControlCenter controller={dockerControlController} />
      </> : leftPanel === 'cad' ? <>
        <WorkspaceExplorer selectedPath={selectedWorkspacePath} onOpen={(entry) => { setWorkspaceSelection(null); setSelectedWorkspacePath(entry.path) }} />
      </> : <>
        {leftPanel === 'explorer'
          ? <WorkspaceExplorer selectedPath={selectedWorkspacePath} onOpen={(entry) => { setWorkspaceSelection(null); setSelectedWorkspacePath(entry.path) }} />
          : leftPanel === 'run'
            ? <DeveloperServicesPanel controller={developerBuild} selectedWorkspacePath={selectedWorkspacePath} onLanguageToolingResult={setLanguageToolingResult} />
            : <SessionSidebar
              activeSession={activeSession}
              onSignIn={openWorkbenchSignIn}
              onOpen={(session) => setSessionRequest({ nonce: Date.now(), session })}
            />}
        <WorkspaceEditor path={selectedWorkspacePath} selection={workspaceSelection} onClose={() => { setWorkspaceSelection(null); setSelectedWorkspacePath(null) }} buildResult={developerBuild.result} languageToolingResult={languageToolingResult} onOpenWorkspacePath={(path) => { setWorkspaceSelection(null); setSelectedWorkspacePath(path) }} />
      </>)}
      <PhotonCadDesktopWorkspace
        projectActionsAvailable={photonCadProjectsAvailable}
        previewSurface={(context) => <PhotonCadDesktopPreviewSurface context={context} />}
        className={layout !== 'chat' && leftPanel === 'cad' ? '' : 'photon-cad-desktop--inactive'}
      />
      <DockGroup
        className="agents-area"
        layout={agentDockLayout}
        onLayoutChange={setAgentDockLayout}
        panels={[
          {
            id: 'hermes',
            label: assistantName,
            render: (controls) => <AgentDock sessionRequest={sessionRequest} onSessionOpened={setActiveSession} onSignIn={openWorkbenchSignIn} onDesktopUiAction={handleDesktopUiAction} dockControls={controls} />,
          },
          {
            id: 'codex',
            label: 'Codex',
            render: (controls) => <CodexAgentPanel active={layout === 'split'} dockControls={controls} />,
          },
        ] satisfies readonly DockPanelRegistration<AgentPanelId>[]}
      />

      <footer className="statusbar">
        <div><button onClick={() => { setLayout('code'); setLeftPanel('sourceControl') }}><GitBranch size={13} /> Source Control</button><span aria-label={buildErrors === null ? 'Build not run' : `${buildErrors} latest build errors`}>ⓧ {buildErrors ?? '—'}</span><span aria-label={buildWarnings === null ? 'Build not run' : `${buildWarnings} latest build warnings`}>△ {buildWarnings ?? '—'}</span>{menuStatus && <span className="menu-command-status" role="status">{menuStatus}</span>}</div>
        <div><button onClick={toggleTerminal}><TerminalSquare size={13} /> {terminalVisible ? 'Hide terminal' : 'Show terminal'}</button><button className="ui-scale-status" title="Adjust interface size" onClick={() => setOpenMenu('view')}><ZoomIn size={12} /> UI {Math.round(uiScale * 100)}%</button>{selectedFileStatus ? <><span title={selectedWorkspacePath ?? undefined}>{selectedFileStatus.fileName}</span><span>UTF-8</span><span>{'{}'} {selectedFileStatus.language}</span></> : <span>No editor file</span>}<span><Sparkles size={12} /> {assistantName}</span></div>
      </footer>
    </div>
  )
}
