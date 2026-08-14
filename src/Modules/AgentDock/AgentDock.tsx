import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import {
  ArrowDown,
  ArrowUp,
  Bell,
  Bot,
  BrainCircuit,
  Braces,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleStop,
  Clock3,
  FileCode2,
  ImageIcon,
  KeyRound,
  LoaderCircle,
  LockKeyhole,
  ListPlus,
  MessageCircleQuestion,
  Mic,
  MicOff,
  Paperclip,
  Pencil,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  SendHorizontal,
  ShieldAlert,
  Sparkles,
  TerminalSquare,
  Trash2,
  Volume2,
  WandSparkles,
  Wrench,
  XCircle,
} from 'lucide-react'
import { useHermesChat } from '../HermesGateway/useHermesChat'
import type { HermesChatMessage, HermesComposerAttachment } from '../HermesGateway/useHermesChat'
import type { HermesNotification } from '../HermesGateway/HermesNotificationAdapter'
import {
  enqueueHermesPrompt,
  HERMES_PROMPT_QUEUE_ADAPTER_VERSION,
  isHermesImmediatePauseInstruction,
  promoteHermesQueuedPrompt,
  removeHermesQueuedPrompt,
  shouldAutoDrainHermesQueue,
  updateHermesQueuedPrompt,
} from '../HermesGateway/HermesPromptQueue'
import type { HermesQueuedPrompt } from '../HermesGateway/HermesPromptQueue'
import { HERMES_RUNTIME_ADAPTER_VERSION } from '../HermesGateway/HermesRuntimeAdapter'
import { createHermesBridgeSnapshot, registerHermesConversationBridge } from '../HermesGateway/HermesConversationBridgeAdapter'
import type { HermesBridgeObservation } from '../HermesGateway/HermesConversationBridgeAdapter'
import type {
  HermesApprovalChoice,
  HermesApprovalRequest,
  HermesInteractivePrompt,
  HermesToolRun,
} from '../HermesGateway/HermesRuntimeAdapter'
import type { HermesSession } from '../HermesSessions/HermesSessionApi'
import { ModelControlPopover } from '../HermesSettings/ModelControlPopover'
import { HermesReasoningControl } from './HermesReasoningControl'
import {
  HERMES_NATURAL_VOICE_CLIENT_REVISION,
  HERMES_NATURAL_VOICE_IDLE,
  HermesNaturalVoicePlayer,
} from './HermesNaturalVoice'
import './HermesNaturalVoice.css'
import {
  HERMES_SPEECH_INPUT_IDLE,
  HermesSpeechInputController,
} from '../HermesSpeechVoice/HermesSpeechInput'
import { isAgentScrollNearBottom, preserveAgentScrollAnchor } from './AgentScroll'
import { InlineDiffCard } from './InlineDiffCard'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'
import { useHermesMemoryStatus } from './HermesMemoryStatus'
import { QuarkCompanion, quarkConversationPrompt } from './QuarkCompanion'
import type { QuarkPhase } from './QuarkCompanion'
import type { HermesDesktopUiAction } from '../HermesGateway/HermesDesktopUiAdapter'
import {
  codexApprovalReviewId,
  HERMES_CODEX_APPROVAL_REVIEW_STATUS_EVENT,
  readCodexApprovalReviewStatus,
  sendApprovalReviewToCodex,
} from '../CodexAgent/CodexApprovalReview'

const HermesMarkdown = lazy(() => import('./HermesMarkdown').then((module) => ({ default: module.HermesMarkdown })))

export type SessionOpenRequest = { nonce: number; session: HermesSession }

type Props = {
  sessionRequest?: SessionOpenRequest | null
  onSessionOpened?: (session: HermesSession) => void
  onSignIn?: () => void
  dockControls?: ReactNode
  onDesktopUiAction?: (action: HermesDesktopUiAction) => void
}

type AgentDockClipboardData = Pick<DataTransfer, 'files' | 'getData' | 'items'>
type AgentDockAttachmentOrigin = Parameters<ReturnType<typeof useHermesChat>['addAttachments']>[1]

export function extractAgentDockClipboardImageFiles(clipboard: AgentDockClipboardData): File[] {
  const itemImages: Array<{ file: File; mediaType: string }> = []
  const isImage = (file: File | null, mediaType = file?.type ?? ''): file is File => (
    Boolean(file && file.size > 0 && mediaType.toLowerCase().startsWith('image/'))
  )

  for (let index = 0; index < clipboard.items.length; index += 1) {
    const item = clipboard.items[index]
    if (item.kind !== 'file') continue
    const file = item.getAsFile()
    if (isImage(file, item.type)) itemImages.push({ file, mediaType: item.type.toLowerCase() })
  }
  if (itemImages.length > 0) {
    const mediaTypes = new Set(itemImages.map((image) => image.mediaType))
    if (mediaTypes.size === 1) return itemImages.map((image) => image.file)
    const preferredMediaType = mediaTypes.has('image/png') ? 'image/png' : itemImages[0].mediaType
    return itemImages.filter((image) => image.mediaType === preferredMediaType).map((image) => image.file)
  }

  const files: File[] = []
  for (let index = 0; index < clipboard.files.length; index += 1) {
    const file = clipboard.files.item(index)
    if (isImage(file)) files.push(file)
  }
  return files
}

