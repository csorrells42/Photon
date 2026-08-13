import type { HermesChatMessage } from '../HermesGateway/useHermesChat'
import type { HermesApprovalRequest } from '../HermesGateway/HermesRuntimeAdapter'

export const HERMES_CODEX_APPROVAL_REVIEW_EVENT = 'hermes-codex-approval-review'
export const HERMES_CODEX_APPROVAL_REVIEW_STATUS_EVENT = 'hermes-codex-approval-review-status'

export type CodexApprovalReviewStatus = 'sent' | 'prepared' | 'failed'

export type CodexApprovalReviewRequest = {
  requestId: string
  prompt: string
}

export type CodexApprovalReviewStatusEvent = {
  requestId: string
  status: CodexApprovalReviewStatus
  message: string
}

export function codexApprovalReviewId(request: Pick<HermesApprovalRequest, 'requestId' | 'command'>) {
  return request.requestId || `command:${request.command}`
}

function boundedConversation(messages: readonly HermesChatMessage[]) {
  return messages
    .filter((message) => !message.interim && message.body.trim())
    .slice(-6)
    .map((message) => `${message.author === 'you' ? 'Chris' : 'Photon'}: ${message.body.trim()}`)
    .join('\n\n')
    .slice(-4_000)
}

export function buildCodexApprovalReviewPrompt(
  request: HermesApprovalRequest,
  messages: readonly HermesChatMessage[],
) {
  const conversation = boundedConversation(messages)
  return [
    'Photon->Codex',
    'Review this pending Photon command approval. Do not execute the command and do not modify files.',
    'Explain in plain English: (1) exactly what it would do, (2) why Photon appears to want it, (3) what could be damaged, and (4) the safer alternative.',
    'If the supplied evidence does not establish Photon\'s intent, say that clearly instead of guessing.',
    '',
    `Approval detector: ${request.description}`,
    `Photon-provided reason: ${request.reason ?? 'No reason was supplied.'}`,
    'Command:',
    request.command || '(No command text was supplied.)',
    conversation ? `\nRecent visible conversation:\n${conversation}` : '\nNo recent visible conversation was available.',
  ].join('\n')
}

export function sendApprovalReviewToCodex(
  request: HermesApprovalRequest,
  messages: readonly HermesChatMessage[],
) {
  const detail: CodexApprovalReviewRequest = {
    requestId: codexApprovalReviewId(request),
    prompt: buildCodexApprovalReviewPrompt(request, messages),
  }
  window.dispatchEvent(new Event('hermes-show-codex'))
  window.dispatchEvent(new CustomEvent(HERMES_CODEX_APPROVAL_REVIEW_EVENT, { detail }))
}

export function readCodexApprovalReviewRequest(event: Event): CodexApprovalReviewRequest | null {
  if (!(event instanceof CustomEvent) || !event.detail || typeof event.detail !== 'object') return null
  const detail = event.detail as Partial<CodexApprovalReviewRequest>
  return typeof detail.requestId === 'string' && typeof detail.prompt === 'string' && detail.prompt.trim()
    ? { requestId: detail.requestId, prompt: detail.prompt.trim() }
    : null
}

export function publishCodexApprovalReviewStatus(detail: CodexApprovalReviewStatusEvent) {
  window.dispatchEvent(new CustomEvent(HERMES_CODEX_APPROVAL_REVIEW_STATUS_EVENT, { detail }))
}

export function readCodexApprovalReviewStatus(event: Event): CodexApprovalReviewStatusEvent | null {
  if (!(event instanceof CustomEvent) || !event.detail || typeof event.detail !== 'object') return null
  const detail = event.detail as Partial<CodexApprovalReviewStatusEvent>
  if (typeof detail.requestId !== 'string' || typeof detail.message !== 'string') return null
  if (detail.status !== 'sent' && detail.status !== 'prepared' && detail.status !== 'failed') return null
  return { requestId: detail.requestId, status: detail.status, message: detail.message }
}
