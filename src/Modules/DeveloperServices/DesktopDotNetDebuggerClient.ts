import { DEVELOPER_SERVICES_PROTOCOL_VERSION, normalizeWorkspaceRelativePath } from './DesktopDeveloperServicesClient'

const maximumFrameCharacters = 1024 * 1024
const maximumOutputCharacters = 64 * 1024

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

export type DebugOperation = 'targets' | 'launch' | 'attach' | 'set-breakpoints' | 'configuration-done'
  | 'threads' | 'stack-trace' | 'scopes' | 'variables' | 'evaluate' | 'continue'
  | 'step-over' | 'step-into' | 'step-out' | 'disconnect'

export type DebugThread = { id: number; name: string }
export type DebugStackFrame = { id: number; name: string; path: string; line: number; column: number }
export type DebugScope = { name: string; variablesReference: number; expensive: boolean; presentationHint: string }
export type DebugVariable = { name: string; value: string; type: string; variablesReference: number; evaluateName: string }

export type DotNetDebugSnapshot = {
  available: boolean
  state: string
  busy: boolean
  targets: string[]
  selectedProgram: string
  breakpoints: Readonly<Record<string, readonly number[]>>
  threads: readonly DebugThread[]
  frames: readonly DebugStackFrame[]
  scopes: readonly DebugScope[]
  variables: readonly DebugVariable[]
  activeThreadId: number | null
  activeFrameId: number | null
  stoppedLocation: { path: string; line: number; column: number } | null
  evaluation: string
  output: string
  error: string
}

type DebugResultFrame = { type: 'result'; requestId: string; operation: DebugOperation; state: string; result: unknown }
type DebugEventFrame = { type: 'event'; event: string; state: string; body: unknown }
type DebugErrorFrame = { type: 'error'; requestId: string; message: string }
type DebugFrame = DebugResultFrame | DebugEventFrame | DebugErrorFrame

type Pending = { operation: DebugOperation; resolve(frame: DebugResultFrame): void; reject(reason: Error): void; abort?: () => void; signal?: AbortSignal }

function bridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function requestId(value: unknown) {
  return typeof value === 'string' && /^[a-zA-Z0-9_:-]{1,128}$/.test(value) ? value : null
}

function operation(value: unknown): DebugOperation | null {
  return value === 'targets' || value === 'launch' || value === 'attach' || value === 'set-breakpoints'
    || value === 'configuration-done' || value === 'threads' || value === 'stack-trace' || value === 'scopes'
    || value === 'variables' || value === 'evaluate' || value === 'continue' || value === 'step-over'
    || value === 'step-into' || value === 'step-out' || value === 'disconnect' ? value : null
}

function state(value: unknown) {
  return typeof value === 'string' && /^[a-z-]{1,32}$/.test(value) ? value : 'unknown'
}

function bounded(value: unknown) {
  try { return JSON.stringify(value).length <= maximumFrameCharacters ? value : undefined } catch { return undefined }
}

export function normalizeDotNetDebugFrame(value: unknown): DebugFrame | null {
  const raw = record(value)
  if (!raw || raw.version !== DEVELOPER_SERVICES_PROTOCOL_VERSION) return null
  if (raw.type === 'developerServices.debug.event') {
    if (typeof raw.event !== 'string' || !['initialized', 'stopped', 'continued', 'output', 'terminated', 'exited', 'faulted'].includes(raw.event)) return null
    const body = bounded(raw.body)
    return body === undefined ? null : { type: 'event', event: raw.event, state: state(raw.state), body }
  }
  const id = requestId(raw.requestId)
  if (!id) return null
  if (raw.type === 'developerServices.error') {
    const message = typeof raw.message === 'string' ? raw.message.slice(0, 1_024) : ''
    return message ? { type: 'error', requestId: id, message } : null
  }
  const debugOperation = operation(raw.operation)
  const result = bounded(raw.result)
  return raw.type === 'developerServices.debug.result' && debugOperation && result !== undefined
    ? { type: 'result', requestId: id, operation: debugOperation, state: state(raw.state), result }
    : null
}

function path(value: unknown) {
  return typeof value === 'string' ? normalizeWorkspaceRelativePath(value) : null
}

function positive(value: unknown, allowZero = false) {
  return typeof value === 'number' && Number.isInteger(value) && value >= (allowZero ? 0 : 1) && value <= 1_000_000_000 ? value : null
}

