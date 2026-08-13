import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { Bot, Check, Code2, ExternalLink, FileCode2, Loader2, RefreshCw, Send, ShieldCheck, Square, TerminalSquare, X } from 'lucide-react'
import { CODEX_APP_SERVER_ADAPTER_VERSION, CodexAppServerClient } from './CodexAppServerClient'
import type { CodexConnectionState, JsonObject, JsonRpcId } from './CodexAppServerClient'
import { isChatGptAccountType, publishCodexAccountTelemetry } from './CodexAccountTelemetry'
import {
  HERMES_CODEX_APPROVAL_REVIEW_EVENT,
  publishCodexApprovalReviewStatus,
  readCodexApprovalReviewRequest,
} from './CodexApprovalReview'

type ChatMessage = { id: string; role: 'user' | 'assistant' | 'system'; text: string }
type ActivityItem = { id: string; kind: 'command' | 'file' | 'tool'; title: string; detail: string; status: string }
type ApprovalRequest = { rpcId: JsonRpcId; title: string; reason: string; detail: string }
type PromptOption = { label: string; description: string }
type PromptQuestion = { id: string; header: string; question: string; isOther: boolean; isSecret: boolean; options: PromptOption[] }
type PromptRequest = { rpcId: JsonRpcId; questions: PromptQuestion[] }
type AccountState = {
  label: string
  authenticated: boolean
  chatGptLinked: boolean
  usedPercent?: number
  weeklyUsedPercent?: number
  weeklyResetAt?: number
  lifetimeTokens?: number
}
type DeviceLogin = { verificationUrl: string; userCode: string }

function object(value: unknown): JsonObject | null { return value && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : null }
function text(value: unknown, fallback = '') { return typeof value === 'string' ? value : fallback }
function number(value: unknown) { return typeof value === 'number' && Number.isFinite(value) ? value : undefined }
function id(value: unknown) { return typeof value === 'string' || typeof value === 'number' ? value : null }

function upsertMessage(messages: ChatMessage[], next: ChatMessage) {
  const index = messages.findIndex((message) => message.id === next.id)
  if (index < 0) return [...messages, next]
  const copy = [...messages]
  copy[index] = next
  return copy
}

function appendMessageDelta(messages: ChatMessage[], messageId: string, delta: string) {
  const existing = messages.find((message) => message.id === messageId)
  return upsertMessage(messages, { id: messageId, role: 'assistant', text: `${existing?.text ?? ''}${delta}` })
}

function upsertActivity(activities: ActivityItem[], next: ActivityItem) {
  const index = activities.findIndex((activity) => activity.id === next.id)
  if (index < 0) return [...activities, next]
  const copy = [...activities]
  copy[index] = next
  return copy
}

function summarizeChanges(value: unknown) {
  if (!Array.isArray(value)) return ''
  return value.map((change) => text(object(change)?.path)).filter(Boolean).join(', ')
}

function normalizeQuestions(value: unknown): PromptQuestion[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((entry) => {
    const raw = object(entry)
    if (!raw || !text(raw.id) || !text(raw.question)) return []
    const options = Array.isArray(raw.options) ? raw.options.flatMap((option) => {
      const item = object(option)
      return item && text(item.label) ? [{ label: text(item.label), description: text(item.description) }] : []
    }) : []
    return [{ id: text(raw.id), header: text(raw.header, 'Codex question'), question: text(raw.question), isOther: raw.isOther === true, isSecret: raw.isSecret === true, options }]
  })
}

function formatTokens(value?: number) {
  if (value === undefined) return null
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}

