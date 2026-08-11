export const HERMES_PROMPT_QUEUE_ADAPTER_VERSION = 1
export const HERMES_PROMPT_QUEUE_LIMIT = 20

const immediatePausePattern = /\b(?:hang\s+tight|hold\s+(?:up|on)|just\s+(?:a\s+)?(?:sec(?:ond)?|moment)|wait(?:\s+(?:just\s+)?(?:a\s+)?(?:sec(?:ond)?|moment))?|stop|pause)\b/i
const pauseNegationPattern = /\b(?:do\s+not|don't|dont|never|not\s+yet|keep\s+going|continue|resume)\b/i

export function isHermesImmediatePauseInstruction(text: string) {
  const normalized = text.trim().replace(/\s+/g, ' ')
  if (!normalized || normalized.length > 240 || normalized.includes('?')) return false
  if (pauseNegationPattern.test(normalized)) return false
  return immediatePausePattern.test(normalized)
}

export type HermesQueuedPrompt<TAttachment> = {
  id: string
  text: string
  attachments: TAttachment[]
  queuedAt: number
}

export type HermesPromptQueueInput<TAttachment> = {
  text: string
  attachments: TAttachment[]
}

type QueueFactoryOptions = {
  createId?: () => string
  now?: () => number
}

export function enqueueHermesPrompt<TAttachment>(
  entries: HermesQueuedPrompt<TAttachment>[],
  input: HermesPromptQueueInput<TAttachment>,
  options: QueueFactoryOptions = {},
) {
  const text = input.text.trim()
  if (!text && input.attachments.length === 0) return entries
  if (entries.length >= HERMES_PROMPT_QUEUE_LIMIT) {
    throw new Error(`Hermes can queue up to ${HERMES_PROMPT_QUEUE_LIMIT} prompts in one conversation.`)
  }

  return [...entries, {
    id: (options.createId ?? (() => crypto.randomUUID()))(),
    text,
    attachments: [...input.attachments],
    queuedAt: (options.now ?? Date.now)(),
  }]
}

export function updateHermesQueuedPrompt<TAttachment>(
  entries: HermesQueuedPrompt<TAttachment>[],
  id: string,
  text: string,
) {
  const nextText = text.trim()
  if (!nextText) return entries
  return entries.map((entry) => entry.id === id ? { ...entry, text: nextText } : entry)
}

export function removeHermesQueuedPrompt<TAttachment>(entries: HermesQueuedPrompt<TAttachment>[], id: string) {
  return entries.filter((entry) => entry.id !== id)
}

export function promoteHermesQueuedPrompt<TAttachment>(entries: HermesQueuedPrompt<TAttachment>[], id: string) {
  const index = entries.findIndex((entry) => entry.id === id)
  if (index <= 0) return entries
  return [entries[index], ...entries.slice(0, index), ...entries.slice(index + 1)]
}

export function shouldAutoDrainHermesQueue(input: {
  busy: boolean
  completionAdvanced: boolean
  connectionOpen: boolean
  parked: boolean
  queueLength: number
}) {
  return input.completionAdvanced
    && input.connectionOpen
    && !input.busy
    && !input.parked
    && input.queueLength > 0
}