function text(value: unknown, maximum = 16_384) { return typeof value === 'string' ? value.slice(0, maximum) : '' }

export class DesktopDotNetDebuggerController {
  private webview: WebViewBridge | null = null
  private sequence = 0
  private readonly listeners = new Set<() => void>()
  private readonly pending = new Map<string, Pending>()
  private snapshot: DotNetDebugSnapshot = {
    available: bridge() !== null, state: 'inactive', busy: false, targets: [], selectedProgram: '', breakpoints: {},
    threads: [], frames: [], scopes: [], variables: [], activeThreadId: null, activeFrameId: null,
    stoppedLocation: null, evaluation: '', output: '', error: '',
  }
  private readonly receive = (event: MessageEvent) => {
    const frame = normalizeDotNetDebugFrame(event.data)
    if (!frame) return
    if (frame.type === 'event') { void this.handleEvent(frame); return }
    const pending = this.pending.get(frame.requestId)
    if (!pending) return
    if (frame.type === 'error') {
      this.complete(frame.requestId)
      pending.reject(new Error(frame.message))
    } else if (frame.operation === pending.operation) {
      this.complete(frame.requestId)
      pending.resolve(frame)
    }
  }

  getSnapshot = () => this.snapshot
  subscribe = (listener: () => void) => { this.listeners.add(listener); this.connect(); return () => this.listeners.delete(listener) }

  setSelectedProgram(value: string) {
    if (!this.snapshot.targets.includes(value)) return
    this.update({ selectedProgram: value })
  }

  async refreshTargets(configuration: 'Debug' | 'Release') {
    try {
      const result = await this.run('targets', { configuration: configuration.toLowerCase() })
      const targets = Array.isArray(result.result) ? result.result.map(path).filter((item): item is string => Boolean(item)).slice(0, 128) : []
      this.update({ targets, selectedProgram: targets.includes(this.snapshot.selectedProgram) ? this.snapshot.selectedProgram : targets[0] ?? '', error: '' })
    } catch (reason) { this.fail(reason) }
  }

  async launch(stopAtEntry = false, argumentsList: readonly string[] = []) {
    if (!this.snapshot.selectedProgram) { this.update({ error: 'Build a runnable .NET target, then select its program.' }); return }
    await this.withBusy(async () => {
      const launched = await this.run('launch', { programPath: this.snapshot.selectedProgram, workingDirectory: '', arguments: [...argumentsList], stopAtEntry })
      this.update({ state: launched.state, error: '', output: '', threads: [], frames: [], scopes: [], variables: [], stoppedLocation: null })
      for (const [sourcePath, lines] of Object.entries(this.snapshot.breakpoints)) await this.setBreakpoints(sourcePath, lines)
      const configured = await this.run('configuration-done', {})
      this.update({ state: configured.state })
    })
  }

  async attach(processId: number) {
    if (!Number.isInteger(processId) || processId <= 0) { this.update({ error: 'Enter a positive .NET process ID.' }); return }
    await this.withBusy(async () => {
      const attached = await this.run('attach', { processId })
      this.update({ state: attached.state, error: '', output: '', threads: [], frames: [], scopes: [], variables: [], stoppedLocation: null })
      for (const [sourcePath, lines] of Object.entries(this.snapshot.breakpoints)) await this.setBreakpoints(sourcePath, lines)
      const configured = await this.run('configuration-done', {})
      this.update({ state: configured.state })
    })
  }

  toggleBreakpoint(sourcePath: string, line: number) {
    const normalized = path(sourcePath)
    if (!normalized || !positive(line)) return
    const current = [...(this.snapshot.breakpoints[normalized] ?? [])]
    const index = current.indexOf(line)
    if (index >= 0) current.splice(index, 1); else current.push(line)
    current.sort((left, right) => left - right)
    const breakpoints = { ...this.snapshot.breakpoints, [normalized]: current }
    this.update({ breakpoints })
    if (this.isSessionActive()) void this.setBreakpoints(normalized, current).catch((reason) => this.fail(reason))
  }

  async continue() { await this.control('continue') }
  async stepOver() { await this.control('step-over') }
  async stepInto() { await this.control('step-into') }
  async stepOut() { await this.control('step-out') }

  async disconnect() {
    await this.withBusy(async () => {
      await this.run('disconnect', {})
      this.update({ state: 'inactive', threads: [], frames: [], scopes: [], variables: [], activeThreadId: null, activeFrameId: null, stoppedLocation: null })
    })
  }

