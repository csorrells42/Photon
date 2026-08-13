import type { HermesChatMessage } from './useHermesChat'
import type { HermesToolRun } from './HermesRuntimeAdapter'

export const HERMES_CONVERSATION_BRIDGE_ADAPTER_VERSION = 1

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type BridgeController = {
  snapshot: () => HermesBridgeSnapshot
  submitTurn: (text: string) => Promise<HermesBridgeSnapshot>
  interrupt: () => Promise<HermesBridgeSnapshot>
  newSession: () => Promise<HermesBridgeSnapshot>
  observe: (message: HermesBridgeObservation) => Promise<HermesBridgeSnapshot>
}

export type HermesBridgeObservation = {
  messageId: string
  sender: 'Chris' | 'Codex' | 'Photon' | 'Ali' | 'Scarlett'
  recipient: 'Chris' | 'Codex' | 'Photon' | 'Ali' | 'Scarlett' | 'Everyone'
  body: string
  header: string
}

export type HermesBridgeSnapshot = {
  assistant: 'Hermes'
  generation: string
  state: string
  isBusy: boolean
  sessionId: string | null
  messages: readonly { id: string; role: 'user' | 'assistant'; text: string }[]
  activities: readonly { id: string; name: string; phase: string; detail?: string }[]
}

function host(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function bounded(value: string | undefined, maximum: number) {
  const text = value?.trim() ?? ''
  return text.length <= maximum ? text : `${text.slice(0, maximum)}\n… truncated`
}

const senders = new Set(['Chris', 'Codex', 'Photon', 'Ali', 'Scarlett'])
const recipients = new Set([...senders, 'Everyone'])

export function normalizeHermesBridgeObservation(value: unknown): HermesBridgeObservation {
  if (!value || typeof value !== 'object') throw new Error('The bridge observation envelope is invalid.')
  const input = value as Record<string, unknown>
  const messageId = typeof input.messageId === 'string' ? input.messageId.trim() : ''
  const sender = typeof input.sender === 'string' ? input.sender.trim() : ''
  const recipient = typeof input.recipient === 'string' ? input.recipient.trim() : ''
  const body = typeof input.body === 'string' ? input.body.trim() : ''
  if (messageId.length < 5 || messageId.length > 128
    || !senders.has(sender)
    || !recipients.has(recipient)
    || sender === recipient
    || body.length < 1
    || body.length > 64 * 1024) throw new Error('The bridge observation envelope is invalid.')
  return {
    messageId,
    sender: sender as HermesBridgeObservation['sender'],
    recipient: recipient as HermesBridgeObservation['recipient'],
    body,
    header: `${sender}->${recipient}`,
  }
}

export function createHermesBridgeSnapshot(
  connection: string,
  busy: boolean,
  sessionId: string | null,
  messages: readonly HermesChatMessage[],
  tools: readonly HermesToolRun[],
  generation = 'renderer:unbound',
): HermesBridgeSnapshot {
  return {
    assistant: 'Hermes',
    generation: bounded(generation, 128),
    state: connection,
    isBusy: busy,
    sessionId,
    messages: messages.slice(-200).map((message) => ({
      id: bounded(message.id, 512),
      role: message.author === 'you' ? 'user' : 'assistant',
      text: bounded(message.body, 64 * 1024),
    })),
    activities: tools.slice(-100).map((tool) => ({
      id: bounded(tool.id, 512),
      name: bounded(tool.name, 512),
      phase: tool.phase,
      ...(tool.context || tool.output ? { detail: bounded(tool.output || tool.context, 8 * 1024) } : {}),
    })),
  }
}

export function registerHermesConversationBridge(controller: BridgeController) {
  const bridge = host()
  if (!bridge) return () => undefined
  const listener = (event: MessageEvent) => {
    const message = event.data as Record<string, unknown> | null
    if (!message || message.type !== 'conversationBridge.request' || message.version !== HERMES_CONVERSATION_BRIDGE_ADAPTER_VERSION) return
    const requestId = typeof message.requestId === 'string' ? message.requestId : ''
    const operation = typeof message.operation === 'string' ? message.operation : ''
    if (!requestId || !['snapshot', 'turn', 'interrupt', 'new', 'observe'].includes(operation)) return
    const payload = message.payload && typeof message.payload === 'object' ? message.payload as Record<string, unknown> : null
    const action = operation === 'snapshot'
      ? Promise.resolve(controller.snapshot())
      : operation === 'interrupt'
        ? controller.interrupt()
        : operation === 'new'
          ? controller.newSession()
        : operation === 'observe'
          ? Promise.resolve().then(() => controller.observe(normalizeHermesBridgeObservation(payload)))
        : typeof payload?.text === 'string' && payload.text.trim()
          ? controller.submitTurn(payload.text.trim())
          : Promise.reject(new Error('The Hermes bridge turn did not contain text.'))
    void action.then((snapshot) => bridge.postMessage({
      type: 'conversationBridge.reply',
      version: HERMES_CONVERSATION_BRIDGE_ADAPTER_VERSION,
      requestId,
      ok: true,
      snapshot,
    })).catch((reason) => bridge.postMessage({
      type: 'conversationBridge.reply',
      version: HERMES_CONVERSATION_BRIDGE_ADAPTER_VERSION,
      requestId,
      ok: false,
      error: bounded(reason instanceof Error ? reason.message : String(reason), 2_000),
    }))
  }
  bridge.addEventListener('message', listener)
  return () => { bridge.removeEventListener('message', listener) }
}
