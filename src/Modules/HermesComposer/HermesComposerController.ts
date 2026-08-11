import {
  HERMES_COMPOSER_LIMITS,
  HermesComposerInputError,
  type HermesComposerControllerOptions,
  type HermesComposerSnapshot,
  type HermesConnectionState,
  type HermesQueuedPrompt,
  type HermesSubmissionHistoryEntry,
  type HermesSubmissionIntent,
  type HermesSubmitNextResult,
  type HermesVoiceState,
} from './types'

const connectionStates = new Set<HermesConnectionState>(['ready', 'reconnecting', 'offline', 'blocked'])
const safeCode = /^[a-z0-9][a-z0-9._-]{0,63}$/u

function defaultIdFactory() {
  let fallback = 0
  return () => globalThis.crypto?.randomUUID?.() ?? `composer-${Date.now().toString(36)}-${(++fallback).toString(36)}`
}

function boundedId(value: string, name: string, maximum: number = HERMES_COMPOSER_LIMITS.optionIdCharacters) {
  if (typeof value !== 'string' || value.length === 0 || value.length > maximum || /[\u0000-\u001f\u007f]/u.test(value)) {
    throw new HermesComposerInputError(`${name} is invalid or exceeds its bound.`)
  }
  return value
}

function boundedPrompt(value: string) {
  if (typeof value !== 'string' || value.trim().length === 0) throw new HermesComposerInputError('Prompt text cannot be empty.')
  if (value.includes('\u0000')) throw new HermesComposerInputError('Prompt text cannot contain null characters.')
  if (value.length > HERMES_COMPOSER_LIMITS.promptCharacters) {
    throw new HermesComposerInputError(`Prompt text cannot exceed ${HERMES_COMPOSER_LIMITS.promptCharacters} characters.`)
  }
  return value
}

function messageId(value: string) {
  return boundedId(value, 'Message ID', HERMES_COMPOSER_LIMITS.messageIdCharacters)
}

function immutableIntent(intent: HermesSubmissionIntent): HermesSubmissionIntent {
  switch (intent.kind) {
    case 'prompt': return Object.freeze({ kind: 'prompt', text: boundedPrompt(intent.text) })
    case 'edit-resend': return Object.freeze({ kind: 'edit-resend', messageId: messageId(intent.messageId), text: boundedPrompt(intent.text) })
    case 'retry': return Object.freeze({ kind: 'retry', messageId: messageId(intent.messageId) })
    case 'regenerate': return Object.freeze({ kind: 'regenerate', messageId: messageId(intent.messageId) })
  }
}

function immutableVoice(state: HermesVoiceState): HermesVoiceState {
  if (state.kind !== 'error') return Object.freeze({ kind: state.kind })
  if (typeof state.message !== 'string' || state.message.trim().length === 0
    || state.message.length > HERMES_COMPOSER_LIMITS.descriptionCharacters || /[\u0000-\u001f\u007f]/u.test(state.message)) {
    throw new HermesComposerInputError('Voice error text is invalid or exceeds its bound.')
  }
  return Object.freeze({ kind: 'error', message: state.message })
}

export class HermesComposerController {
  readonly #submit: HermesComposerControllerOptions['submit']
  readonly #queueLimit: number
  readonly #historyLimit: number
  readonly #createId: () => string
  readonly #now: () => number
  readonly #listeners = new Set<() => void>()
  readonly #attemptedQueueIds = new Set<string>()
  #queue: HermesQueuedPrompt[] = []
  #history: HermesSubmissionHistoryEntry[] = []
  #inFlight: HermesQueuedPrompt | null = null
  #abortController: AbortController | null = null
  #connection: HermesConnectionState
  #voice: HermesVoiceState
  #revision = 0
  #disposed = false
  #snapshot: HermesComposerSnapshot

