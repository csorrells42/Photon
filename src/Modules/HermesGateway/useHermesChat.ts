import { useCallback, useEffect, useRef, useState } from 'react'
import { hermesGateway, isHermesConnectionCancelledError } from './HermesGatewayClient'
import type { HermesConnectionState, HermesGatewayEvent } from './HermesGatewayClient'
import {
  mergeHermesToolEvent,
  normalizeHermesApproval,
  normalizeHermesInteractivePrompt,
} from './HermesRuntimeAdapter'
import type {
  HermesApprovalChoice,
  HermesApprovalRequest,
  HermesInteractivePrompt,
  HermesToolRun,
} from './HermesRuntimeAdapter'
import { hermesSessionApi, storedMessageText } from '../HermesSessions/HermesSessionApi'
import {
  effectiveHermesReasoningEffort,
  hermesModelAdapter,
  hermesReasoningControlForSelection,
} from '../HermesSettings/HermesModelAdapter'
import type {
  HermesApprovalMode,
  HermesModelCatalog,
  HermesModelSelection,
  HermesReasoningEffort,
} from '../HermesSettings/HermesModelAdapter'
import {
  buildPromptWithAttachments,
  hermesAttachmentAdapter,
  validateAttachment,
} from './HermesAttachmentAdapter'
import type { HermesAttachmentKind, HermesStagedAttachment } from './HermesAttachmentAdapter'
import { subscribeHermesAuthChanged } from '../HermesSystem/HermesAuthEvents'
import {
  canStartHermesChat,
  HERMES_RECONNECT_DELAYS_MS,
  HermesSessionOpenGeneration,
  isHermesAuthenticationError,
  isHermesMissingSessionError,
  reconnectDelayMs,
  resumeHermesSession,
  shouldAcceptHermesSessionEvent,
  shouldRecoverHermesSession,
} from './HermesConnectionRecovery'
import {
  mergeHermesNotification,
  removeExpiredHermesNotifications,
} from './HermesNotificationAdapter'
import type { HermesNotification } from './HermesNotificationAdapter'
import { normalizeHermesDesktopUiAction, shouldAcceptHermesDesktopUiAction } from './HermesDesktopUiAdapter'
import type { HermesDesktopUiAction } from './HermesDesktopUiAdapter'
import { reasoningCatalogSelection, reasoningEffortFromSessionInfo } from './HermesReasoningState'
import { desktopDeveloperServicesClient } from '../DeveloperServices/DesktopDeveloperServicesClient'
import { desktopDotNetDebuggerController } from '../DeveloperServices/DesktopDotNetDebuggerClient'
import { handleNativeDeveloperRequest } from './HermesNativeDeveloperBridge'
import {
  HermesVisionInputError,
  hermesGatewayVisionController,
  readHermesVisionFileBytes,
  resolveSelectedHermesVisionCapability,
  type HermesVisionInputController,
  type HermesVisionInputOrigin,
} from '../HermesVisionInput'

export type HermesComposerAttachment = {
  id: string
  file: File
  kind: HermesAttachmentKind
  origin: HermesVisionInputOrigin
  label: string
  size: number
  status: 'ready' | 'uploading' | 'staged' | 'error'
  error?: string
  attachedSessionId?: string
  staged?: HermesStagedAttachment
}

export type HermesChatMessage = {
  id: string
  author: 'you' | 'hermes'
  body: string
  streaming?: boolean
  interim?: boolean
  attachments?: Array<Pick<HermesComposerAttachment, 'id' | 'kind' | 'label' | 'size'>>
}

export type HermesReasoningState = {
  body: string
  streaming: boolean
}

type HermesVisionAttachmentStage = Pick<HermesComposerAttachment, 'id' | 'file' | 'kind' | 'label' | 'origin'>

function visionStageInvalidated() {
  return new HermesVisionInputError('provider-failed', 'Hermes could not attach the image. Try again.')
}

export async function stageHermesVisionAttachmentForSend(
  controller: Pick<HermesVisionInputController, 'stage'>,
  sessionId: string,
  attachment: HermesVisionAttachmentStage,
  isCurrent: () => boolean,
): Promise<HermesStagedAttachment> {
  if (attachment.kind !== 'image' || !isCurrent()) throw visionStageInvalidated()

  const bytes = await readHermesVisionFileBytes(attachment.file)
  if (!isCurrent()) throw visionStageInvalidated()

  const receipt = await controller.stage(sessionId, {
    id: attachment.id,
    origin: attachment.origin,
    displayName: attachment.label,
    ...(attachment.file.type ? { declaredMediaType: attachment.file.type } : {}),
    bytes,
  })
  if (!isCurrent()) throw visionStageInvalidated()

  if (
    receipt.consumed.kind !== 'bytes'
    || receipt.consumed.byteLength !== bytes.byteLength
    || receipt.availableCarrierKinds.length !== 1
    || receipt.availableCarrierKinds[0] !== 'bytes'
    || receipt.unconsumedCarrierKinds.length !== 0
  ) {
    throw visionStageInvalidated()
  }

  return { kind: 'image', label: attachment.label }
}

