import { useEffect, useRef, useState } from 'react'
import { CircleStop, Eraser, MonitorUp, RefreshCw, ShieldCheck } from 'lucide-react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import {
  DesktopHostTerminalClient,
  isNativeTerminalAvailable,
  NATIVE_TERMINAL_ADAPTER_VERSION,
} from './DesktopHostTerminalClient'
import type { NativeTerminalConnectionState } from './DesktopHostTerminalClient'
import type { NativeTerminalCommandRequest } from './DesktopHostTerminalClient'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'

type RestartClient = Pick<DesktopHostTerminalClient, 'start' | 'stop'>
type RestartTerminal = Pick<Terminal, 'cols' | 'rows' | 'reset'>
type RestartTimer = ReturnType<typeof globalThis.setTimeout>

export function createNativeTerminalRestartController(
  schedule: (callback: () => void, delayMs: number) => RestartTimer = globalThis.setTimeout,
  cancel: (timer: RestartTimer) => void = globalThis.clearTimeout,
) {
  let disposed = false
  let pending: RestartTimer | null = null

  return {
    restart(client: RestartClient, terminal: RestartTerminal) {
      if (disposed) return
      if (pending !== null) cancel(pending)
      client.stop()
      terminal.reset()
      pending = schedule(() => {
        pending = null
        if (disposed) return
        client.start(terminal.cols, terminal.rows)
      }, 180)
    },
    dispose() {
      disposed = true
      if (pending !== null) {
        cancel(pending)
        pending = null
      }
    },
  }
}

export function NativeTerminalSurface({ commandRequest = null, onCommandHandled }: { commandRequest?: NativeTerminalCommandRequest | null; onCommandHandled?: (nonce: number) => void }) {
  const [assistantName] = useAssistantDisplayName()
  const containerRef = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const clientRef = useRef<DesktopHostTerminalClient | null>(null)
  const restartRef = useRef<ReturnType<typeof createNativeTerminalRestartController> | null>(null)
  const commandRequestRef = useRef(commandRequest)
  const commandHandledRef = useRef(onCommandHandled)
  const lastCommandNonceRef = useRef<number | null>(null)
  const [available, setAvailable] = useState(isNativeTerminalAvailable())
  const [connection, setConnection] = useState<NativeTerminalConnectionState>(available ? 'connecting' : 'browser')
  const [details, setDetails] = useState('Native desktop terminal')

  commandRequestRef.current = commandRequest
  commandHandledRef.current = onCommandHandled

  function sendPendingCommand() {
    const request = commandRequestRef.current
    const client = clientRef.current
    if (!request || !client || client.connectionState !== 'open' || lastCommandNonceRef.current === request.nonce) return
    lastCommandNonceRef.current = request.nonce
    client.write(`${request.command}\r`)
    terminalRef.current?.focus()
    commandHandledRef.current?.(request.nonce)
  }

  useEffect(() => { sendPendingCommand() }, [commandRequest])

  useEffect(() => {
    const ready = () => setAvailable(isNativeTerminalAvailable())
    window.addEventListener('hermes-desktop-ready', ready)
    ready()
    return () => window.removeEventListener('hermes-desktop-ready', ready)
  }, [])

  useEffect(() => {
    const container = containerRef.current
    if (!available || !container) return

    const terminal = new Terminal({
      allowProposedApi: false,
      convertEol: false,
      cursorBlink: true,
      cursorStyle: 'bar',
      fontFamily: "'Cascadia Mono', 'Cascadia Code', Consolas, monospace",
      fontSize: 11,
      lineHeight: 1.15,
      scrollback: 5000,
      theme: {
        background: '#090c13',
        foreground: '#c8cfdd',
        cursor: '#9b85ff',
        cursorAccent: '#090c13',
        selectionBackground: '#5f4bb866',
        black: '#111521', red: '#e07186', green: '#5de5c1', yellow: '#e1bc6a',
        blue: '#77a7ff', magenta: '#c98bf1', cyan: '#5dd5df', white: '#dce2f2',
        brightBlack: '#697386', brightRed: '#f08b9e', brightGreen: '#7ef0ce', brightYellow: '#f0cf85',
        brightBlue: '#93bbff', brightMagenta: '#dca6ff', brightCyan: '#7de9ed', brightWhite: '#f5f7fb',
      },
    })
    const fit = new FitAddon()
    terminal.loadAddon(fit)
    terminal.open(container)
    fit.fit()

    const client = new DesktopHostTerminalClient()
    const restart = createNativeTerminalRestartController()
    terminalRef.current = terminal
    fitRef.current = fit
    clientRef.current = client
    restartRef.current = restart
    const removeState = client.onState(setConnection)
    const removeFrame = client.onFrame((frame) => {
      if (frame.type === 'output' && frame.data) terminal.write(frame.data)
      if (frame.type === 'ready') {
        setDetails(`${frame.shell ?? 'PowerShell'} · PID ${frame.processId ?? '?'} · ${frame.cwd ?? ''}`)
        terminal.focus()
        sendPendingCommand()
      }
      if (frame.type === 'error') terminal.writeln(`\r\n\x1b[31mHermes host: ${frame.message ?? 'Terminal error'}\x1b[0m`)
      if (frame.type === 'exit') terminal.writeln(`\r\n\x1b[90m[${frame.message ?? `process exited ${frame.exitCode ?? ''}`}]\x1b[0m`)
    })
    const input = terminal.onData((data) => client.write(data))
    const resize = new ResizeObserver(() => {
      requestAnimationFrame(() => {
        if (!container.isConnected) return
        fit.fit()
        client.resize(terminal.cols, terminal.rows)
      })
    })
    resize.observe(container)
    client.start(terminal.cols, terminal.rows)

    return () => {
      restart.dispose()
      resize.disconnect()
      input.dispose()
      removeFrame()
      removeState()
      client.close()
      terminal.dispose()
      terminalRef.current = null
      fitRef.current = null
      clientRef.current = null
      restartRef.current = null
    }
  }, [available])

  if (!available) {
    return (
      <div className="native-terminal-unavailable">
        <MonitorUp size={20} />
        <span><strong>PowerShell is available in the desktop app</strong><small>The browser keeps the same Workbench, but native command execution stays disabled for security. Use the {assistantName} Console tab here.</small></span>
      </div>
    )
  }

  return (
    <div className="native-terminal-surface">
      <div className="native-terminal-meta">
        <span><i className={`console-status ${connection}`} /> {details}</span>
        <span className="native-terminal-trust"><ShieldCheck size={11} /> host adapter v{NATIVE_TERMINAL_ADAPTER_VERSION}</span>
        <button type="button" title="Clear terminal" onClick={() => terminalRef.current?.clear()}><Eraser size={12} /></button>
        <button type="button" title="Restart PowerShell" onClick={() => {
          const client = clientRef.current
          const terminal = terminalRef.current
          if (!client || !terminal) return
          restartRef.current?.restart(client, terminal)
        }}><RefreshCw size={12} /></button>
        <button type="button" title="Stop PowerShell" onClick={() => clientRef.current?.stop()}><CircleStop size={12} /></button>
      </div>
      <div className="native-terminal-xterm" ref={containerRef} />
    </div>
  )
}
