export const HERMES_COMPOSER_LIMITS = Object.freeze({
  promptCharacters: 64 * 1024,
  queueItems: 20,
  historyItems: 50,
  catalogItems: 100,
  completionItems: 8,
  optionIdCharacters: 128,
  optionLabelCharacters: 80,
  descriptionCharacters: 240,
  messageIdCharacters: 256,
})

export type HermesConnectionState = 'ready' | 'reconnecting' | 'offline' | 'blocked'
export type HermesVoiceState =
  | { kind: 'idle' }
  | { kind: 'listening' }
  | { kind: 'processing' }
  | { kind: 'error'; message: string }

export type HermesSubmissionIntent =
  | { kind: 'prompt'; text: string }
  | { kind: 'edit-resend'; messageId: string; text: string }
  | { kind: 'retry'; messageId: string }
  | { kind: 'regenerate'; messageId: string }

export interface HermesQueuedPrompt {
  readonly id: string
  readonly intent: HermesSubmissionIntent
  readonly queuedAt: number
}

export type HermesSubmissionOutcome = 'succeeded' | 'failed' | 'cancelled'

export interface HermesSubmissionHistoryEntry {
  readonly id: string
  readonly queueItemId: string
  readonly intent: HermesSubmissionIntent
  readonly startedAt: number
  readonly finishedAt: number
  readonly outcome: HermesSubmissionOutcome
  readonly code?: string
}

export interface HermesComposerSnapshot {
  readonly queue: readonly HermesQueuedPrompt[]
  readonly history: readonly HermesSubmissionHistoryEntry[]
  readonly inFlight: HermesQueuedPrompt | null
  readonly connection: HermesConnectionState
  readonly voice: HermesVoiceState
  readonly revision: number
}

export interface HermesSubmissionReceipt {
  readonly accepted: boolean
  readonly code?: string
}

export type HermesSubmitCallback = (
  intent: Readonly<HermesSubmissionIntent>,
  context: Readonly<{ queueItemId: string; signal: AbortSignal }>,
) => Promise<HermesSubmissionReceipt> | HermesSubmissionReceipt

export interface HermesComposerControllerOptions {
  readonly submit: HermesSubmitCallback
  readonly queueLimit?: number
  readonly historyLimit?: number
  readonly createId?: () => string
  readonly now?: () => number
  readonly initialConnection?: HermesConnectionState
  readonly initialVoice?: HermesVoiceState
}

export type HermesSubmitNextResult =
  | { status: 'submitted'; history: HermesSubmissionHistoryEntry }
  | { status: 'failed'; history: HermesSubmissionHistoryEntry }
  | { status: 'cancelled'; history: HermesSubmissionHistoryEntry }
  | { status: 'empty' | 'busy' | 'unavailable' }

export interface HermesMentionOption {
  readonly id: string
  readonly label: string
  readonly description?: string
}

export interface HermesCommandOption {
  readonly id: string
  readonly name: string
  readonly description?: string
  readonly usage?: string
}

export interface HermesCompletionCatalogs {
  readonly mentions?: readonly HermesMentionOption[]
  readonly commands?: readonly HermesCommandOption[]
}

export type HermesCompletionOption =
  | { readonly kind: 'mention'; readonly id: string; readonly value: string; readonly description?: string }
  | { readonly kind: 'command'; readonly id: string; readonly value: string; readonly description?: string; readonly usage?: string }

export interface HermesCompletionSession {
  readonly kind: 'mention' | 'command'
  readonly trigger: '@' | '/'
  readonly query: string
  readonly replaceStart: number
  readonly replaceEnd: number
  readonly options: readonly HermesCompletionOption[]
  readonly selectedIndex: number
}

export interface HermesCompletionInsertion {
  readonly text: string
  readonly cursor: number
}

export interface HermesMessageReference {
  readonly messageId: string
  readonly text: string
  readonly label?: string
}

export type HermesVoiceAction = 'start' | 'stop' | 'cancel'

export class HermesComposerInputError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'HermesComposerInputError'
  }
}
