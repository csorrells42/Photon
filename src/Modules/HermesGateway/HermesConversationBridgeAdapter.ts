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
}

export type HermesBridgeSnapshot = {
  assistant: 'Hermes'
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

export function createHermesBridgeSnapshot(
  connection: string,
  busy: boolean,
  sessionId: string | null,
  messages: readonly HermesChatMessage[],
  tools: readonly HermesToolRun[],
): HermesBridgeSnapshot {
  return {
    assistant: 'Hermes',
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
    if (!requestId || !['snapshot', 'turn', 'interrupt'].includes(operation)) return
    const payload = message.payload && typeof message.payload === 'object' ? message.payload as Record<string, unknown> : null
    const action = operation === 'snapshot'
      ? Promise.resolve(controller.snapshot())
      : operation === 'interrupt'
        ? controller.interrupt()
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