function PromptCard({ request, onAnswer }: { request: PromptRequest; onAnswer: (answers: Record<string, string[]>) => void }) {
  const [draft, setDraft] = useState<{ requestId: PromptRequest['rpcId']; answers: Record<string, string[]> }>({
    requestId: request.rpcId,
    answers: {},
  })
  const answers = draft.requestId === request.rpcId ? draft.answers : {}
  const updateAnswer = (questionId: string, values: string[]) => {
    setDraft((current) => ({
      requestId: request.rpcId,
      answers: {
        ...(current.requestId === request.rpcId ? current.answers : {}),
        [questionId]: values,
      },
    }))
  }
  const complete = request.questions.every((question) => (answers[question.id]?.length ?? 0) > 0)
  return (
    <section className="codex-request codex-prompt-card">
      <header><ShieldCheck size={14} /><div><strong>Codex needs your input</strong><small>Answers return only to the local Codex app-server.</small></div></header>
      {request.questions.map((question) => (
        <div className="codex-question" key={question.id}>
          <b>{question.header}</b><p>{question.question}</p>
          {question.options.length > 0 && <div>{question.options.map((option) => <button type="button" className={answers[question.id]?.[0] === option.label ? 'selected' : ''} onClick={() => updateAnswer(question.id, [option.label])} key={option.label}>{option.label}<small>{option.description}</small></button>)}</div>}
          {(question.options.length === 0 || question.isOther) && <input type={question.isSecret ? 'password' : 'text'} value={answers[question.id]?.[0] ?? ''} placeholder={question.isSecret ? 'Private answer' : 'Type an answer'} onChange={(event) => updateAnswer(question.id, event.target.value ? [event.target.value] : [])} />}
        </div>
      ))}
      <footer><button type="button" className="primary" disabled={!complete} onClick={() => onAnswer(answers)}><Check size={12} /> Send answers</button></footer>
    </section>
  )
}

