import type { HermesGatewayEvent } from './HermesGatewayClient'

export const HERMES_RUNTIME_ADAPTER_VERSION = 1

export type HermesToolPhase = 'running' | 'complete' | 'error'

export type HermesToolRun = {
  id: string
  name: string
  phase: HermesToolPhase
  context?: string
  input?: string
  output?: string
  durationSeconds?: number
  inlineDiff?: string
}

export type HermesApprovalChoice = 'once' | 'session' | 'always' | 'deny'

export type HermesApprovalRequest = {
  requestId: string
  command: string
  description: string
  reason: string | null
  choices: HermesApprovalChoice[]
  allowPermanent: boolean
  smartDenied: boolean
  sessionId: string | null
}

export type HermesInteractivePrompt =
  | {
      kind: 'clarify'
      requestId: string
      sessionId: string | null
      question: string
      choices: string[]
    }
  | {
      kind: 'sudo'
      requestId: string
      sessionId: string | null
    }
  | {
      kind: 'secret'
      requestId: string
      sessionId: string | null
      envVar: string
      prompt: string
    }

const TOOL_EVENTS = new Set(['tool.start', 'tool.progress', 'tool.complete'])
const APPROVAL_CHOICES = new Set<HermesApprovalChoice>(['once', 'session', 'always', 'deny'])
const MAX_TOOL_RUNS = 30
export const HERMES_TOOL_DETAIL_LIMIT = 8_000
export const HERMES_TOOL_TRUNCATION_SUFFIX = '\n… output truncated'

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function compactValue(value: unknown): string | undefined {
  if (value === undefined || value === null || value === '') return undefined

  let text: string
  if (typeof value === 'string') text = value
  else {
    try { text = JSON.stringify(value, null, 2) }
    catch { text = String(value) }
  }

  text = text.trim()
  if (!text) return undefined
  return text.length > HERMES_TOOL_DETAIL_LIMIT
    ? `${text.slice(0, HERMES_TOOL_DETAIL_LIMIT)}${HERMES_TOOL_TRUNCATION_SUFFIX}`
    : text
}

function toolId(event: HermesGatewayEvent) {
  return nonEmptyString(event.payload?.tool_id)
    ?? nonEmptyString(event.payload?.tool_call_id)
    ?? nonEmptyString(event.payload?.id)
}

function toolName(event: HermesGatewayEvent) {
  return nonEmptyString(event.payload?.name)
    ?? nonEmptyString(event.payload?.tool)
    ?? 'tool'
}

function matchingToolIndex(tools: HermesToolRun[], event: HermesGatewayEvent) {
  const explicitId = toolId(event)
  if (explicitId) return tools.findIndex((tool) => tool.id === explicitId)

  const name = toolName(event)
  for (let index = tools.length - 1; index >= 0; index -= 1) {
    if (tools[index].name === name && tools[index].phase === 'running') return index
  }
  return -1
}

export function mergeHermesToolEvent(
  current: HermesToolRun[],
  event: HermesGatewayEvent,
  fallbackId = `tool-${Date.now()}`,
): HermesToolRun[] {
  if (!TOOL_EVENTS.has(event.type)) return current

  const payload = event.payload ?? {}
  const existingIndex = matchingToolIndex(current, event)
  const existing = existingIndex >= 0 ? current[existingIndex] : undefined
  const hasError = payload.error === true || Boolean(nonEmptyString(payload.error))
  const phase: HermesToolPhase = event.type === 'tool.complete'
    ? hasError ? 'error' : 'complete'
    : 'running'

  const duration = typeof payload.duration_s === 'number' && Number.isFinite(payload.duration_s)
    ? payload.duration_s
    : existing?.durationSeconds

  const next: HermesToolRun = {
    id: toolId(event) ?? existing?.id ?? fallbackId,
    name: toolName(event) || existing?.name || 'tool',
    phase,
    context: nonEmptyString(payload.context)
      ?? nonEmptyString(payload.summary)
      ?? nonEmptyString(payload.status)
      ?? existing?.context,
    input: compactValue(payload.args ?? payload.arguments ?? payload.input) ?? existing?.input,
    output: compactValue(payload.result ?? (hasError ? payload.error : undefined)) ?? existing?.output,
    durationSeconds: duration,
    inlineDiff: compactValue(payload.inline_diff) ?? existing?.inlineDiff,
  }

  const copy = [...current]
  if (existingIndex >= 0) copy[existingIndex] = next
  else copy.push(next)
  return copy.slice(-MAX_TOOL_RUNS)
}

export function normalizeHermesApproval(
  event: HermesGatewayEvent,
  activeSessionId: string | null,
): HermesApprovalRequest | null {
  if (event.type !== 'approval.request') return null

  const payload = event.payload ?? {}
  const smartDenied = payload.smart_denied === true
  const allowPermanent = payload.allow_permanent !== false
  const suppliedChoices = Array.isArray(payload.choices)
    ? payload.choices.filter((choice): choice is HermesApprovalChoice =>
        typeof choice === 'string' && APPROVAL_CHOICES.has(choice as HermesApprovalChoice))
    : []

  const defaults: HermesApprovalChoice[] = smartDenied
    ? ['once', 'deny']
    : ['once', 'session', 'always', 'deny']

  const choices = (suppliedChoices.length ? suppliedChoices : defaults)
    .filter((choice) => allowPermanent || choice !== 'always')

  return {
    requestId: nonEmptyString(payload.request_id) ?? '',
    command: nonEmptyString(payload.command) ?? '',
    description: nonEmptyString(payload.description) ?? 'Hermes needs approval to run a protected command.',
    reason: nonEmptyString(payload.reason)
      ?? nonEmptyString(payload.purpose)
      ?? nonEmptyString(payload.intent)
      ?? null,
    choices,
    allowPermanent,
    smartDenied,
    sessionId: event.session_id ?? activeSessionId,
  }
}

export function normalizeHermesInteractivePrompt(
  event: HermesGatewayEvent,
  activeSessionId: string | null,
): HermesInteractivePrompt | null {
  const payload = event.payload ?? {}
  const requestId = nonEmptyString(payload.request_id)
  const sessionId = event.session_id ?? activeSessionId

  if (event.type === 'clarify.request') {
    const question = nonEmptyString(payload.question)
    if (!requestId || !question) return null
    const choices = Array.isArray(payload.choices)
      ? payload.choices
          .filter((choice): choice is string => typeof choice === 'string')
          .map((choice) => choice.trim())
          .filter(Boolean)
      : []
    return { kind: 'clarify', requestId, sessionId, question, choices }
  }

  if (event.type === 'sudo.request') {
    return requestId ? { kind: 'sudo', requestId, sessionId } : null
  }

  if (event.type === 'secret.request') {
    if (!requestId) return null
    return {
      kind: 'secret',
      requestId,
      sessionId,
      envVar: nonEmptyString(payload.env_var) ?? 'Secret value',
      prompt: nonEmptyString(payload.prompt) ?? 'Enter the secret requested by Hermes.',
    }
  }

  return null
}