  async selectFrame(frame: DebugStackFrame) {
    this.update({ activeFrameId: frame.id, stoppedLocation: frame.path ? { path: frame.path, line: frame.line, column: frame.column } : this.snapshot.stoppedLocation })
    if (frame.path && typeof window !== 'undefined') window.dispatchEvent(new CustomEvent('hermes-workspace-navigate', { detail: { path: frame.path, line: frame.line, column: frame.column } }))
    await this.loadScopes(frame.id)
  }

  async evaluate(expression: string) {
    if (!expression.trim()) return
    try {
      const response = await this.run('evaluate', { expression: expression.slice(0, 16 * 1024), frameId: this.snapshot.activeFrameId ?? -1, context: 'repl' })
      const result = record(response.result)
      this.update({ evaluation: text(result?.result, maximumOutputCharacters), error: '' })
    } catch (reason) { this.fail(reason) }
  }

  private async setBreakpoints(sourcePath: string, lines: readonly number[]) {
    await this.run('set-breakpoints', { sourcePath, breakpoints: lines.slice(0, 2_048).map((line) => ({ line })) })
  }

  private async control(operation: Extract<DebugOperation, 'continue' | 'step-over' | 'step-into' | 'step-out'>) {
    const threadId = this.snapshot.activeThreadId
    if (!threadId) return
    await this.withBusy(async () => {
      const response = await this.run(operation, { threadId })
      this.update({ state: response.state, stoppedLocation: null, scopes: [], variables: [] })
    })
  }

  private async handleEvent(frame: DebugEventFrame) {
    if (frame.event === 'output') {
      const output = text(record(frame.body)?.output, maximumOutputCharacters)
      this.update({ output: (this.snapshot.output + output).slice(-maximumOutputCharacters), state: frame.state })
      return
    }
    if (frame.event === 'stopped') {
      const threadId = positive(record(frame.body)?.threadId)
      this.update({ state: 'stopped', activeThreadId: threadId, error: '' })
      await this.hydrateStopped(threadId)
      return
    }
    if (frame.event === 'continued') this.update({ state: 'running', stoppedLocation: null, scopes: [], variables: [] })
    else if (frame.event === 'terminated' || frame.event === 'exited') this.update({ state: 'exited', threads: [], frames: [], scopes: [], variables: [], stoppedLocation: null })
    else this.update({ state: frame.state })
  }

  private async hydrateStopped(preferredThreadId: number | null) {
    try {
      const threadsResponse = await this.run('threads', {})
      const threads = parseThreads(threadsResponse.result)
      const threadId = preferredThreadId ?? threads[0]?.id ?? null
      this.update({ threads, activeThreadId: threadId })
      if (!threadId) return
      const framesResponse = await this.run('stack-trace', { threadId, startFrame: 0, levels: 200 })
      const frames = parseFrames(framesResponse.result)
      const frame = frames[0]
      this.update({ frames, activeFrameId: frame?.id ?? null, stoppedLocation: frame?.path ? { path: frame.path, line: frame.line, column: frame.column } : null })
      if (frame) await this.loadScopes(frame.id)
    } catch (reason) { this.fail(reason) }
  }

  private async loadScopes(frameId: number) {
    const scopesResponse = await this.run('scopes', { frameId })
    const scopes = parseScopes(scopesResponse.result)
    this.update({ scopes, variables: [] })
    const variables: DebugVariable[] = []
    for (const scope of scopes.filter((candidate) => !candidate.expensive).slice(0, 8)) {
      if (scope.variablesReference <= 0) continue
      const response = await this.run('variables', { variablesReference: scope.variablesReference, start: 0, count: 200 })
      variables.push(...parseVariables(response.result))
      if (variables.length >= 1_000) break
    }
    this.update({ variables: variables.slice(0, 1_000) })
  }

  private isSessionActive() { return !['inactive', 'exited', 'disconnected', 'faulted', 'unknown'].includes(this.snapshot.state) }

  private async withBusy(action: () => Promise<void>) {
    if (this.snapshot.busy) return
    this.update({ busy: true, error: '' })
    try { await action() } catch (reason) { this.fail(reason) } finally { this.update({ busy: false }) }
  }

  private fail(reason: unknown) { this.update({ error: reason instanceof Error ? reason.message : 'The .NET debugger request failed.' }) }