export function CodexPanel({ active, dockControls }: { active: boolean; dockControls?: ReactNode }) {
  const clientRef = useRef<CodexAppServerClient | null>(null)
  const workspaceRef = useRef<string | null>(null)
  const threadRef = useRef<string | null>(null)
  const turnRef = useRef<string | null>(null)
  const bootstrapRef = useRef(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const [connection, setConnection] = useState<CodexConnectionState>('connecting')
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [activities, setActivities] = useState<ActivityItem[]>([])
  const [approvals, setApprovals] = useState<ApprovalRequest[]>([])
  const [prompt, setPrompt] = useState<PromptRequest | null>(null)
  const [account, setAccount] = useState<AccountState>({ label: 'Checking account', authenticated: false, chatGptLinked: false })
  const [deviceLogin, setDeviceLogin] = useState<DeviceLogin | null>(null)

  useEffect(() => {
    publishCodexAccountTelemetry({ checked: ready, ...account })
  }, [account, ready])

  useEffect(() => {
    const client = new CodexAppServerClient()
    clientRef.current = client

    async function refreshUsage() {
      try {
        const limits = object(await client.request('account/rateLimits/read'))
        const primary = object(object(limits?.rateLimits)?.primary)
        const secondary = object(object(limits?.rateLimits)?.secondary)
        const usedPercent = number(primary?.usedPercent)
        const secondaryUsedPercent = number(secondary?.usedPercent)
        const primaryWindowMinutes = number(primary?.windowDurationMins)
        const secondaryWindowMinutes = number(secondary?.windowDurationMins)
        const primaryIsWeekly = primaryWindowMinutes !== undefined && primaryWindowMinutes >= 6 * 24 * 60
        const secondaryIsWeekly = secondaryWindowMinutes !== undefined && secondaryWindowMinutes >= 6 * 24 * 60
        const weeklyUsedPercent = secondaryIsWeekly
          ? secondaryUsedPercent
          : primaryIsWeekly ? usedPercent : undefined
        const weeklyResetAt = number((secondaryIsWeekly ? secondary : primaryIsWeekly ? primary : null)?.resetsAt)
        setAccount((current) => ({ ...current, usedPercent, weeklyUsedPercent, weeklyResetAt }))
      } catch { }
      try {
        const usage = object(await client.request('account/usage/read'))
        const lifetimeTokens = number(object(usage?.summary)?.lifetimeTokens)
        if (lifetimeTokens !== undefined) setAccount((current) => ({ ...current, lifetimeTokens }))
      } catch { }
    }

    async function refreshAccount() {
      const result = object(await client.request('account/read', { refreshToken: false }))
      const current = object(result?.account)
      const requiresAuth = result?.requiresOpenaiAuth === true
      const plan = text(current?.planType)
      const type = text(current?.type)
      const authenticated = current !== null || !requiresAuth
      const chatGptLinked = isChatGptAccountType(type)
      setAccount({ label: plan ? `ChatGPT ${plan}` : type === 'apiKey' ? 'OpenAI API' : authenticated ? 'Codex ready' : 'Sign in required', authenticated, chatGptLinked })
      setReady(true)
      if (chatGptLinked) void refreshUsage()
    }

    async function bootstrap(initialized: boolean) {
      if (bootstrapRef.current) return
      bootstrapRef.current = true
      setError(null)
      try {
        if (!initialized) {
          await client.request('initialize', { clientInfo: { name: 'hermes_workbench', title: 'Hermes Workbench', version: '0.1.0' }, capabilities: null })
          client.notify('initialized', {})
        }
        await refreshAccount()
      } catch (exception) {
        bootstrapRef.current = false
        setError(exception instanceof Error ? exception.message : 'Codex initialization failed.')
      }
    }

    function handleProtocol(payload: JsonObject) {
      const method = text(payload.method)
      const parameters = object(payload.params)
      if (!method) return
      if (method === 'item/agentMessage/delta') {
        const messageId = text(parameters?.itemId)
        if (messageId) setMessages((current) => appendMessageDelta(current, messageId, text(parameters?.delta)))
      }
      if (method === 'item/completed') {
        const item = object(parameters?.item)
        const itemId = text(item?.id)
        if (item?.type === 'agentMessage' && itemId) setMessages((current) => upsertMessage(current, { id: itemId, role: 'assistant', text: text(item.text) }))
        if (itemId && item?.type === 'commandExecution') setActivities((current) => upsertActivity(current, { id: itemId, kind: 'command', title: text(item.command, 'Command'), detail: text(item.aggregatedOutput), status: text(item.status, 'completed') }))
        if (itemId && item?.type === 'fileChange') setActivities((current) => upsertActivity(current, { id: itemId, kind: 'file', title: 'File changes', detail: summarizeChanges(item.changes), status: text(item.status, 'completed') }))
      }
      if (method === 'item/started') {
        const item = object(parameters?.item)
        const itemId = text(item?.id)
        if (itemId && item?.type === 'commandExecution') setActivities((current) => upsertActivity(current, { id: itemId, kind: 'command', title: text(item.command, 'Command'), detail: text(item.cwd), status: text(item.status, 'running') }))
        if (itemId && item?.type === 'fileChange') setActivities((current) => upsertActivity(current, { id: itemId, kind: 'file', title: 'Editing files', detail: summarizeChanges(item.changes), status: text(item.status, 'running') }))
        if (itemId && item?.type === 'mcpToolCall') setActivities((current) => upsertActivity(current, { id: itemId, kind: 'tool', title: `${text(item.server)} / ${text(item.tool)}`, detail: '', status: text(item.status, 'running') }))
      }
      if (method === 'turn/started') turnRef.current = text(object(parameters?.turn)?.id) || null
      if (method === 'turn/completed') { turnRef.current = null; setBusy(false) }
      if (method === 'error') {
        turnRef.current = null
        setBusy(false)
        setError(text(object(parameters?.error)?.message, 'The Codex turn failed.'))
      }
      if (method === 'account/updated' || method === 'account/login/completed') void refreshAccount()
      if (method === 'account/rateLimits/updated') void refreshUsage()
      if (method === 'serverRequest/resolved') {
        const requestId = id(parameters?.requestId)
        if (requestId !== null) setApprovals((current) => current.filter((approval) => String(approval.rpcId) !== String(requestId)))
      }
      const rpcId = id(payload.id)
      if (rpcId !== null && (method === 'item/commandExecution/requestApproval' || method === 'item/fileChange/requestApproval')) {
        const command = text(parameters?.command)
        const grantRoot = text(parameters?.grantRoot)
        setApprovals((current) => [...current.filter((approval) => String(approval.rpcId) !== String(rpcId)), {
          rpcId,
          title: method.includes('commandExecution') ? 'Run this command?' : 'Apply these file changes?',
          reason: text(parameters?.reason, 'Codex is waiting for your approval.'),
          detail: command || grantRoot || text(parameters?.cwd),
        }])
      }
      if (rpcId !== null && method === 'item/tool/requestUserInput') {
        const questions = normalizeQuestions(parameters?.questions)
        if (questions.length > 0) setPrompt({ rpcId, questions })
      }
    }

    const removeState = client.onState(setConnection)
    const removeFrame = client.onFrame((frame) => {
      if (frame.type === 'ready') {
        workspaceRef.current = frame.cwd ?? null
        void bootstrap(frame.initialized === true)
      }
      if (frame.type === 'protocol' && frame.payload) handleProtocol(frame.payload)
      if (frame.type === 'error' || frame.type === 'unavailable') setError(frame.message ?? 'Codex is unavailable.')
      if (frame.type === 'exit') { turnRef.current = null; setBusy(false) }
    })
    client.start()
    return () => {
      removeFrame()
      removeState()
      client.close()
      clientRef.current = null
    }
  }, [])

  useEffect(() => {
    if (!active) return
    requestAnimationFrame(() => scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' }))
  }, [active, messages, activities, approvals, prompt])

  const startCodexTurn = useCallback(async (message: string) => {
    const client = clientRef.current
    if (!client || !message || busy || !ready || !account.authenticated || connection !== 'open') {
      throw new Error('Codex is not ready to review this command yet.')
    }
    setError(null)
    setMessages((current) => [...current, { id: `user-${Date.now()}`, role: 'user', text: message }])
    setBusy(true)
    let threadId = threadRef.current
    if (!threadId) {
      const result = object(await client.request('thread/start', {
        cwd: workspaceRef.current,
        approvalPolicy: 'on-request',
        sandbox: 'workspace-write',
        personality: 'friendly',
        serviceName: 'hermes_workbench',
      }))
      threadId = text(object(result?.thread)?.id)
      if (!threadId) throw new Error('Codex did not return a thread id.')
      threadRef.current = threadId
    }
    await client.request('turn/start', { threadId, input: [{ type: 'text', text: message, text_elements: [] }] })
  }, [account.authenticated, busy, connection, ready])

  async function sendMessage(event: FormEvent) {
    event.preventDefault()
    const message = draft.trim()
    if (!message) return
    setDraft('')
    try {
      await startCodexTurn(message)
    } catch (exception) {
      setBusy(false)
      setDraft(message)
      setError(exception instanceof Error ? exception.message : 'Could not start the Codex turn.')
    }
  }

  useEffect(() => {
    const review = (event: Event) => {
      const request = readCodexApprovalReviewRequest(event)
      if (!request) return

      if (!ready || !account.authenticated || connection !== 'open' || busy) {
        setDraft(request.prompt)
        setError('Codex review is prepared in the composer. Send it when Codex is ready.')
        publishCodexApprovalReviewStatus({
          requestId: request.requestId,
          status: 'prepared',
          message: 'Codex review prepared; send it when Codex is ready.',
        })
        return
      }

      void startCodexTurn(request.prompt).then(() => {
        publishCodexApprovalReviewStatus({
          requestId: request.requestId,
          status: 'sent',
          message: 'Sent to Codex for explanation. Nothing was approved or executed.',
        })
      }).catch((exception) => {
        const message = exception instanceof Error ? exception.message : 'Codex could not review the command.'
        setDraft(request.prompt)
        setBusy(false)
        setError(message)
        publishCodexApprovalReviewStatus({ requestId: request.requestId, status: 'failed', message })
      })
    }
    window.addEventListener(HERMES_CODEX_APPROVAL_REVIEW_EVENT, review)
    return () => window.removeEventListener(HERMES_CODEX_APPROVAL_REVIEW_EVENT, review)
  }, [account.authenticated, busy, connection, ready, startCodexTurn])

  async function startLogin() {
    const client = clientRef.current
    if (!client) return
    setError(null)
    try {
      const result = object(await client.request('account/login/start', { type: 'chatgptDeviceCode' }))
      const verificationUrl = text(result?.verificationUrl)
      const userCode = text(result?.userCode)
      if (!verificationUrl || !userCode) throw new Error('Codex did not return a device sign-in code.')
      setDeviceLogin({ verificationUrl, userCode })
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : 'Could not start Codex sign-in.')
    }
  }

  useEffect(() => {
    const start = () => void startLogin()
    window.addEventListener('hermes-codex-login-requested', start)
    return () => window.removeEventListener('hermes-codex-login-requested', start)
  }, [])

  function answerApproval(request: ApprovalRequest, decision: 'accept' | 'acceptForSession' | 'decline') {
    clientRef.current?.respond(request.rpcId, { decision })
    setApprovals((current) => current.filter((approval) => approval !== request))
  }

  function answerPrompt(answers: Record<string, string[]>) {
    if (!prompt) return
    clientRef.current?.respond(prompt.rpcId, { answers: Object.fromEntries(Object.entries(answers).map(([questionId, values]) => [questionId, { answers: values }])) })
    setPrompt(null)
  }

  function newChat() {
    if (busy) return
    threadRef.current = null
    turnRef.current = null
    setMessages([])
    setActivities([])
    setApprovals([])
    setPrompt(null)
    setError(null)
  }

  const usage = account.usedPercent === undefined ? null : `${Math.round(account.usedPercent)}% used`
  const tokens = formatTokens(account.lifetimeTokens)
  return (
    <section className={`codex-panel ${active ? 'active' : ''}`}>
      <header>
        <span className="codex-mark"><Code2 size={14} /></span>
        <div className="codex-title"><strong>Codex</strong><small>{account.label}{usage ? ` · ${usage}` : ''}</small></div>
        {dockControls}
        <button type="button" title="New Codex chat" disabled={busy} onClick={newChat}><RefreshCw size={13} /></button>
      </header>
      <div className="codex-conversation" ref={scrollRef}>
        {messages.length === 0 && activities.length === 0 && <div className="codex-empty"><Code2 size={28} /><strong>Second agent, same workspace</strong><p>Codex uses its official app-server protocol, streams work here, and keeps approvals in your hands.</p>{tokens && <small>{tokens} lifetime Codex tokens</small>}</div>}
        {messages.map((message) => <article className={`codex-message ${message.role}`} key={message.id}><span>{message.role === 'user' ? 'You' : message.role === 'assistant' ? 'C' : 'i'}</span><div><strong>{message.role === 'user' ? 'You' : message.role === 'assistant' ? 'Codex' : 'Workbench'}</strong><p>{message.text || (busy ? 'Thinking…' : '')}</p></div></article>)}
        {activities.slice(-8).map((activity) => <article className={`codex-activity ${activity.status}`} key={activity.id}><span>{activity.kind === 'command' ? <TerminalSquare size={12} /> : activity.kind === 'file' ? <FileCode2 size={12} /> : <Bot size={12} />}</span><div><strong>{activity.title}</strong><small>{activity.detail || activity.status}</small></div><i>{activity.status === 'inProgress' || activity.status === 'running' ? <Loader2 size={11} /> : <Check size={11} />}</i></article>)}
        {approvals.map((approval) => <section className="codex-request" key={String(approval.rpcId)}><header><ShieldCheck size={14} /><div><strong>{approval.title}</strong><small>{approval.reason}</small></div></header>{approval.detail && <pre>{approval.detail}</pre>}<footer><button type="button" className="primary" onClick={() => answerApproval(approval, 'accept')}><Check size={12} /> Allow once</button><button type="button" onClick={() => answerApproval(approval, 'acceptForSession')}>Allow session</button><button type="button" className="reject" onClick={() => answerApproval(approval, 'decline')}><X size={12} /> Decline</button></footer></section>)}
        {prompt && <PromptCard key={String(prompt.rpcId)} request={prompt} onAnswer={answerPrompt} />}
        {error && <div className="codex-error">{error}</div>}
        {connection === 'browser' && <div className="codex-notice"><Code2 size={18} /><strong>Codex is available in the desktop app</strong><span>The web preview cannot start local agents or commands.</span></div>}
        {!account.authenticated && connection === 'open' && <div className="codex-login"><ShieldCheck size={19} /><strong>Connect your Codex account</strong><p>This uses Codex-managed ChatGPT device sign-in. No API key enters the web interface.</p><button type="button" onClick={startLogin}>Start secure sign-in</button>{deviceLogin && <div><code>{deviceLogin.userCode}</code><a href={deviceLogin.verificationUrl} target="_blank" rel="noreferrer">Open sign-in <ExternalLink size={11} /></a></div>}</div>}
      </div>
      <form className="codex-composer" onSubmit={sendMessage}>
        <textarea value={draft} onChange={(event) => setDraft(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); event.currentTarget.form?.requestSubmit() } }} disabled={!ready || !account.authenticated || connection !== 'open'} placeholder={connection === 'browser' ? 'Open the desktop app to use Codex' : !account.authenticated ? 'Connect Codex to continue' : 'Ask Codex about this workspace…'} />
        <footer><span><i className={`console-status ${connection}`} /> host adapter v{CODEX_APP_SERVER_ADAPTER_VERSION}</span>{busy ? <button type="button" title="Interrupt turn" onClick={() => { const client = clientRef.current; if (client && threadRef.current && turnRef.current) void client.request('turn/interrupt', { threadId: threadRef.current, turnId: turnRef.current }); }}><Square size={11} /></button> : <button type="submit" disabled={!draft.trim() || !ready || !account.authenticated}><Send size={12} /></button>}</footer>
      </form>
    </section>
  )
}