export function handleAgentDockClipboardImagePaste(
  clipboard: AgentDockClipboardData,
  addAttachments: (files: File[], origin?: AgentDockAttachmentOrigin) => void,
  preventDefault: () => void,
) {
  const images = extractAgentDockClipboardImageFiles(clipboard)
  if (images.length === 0) return false
  addAttachments(images, 'clipboard')
  if (!clipboard.getData('text/plain')) preventDefault()
  return true
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0)} MB`
}

const suggestions = [
  { icon: FileCode2, label: 'Explain this workspace' },
  { icon: Search, label: 'Find the next task' },
  { icon: Wrench, label: 'Check Docker services' },
]

export function scheduleToolRunDetailsHeightChange(
  row: HTMLElement,
  before: ToolRunAnchorMeasurement,
  onDetailsHeightChange: (row: HTMLElement, before: ToolRunAnchorMeasurement) => void,
  requestFrame: (callback: FrameRequestCallback) => number = window.requestAnimationFrame,
) {
  if (before.height <= 0) return
  requestFrame(() => onDetailsHeightChange(row, before))
}

export type ToolRunAnchorMeasurement = { height: number; bottom: number }

export function measureToolRunAnchor(row: Pick<HTMLElement, 'offsetHeight' | 'getBoundingClientRect'>): ToolRunAnchorMeasurement {
  return { height: row.offsetHeight, bottom: row.getBoundingClientRect().bottom }
}

export function preserveToolRunReadingAnchor(
  conversation: Pick<HTMLElement, 'scrollTop' | 'getBoundingClientRect'>,
  row: Pick<HTMLElement, 'offsetHeight' | 'getBoundingClientRect'>,
  before: ToolRunAnchorMeasurement,
  following: boolean,
  selectionTouchesRow = false,
) {
  if (following || selectionTouchesRow || before.bottom > conversation.getBoundingClientRect().top) return false
  const heightDelta = row.offsetHeight - before.height
  if (heightDelta === 0) return false
  conversation.scrollTop = preserveAgentScrollAnchor(conversation.scrollTop, heightDelta)
  return true
}
export function ToolRunRow({
  tool,
  initiallyExpanded = false,
  onDetailsHeightChange,
}: {
  tool: HermesToolRun
  initiallyExpanded?: boolean
  onDetailsHeightChange?: (row: HTMLElement, before: ToolRunAnchorMeasurement) => void
}) {
  const [expanded, setExpanded] = useState(initiallyExpanded)
  const rowRef = useRef<HTMLElement>(null)
  const details = [tool.context, tool.input, tool.output, tool.inlineDiff].filter(Boolean)
  const Icon = tool.phase === 'running' ? LoaderCircle : tool.phase === 'error' ? XCircle : CheckCircle2

  function toggleDetails() {
    const row = rowRef.current
    const before = row ? measureToolRunAnchor(row) : null
    setExpanded((value) => !value)
    if (row && before && onDetailsHeightChange) scheduleToolRunDetailsHeightChange(row, before, onDetailsHeightChange)
  }

  return (
    <article ref={rowRef} className={`tool-run ${tool.phase}`}>
      <button
        type="button"
        className="tool-run-summary"
        aria-expanded={expanded}
        disabled={details.length === 0}
        onClick={toggleDetails}
      >
        <span className="tool-run-icon"><Icon size={13} /></span>
        <span className="tool-run-copy">
          <strong>{tool.name}</strong>
          <small>{tool.context ?? (tool.phase === 'running' ? 'Running…' : tool.phase === 'error' ? 'Failed' : 'Completed')}</small>
        </span>
        {typeof tool.durationSeconds === 'number' && (
          <span className="tool-duration"><Clock3 size={10} /> {tool.durationSeconds.toFixed(2)}s</span>
        )}
        {details.length > 0 && <ChevronRight size={13} className={expanded ? 'expanded' : ''} />}
      </button>
      {expanded && (
        <div className="tool-run-details">
          {tool.input && <><span>Input</span><pre>{tool.input}</pre></>}
          {tool.output && <><span>{tool.phase === 'error' ? 'Error' : 'Output'}</span><pre>{tool.output}</pre></>}
          {tool.inlineDiff && <><span>File changes</span><InlineDiffCard raw={tool.inlineDiff} /></>}
        </div>
      )}
    </article>
  )
}

function ToolTimeline({ activity, tools, onDetailsHeightChange }: { activity: string | null; tools: HermesToolRun[]; onDetailsHeightChange: (row: HTMLElement, before: ToolRunAnchorMeasurement) => void }) {
  const latestTool = tools[tools.length - 1]
  const activeTool = [...tools].reverse().find((tool) => tool.phase === 'running')
  const summary = activity
    ?? activeTool?.context
    ?? activeTool?.name
    ?? latestTool?.context
    ?? latestTool?.name

  return (
    <details className="tool-timeline">
      <summary>
        <TerminalSquare size={13} />
        <span>
          <strong>Tool activity</strong>
          {summary && <em>{summary}</em>}
        </span>
        <small>{tools.length}</small>
        <ChevronRight size={13} className="tool-timeline-chevron" />
      </summary>
      <div className="tool-timeline-list">
        {tools.map((tool) => <ToolRunRow key={tool.id} tool={tool} onDetailsHeightChange={onDetailsHeightChange} />)}
      </div>
    </details>
  )
}

type NotificationCenterProps = {
  notifications: HermesNotification[]
  onClear: () => void
  onDismiss: (id: string) => void
}

function NotificationCenter({ notifications, onClear, onDismiss }: NotificationCenterProps) {
  const [assistantName] = useAssistantDisplayName()
  const [open, setOpen] = useState(false)
  const centerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const closeOnPointerDown = (event: PointerEvent) => {
      if (!centerRef.current?.contains(event.target as Node)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', closeOnPointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnPointerDown)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  return (
    <div className="notification-center" ref={centerRef}>
      <button
        type="button"
        className="notification-trigger"
        aria-label={`${notifications.length || 'No'} ${assistantName} notification${notifications.length === 1 ? '' : 's'}`}
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((current) => !current)}
      >
        <Bell size={15} />
        {notifications.length > 0 && <span aria-hidden="true">{notifications.length > 9 ? '9+' : notifications.length}</span>}
      </button>
      {open && (
        <section className="notification-popover" role="dialog" aria-label={`${assistantName} notifications`}>
          <header>
            <div><strong>Notifications</strong><small>Live notices from {assistantName}</small></div>
            {notifications.length > 0 && <button type="button" onClick={onClear}>Clear all</button>}
          </header>
          {notifications.length === 0 ? (
            <p className="notification-empty"><Bell size={17} /> No new notifications</p>
          ) : (
            <div className="notification-list" aria-live="polite">
              {notifications.map((notice) => {
                const Icon = notice.level === 'error'
                  ? XCircle
                  : notice.level === 'warning'
                    ? ShieldAlert
                    : CheckCircle2
                return (
                  <article className={`notification-item ${notice.level}`} key={notice.id}>
                    <span><Icon size={14} /></span>
                    <div>
                      <strong>{notice.message}</strong>
                      {notice.detail && <p>{notice.detail}</p>}
                      <small>{notice.sticky ? 'Persistent notice' : 'Closes automatically'}</small>
                    </div>
                    <button type="button" aria-label={`Dismiss ${notice.message}`} onClick={() => onDismiss(notice.id)}>×</button>
                  </article>
                )
              })}
            </div>
          )}
          <footer>Notification adapter v1 · No notice text is stored</footer>
        </section>
      )}
    </div>
  )
}

type ApprovalCardProps = {
  request: HermesApprovalRequest
  messages: readonly HermesChatMessage[]
  submitting: HermesApprovalChoice | null
  respond: (choice: HermesApprovalChoice) => Promise<void>
}

function ApprovalCard({ request, messages, submitting, respond }: ApprovalCardProps) {
  const [assistantName] = useAssistantDisplayName()
  const [confirmation, setConfirmation] = useState({ requestId: request.requestId, active: false })
  const [reviewStatus, setReviewStatus] = useState('')
  const confirmAlways = confirmation.requestId === request.requestId && confirmation.active
  const busy = submitting !== null

  useEffect(() => {
    const reviewId = codexApprovalReviewId(request)
    const update = (event: Event) => {
      const status = readCodexApprovalReviewStatus(event)
      if (status?.requestId === reviewId) setReviewStatus(status.message)
    }
    window.addEventListener(HERMES_CODEX_APPROVAL_REVIEW_STATUS_EVENT, update)
    return () => window.removeEventListener(HERMES_CODEX_APPROVAL_REVIEW_STATUS_EVENT, update)
  }, [request])

  function submit(choice: HermesApprovalChoice) {
    void respond(choice).catch(() => undefined)
  }

  return (
    <section className="approval-card" aria-live="assertive">
      <header>
        <span><ShieldAlert size={15} /></span>
        <div>
          <strong>Approval required</strong>
          <small>{request.smartDenied ? `${assistantName} safety review needs your decision` : request.description}</small>
        </div>
      </header>
      <div className="approval-explanation">
        <section>
          <strong>What {assistantName} is doing</strong>
          <p>{request.description}</p>
        </section>
        <section className={request.reason ? '' : 'missing'}>
          <strong>Why {assistantName} is doing it</strong>
          <p>{request.reason ?? `${assistantName} did not provide a reason for this command. Do not approve it until the intent is clear.`}</p>
        </section>
      </div>
      {request.command && (
        <details className="approval-command">
          <summary>Show technical command</summary>
          <pre>{request.command}</pre>
        </details>
      )}
      <div className="approval-review">
        <button type="button" onClick={() => {
          setReviewStatus('Opening Codex reviewâ€¦')
          sendApprovalReviewToCodex(request, messages)
        }}><SendHorizontal size={13} /> Send to Codex</button>
        {reviewStatus && <span role="status">{reviewStatus}</span>}
      </div>
      {confirmAlways ? (
        <div className="approval-confirm">
          <p>This permanently adds the matching command pattern to {assistantName}’s Hermes runtime allowlist.</p>
          <div>
            <button type="button" disabled={busy} onClick={() => setConfirmation({ requestId: request.requestId, active: false })}>Cancel</button>
            <button type="button" className="danger" disabled={busy} onClick={() => submit('always')}>
              {submitting === 'always' ? 'Saving…' : 'Confirm always allow'}
            </button>
          </div>
        </div>
      ) : (
        <div className="approval-actions">
          {request.choices.includes('once') && (
            <button type="button" className="primary" disabled={busy} onClick={() => submit('once')}>
              {submitting === 'once' ? 'Running…' : 'Run once'}
            </button>
          )}
          {request.choices.includes('session') && (
            <button type="button" disabled={busy} onClick={() => submit('session')}>Allow this session</button>
          )}
          {request.choices.includes('always') && request.allowPermanent && (
            <button type="button" disabled={busy} onClick={() => setConfirmation({ requestId: request.requestId, active: true })}>Always allow…</button>
          )}
          {request.choices.includes('deny') && (
            <button type="button" className="reject" disabled={busy} onClick={() => submit('deny')}>Reject</button>
          )}
        </div>
      )}
    </section>
  )
}

type PromptCardProps = {
  request: HermesInteractivePrompt
  submitting: boolean
  respond: (value: string) => Promise<void>
}

function PromptCard({ request, submitting, respond }: PromptCardProps) {
  const [assistantName] = useAssistantDisplayName()
  const [draft, setDraft] = useState({ requestId: request.requestId, value: '' })
  const value = draft.requestId === request.requestId ? draft.value : ''
  const isClarify = request.kind === 'clarify'
  const Icon = isClarify ? MessageCircleQuestion : request.kind === 'sudo' ? LockKeyhole : KeyRound
  const title = isClarify ? `${assistantName} has a question` : request.kind === 'sudo' ? 'Administrator password' : request.envVar
  const description = isClarify
    ? request.question
    : request.kind === 'sudo'
      ? 'The command needs elevated Windows or system permissions.'
      : request.prompt

  function submit(answer: string) {
    void respond(answer).then(() => setDraft({ requestId: request.requestId, value: '' })).catch(() => undefined)
  }

  return (
    <section className={`prompt-card ${request.kind}`} aria-live="assertive">
      <header>
        <span><Icon size={15} /></span>
        <div><strong>{title}</strong><small>{description}</small></div>
      </header>

      {isClarify && request.choices.length > 0 && (
        <div className="prompt-choices">
          {request.choices.map((choice) => (
            <button type="button" disabled={submitting} key={choice} onClick={() => submit(choice)}>{choice}</button>
          ))}
        </div>
      )}

      <form onSubmit={(event) => { event.preventDefault(); if (value || isClarify) submit(value) }}>
        <input
          autoComplete={isClarify ? 'off' : 'new-password'}
          autoFocus
          disabled={submitting}
          placeholder={isClarify ? 'Type a different answer…' : 'Value is sent directly to the Hermes runtime'}
          type={isClarify ? 'text' : 'password'}
          value={value}
          onChange={(event) => setDraft({ requestId: request.requestId, value: event.target.value })}
        />
        <button type="submit" className="primary" disabled={submitting || (!value && !isClarify)}>
          {submitting ? 'Sending…' : 'Submit'}
        </button>
        <button type="button" disabled={submitting} onClick={() => submit('')}>
          {isClarify ? 'Skip' : 'Cancel'}
        </button>
      </form>
      {!isClarify && <p className="prompt-privacy"><LockKeyhole size={10} /> The value is not added to the conversation transcript.</p>}
    </section>
  )
}

export function AgentDock({ sessionRequest, onSessionOpened, onSignIn, dockControls, onDesktopUiAction }: Props) {
  const [bridgeGeneration] = useState(() => {
    const bytes = crypto.getRandomValues(new Uint8Array(16))
    return `renderer:${Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('')}`
  })
  const [assistantName] = useAssistantDisplayName()
  const [draft, setDraft] = useState('')
  const [queuedPrompts, setQueuedPrompts] = useState<HermesQueuedPrompt<HermesComposerAttachment>[]>([])
  const [queueParked, setQueueParked] = useState(false)
  const [queueNotice, setQueueNotice] = useState<string | null>(null)
  const [editingQueueId, setEditingQueueId] = useState<string | null>(null)
  const [editingQueueText, setEditingQueueText] = useState('')
  const [nearBottom, setNearBottom] = useState(true)
  const [modelMenuOpen, setModelMenuOpen] = useState(false)
  const [agentMenuOpen, setAgentMenuOpen] = useState(false)
  const [bridgeObservations, setBridgeObservations] = useState<HermesBridgeObservation[]>([])
  const [naturalVoice, setNaturalVoice] = useState(HERMES_NATURAL_VOICE_IDLE)
  const [automaticVoiceEnabled, setAutomaticVoiceEnabled] = useState(false)
  const [speechInput, setSpeechInput] = useState(HERMES_SPEECH_INPUT_IDLE)
  const [quarkActive, setQuarkActive] = useState(false)
  const [quarkPhase, setQuarkPhase] = useState<QuarkPhase>('idle')
  const [quarkReason, setQuarkReason] = useState<string | undefined>()
  const [quarkSpeechInput, setQuarkSpeechInput] = useState(HERMES_SPEECH_INPUT_IDLE)
  const conversationRef = useRef<HTMLDivElement>(null)
  const composerRef = useRef<HTMLTextAreaElement>(null)
  const agentMenuRef = useRef<HTMLDivElement>(null)
  const followLatestRef = useRef(true)
  const readingAnchorRef = useRef<{ element: HTMLElement; top: number } | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const handledSessionNonce = useRef<number | null>(null)
  const queuedPromptsRef = useRef(queuedPrompts)
  const drainingQueueRef = useRef(false)
  const lastCompletionRef = useRef(0)
  const naturalVoicePlayerRef = useRef<HermesNaturalVoicePlayer | null>(null)
  const speechInputControllerRef = useRef<HermesSpeechInputController | null>(null)
  const lastAutoSpokenMessageIdRef = useRef<string | null>(null)
  const quarkActiveRef = useRef(false)
  const quarkAwaitingCompletionRef = useRef<number | null>(null)
  if (naturalVoicePlayerRef.current?.revision !== HERMES_NATURAL_VOICE_CLIENT_REVISION) {
    naturalVoicePlayerRef.current?.dispose()
    naturalVoicePlayerRef.current = new HermesNaturalVoicePlayer()
  }
  if (!speechInputControllerRef.current) speechInputControllerRef.current = new HermesSpeechInputController()
  quarkActiveRef.current = quarkActive
  const captureReadingAnchor = useCallback(() => {
    const conversation = conversationRef.current
    const content = conversation?.querySelector<HTMLElement>('.message-list, .welcome')
    if (!conversation || !content || followLatestRef.current) {
      readingAnchorRef.current = null
      return
    }
    const viewportTop = conversation.getBoundingClientRect().top
    const element = Array.from(content.children).find((candidate) => (
      candidate instanceof HTMLElement && candidate.getBoundingClientRect().bottom > viewportTop
    )) as HTMLElement | undefined
    readingAnchorRef.current = element ? { element, top: element.getBoundingClientRect().top } : null
  }, [])
  const preserveReadingAnchor = useCallback((row: HTMLElement, before: ToolRunAnchorMeasurement) => {
    const conversation = conversationRef.current
    const selection = window.getSelection()
    const selectionTouchesRow = Boolean(selection && !selection.isCollapsed && (
      (selection.anchorNode && row.contains(selection.anchorNode))
      || (selection.focusNode && row.contains(selection.focusNode))
    ))
    if (conversation) {
      preserveToolRunReadingAnchor(conversation, row, before, followLatestRef.current, selectionTouchesRow)
      captureReadingAnchor()
    }
  }, [captureReadingAnchor])
  const {
    activeStoredSessionId,
    addAttachments,
    approvalSubmitting,
    approvalMode,
    approvalModeSaving,
    attachments,
    authenticationRequired,
    busy,
    canStartNewChat,
    cancelModelConfirmation,
    clearAttachments,
    clearNotifications,
    confirmModelSelection,
    connect,
    connection,
    dismissNotification,
    error,
    loadingSession,
    messages,
    modelCatalog,
    modelLoading,
    modelSelection,
    modelSwitching,
    newChat,
    notifications,
    openSession,
    pendingApproval,
    pendingModelConfirmation,
    pendingPrompt,
    promptSubmitting,
    reasoning,
    reasoningEffort,
    reasoningEffortSaving,
    refreshModels,
    removeAttachment,
    respondToApproval,
    respondToPrompt,
    selectModel,
    send,
    setApprovalMode,
    setReasoningEffort,
    stop,
    toolActivity,
    toolRuns,
    turnCompletionCount,
  } = useHermesChat({ onDesktopUiAction })
  const memoryStatus = useHermesMemoryStatus(connection)

  useEffect(() => {
    naturalVoicePlayerRef.current?.stop()
    setNaturalVoice(HERMES_NATURAL_VOICE_IDLE)
    speechInputControllerRef.current?.cancel(false)
    setSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    setQuarkActive(false)
    setQuarkPhase('idle')
    setQuarkReason(undefined)
    setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    quarkAwaitingCompletionRef.current = null
    lastAutoSpokenMessageIdRef.current = null
  }, [activeStoredSessionId])

  useEffect(() => () => {
    naturalVoicePlayerRef.current?.stop(false)
    speechInputControllerRef.current?.cancel(false)
  }, [])

  useEffect(() => {
    if (speechInput.phase !== 'ready' || !speechInput.transcript) return
    setDraft((current) => current.trim()
      ? `${current.trimEnd()} ${speechInput.transcript}`
      : speechInput.transcript ?? '')
    setSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    window.requestAnimationFrame(() => composerRef.current?.focus())
  }, [speechInput])

  useEffect(() => {
    if (!quarkActive || quarkPhase !== 'idle' || connection !== 'open' || loadingSession || busy) return
    setQuarkReason(undefined)
    void speechInputControllerRef.current?.start(setQuarkSpeechInput)
  }, [busy, connection, loadingSession, quarkActive, quarkPhase])

  useEffect(() => {
    if (!quarkActive) return
    if (quarkSpeechInput.phase === 'requesting' || quarkSpeechInput.phase === 'listening') {
      setQuarkPhase('listening')
      return
    }
    if (quarkSpeechInput.phase === 'transcribing') {
      setQuarkPhase('thinking')
      return
    }
    if (quarkSpeechInput.phase === 'error' || quarkSpeechInput.phase === 'unavailable') {
      setQuarkReason(quarkSpeechInput.reason || 'The selected microphone is unavailable.')
      setQuarkPhase('error')
      return
    }
    if (quarkSpeechInput.phase !== 'ready' || !quarkSpeechInput.transcript) return
    const transcript = quarkSpeechInput.transcript
    setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    setQuarkPhase('thinking')
    quarkAwaitingCompletionRef.current = turnCompletionCount
    void send(quarkConversationPrompt(transcript), [], transcript).catch((reason) => {
      quarkAwaitingCompletionRef.current = null
      setQuarkReason(reason instanceof Error ? reason.message : 'Photon could not answer this voice turn.')
      setQuarkPhase('error')
    })
  }, [quarkActive, quarkSpeechInput, send, turnCompletionCount])

  useEffect(() => {
    const startingCompletion = quarkAwaitingCompletionRef.current
    if (!quarkActive || startingCompletion === null || busy || turnCompletionCount <= startingCompletion) return
    quarkAwaitingCompletionRef.current = null
    const reply = [...messages].reverse().find((candidate) => (
      candidate.author === 'hermes' && candidate.body && !candidate.streaming && !candidate.interim
    ))
    if (!reply?.body) {
      setQuarkReason('Photon completed the turn without a spoken reply.')
      setQuarkPhase('error')
      return
    }
    setQuarkPhase('speaking')
    void naturalVoicePlayerRef.current?.toggle(reply.id, reply.body, (state) => {
      setNaturalVoice(state)
      if (!quarkActiveRef.current) return
      if (state.phase === 'error') {
        setQuarkReason('The local speaking voice is unavailable.')
        setQuarkPhase('error')
      } else if (state.phase === 'idle') {
        setQuarkPhase('idle')
      } else {
        setQuarkPhase('speaking')
      }
    })
  }, [busy, messages, quarkActive, turnCompletionCount])

  useEffect(() => {
    if (!quarkActive || (quarkSpeechInput.phase !== 'requesting' && quarkSpeechInput.phase !== 'listening')) return
    const timer = window.setTimeout(() => {
      if (speechInputControllerRef.current?.isListening) {
        speechInputControllerRef.current.stop()
        return
      }
      speechInputControllerRef.current?.cancel(false)
      setQuarkReason('The selected microphone did not start in time.')
      setQuarkPhase('error')
    }, 10_000)
    return () => window.clearTimeout(timer)
  }, [quarkActive, quarkSpeechInput.phase])

  useEffect(() => {
    if (!automaticVoiceEnabled || busy) return
    const message = [...messages].reverse().find((candidate) => (
      candidate.author === 'hermes' && candidate.body && !candidate.streaming && !candidate.interim
    ))
    if (!message || message.id === lastAutoSpokenMessageIdRef.current) return
    lastAutoSpokenMessageIdRef.current = message.id
    void naturalVoicePlayerRef.current?.toggle(message.id, message.body, setNaturalVoice)
  }, [automaticVoiceEnabled, busy, messages])

  const bridgeStateRef = useRef({ activeStoredSessionId, busy, canStartNewChat, connection, error, messages, newChat, send, stop, toolRuns, turnCompletionCount })
  bridgeStateRef.current = { activeStoredSessionId, busy, canStartNewChat, connection, error, messages, newChat, send, stop, toolRuns, turnCompletionCount }

  useEffect(() => registerHermesConversationBridge({
    snapshot: () => {
      const current = bridgeStateRef.current
      return createHermesBridgeSnapshot(current.connection, current.busy, current.activeStoredSessionId, current.messages, current.toolRuns, bridgeGeneration)
    },
    submitTurn: async (text) => {
      const current = bridgeStateRef.current
      if (current.connection !== 'open') throw new Error('The visible Hermes dock is not connected.')
      if (current.busy) throw new Error('Hermes is already processing a turn.')
      const startingCompletion = current.turnCompletionCount
      await current.send(text, [])
      const deadline = Date.now() + 30 * 60 * 1_000
      while (Date.now() < deadline) {
        const latest = bridgeStateRef.current
        if (!latest.busy && latest.turnCompletionCount > startingCompletion) break
        if (!latest.busy && latest.error) throw new Error(latest.error)
        await new Promise((resolve) => window.setTimeout(resolve, 50))
      }
      const completed = bridgeStateRef.current
      if (completed.busy || completed.turnCompletionCount <= startingCompletion) throw new Error('Hermes did not complete the bridge turn before the timeout.')
      return createHermesBridgeSnapshot(completed.connection, completed.busy, completed.activeStoredSessionId, completed.messages, completed.toolRuns, bridgeGeneration)
    },
    interrupt: async () => {
      await bridgeStateRef.current.stop()
      const current = bridgeStateRef.current
      return createHermesBridgeSnapshot(current.connection, current.busy, current.activeStoredSessionId, current.messages, current.toolRuns, bridgeGeneration)
    },
    newSession: async () => {
      const current = bridgeStateRef.current
      if (!current.canStartNewChat || current.busy) throw new Error('The visible Hermes dock cannot start a new chat while a turn is active.')
      current.newChat()
      await new Promise((resolve) => window.setTimeout(resolve, 0))
      const fresh = bridgeStateRef.current
      return createHermesBridgeSnapshot(fresh.connection, fresh.busy, fresh.activeStoredSessionId, fresh.messages, fresh.toolRuns, bridgeGeneration)
    },
    observe: async (observation) => {
      setBridgeObservations((current) => current.some((item) => item.messageId === observation.messageId)
        ? current
        : [...current.slice(-199), observation])
      const current = bridgeStateRef.current
      return createHermesBridgeSnapshot(current.connection, current.busy, current.activeStoredSessionId, current.messages, current.toolRuns, bridgeGeneration)
    },
  }), [bridgeGeneration])

  useEffect(() => { setBridgeObservations([]) }, [activeStoredSessionId, bridgeGeneration])

  useEffect(() => { queuedPromptsRef.current = queuedPrompts }, [queuedPrompts])

  useEffect(() => {
    if (!agentMenuOpen) return
    const closeOnPointerDown = (event: PointerEvent) => {
      if (!agentMenuRef.current?.contains(event.target as Node)) setAgentMenuOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAgentMenuOpen(false)
    }
    document.addEventListener('pointerdown', closeOnPointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnPointerDown)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [agentMenuOpen])

  const drainQueuedPrompt = useCallback(async (id?: string) => {
    if (drainingQueueRef.current || busy || connection !== 'open') return false
    const entry = id
      ? queuedPromptsRef.current.find((candidate) => candidate.id === id)
      : queuedPromptsRef.current[0]
    if (!entry) return false

    drainingQueueRef.current = true
    setQueueNotice(null)
    try {
      await send(entry.text, entry.attachments)
      setQueuedPrompts((current) => removeHermesQueuedPrompt(current, entry.id))
      setQueueParked(false)
      return true
    } catch {
      setQueueParked(true)
      setQueueNotice(`That queued prompt was kept. Review it, then resume the queue when ${assistantName} is ready.`)
      return false
    } finally {
      drainingQueueRef.current = false
    }
  }, [busy, connection, send])

  useEffect(() => {
    const completionAdvanced = turnCompletionCount > lastCompletionRef.current
    lastCompletionRef.current = turnCompletionCount
    if (shouldAutoDrainHermesQueue({
      busy,
      completionAdvanced,
      connectionOpen: connection === 'open',
      parked: queueParked,
      queueLength: queuedPrompts.length,
    })) void drainQueuedPrompt()
  }, [busy, connection, drainQueuedPrompt, queueParked, queuedPrompts.length, turnCompletionCount])

  function queueCurrentDraft(text = draft, queuedAttachments = attachments) {
    if (!text.trim() && queuedAttachments.length === 0) return false
    try {
      setQueuedPrompts((current) => enqueueHermesPrompt(current, { text, attachments: queuedAttachments }))
      setQueueParked(false)
      setQueueNotice(null)
      setDraft('')
      clearAttachments()
      window.requestAnimationFrame(() => composerRef.current?.focus())
      return true
    } catch (reason) {
      setQueueNotice(reason instanceof Error ? reason.message : 'Could not queue that prompt.')
      return false
    }
  }

  function saveQueuedEdit(id: string) {
    if (!editingQueueText.trim()) return
    setQueuedPrompts((current) => updateHermesQueuedPrompt(current, id, editingQueueText))
    setEditingQueueId(null)
    setEditingQueueText('')
  }

  function editMessage(message: HermesChatMessage) {
    setDraft(message.body)
    window.requestAnimationFrame(() => {
      composerRef.current?.focus()
      composerRef.current?.setSelectionRange(message.body.length, message.body.length)
    })
  }

  async function retryMessage(message: HermesChatMessage) {
    if (message.attachments?.length) return
    if (turnActive) {
      queueCurrentDraft(message.body, [])
      return
    }
    await send(message.body, []).catch(() => setDraft(message.body))
  }

  useEffect(() => {
    if (!sessionRequest || handledSessionNonce.current === sessionRequest.nonce) return
    if (queuedPromptsRef.current.length > 0) {
      setQueueNotice('Clear or finish the queued prompts before switching conversations.')
      return
    }
    handledSessionNonce.current = sessionRequest.nonce
    setModelMenuOpen(false)
    cancelModelConfirmation()
    void openSession(sessionRequest.session.id, sessionRequest.session.profile)
      .then((opened) => { if (opened) onSessionOpened?.(sessionRequest.session) })
      .catch(() => undefined)
  }, [cancelModelConfirmation, onSessionOpened, openSession, queuedPrompts.length, sessionRequest])

  useEffect(() => {
    const element = conversationRef.current
    if (!element || !followLatestRef.current) return
    const frame = window.requestAnimationFrame(() => {
      if (!followLatestRef.current) return
      element.scrollTo({ top: element.scrollHeight, behavior: 'auto' })
      setNearBottom(true)
    })
    return () => window.cancelAnimationFrame(frame)
  }, [busy, error, messages, pendingApproval, pendingPrompt, reasoning, toolActivity, toolRuns])

  useEffect(() => {
    const element = conversationRef.current
    const content = element?.querySelector<HTMLElement>('.message-list, .welcome')
    if (!element || !content || typeof ResizeObserver === 'undefined') return

    let frame: number | null = null
    const observer = new ResizeObserver(() => {
      if (frame !== null) window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(() => {
        frame = null
        if (followLatestRef.current) {
          readingAnchorRef.current = null
          element.scrollTo({ top: element.scrollHeight, behavior: 'auto' })
          setNearBottom(true)
          return
        }
        const anchor = readingAnchorRef.current
        if (anchor?.element.isConnected) {
          const delta = anchor.element.getBoundingClientRect().top - anchor.top
          if (delta !== 0) element.scrollTop = preserveAgentScrollAnchor(element.scrollTop, delta)
        }
        captureReadingAnchor()
      })
    })
    captureReadingAnchor()
    observer.observe(content)
    return () => {
      observer.disconnect()
      if (frame !== null) window.cancelAnimationFrame(frame)
    }
  }, [captureReadingAnchor, messages.length, pendingApproval, pendingPrompt, toolRuns.length])

  async function sendMessage(event: FormEvent) {
    event.preventDefault()
    const prompt = draft.trim()
    if ((!prompt && attachments.length === 0) || connection !== 'open') return
    if (turnActive && attachments.length === 0 && isHermesImmediatePauseInstruction(prompt)) {
      setDraft('')
      if (queuedPromptsRef.current.length > 0) setQueueParked(true)
      setQueueNotice(`${assistantName} was interrupted and is paused. Send an explicit resume instruction when you are ready.`)
      await stop().catch(() => setDraft(prompt))
      return
    }
    if (turnActive) {
      queueCurrentDraft(prompt, attachments)
      return
    }
    setDraft('')
    await send(prompt).catch(() => setDraft(prompt))
  }

  function talkWithQuark() {
    if (!quarkActive) {
      naturalVoicePlayerRef.current?.stop(false)
      speechInputControllerRef.current?.cancel(false)
      setSpeechInput(HERMES_SPEECH_INPUT_IDLE)
      setAutomaticVoiceEnabled(false)
      setQuarkReason(undefined)
      setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
      setQuarkPhase('idle')
      setQuarkActive(true)
      return
    }
    if (speechInputControllerRef.current?.isListening) {
      speechInputControllerRef.current.stop()
      return
    }
    if (quarkPhase === 'error') {
      setQuarkReason(undefined)
      setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
      setQuarkPhase('idle')
      return
    }
    if (quarkPhase === 'paused') {
      setQuarkPhase('idle')
    }
  }

  function recallQuark() {
    if (!quarkActive) {
      setQuarkReason(undefined)
      setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
      setQuarkPhase('idle')
      setQuarkActive(true)
    }
    const host = (window as Window & { chrome?: { webview?: { postMessage: (message: unknown) => void } } }).chrome?.webview
    host?.postMessage({ type: 'quark.companion.recall', version: 1 })
  }

  function exitQuarkConversation() {
    quarkActiveRef.current = false
    quarkAwaitingCompletionRef.current = null
    speechInputControllerRef.current?.cancel(false)
    naturalVoicePlayerRef.current?.stop(false)
    setQuarkActive(false)
    setQuarkReason(undefined)
    setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    setQuarkPhase('idle')
    if (busy) void stop().catch(() => undefined)
  }

  function stopQuarkActivity() {
    quarkAwaitingCompletionRef.current = null
    speechInputControllerRef.current?.cancel(false)
    naturalVoicePlayerRef.current?.stop(false)
    setQuarkSpeechInput(HERMES_SPEECH_INPUT_IDLE)
    setQuarkReason(undefined)
    setQuarkPhase('paused')
    if (busy) void stop().catch(() => undefined)
  }

  function startNewChat() {
    if (queuedPromptsRef.current.length > 0) {
      setQueueNotice('Clear or finish the queued prompts before starting a new chat.')
      return
    }
    newChat()
  }

  function stopAndParkQueue() {
    if (queuedPromptsRef.current.length > 0) setQueueParked(true)
    void stop().catch(() => undefined)
  }

  function clearQueue() {
    setQueuedPrompts([])
    setQueueParked(false)
    setQueueNotice(null)
    setEditingQueueId(null)
    setEditingQueueText('')
  }

  function resumeQueue() {
    setQueueParked(false)
    setQueueNotice(null)
    if (!busy) void drainQueuedPrompt()
  }

  const connectionLabel = connection === 'open'
    ? 'Gateway connected'
    : connection === 'connecting'
      ? 'Connecting…'
      : connection === 'reconnecting'
        ? 'Reconnecting…'
        : connection === 'error'
          ? 'Connection needs attention'
          : 'Gateway disconnected'
  const attachmentUploading = attachments.some((attachment) => attachment.status === 'uploading')
  const turnActive = busy || Boolean(pendingApproval) || Boolean(pendingPrompt)
  const connectionActionLabel = authenticationRequired
    ? 'Sign in to Hermes'
    : connection === 'open'
      ? 'Refresh Hermes connection'
      : 'Reconnect Hermes'

  return (
    <aside className="agent-dock">
      <header className="dock-header">
        <div className="dock-title">
          <span className="agent-orb"><Sparkles size={15} /></span>
          <div><strong>{assistantName}</strong><span>Agent workspace</span></div>
        </div>
        <div className="dock-actions">
          {dockControls}
          <button
            type="button"
            className="dock-voice-action"
            aria-label="Bring Quark back"
            title="Bring Quark back beside Photon"
            onClick={recallQuark}
          ><Bot size={16} /></button>
          <button
            type="button"
            className="dock-voice-action"
            data-active={speechInput.phase === 'requesting' || speechInput.phase === 'listening' || speechInput.phase === 'transcribing'}
            aria-label={speechInput.phase === 'listening' ? 'Stop listening and transcribe' : 'Let Photon hear you'}
            title={speechInput.phase === 'listening' ? 'Stop listening and transcribe locally' : 'Record with the microphone for local Whisper transcription'}
            disabled={connection !== 'open' || loadingSession || quarkActive || speechInput.phase === 'requesting' || speechInput.phase === 'transcribing'}
            onClick={() => {
              if (speechInputControllerRef.current?.isListening) speechInputControllerRef.current.stop()
              else void speechInputControllerRef.current?.start(setSpeechInput)
            }}
          >
            {speechInput.phase === 'requesting' || speechInput.phase === 'transcribing'
              ? <LoaderCircle className="spin" size={16} />
              : speechInput.phase === 'listening'
                ? <MicOff size={16} />
                : <Mic size={16} />}
          </button>
          <button
            type="button"
            className="dock-voice-action"
            data-active={automaticVoiceEnabled}
            aria-pressed={automaticVoiceEnabled}
            aria-label={automaticVoiceEnabled ? 'Turn off Photon voice' : 'Turn on Photon voice'}
            title={automaticVoiceEnabled ? 'Stop automatic local voice output' : 'Speak completed responses with local Kokoro'}
            onClick={() => {
              setAutomaticVoiceEnabled((enabled) => {
                if (enabled) naturalVoicePlayerRef.current?.stop()
                return !enabled
              })
            }}
          ><Volume2 size={16} /></button>
          <NotificationCenter notifications={notifications} onClear={clearNotifications} onDismiss={dismissNotification} />
          <button
            aria-label="New chat"
            title={queuedPrompts.length ? 'Finish or clear queued prompts first' : 'New chat'}
            disabled={!canStartNewChat || queuedPrompts.length > 0}
            onClick={startNewChat}
          ><Plus size={17} /></button>
          <button
            aria-label={connectionActionLabel}
            onClick={() => authenticationRequired ? onSignIn?.() : void connect()}
          ><RefreshCw size={15} /></button>
          <div className="agent-options" ref={agentMenuRef}>
            <button
              aria-label="Agent options"
              aria-expanded={agentMenuOpen}
              aria-haspopup="menu"
              onClick={() => setAgentMenuOpen((open) => !open)}
            >•••</button>
            {agentMenuOpen && (
              <div className="agent-options-menu" role="menu" aria-label={`${assistantName} agent options`}>
                <button role="menuitem" disabled={!canStartNewChat || queuedPrompts.length > 0} onClick={() => { setAgentMenuOpen(false); startNewChat() }}>
                  <Plus size={14} /><span><strong>New chat</strong><small>Start a clean {assistantName} session</small></span>
                </button>
                <button role="menuitem" onClick={() => { setAgentMenuOpen(false); authenticationRequired ? onSignIn?.() : void connect() }}>
                  <RefreshCw size={14} /><span><strong>{connectionActionLabel}</strong><small>Restore the gateway session</small></span>
                </button>
                <button role="menuitem" disabled={!turnActive} onClick={() => { setAgentMenuOpen(false); stopAndParkQueue() }}>
                  <CircleStop size={14} /><span><strong>Stop current turn</strong><small>Interrupt {assistantName} and keep the queue</small></span>
                </button>
                <button role="menuitem" onClick={() => { setAgentMenuOpen(false); setModelMenuOpen(true) }}>
                  <WandSparkles size={14} /><span><strong>Model and approvals</strong><small>Choose the model and safety mode</small></span>
                </button>
                <button role="menuitem" disabled={queuedPrompts.length === 0} onClick={() => { setAgentMenuOpen(false); clearQueue() }}>
                  <Trash2 size={14} /><span><strong>Clear prompt queue</strong><small>Remove all waiting instructions</small></span>
                </button>
              </div>
            )}
          </div>
        </div>
      </header>

      <div
        className="conversation"
        ref={conversationRef}
        onScroll={(event) => {
          const element = event.currentTarget
          const nextNearBottom = isAgentScrollNearBottom(element)
          followLatestRef.current = nextNearBottom
          setNearBottom(nextNearBottom)
          if (nextNearBottom) readingAnchorRef.current = null
          else captureReadingAnchor()
        }}
      >
        {messages.length === 0 && bridgeObservations.length === 0 && toolRuns.length === 0 && !pendingApproval && !pendingPrompt ? (
          <section className="welcome">
            <div className="welcome-mark"><Bot size={28} /></div>
            <p className="eyebrow">LOCAL AGENT · {loadingSession ? 'OPENING SESSION' : connection === 'open' ? 'READY' : connection.toUpperCase()}</p>
            <h1>What should we build?</h1>
            <p className="welcome-copy">
              {assistantName} can work across this project with Serena's code tools, Docker, and the local workspace.
            </p>
            {error && (
              <div className="connection-error">
                <span>{error}</span>
                {authenticationRequired
                  ? <button type="button" onClick={onSignIn}>Sign in inside Workbench</button>
                  : canStartNewChat
                    ? <button type="button" onClick={startNewChat}>Start a new chat</button>
                    : <button type="button" onClick={() => void connect()}>Retry connection</button>}
              </div>
            )}
            <div className="suggestion-grid">
              {suggestions.map(({ icon: Icon, label }) => (
                <button key={label} onClick={() => setDraft(label)}>
                  <Icon size={16} /><span>{label}</span><ArrowUp size={13} className="suggestion-arrow" />
                </button>
              ))}
            </div>
          </section>
        ) : (
          <div className="message-list">
            {messages.map((message) => (
              <article className={`message ${message.author}`} key={message.id}>
                <div className="message-avatar">{message.author === 'hermes' ? <Sparkles size={14} /> : 'CS'}</div>
                <div>
                  <strong>{message.author === 'hermes' ? assistantName : 'You'}</strong>
                  {message.body && (
                    <Suspense fallback={<p>{message.body}</p>}>
                      <HermesMarkdown content={message.body} />
                    </Suspense>
                  )}
                  {message.attachments && message.attachments.length > 0 && (
                    <div className="message-attachments">
                      {message.attachments.map((attachment) => {
                        const Icon = attachment.kind === 'image' ? ImageIcon : FileCode2
                        return (
                          <span key={attachment.id}>
                            <Icon size={12} />
                            <b>{attachment.label}</b>
                            <small>{formatBytes(attachment.size)}</small>
                          </span>
                        )
                      })}
                    </div>
                  )}
                  {!message.body && !message.attachments?.length && <p>{message.streaming ? 'Thinking…' : ''}</p>}
                  {message.streaming && <small>Streaming from {assistantName}</small>}
                  {message.interim && <small>Progress update</small>}
                  {message.author === 'you' && message.body && (
                    <div className="message-actions">
                      <button type="button" onClick={() => editMessage(message)} title="Load this prompt into the composer">
                        <Pencil size={12} /> Edit and resend
                      </button>
                      <button
                        type="button"
                        disabled={Boolean(message.attachments?.length)}
                        onClick={() => void retryMessage(message)}
                        title={message.attachments?.length ? 'Reattach the original files before trying again' : busy ? 'Add this prompt to the queue' : 'Send this prompt again'}
                      >
                        <RotateCcw size={12} /> Try again
                      </button>
                    </div>
                  )}
                  {message.author === 'hermes' && message.body && !message.streaming && !message.interim && (
                    <div className="message-actions hermes-natural-voice-actions">
                      <button
                        type="button"
                        data-active={naturalVoice.messageId === message.id && naturalVoice.phase !== 'idle'}
                        title="Read aloud with the on-device Kokoro voice"
                        aria-label={naturalVoice.messageId === message.id && naturalVoice.phase !== 'idle' ? 'Stop reading aloud' : 'Read response aloud'}
                        onClick={() => void naturalVoicePlayerRef.current?.toggle(message.id, message.body ?? '', setNaturalVoice)}
                      >
                        {naturalVoice.messageId === message.id && naturalVoice.phase === 'loading'
                          ? <LoaderCircle className="hermes-natural-voice-spinner" size={12} />
                          : naturalVoice.messageId === message.id && naturalVoice.phase === 'playing'
                            ? <CircleStop size={12} />
                            : <Volume2 size={12} />}
                        {naturalVoice.messageId === message.id && naturalVoice.phase === 'loading'
                          ? 'Preparing local voice…'
                          : naturalVoice.messageId === message.id && naturalVoice.phase === 'playing'
                            ? 'Stop'
                            : 'Read aloud'}
                      </button>
                      {naturalVoice.messageId === message.id && naturalVoice.phase === 'error' && (
                        <small className="hermes-natural-voice-error" role="status">Local voice unavailable</small>
                      )}
                    </div>
                  )}
                </div>
              </article>
            ))}
            {bridgeObservations.map((message) => (
              <article className="message external" key={message.messageId} data-message-id={message.messageId}>
                <div className="message-avatar"><Bot size={14} /></div>
                <div>
                  <strong>{message.header}</strong>
                  <p>{message.body}</p>
                  <small>Assistant conversation bus · observed without invoking {assistantName}</small>
                </div>
              </article>
            ))}
            {reasoning.body && (
              <details className="reasoning-card" open={reasoning.streaming}>
                <summary>
                  <BrainCircuit size={13} />
                  <span>{reasoning.streaming ? `${assistantName} is reasoning` : 'View reasoning'}</span>
                  <ChevronRight size={13} />
                </summary>
                <p>{reasoning.body}</p>
              </details>
            )}
            {toolRuns.length > 0 && (
              <ToolTimeline activity={toolActivity} tools={toolRuns} onDetailsHeightChange={preserveReadingAnchor} />
            )}
            {error && <div className="inline-error">{error}</div>}
          </div>
        )}
        {!nearBottom && (
          <button className="jump-latest" onClick={() => {
            const element = conversationRef.current
            if (!element) return
            followLatestRef.current = true
            setNearBottom(true)
            element.scrollTo({ top: element.scrollHeight, behavior: 'auto' })
          }}>
            <ArrowDown size={14} /> Jump to latest
          </button>
        )}
      </div>

      {modelMenuOpen && !loadingSession && (
        <ModelControlPopover
          approvalMode={approvalMode}
          approvalModeSaving={approvalModeSaving}
          catalog={modelCatalog}
          loading={modelLoading}
          onCancelConfirmation={cancelModelConfirmation}
          onClose={() => setModelMenuOpen(false)}
          onConfirmSelection={confirmModelSelection}
          onRefresh={refreshModels}
          onSelect={selectModel}
          onSetApprovalMode={setApprovalMode}
          pendingConfirmation={pendingModelConfirmation}
          selection={modelSelection}
          switching={modelSwitching}
        />
      )}

      <div className="composer-stack">
      {queuedPrompts.length > 0 && (
        <section className={`prompt-queue ${queueParked ? 'parked' : ''}`} aria-label={`Queued ${assistantName} prompts`}>
          <header>
            <div><ListPlus size={14} /><strong>{queuedPrompts.length} queued</strong><small>Queue adapter v{HERMES_PROMPT_QUEUE_ADAPTER_VERSION}</small></div>
            <div>
              {queueParked && <button type="button" onClick={resumeQueue}><SendHorizontal size={12} /> Resume</button>}
              <button type="button" onClick={clearQueue}><Trash2 size={12} /> Clear</button>
            </div>
          </header>
          <div className="prompt-queue-list">
            {queuedPrompts.map((entry, index) => (
              <article key={entry.id}>
                <span className="queue-position">{index + 1}</span>
                {editingQueueId === entry.id ? (
                  <div className="queue-edit">
                    <textarea value={editingQueueText} onChange={(event) => setEditingQueueText(event.target.value)} rows={2} aria-label={`Edit queued prompt ${index + 1}`} />
                    <div><button type="button" onClick={() => saveQueuedEdit(entry.id)}>Save</button><button type="button" onClick={() => setEditingQueueId(null)}>Cancel</button></div>
                  </div>
                ) : (
                  <div className="queue-copy">
                    <p>{entry.text || 'Attachment-only prompt'}</p>
                    <small>{entry.attachments.length ? `${entry.attachments.length} attachment${entry.attachments.length === 1 ? '' : 's'}` : 'Text prompt'}</small>
                  </div>
                )}
                <div className="queue-actions">
                  <button type="button" disabled={busy || editingQueueId === entry.id} title="Send this queued prompt next" onClick={() => {
                    setQueueParked(false)
                    void drainQueuedPrompt(entry.id)
                  }}><SendHorizontal size={12} /></button>
                  <button type="button" disabled={editingQueueId === entry.id} title="Edit queued prompt" onClick={() => {
                    setEditingQueueId(entry.id)
                    setEditingQueueText(entry.text)
                  }}><Pencil size={12} /></button>
                  <button type="button" disabled={index === 0 || editingQueueId === entry.id} title="Move to front" onClick={() => setQueuedPrompts((current) => promoteHermesQueuedPrompt(current, entry.id))}><ArrowUp size={12} /></button>
                  <button type="button" disabled={editingQueueId === entry.id} title="Remove queued prompt" onClick={() => {
                    setQueuedPrompts((current) => removeHermesQueuedPrompt(current, entry.id))
                    if (queuedPrompts.length === 1) setQueueParked(false)
                  }}><XCircle size={12} /></button>
                </div>
              </article>
            ))}
          </div>
        </section>
      )}
      {queueNotice && <div className="queue-notice"><span>{queueNotice}</span><button type="button" aria-label="Dismiss queue notice" onClick={() => setQueueNotice(null)}><XCircle size={13} /></button></div>}

      {(pendingApproval || pendingPrompt) && (
        <div className="agent-interaction-tray" aria-live="polite">
          {pendingApproval && (
            <ApprovalCard
              key={pendingApproval.requestId}
              request={pendingApproval}
              messages={messages}
              submitting={approvalSubmitting}
              respond={respondToApproval}
            />
          )}
          {pendingPrompt && (
            <PromptCard key={pendingPrompt.requestId} request={pendingPrompt} submitting={promptSubmitting} respond={respondToPrompt} />
          )}
        </div>
      )}

      {(speechInput.phase === 'listening' || speechInput.phase === 'transcribing' || speechInput.phase === 'error' || speechInput.phase === 'unavailable') && (
        <div className={`speech-input-status is-${speechInput.phase}`} role="status" aria-live="polite">
          {speechInput.phase === 'listening'
            ? 'Listening locally — press the microphone again to transcribe.'
            : speechInput.phase === 'transcribing'
              ? 'Local Whisper is transcribing…'
              : speechInput.reason}
          {(speechInput.phase === 'error' || speechInput.phase === 'unavailable') && (
            <button type="button" aria-label="Dismiss microphone status" onClick={() => setSpeechInput(HERMES_SPEECH_INPUT_IDLE)}><XCircle size={13} /></button>
          )}
        </div>
      )}

      <QuarkCompanion
        active={quarkActive}
        disabled={connection !== 'open' || loadingSession || (!quarkActive && turnActive)}
        reviewActive={Boolean(pendingApproval || pendingPrompt)}
        phase={quarkPhase}
        reason={quarkReason}
        onExit={exitQuarkConversation}
        onRetry={talkWithQuark}
        onStop={stopQuarkActivity}
        onTalk={talkWithQuark}
      />

      <form className="composer" onSubmit={sendMessage}>
        {attachments.length > 0 && (
          <div className="attachment-tray" aria-label="Attached files">
            {attachments.map((attachment) => {
              const Icon = attachment.kind === 'image' ? ImageIcon : FileCode2
              return (
                <div className={`attachment-chip ${attachment.status}`} key={attachment.id} title={attachment.error}>
                  <span className="attachment-kind"><Icon size={13} /></span>
                  <span className="attachment-copy">
                    <b>{attachment.label}</b>
                    <small>{attachment.status === 'uploading' ? `Uploading to ${assistantName}…` : attachment.status === 'error' ? attachment.error : formatBytes(attachment.size)}</small>
                  </span>
                  {attachment.status === 'uploading'
                    ? <LoaderCircle className="spin" size={13} />
                    : <button type="button" aria-label={`Remove ${attachment.label}`} onClick={() => removeAttachment(attachment.id)}><XCircle size={13} /></button>}
                </div>
              )
            })}
          </div>
        )}
        <textarea
          ref={composerRef}
          aria-label={`Message ${assistantName}`}
          value={draft}
          disabled={connection !== 'open' || loadingSession}
          onChange={(event) => setDraft(event.target.value)}
          onPaste={(event) => handleAgentDockClipboardImagePaste(
            event.clipboardData,
            addAttachments,
            () => event.preventDefault(),
          )}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault()
              event.currentTarget.form?.requestSubmit()
            }
          }}
          placeholder={loadingSession ? 'Opening conversation…' : connection === 'open' ? turnActive ? 'Queue another instruction…' : `Ask ${assistantName} anything…` : connectionLabel}
          rows={3}
        />
        <div className="composer-toolbar">
          <div>
            <input
              ref={fileInputRef}
              className="attachment-input"
              type="file"
              multiple
              tabIndex={-1}
              onChange={(event) => {
                addAttachments(Array.from(event.target.files ?? []), 'file-picker')
                event.target.value = ''
              }}
            />
            <button
              type="button"
              aria-label="Attach files or images"
              disabled={connection !== 'open'}
              onClick={() => fileInputRef.current?.click()}
            >
              <Paperclip size={17} />
            </button>
            <button
              type="button"
              className="model-button"
              aria-expanded={modelMenuOpen}
              disabled={loadingSession}
              title={loadingSession ? 'Model controls are unavailable while a conversation is opening' : 'Model and approval controls'}
              onClick={() => setModelMenuOpen((open) => !open)}
            >
              <WandSparkles size={15} />
              {modelSelection?.model ? modelSelection.model.split('/').pop() : assistantName}
              <ChevronDown size={13} />
            </button>
            <HermesReasoningControl
              disabled={connection !== 'open' || loadingSession || modelLoading || modelSwitching || reasoningEffortSaving}
              effort={reasoningEffort}
              modelSelection={modelSelection}
              onChange={(effort) => { void setReasoningEffort(effort).catch(() => undefined) }}
              options={modelCatalog?.reasoningControl}
            />
          </div>
          <div className="composer-submit-actions">
            {turnActive && (draft.trim() || attachments.length > 0) && (
              <button className="queue-button" aria-label="Queue prompt" type="submit" disabled={attachmentUploading} title="Queue this prompt after the current turn">
                <ListPlus size={16} />
              </button>
            )}
            <button
              className="send-button"
              aria-label={attachmentUploading ? 'Uploading attachments' : turnActive ? 'Stop' : 'Send'}
              type={turnActive ? 'button' : 'submit'}
              disabled={connection !== 'open' || attachmentUploading}
              onClick={turnActive && !attachmentUploading ? stopAndParkQueue : undefined}
            >
              {attachmentUploading ? <LoaderCircle className="spin" size={16} /> : turnActive ? <CircleStop size={17} /> : <ArrowUp size={18} />}
            </button>
          </div>
        </div>
      </form>
      </div>

      <footer className="dock-footer">
        <span><i className={`status-dot ${connection}`} /> {connectionLabel}</span>
        <span className={`memory-status ${memoryStatus.state}`} title={memoryStatus.title}>{memoryStatus.label}</span>
        <span><Braces size={13} /> {toolActivity ?? `Hermes adapter v${HERMES_RUNTIME_ADAPTER_VERSION}`}</span>
      </footer>
    </aside>
  )
}