function eventText(event: HermesGatewayEvent) {
  const value = event.payload?.text ?? event.payload?.rendered
  return typeof value === 'string' ? value : ''
}

function findStreamingMessage(messages: HermesChatMessage[]) {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    if (messages[index].author === 'hermes' && messages[index].streaming) return index
  }
  return -1
}

export function useHermesChat(options: { onDesktopUiAction?: (action: HermesDesktopUiAction) => void } = {}) {
  const [connection, setConnection] = useState<HermesConnectionState>('idle')
  const [error, setError] = useState<string | null>(null)
  const [messages, setMessages] = useState<HermesChatMessage[]>([])
  const [reasoning, setReasoning] = useState<HermesReasoningState>({ body: '', streaming: false })
  const [busy, setBusy] = useState(false)
  const [toolActivity, setToolActivity] = useState<string | null>(null)
  const [toolRuns, setToolRuns] = useState<HermesToolRun[]>([])
  const [pendingApproval, setPendingApproval] = useState<HermesApprovalRequest | null>(null)
  const [approvalSubmission, setApprovalSubmission] = useState<{ requestId: string; choice: HermesApprovalChoice } | null>(null)
  const [pendingPrompt, setPendingPrompt] = useState<HermesInteractivePrompt | null>(null)
  const [promptSubmissionId, setPromptSubmissionId] = useState<string | null>(null)
  const [loadingSession, setLoadingSession] = useState(false)
  const [modelCatalog, setModelCatalog] = useState<HermesModelCatalog | null>(null)
  const [modelSelection, setModelSelection] = useState<HermesModelSelection | null>(null)
  const [modelLoading, setModelLoading] = useState(false)
  const [modelSwitching, setModelSwitching] = useState(false)
  const [modelSwitchPending, setModelSwitchPending] = useState(false)
  const [pendingModelConfirmation, setPendingModelConfirmation] = useState<null | {
    message: string
    selection: HermesModelSelection
  }>(null)
  const [approvalMode, setApprovalModeState] = useState<HermesApprovalMode>('smart')
  const [approvalModeSaving, setApprovalModeSaving] = useState(false)
  const [reasoningEffort, setReasoningEffortState] = useState<HermesReasoningEffort | null>(null)
  const [reasoningEffortSaving, setReasoningEffortSaving] = useState(false)
  const [attachments, setAttachments] = useState<HermesComposerAttachment[]>([])
  const [turnCompletionCount, setTurnCompletionCount] = useState(0)
  const [notifications, setNotifications] = useState<HermesNotification[]>([])
  const sessionId = useRef<string | null>(null)
  const storedSessionId = useRef<string | null>(null)
  const sessionProfile = useRef<string | undefined>(undefined)
  const connectionGeneration = useRef(0)
  const sessionOpenGeneration = useRef(new HermesSessionOpenGeneration())
  const modelLoadGeneration = useRef(new HermesSessionOpenGeneration())
  const reasoningLoadGeneration = useRef(new HermesSessionOpenGeneration())
  const reasoningSaveGeneration = useRef(new HermesSessionOpenGeneration())
  const sessionOpening = useRef(false)
  const sessionRecoveryBlocked = useRef<string | null>(null)
  const hadOpenConnection = useRef(false)
  const desktopUiActionRef = useRef(options.onDesktopUiAction)
  desktopUiActionRef.current = options.onDesktopUiAction
  const reconnecting = useRef(false)
  const modelSelectionRef = useRef<HermesModelSelection | null>(modelSelection)
  modelSelectionRef.current = modelSelection
  const approvalSubmitting = pendingApproval && approvalSubmission?.requestId === pendingApproval.requestId
    ? approvalSubmission.choice
    : null
  const promptSubmitting = pendingPrompt !== null && promptSubmissionId === pendingPrompt.requestId
  const selectedModelVisionCapability = resolveSelectedHermesVisionCapability(modelSelection)

  const dismissNotification = useCallback((id: string) => {
    setNotifications((current) => current.filter((notice) => notice.id !== id))
  }, [])

  const clearNotifications = useCallback(() => setNotifications([]), [])

  useEffect(() => {
    const nextExpiry = notifications.reduce<number | null>((earliest, notice) => {
      if (notice.expiresAt === undefined) return earliest
      return earliest === null || notice.expiresAt < earliest ? notice.expiresAt : earliest
    }, null)
    if (nextExpiry === null) return
    const timer = window.setTimeout(() => {
      setNotifications((current) => removeExpiredHermesNotifications(current))
    }, Math.max(0, nextExpiry - Date.now()))
    return () => window.clearTimeout(timer)
  }, [notifications])

  const loadModelCatalog = useCallback(async (
    refresh = false,
    targetSessionId = sessionId.current ?? undefined,
    reasoningSelection?: HermesModelSelection,
  ) => {
    if (hermesGateway.connectionState !== 'open') return
    const generation = modelLoadGeneration.current.begin()
    setModelLoading(true)
    try {
      const catalog = await hermesModelAdapter.options(targetSessionId, refresh, reasoningSelection)
      if (!modelLoadGeneration.current.isCurrent(generation)) return
      setModelCatalog(catalog)
      const catalogSelection = catalog.currentModel && catalog.currentProvider
        ? { model: catalog.currentModel, provider: catalog.currentProvider }
        : null
      setModelSelection((current) => reasoningCatalogSelection(
        current,
        catalogSelection,
        reasoningSelection,
        Boolean(targetSessionId),
      ))
    } catch (reason) {
      if (!modelLoadGeneration.current.isCurrent(generation)) return
      setError(reason instanceof Error ? reason.message : 'Could not load Hermes model options.')
    } finally {
      if (modelLoadGeneration.current.isCurrent(generation)) setModelLoading(false)
    }
  }, [])

  const syncApprovalMode = useCallback(async () => {
    if (hermesGateway.connectionState !== 'open') return
    try { setApprovalModeState(await hermesModelAdapter.getApprovalMode()) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not read the Hermes approval mode.') }
  }, [])

  const syncReasoningEffort = useCallback(async (targetSessionId = sessionId.current ?? undefined) => {
    if (hermesGateway.connectionState !== 'open') return
    const generation = reasoningLoadGeneration.current.begin()
    try {
      const effort = await hermesModelAdapter.getReasoningEffort(targetSessionId)
      if (reasoningLoadGeneration.current.isCurrent(generation)) setReasoningEffortState(effort)
    } catch (reason) {
      if (reasoningLoadGeneration.current.isCurrent(generation)) {
        setError(reason instanceof Error ? reason.message : 'Could not read the Hermes reasoning effort.')
      }
    }
  }, [])

  const recoverActiveSession = useCallback(async () => {
    if (sessionRecoveryBlocked.current) throw new Error(sessionRecoveryBlocked.current)
    const durableId = storedSessionId.current
    if (!durableId) {
      if (sessionId.current) {
        sessionId.current = null
        setBusy(false)
        setReasoning((current) => ({ ...current, streaming: false }))
        sessionRecoveryBlocked.current = 'Hermes reconnected, but this conversation was not durable yet. Start a new chat to continue safely.'
        throw new Error(sessionRecoveryBlocked.current)
      }
      return
    }

    const targetProfile = sessionProfile.current
    try {
      const resumed = await resumeHermesSession(
        (method, params, timeoutMs) => hermesGateway.request(method, params, timeoutMs),
        durableId,
        targetProfile,
      )
      if (storedSessionId.current !== durableId || sessionProfile.current !== targetProfile) return
      sessionId.current = resumed.runtimeSessionId
      storedSessionId.current = resumed.storedSessionId
      sessionRecoveryBlocked.current = null
    } catch (reason) {
      if (isHermesMissingSessionError(reason) && storedSessionId.current === durableId) {
        sessionId.current = null
        storedSessionId.current = null
        sessionProfile.current = undefined
        setBusy(false)
        setReasoning((current) => ({ ...current, streaming: false }))
        sessionRecoveryBlocked.current = 'Hermes reconnected, but the previous conversation no longer exists. Start a new chat to continue safely.'
        throw new Error(sessionRecoveryBlocked.current)
      }
      throw reason
    }
  }, [])

  const connect = useCallback(async () => {
    const generation = ++connectionGeneration.current
    setError(null)
    const isRecovery = shouldRecoverHermesSession(
      hadOpenConnection.current,
      hermesGateway.connectionState === 'open',
    )
    reconnecting.current = isRecovery
    if (isRecovery) setConnection('reconnecting')
    try {
      await hermesGateway.connect(isRecovery)
      if (generation !== connectionGeneration.current) return
      if (isRecovery) await recoverActiveSession()
      if (generation !== connectionGeneration.current) return
      await Promise.all([loadModelCatalog(), syncApprovalMode(), syncReasoningEffort()])
      if (generation !== connectionGeneration.current) return
      hadOpenConnection.current = true
      setConnection('open')
    }
    catch (reason) {
      if (generation !== connectionGeneration.current || isHermesConnectionCancelledError(reason)) return
      setConnection('error')
      setError(reason instanceof Error ? reason.message : 'Could not connect to Hermes.')
    } finally {
      reconnecting.current = false
    }
  }, [loadModelCatalog, recoverActiveSession, syncApprovalMode, syncReasoningEffort])

  useEffect(() => {
    let disposed = false
    let reconnectTimer: number | null = null
    let reconnectAttempt = 0
    const gatewayOpen = () => hermesGateway.connectionState === 'open'

    const clearReconnectTimer = () => {
      if (reconnectTimer !== null) {
        window.clearTimeout(reconnectTimer)
        reconnectTimer = null
      }
    }

    const scheduleReconnect = () => {
      if (disposed || reconnecting.current || reconnectTimer !== null || gatewayOpen()) return
      if (reconnectAttempt >= HERMES_RECONNECT_DELAYS_MS.length) {
        setConnection('error')
        setError(`Hermes could not reconnect after ${HERMES_RECONNECT_DELAYS_MS.length} attempts. Verify Docker is running, then use Reconnect Hermes.`)
        return
      }

      const delay = reconnectDelayMs(reconnectAttempt)
      reconnectAttempt += 1
      setConnection('reconnecting')
      reconnectTimer = window.setTimeout(() => {
        reconnectTimer = null
        void attemptReconnect()
      }, delay)
    }

    const attemptReconnect = async () => {
      if (disposed || reconnecting.current || gatewayOpen()) return
      reconnecting.current = true
      setConnection('reconnecting')
      let retry = false
      try {
        await hermesGateway.connect(true)
        await recoverActiveSession()
        if (disposed) return
        await Promise.all([loadModelCatalog(), syncApprovalMode(), syncReasoningEffort()])
        reconnectAttempt = 0
        hadOpenConnection.current = true
        setError(null)
        setConnection('open')
      } catch (reason) {
        if (disposed) return
        const message = reason instanceof Error ? reason.message : 'Could not reconnect to Hermes.'
        if (isHermesAuthenticationError(reason) || gatewayOpen()) {
          setConnection('error')
          setError(message)
        } else {
          setError(`Hermes connection dropped. Reconnect attempt ${reconnectAttempt} of ${HERMES_RECONNECT_DELAYS_MS.length} failed.`)
          retry = true
        }
      } finally {
        reconnecting.current = false
        if (retry && !disposed) scheduleReconnect()
      }
    }

    const reconnectNow = () => {
      if (disposed || !hadOpenConnection.current || gatewayOpen()) return
      clearReconnectTimer()
      reconnectAttempt = 0
      void attemptReconnect()
    }

    const removeState = hermesGateway.onState((state) => {
      if (state === 'open') {
        hadOpenConnection.current = true
        reconnectAttempt = 0
        clearReconnectTimer()
        setConnection(reconnecting.current ? 'reconnecting' : 'open')
      } else if (reconnecting.current) {
        setConnection('reconnecting')
      } else {
        setConnection(state)
        if (hadOpenConnection.current && (state === 'closed' || state === 'error')) scheduleReconnect()
      }
    })
    const removeEvent = hermesGateway.onEvent((event) => {
      if (event.type === 'notification.show' || event.type === 'notification.clear') {
        setNotifications((current) => mergeHermesNotification(current, event))
        return
      }
      // Session A remains capable of emitting late frames while session B is
      // resuming. The transition has no committed runtime identity, so no
      // transcript, tool, prompt, approval, or error frame is safe to publish.
      if (!shouldAcceptHermesSessionEvent(sessionOpening.current, sessionId.current, event.session_id)) return

      const desktopUiAction = normalizeHermesDesktopUiAction(event)
      if (desktopUiAction) {
        if (!shouldAcceptHermesDesktopUiAction(sessionOpening.current, sessionId.current, event.session_id)) return
        desktopUiActionRef.current?.(desktopUiAction)
        return
      }

      if (event.type === 'message.start') {
        setBusy(true)
        setReasoning({ body: '', streaming: true })
        setMessages((current) => [...current, { id: crypto.randomUUID(), author: 'hermes', body: '', streaming: true }])
      } else if (event.type === 'message.delta') {
        const delta = eventText(event)
        setMessages((current) => {
          const copy = [...current]
          const index = findStreamingMessage(copy)
          if (index >= 0) copy[index] = { ...copy[index], body: copy[index].body + delta }
          else if (delta) copy.push({ id: crypto.randomUUID(), author: 'hermes', body: delta, streaming: true })
          return copy
        })
      } else if (event.type === 'message.interim') {
        const interimText = eventText(event)
        setMessages((current) => {
          const copy = [...current]
          const index = findStreamingMessage(copy)
          if (index >= 0) {
            copy[index] = {
              ...copy[index],
              body: interimText || copy[index].body,
              streaming: false,
              interim: true,
            }
          } else if (interimText) {
            copy.push({ id: crypto.randomUUID(), author: 'hermes', body: interimText, interim: true })
          }
          return copy
        })
      } else if (event.type === 'reasoning.delta') {
        const delta = eventText(event)
        if (delta) setReasoning((current) => ({ body: current.body + delta, streaming: true }))
      } else if (event.type === 'reasoning.available') {
        const text = eventText(event)
        if (text) setReasoning({ body: text, streaming: true })
      } else if (event.type === 'session.info') {
        const model = typeof event.payload?.model === 'string' ? event.payload.model.trim() : ''
        const provider = typeof event.payload?.provider === 'string' ? event.payload.provider.trim() : ''
        if (model || provider) {
          const nextSelection = {
            model: model || modelSelectionRef.current?.model || '',
            provider: provider || modelSelectionRef.current?.provider || '',
          }
          setModelSelection((current) => ({
            model: model || current?.model || '',
            provider: provider || current?.provider || '',
          }))
          void loadModelCatalog(true, sessionId.current ?? undefined, nextSelection)
        }
        if (typeof event.payload?.model_switch_pending === 'boolean') {
          setModelSwitchPending(event.payload.model_switch_pending)
        }
        const mode = event.payload?.approval_mode
        if (mode === 'manual' || mode === 'smart' || mode === 'off') setApprovalModeState(mode)
        setReasoningEffortState((current) => reasoningEffortFromSessionInfo(event.payload, current))
      } else if (event.type === 'message.complete') {
        const finalText = eventText(event)
        setBusy(false)
        setTurnCompletionCount((count) => count + 1)
        setToolActivity(null)
        setPendingApproval(null)
        setApprovalSubmission(null)
        setPendingPrompt(null)
        setPromptSubmissionId(null)
        setReasoning((current) => ({ ...current, streaming: false }))
        setMessages((current) => {
          const copy = [...current]
          const index = findStreamingMessage(copy)
          if (index >= 0) copy[index] = { ...copy[index], body: finalText || copy[index].body, streaming: false }
          else if (finalText) copy.push({ id: crypto.randomUUID(), author: 'hermes', body: finalText })
          return copy
        })
      } else if (event.type === 'tool.start' || event.type === 'tool.progress') {
        const name = event.payload?.name ?? event.payload?.tool
        setToolActivity(typeof name === 'string' ? `Using ${name}` : 'Using a tool')
        setToolRuns((current) => mergeHermesToolEvent(current, event, crypto.randomUUID()))
      } else if (event.type === 'tool.complete') {
        setToolActivity(null)
        setToolRuns((current) => mergeHermesToolEvent(current, event, crypto.randomUUID()))
      } else if (event.type === 'tool.generating') {
        const name = event.payload?.name
        setToolActivity(typeof name === 'string' ? `Preparing ${name}` : 'Preparing a tool')
      } else if (event.type === 'approval.request') {
        const request = normalizeHermesApproval(event, sessionId.current)
        if (request) {
          setBusy(true)
          setPendingApproval({ ...request, requestId: request.requestId || crypto.randomUUID() })
          setApprovalSubmission(null)
        }
      } else if (event.type === 'clarify.request' || event.type === 'sudo.request' || event.type === 'secret.request') {
        const prompt = normalizeHermesInteractivePrompt(event, sessionId.current)
        if (prompt) {
          setBusy(true)
          setPendingPrompt(prompt)
          setPromptSubmissionId(null)
        }
      } else if (event.type === 'developer.native.request') {
        void handleNativeDeveloperRequest(event, hermesGateway, desktopDeveloperServicesClient, desktopDotNetDebuggerController)
          .catch((reason) => setError(reason instanceof Error ? reason.message : 'The Windows developer bridge failed.'))
      } else if (event.type === 'error') {
        setBusy(false)
        setPendingApproval(null)
        setApprovalSubmission(null)
        setPendingPrompt(null)
        setPromptSubmissionId(null)
        setReasoning((current) => ({ ...current, streaming: false }))
        setError(eventText(event) || 'Hermes reported an error.')
      }
    })

    const onOnline = () => reconnectNow()
    const onVisible = () => { if (document.visibilityState === 'visible') reconnectNow() }
    const removeAuth = subscribeHermesAuthChanged((state) => {
      clearReconnectTimer()
      reconnectAttempt = 0
      if (state === 'signed-out') {
        ++connectionGeneration.current
        sessionOpenGeneration.current.invalidate()
        modelLoadGeneration.current.invalidate()
        sessionOpening.current = false
        sessionId.current = null
        storedSessionId.current = null
        sessionProfile.current = undefined
        setLoadingSession(false)
        setBusy(false)
        setMessages([])
        setReasoning({ body: '', streaming: false })
        setToolActivity(null)
        setToolRuns([])
        setPendingApproval(null)
        setApprovalSubmission(null)
        setPendingPrompt(null)
        setPromptSubmissionId(null)
        setAttachments([])
        setModelCatalog(null)
        setModelSelection(null)
        setReasoningEffortState(null)
        setPendingModelConfirmation(null)
        hadOpenConnection.current = false
        hermesGateway.close()
        setNotifications([])
        setConnection('error')
        setError('Sign in to Hermes from Workbench Account to continue.')
        return
      }
      void connect()
    })
    window.addEventListener('online', onOnline)
    document.addEventListener('visibilitychange', onVisible)
    void connect()
    return () => {
      disposed = true
      clearReconnectTimer()
      window.removeEventListener('online', onOnline)
      document.removeEventListener('visibilitychange', onVisible)
      removeAuth()
      removeState()
      removeEvent()
    }
  }, [connect, loadModelCatalog, recoverActiveSession, syncApprovalMode, syncReasoningEffort])

  const addAttachments = useCallback((files: File[], origin: HermesVisionInputOrigin = 'file-picker') => {
    const accepted: HermesComposerAttachment[] = []
    const rejected: string[] = []

    for (const file of files) {
      try {
        const kind = validateAttachment(file)
        accepted.push({
          id: crypto.randomUUID(),
          file,
          kind,
          origin,
          label: file.name,
          size: file.size,
          status: 'ready',
        })
      } catch (reason) {
        rejected.push(reason instanceof Error ? reason.message : `Could not attach ${file.name}.`)
      }
    }

    if (accepted.length) setAttachments((current) => [...current, ...accepted])
    setError(rejected.length ? rejected.join(' ') : null)
  }, [])

  const removeAttachment = useCallback((id: string) => {
    setAttachments((current) => current.filter((attachment) => attachment.id !== id))
  }, [])

  const clearAttachments = useCallback(() => setAttachments([]), [])

  const send = useCallback(async (text: string, attachmentOverride?: HermesComposerAttachment[]) => {
    if (connection !== 'open') throw new Error('Hermes is not connected.')
    const sendConnectionGeneration = connectionGeneration.current
    const fromComposer = attachmentOverride === undefined
    let queuedAttachments = [...(attachmentOverride ?? attachments)]
    if (!text.trim() && queuedAttachments.length === 0) return
    setError(null)
    setBusy(true)
    if (fromComposer) setAttachments([])

    try {
      if (!sessionId.current) {
        const selection = modelSelection
        const reasoningControl = hermesReasoningControlForSelection(modelCatalog, selection)
        const verifiedReasoningEffort = effectiveHermesReasoningEffort(reasoningEffort, reasoningControl)
        const created = await hermesGateway.request<{ session_id: string; stored_session_id?: string }>('session.create', {
          cols: 96,
          source: 'desktop',
          ...(selection?.model ? {
            model: selection.model,
            ...(selection.provider ? { provider: selection.provider } : {}),
          } : {}),
          ...(verifiedReasoningEffort ? { reasoning_effort: verifiedReasoningEffort } : {}),
        })
        if (sendConnectionGeneration !== connectionGeneration.current || sessionOpening.current) {
          throw visionStageInvalidated()
        }
        sessionId.current = created.session_id
        storedSessionId.current = created.stored_session_id ?? null
        sessionProfile.current = undefined
        sessionRecoveryBlocked.current = null
      }

      const activeSessionId = sessionId.current
      const isCurrentAttachmentSession = () => (
        sendConnectionGeneration === connectionGeneration.current
        && !sessionOpening.current
        && sessionId.current === activeSessionId
      )
      const stagedAttachments: HermesStagedAttachment[] = []
      for (const attachment of queuedAttachments) {
        if (attachment.staged && attachment.attachedSessionId === activeSessionId) {
          stagedAttachments.push(attachment.staged)
          continue
        }

        try {
          const staged = attachment.kind === 'image'
            ? await stageHermesVisionAttachmentForSend(
              hermesGatewayVisionController,
              activeSessionId,
              attachment,
              isCurrentAttachmentSession,
            )
            : await hermesAttachmentAdapter.stage(activeSessionId, attachment.file)
          if (!isCurrentAttachmentSession()) throw visionStageInvalidated()
          stagedAttachments.push(staged)
          queuedAttachments = queuedAttachments.map((item) => item.id === attachment.id
            ? { ...item, status: 'staged', staged, attachedSessionId: activeSessionId }
            : item)
        } catch (reason) {
          const message = reason instanceof Error ? reason.message : `Hermes could not attach ${attachment.label}.`
          queuedAttachments = queuedAttachments.map((item) => item.id === attachment.id
            ? { ...item, status: 'error', error: message }
            : item)
          throw reason
        }
      }

      if (!isCurrentAttachmentSession()) throw visionStageInvalidated()
      const promptText = buildPromptWithAttachments(text, stagedAttachments)
      setMessages((current) => [...current, {
        id: crypto.randomUUID(),
        author: 'you',
        body: text.trim(),
        attachments: queuedAttachments.map(({ id, kind, label, size }) => ({ id, kind, label, size })),
      }])
      await hermesGateway.request('prompt.submit', { session_id: activeSessionId, text: promptText }, 1_800_000)
    } catch (reason) {
      setBusy(false)
      if (fromComposer) {
        setAttachments((current) => {
          const existing = new Set(current.map((attachment) => attachment.id))
          return [...queuedAttachments.filter((attachment) => !existing.has(attachment.id)), ...current]
        })
      }
      const message = reason instanceof Error ? reason.message : 'Hermes could not send the message.'
      setError(message)
      throw reason
    }
  }, [attachments, connection, modelCatalog, modelSelection, reasoningEffort])

  const stop = useCallback(async () => {
    if (!sessionId.current) return
    try {
      await hermesGateway.request('session.interrupt', { session_id: sessionId.current })
    } finally {
      setBusy(false)
      setToolActivity(null)
      setPendingApproval(null)
      setApprovalSubmission(null)
      setPendingPrompt(null)
      setPromptSubmissionId(null)
      setReasoning((current) => ({ ...current, streaming: false }))
    }
  }, [])

  const newChat = useCallback(() => {
    if (!canStartHermesChat(busy, hermesGateway.connectionState === 'open')) return
    sessionOpenGeneration.current.invalidate()
    modelLoadGeneration.current.invalidate()
    reasoningLoadGeneration.current.invalidate()
    reasoningSaveGeneration.current.invalidate()
    sessionOpening.current = false
    setLoadingSession(false)
    sessionId.current = null
    storedSessionId.current = null
    sessionProfile.current = undefined
    sessionRecoveryBlocked.current = null
    setMessages([])
    setError(null)
    setToolActivity(null)
    setToolRuns([])
    setPendingApproval(null)
    setApprovalSubmission(null)
    setPendingPrompt(null)
    setPromptSubmissionId(null)
    setAttachments([])
    setReasoning({ body: '', streaming: false })
    setReasoningEffortState(null)
    setReasoningEffortSaving(false)
    setModelSwitchPending(false)
    setConnection('open')
    void syncReasoningEffort(undefined)
  }, [busy, syncReasoningEffort])

  const openSession = useCallback(async (storedId: string, profile?: string) => {
    if (busy || loadingSession) return false
    const generation = sessionOpenGeneration.current.begin()
    modelLoadGeneration.current.invalidate()
    reasoningLoadGeneration.current.invalidate()
    reasoningSaveGeneration.current.invalidate()
    sessionOpening.current = true
    sessionId.current = null
    setLoadingSession(true)
    setError(null)
    setToolActivity(null)
    setToolRuns([])
    setPendingApproval(null)
    setApprovalSubmission(null)
    setPendingPrompt(null)
    setPromptSubmissionId(null)
    setPendingModelConfirmation(null)
    setAttachments([])
    setReasoning({ body: '', streaming: false })
    setReasoningEffortState(null)
    setReasoningEffortSaving(false)
    setModelSwitchPending(false)
    sessionRecoveryBlocked.current = null

    try {
      if (hermesGateway.connectionState !== 'open') await hermesGateway.connect()
      if (!sessionOpenGeneration.current.isCurrent(generation)) return false

      const [history, resumed] = await Promise.all([
        hermesSessionApi.messages(storedId, profile),
        hermesGateway.request<{ session_id: string; stored_session_id?: string }>('session.resume', {
          session_id: storedId,
          cols: 96,
          source: 'desktop',
          omit_messages: true,
          ...(profile ? { profile } : {}),
        }),
      ])

      if (!sessionOpenGeneration.current.isCurrent(generation)) return false
      await loadModelCatalog(false, resumed.session_id)
      if (!sessionOpenGeneration.current.isCurrent(generation)) return false

      sessionId.current = resumed.session_id
      storedSessionId.current = resumed.stored_session_id ?? history.sessionId ?? storedId
      sessionProfile.current = profile?.trim() || undefined
      await syncReasoningEffort(resumed.session_id)
      setToolActivity(null)
      setToolRuns([])
      setPendingApproval(null)
      setApprovalSubmission(null)
      setPendingPrompt(null)
      setPromptSubmissionId(null)
      setReasoning({ body: '', streaming: false })
      setMessages(history.messages.flatMap((message, index): HermesChatMessage[] => {
        if (message.role !== 'user' && message.role !== 'assistant') return []
        const body = storedMessageText(message).trim()
        if (!body) return []
        return [{
          id: `stored-${message.id ?? message.row_id ?? index}`,
          author: message.role === 'user' ? 'you' : 'hermes',
          body,
        }]
      }))
      return true
    } catch (reason) {
      if (!sessionOpenGeneration.current.isCurrent(generation)) return false
      setError(reason instanceof Error ? reason.message : 'Could not open the Hermes session.')
      throw reason
    } finally {
      if (sessionOpenGeneration.current.isCurrent(generation)) {
        sessionOpening.current = false
        setLoadingSession(false)
      }
    }
  }, [busy, loadModelCatalog, loadingSession, syncReasoningEffort])

  const respondToApproval = useCallback(async (choice: HermesApprovalChoice) => {
    const request = pendingApproval
    if (!request || approvalSubmitting) return
    if (!request.choices.includes(choice)) throw new Error(`Hermes did not offer the approval choice "${choice}".`)
    if (choice === 'always' && !request.allowPermanent) throw new Error('Permanent approval is disabled for this command.')

    setApprovalSubmission({ requestId: request.requestId, choice })
    setError(null)
    try {
      await hermesGateway.request<{ resolved?: boolean }>('approval.respond', {
        choice,
        ...(request.sessionId ? { session_id: request.sessionId } : {}),
      })
      setPendingApproval((current) => current?.requestId === request.requestId ? null : current)
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Hermes could not record the approval decision.'
      setError(message)
      throw reason
    } finally {
      setApprovalSubmission((current) => current?.requestId === request.requestId ? null : current)
    }
  }, [approvalSubmitting, pendingApproval])

  const respondToPrompt = useCallback(async (value: string) => {
    const prompt = pendingPrompt
    if (!prompt || promptSubmitting) return

    const method = prompt.kind === 'clarify'
      ? 'clarify.respond'
      : prompt.kind === 'sudo'
        ? 'sudo.respond'
        : 'secret.respond'
    const params = prompt.kind === 'clarify'
      ? { request_id: prompt.requestId, answer: value }
      : prompt.kind === 'sudo'
        ? { request_id: prompt.requestId, password: value }
        : { request_id: prompt.requestId, value }

    setPromptSubmissionId(prompt.requestId)
    setError(null)
    try {
      await hermesGateway.request(method, params)
      setPendingPrompt((current) => current?.requestId === prompt.requestId ? null : current)
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Hermes could not submit the requested input.'
      setError(message)
      throw reason
    } finally {
      setPromptSubmissionId((current) => current === prompt.requestId ? null : current)
    }
  }, [pendingPrompt, promptSubmitting])

  const selectModel = useCallback(async (selection: HermesModelSelection, confirmExpensiveModel = false) => {
    if (modelSwitching || modelSwitchPending || sessionOpening.current) return false
    const targetSessionId = sessionId.current
    if (!targetSessionId) {
      reasoningLoadGeneration.current.invalidate()
      reasoningSaveGeneration.current.invalidate()
      setReasoningEffortState(null)
      setReasoningEffortSaving(false)
      setModelSelection(selection)
      setPendingModelConfirmation(null)
      await loadModelCatalog(false, undefined, selection)
      await syncReasoningEffort(undefined)
      return true
    }

    setModelSwitching(true)
    setError(null)
    try {
      const result = await hermesModelAdapter.select(selection, targetSessionId, confirmExpensiveModel)
      if (sessionOpening.current || sessionId.current !== targetSessionId) return false
      if (result.confirmRequired) {
        setPendingModelConfirmation({
          message: result.confirmMessage || 'This model may have significantly higher usage costs.',
          selection,
        })
        return false
      }
      reasoningLoadGeneration.current.invalidate()
      reasoningSaveGeneration.current.invalidate()
      setReasoningEffortState(null)
      setReasoningEffortSaving(false)
      const appliedSelection = { model: result.model || selection.model, provider: selection.provider }
      setModelSelection(appliedSelection)
      setModelSwitchPending(result.deferred)
      setPendingModelConfirmation(null)
      await loadModelCatalog(true, targetSessionId, appliedSelection)
      if (!result.deferred) await syncReasoningEffort(targetSessionId)
      return true
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Hermes could not switch models.')
      throw reason
    } finally {
      setModelSwitching(false)
    }
  }, [loadModelCatalog, modelSwitchPending, modelSwitching, syncReasoningEffort])

  const changeReasoningEffort = useCallback(async (effort: HermesReasoningEffort) => {
    if (sessionOpening.current || reasoningEffortSaving) return
    const control = hermesReasoningControlForSelection(modelCatalog, modelSelection)
    if (!control || (control.source !== 'server' && control.source !== 'compatibility') || !control.options.includes(effort)) {
      const message = 'The selected model did not verify that reasoning option.'
      setError(message)
      throw new Error(message)
    }
    const targetSessionId = sessionId.current
    if (!targetSessionId) {
      setReasoningEffortState(effort)
      return
    }
    const previous = reasoningEffort
    const targetSelection = modelSelection
    const generation = reasoningSaveGeneration.current.begin()
    setReasoningEffortState(effort)
    setReasoningEffortSaving(true)
    setError(null)
    try {
      const saved = await hermesModelAdapter.setReasoningEffort(effort, targetSessionId)
      if (
        reasoningSaveGeneration.current.isCurrent(generation)
        && !sessionOpening.current
        && sessionId.current === targetSessionId
        && modelSelectionRef.current?.model === targetSelection?.model
        && modelSelectionRef.current?.provider === targetSelection?.provider
      ) setReasoningEffortState(saved)
    } catch (reason) {
      const currentSave = reasoningSaveGeneration.current.isCurrent(generation)
      if (currentSave && !sessionOpening.current && sessionId.current === targetSessionId) {
        setReasoningEffortState(previous)
        setError(reason instanceof Error ? reason.message : 'Hermes could not update the reasoning effort.')
      }
      throw reason
    } finally {
      if (reasoningSaveGeneration.current.isCurrent(generation)) setReasoningEffortSaving(false)
    }
  }, [modelCatalog, modelSelection, reasoningEffort, reasoningEffortSaving])

  const changeApprovalMode = useCallback(async (mode: HermesApprovalMode) => {
    if (sessionOpening.current || approvalModeSaving || mode === approvalMode) return
    const previous = approvalMode
    setApprovalModeState(mode)
    setApprovalModeSaving(true)
    setError(null)
    try { setApprovalModeState(await hermesModelAdapter.setApprovalMode(mode)) }
    catch (reason) {
      setApprovalModeState(previous)
      setError(reason instanceof Error ? reason.message : 'Hermes could not update the approval mode.')
      throw reason
    } finally {
      setApprovalModeSaving(false)
    }
  }, [approvalMode, approvalModeSaving])

  return {
    activeStoredSessionId: storedSessionId.current,
    addAttachments,
    approvalMode,
    approvalModeSaving,
    approvalSubmitting,
    attachments,
    authenticationRequired: error ? isHermesAuthenticationError(error) : false,
    busy,
    canStartNewChat: canStartHermesChat(busy, hermesGateway.connectionState === 'open'),
    clearAttachments,
    clearNotifications,
    connect,
    connection,
    error,
    loadingSession,
    messages,
    modelCatalog,
    modelLoading,
    modelSelection,
    selectedModelVisionCapability,
    modelSwitching: modelSwitching || modelSwitchPending,
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
    refreshModels: () => sessionOpening.current ? Promise.resolve() : loadModelCatalog(true, sessionId.current ?? undefined, modelSelection ?? undefined),
    removeAttachment,
    dismissNotification,
    respondToApproval,
    respondToPrompt,
    selectModel,
    setReasoningEffort: changeReasoningEffort,
    setApprovalMode: changeApprovalMode,
    cancelModelConfirmation: () => setPendingModelConfirmation(null),
    confirmModelSelection: () => pendingModelConfirmation
      ? selectModel(pendingModelConfirmation.selection, true)
      : Promise.resolve(false),
    send,
    stop,
    toolActivity,
    toolRuns,
    turnCompletionCount,
  }
}