  constructor(options: HermesComposerControllerOptions) {
    if (!options || typeof options.submit !== 'function') throw new HermesComposerInputError('A submission callback is required.')
    this.#submit = options.submit
    this.#queueLimit = options.queueLimit ?? HERMES_COMPOSER_LIMITS.queueItems
    this.#historyLimit = options.historyLimit ?? HERMES_COMPOSER_LIMITS.historyItems
    if (!Number.isInteger(this.#queueLimit) || this.#queueLimit < 1 || this.#queueLimit > HERMES_COMPOSER_LIMITS.queueItems) {
      throw new HermesComposerInputError(`Queue limit must be between 1 and ${HERMES_COMPOSER_LIMITS.queueItems}.`)
    }
    if (!Number.isInteger(this.#historyLimit) || this.#historyLimit < 1 || this.#historyLimit > HERMES_COMPOSER_LIMITS.historyItems) {
      throw new HermesComposerInputError(`History limit must be between 1 and ${HERMES_COMPOSER_LIMITS.historyItems}.`)
    }
    this.#createId = options.createId ?? defaultIdFactory()
    this.#now = options.now ?? Date.now
    this.#connection = options.initialConnection ?? 'ready'
    if (!connectionStates.has(this.#connection)) throw new HermesComposerInputError('Initial connection state is invalid.')
    this.#voice = immutableVoice(options.initialVoice ?? { kind: 'idle' })
    this.#snapshot = this.#buildSnapshot()
  }

  getSnapshot = (): HermesComposerSnapshot => this.#snapshot

  subscribe = (listener: () => void) => {
    this.#assertActive()
    this.#listeners.add(listener)
    return () => this.#listeners.delete(listener)
  }

  enqueuePrompt(text: string) {
    return this.#enqueue({ kind: 'prompt', text })
  }

  enqueueEditResend(sourceMessageId: string, text: string) {
    return this.#enqueue({ kind: 'edit-resend', messageId: sourceMessageId, text })
  }

  enqueueRetry(sourceMessageId: string) {
    return this.#enqueue({ kind: 'retry', messageId: sourceMessageId })
  }

  enqueueRegenerate(sourceMessageId: string) {
    return this.#enqueue({ kind: 'regenerate', messageId: sourceMessageId })
  }

  editQueueItem(queueItemId: string, text: string) {
    this.#assertActive()
    const index = this.#queue.findIndex((item) => item.id === queueItemId)
    if (index < 0) throw new HermesComposerInputError('Queued prompt was not found.')
    const item = this.#queue[index]
    if (item.intent.kind !== 'prompt' && item.intent.kind !== 'edit-resend') {
      throw new HermesComposerInputError('Retry and regenerate intents do not contain editable prompt text.')
    }
    const intent = item.intent.kind === 'prompt'
      ? immutableIntent({ kind: 'prompt', text })
      : immutableIntent({ kind: 'edit-resend', messageId: item.intent.messageId, text })
    this.#queue = this.#queue.map((candidate, candidateIndex) =>
      candidateIndex === index ? Object.freeze({ ...item, intent }) : candidate)
    this.#publish()
  }

  removeQueueItem(queueItemId: string) {
    this.#assertActive()
    const next = this.#queue.filter((item) => item.id !== queueItemId)
    if (next.length === this.#queue.length) return false
    this.#queue = next
    this.#publish()
    return true
  }

  moveQueueItem(queueItemId: string, destinationIndex: number) {
    this.#assertActive()
    if (!Number.isInteger(destinationIndex) || destinationIndex < 0 || destinationIndex >= this.#queue.length) {
      throw new HermesComposerInputError('Queue destination is outside the queue.')
    }
    const sourceIndex = this.#queue.findIndex((item) => item.id === queueItemId)
    if (sourceIndex < 0) throw new HermesComposerInputError('Queued prompt was not found.')
    if (sourceIndex === destinationIndex) return
    const next = [...this.#queue]
    const [item] = next.splice(sourceIndex, 1)
    next.splice(destinationIndex, 0, item)
    this.#queue = next
    this.#publish()
  }

  setConnection(state: HermesConnectionState) {
    this.#assertActive()
    if (!connectionStates.has(state)) throw new HermesComposerInputError('Connection state is invalid.')
    if (state === this.#connection) return
    this.#connection = state
    this.#publish()
  }

  setVoiceState(state: HermesVoiceState) {
    this.#assertActive()
    this.#voice = immutableVoice(state)
    this.#publish()
  }

  cancelSubmission() {
    this.#assertActive()
    if (!this.#abortController || this.#abortController.signal.aborted) return false
    this.#abortController.abort()
    return true
  }

  recover(historyEntryId: string) {
    this.#assertActive()
    const entry = this.#history.find((candidate) => candidate.id === historyEntryId)
    if (!entry) throw new HermesComposerInputError('Submission history entry was not found.')
    if (entry.outcome === 'succeeded') throw new HermesComposerInputError('Successful submissions cannot be recovered into the queue.')
    return this.#enqueue(entry.intent)
  }

  clearHistory() {
    this.#assertActive()
    if (this.#history.length === 0) return
    this.#history = []
    this.#publish()
  }

  async submitNext(): Promise<HermesSubmitNextResult> {
    this.#assertActive()
    if (this.#inFlight) return { status: 'busy' }
    if (this.#connection !== 'ready') return { status: 'unavailable' }
    const item = this.#queue[0]
    if (!item) return { status: 'empty' }
    if (this.#attemptedQueueIds.has(item.id)) throw new HermesComposerInputError('A queued prompt cannot be submitted more than once.')

    this.#attemptedQueueIds.add(item.id)
    this.#queue = this.#queue.slice(1)
    this.#inFlight = item
    this.#abortController = new AbortController()
    const activeAbort = this.#abortController
    const startedAt = this.#timestamp()
    this.#publish()

    let outcome: HermesSubmissionHistoryEntry['outcome'] = 'failed'
    let code: string | undefined
    try {
      const receipt = await this.#submit(item.intent, Object.freeze({ queueItemId: item.id, signal: activeAbort.signal }))
      if (activeAbort.signal.aborted) {
        outcome = 'cancelled'
      } else if (receipt && receipt.accepted === true) {
        outcome = 'succeeded'
      } else {
        outcome = 'failed'
        code = typeof receipt?.code === 'string' && safeCode.test(receipt.code) ? receipt.code : 'submission-rejected'
      }
    } catch {
      outcome = activeAbort.signal.aborted ? 'cancelled' : 'failed'
      if (outcome === 'failed') code = 'submission-failed'
    }

    const history = Object.freeze({
      id: this.#nextId('History ID'),
      queueItemId: item.id,
      intent: item.intent,
      startedAt,
      finishedAt: this.#timestamp(),
      outcome,
      ...(code ? { code } : {}),
    }) satisfies HermesSubmissionHistoryEntry
    this.#history = [...this.#history, history].slice(-this.#historyLimit)
    this.#attemptedQueueIds.clear()
    for (const retained of this.#history) this.#attemptedQueueIds.add(retained.queueItemId)
    if (this.#abortController === activeAbort) this.#abortController = null
    if (this.#inFlight?.id === item.id) this.#inFlight = null
    this.#publish()

    return outcome === 'succeeded'
      ? { status: 'submitted', history }
      : outcome === 'cancelled'
        ? { status: 'cancelled', history }
        : { status: 'failed', history }
  }

  dispose() {
    if (this.#disposed) return
    this.#disposed = true
    this.#abortController?.abort()
    this.#abortController = null
    this.#queue = []
    this.#history = []
    this.#inFlight = null
    this.#attemptedQueueIds.clear()
    this.#listeners.clear()
    this.#snapshot = this.#buildSnapshot()
  }

  #enqueue(intent: HermesSubmissionIntent) {
    this.#assertActive()
    if (this.#queue.length >= this.#queueLimit) {
      throw new HermesComposerInputError(`Prompt queue supports up to ${this.#queueLimit} items.`)
    }
    const item = Object.freeze({
      id: this.#nextId('Queue item ID'),
      intent: immutableIntent(intent),
      queuedAt: this.#timestamp(),
    }) satisfies HermesQueuedPrompt
    this.#queue = [...this.#queue, item]
    this.#publish()
    return item
  }

  #nextId(name: string) {
    for (let attempt = 0; attempt < 16; attempt += 1) {
      const id = boundedId(this.#createId(), name)
      const collision = this.#attemptedQueueIds.has(id)
        || this.#queue.some((item) => item.id === id)
        || this.#history.some((item) => item.id === id || item.queueItemId === id)
        || this.#inFlight?.id === id
      if (!collision) return id
    }
    throw new HermesComposerInputError(`${name} factory repeatedly returned a duplicate.`)
  }

  #timestamp() {
    const value = this.#now()
    if (!Number.isFinite(value) || value < 0) throw new HermesComposerInputError('Clock returned an invalid timestamp.')
    return value
  }

  #assertActive() {
    if (this.#disposed) throw new HermesComposerInputError('Composer controller is disposed.')
  }

  #buildSnapshot(): HermesComposerSnapshot {
    return Object.freeze({
      queue: Object.freeze([...this.#queue]),
      history: Object.freeze([...this.#history]),
      inFlight: this.#inFlight,
      connection: this.#connection,
      voice: this.#voice,
      revision: this.#revision,
    })
  }

  #publish() {
    this.#revision += 1
    this.#snapshot = this.#buildSnapshot()
    for (const listener of this.#listeners) listener()
  }
}