  private run(operation: DebugOperation, payload: Record<string, unknown>, signal?: AbortSignal) {
    const webview = this.connect()
    if (!webview) return Promise.reject(new Error('The .NET debugger is available in the Hermes desktop app.'))
    if (signal?.aborted) return Promise.reject(new Error('The .NET debugger request was cancelled.'))
    const id = this.nextId(operation)
    return new Promise<DebugResultFrame>((resolve, reject) => {
      const pending: Pending = { operation, resolve, reject }
      if (signal) {
        pending.signal = signal
        pending.abort = () => {
          if (!this.pending.has(id)) return
          this.complete(id)
          try { webview.postMessage({ type: 'developerServices.debug.cancel', version: DEVELOPER_SERVICES_PROTOCOL_VERSION, requestId: this.nextId('cancel'), targetRequestId: id }) } catch { }
          reject(new Error('The .NET debugger request was cancelled.'))
        }
        signal.addEventListener('abort', pending.abort, { once: true })
      }
      this.pending.set(id, pending)
      try { webview.postMessage({ type: messageType(operation), version: DEVELOPER_SERVICES_PROTOCOL_VERSION, requestId: id, ...payload }) }
      catch (reason) { this.complete(id); reject(reason instanceof Error ? reason : new Error('The .NET debugger request could not be sent.')) }
    })
  }

  private complete(id: string) {
    const pending = this.pending.get(id)
    this.pending.delete(id)
    if (pending?.abort) pending.signal?.removeEventListener('abort', pending.abort)
  }

  private connect() {
    const next = bridge()
    if (next && next !== this.webview) {
      this.webview?.removeEventListener('message', this.receive)
      this.webview = next
      next.addEventListener('message', this.receive)
      this.update({ available: true })
    }
    return next
  }

  private nextId(kind: string) { this.sequence = (this.sequence + 1) % Number.MAX_SAFE_INTEGER; return `debug-${kind}:${Date.now().toString(36)}:${this.sequence.toString(36)}` }
  private update(patch: Partial<DotNetDebugSnapshot>) { this.snapshot = { ...this.snapshot, ...patch }; this.listeners.forEach((listener) => listener()) }
}

function messageType(operation: DebugOperation) {
  const names: Record<DebugOperation, string> = {
    targets: 'targets', launch: 'launch', attach: 'attach', 'set-breakpoints': 'setBreakpoints',
    'configuration-done': 'configurationDone', threads: 'threads', 'stack-trace': 'stackTrace', scopes: 'scopes',
    variables: 'variables', evaluate: 'evaluate', continue: 'continue', 'step-over': 'stepOver',
    'step-into': 'stepInto', 'step-out': 'stepOut', disconnect: 'disconnect',
  }
  return `developerServices.debug.${names[operation]}`
}

function parseThreads(value: unknown): DebugThread[] { return (Array.isArray(value) ? value : []).slice(0, 1_000).flatMap((item) => { const raw = record(item); const id = positive(raw?.id); return id ? [{ id, name: text(raw?.name, 512) }] : [] }) }
function parseFrames(value: unknown): DebugStackFrame[] { return (Array.isArray(value) ? value : []).slice(0, 1_000).flatMap((item) => { const raw = record(item); const id = positive(raw?.id, true); const framePath = path(raw?.path); const line = positive(raw?.line); const column = positive(raw?.column); return id !== null && line && column ? [{ id, name: text(raw?.name, 1_024), path: framePath ?? '', line, column }] : [] }) }
function parseScopes(value: unknown): DebugScope[] { return (Array.isArray(value) ? value : []).slice(0, 256).flatMap((item) => { const raw = record(item); const reference = positive(raw?.variablesReference, true); return raw && reference !== null ? [{ name: text(raw.name, 512), variablesReference: reference, expensive: raw.expensive === true, presentationHint: text(raw.presentationHint, 128) }] : [] }) }
function parseVariables(value: unknown): DebugVariable[] { return (Array.isArray(value) ? value : []).slice(0, 1_000).flatMap((item) => { const raw = record(item); const reference = positive(raw?.variablesReference, true); return raw && reference !== null ? [{ name: text(raw.name, 512), value: text(raw.value), type: text(raw.type, 512), variablesReference: reference, evaluateName: text(raw.evaluateName, 2_048) }] : [] }) }

export const desktopDotNetDebuggerController = new DesktopDotNetDebuggerController()
