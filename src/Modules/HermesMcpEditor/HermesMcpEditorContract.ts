export const HERMES_MCP_EDITOR_CONTRACT_VERSION = 3 as const

export const HERMES_MCP_REVIEW_LIMITS = {
  args: 64,
  argument: 1_024,
  command: 1_024,
  environmentNames: 64,
  name: 128,
  url: 4_096,
} as const

export type HermesMcpEditorTransport = 'http' | 'stdio' | 'unknown'
export type HermesMcpEditorAuth = 'header' | 'oauth' | null
export type HermesMcpEditorRisk = 'none' | 'transport' | 'source' | 'command'

export type HermesMcpConfiguredServerSnapshot = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  serverId: string
  revision: string
  name: string
  transport: HermesMcpEditorTransport
  url: string
  command: string
  args: string[]
  environmentVariableNames: string[]
  auth: HermesMcpEditorAuth
  enabled: boolean
}

export type HermesMcpSafeEdit = Pick<
  HermesMcpConfiguredServerSnapshot,
  'transport' | 'url' | 'command' | 'args' | 'environmentVariableNames' | 'auth' | 'enabled'
>

export type HermesMcpEditDraft = HermesMcpSafeEdit

export type HermesMcpEditorReason =
  | 'ready'
  | 'validation-error'
  | 'unavailable'
  | 'stale-revision'
  | 'stale-review'
  | 'expired-review'
  | 'unknown-review'
  | 'secret-rebind-required'
  | 'risk-confirmation-required'
  | 'commit-failed'
  | 'committed'

export type HermesMcpEditorReviewRequest = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  serverId: string
  revision: string
  edit: HermesMcpSafeEdit
}

export type HermesMcpEditorReadyReview = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  status: 'ready'
  reason: 'ready'
  reviewHandle: string
  risk: HermesMcpEditorRisk
}

export type HermesMcpEditorReviewResult =
  | HermesMcpEditorReadyReview
  | {
      contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
      status: 'rejected' | 'unavailable'
      reason: Extract<HermesMcpEditorReason, 'validation-error' | 'unavailable' | 'stale-revision' | 'secret-rebind-required'>
    }

export type HermesMcpEditorCommitRequest = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  reviewHandle: string
  riskConfirmed: boolean
}

export type HermesMcpEditorCommitResult = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  status: 'success' | 'failure' | 'unavailable'
  reason: Exclude<HermesMcpEditorReason, 'ready' | 'validation-error'>
}

export type HermesMcpEditorDiscardRequest = {
  contractVersion: typeof HERMES_MCP_EDITOR_CONTRACT_VERSION
  reviewHandle: string
}

export type HermesMcpEditorController = {
  review(request: HermesMcpEditorReviewRequest): Promise<HermesMcpEditorReviewResult>
  commit(request: HermesMcpEditorCommitRequest): Promise<HermesMcpEditorCommitResult>
  discard(request: HermesMcpEditorDiscardRequest): Promise<void> | void
}

export type HermesMcpReviewChange = {
  field: string
  before: string
  after: string
  kind: 'added' | 'removed' | 'changed'
}

export type HermesMcpEditValidationIssue =
  | 'invalid-transport'
  | 'invalid-http-url'
  | 'invalid-command'
  | 'invalid-argument'
  | 'invalid-environment-name'
  | 'too-many-arguments'
  | 'too-many-environment-names'

const ENVIRONMENT_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/
const CONTROL_OR_DIRECTIONAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/u
const CONTROL_OR_DIRECTIONAL_GLOBAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/gu
const INLINE_CREDENTIAL_ARGUMENT = /(?:^|[\s"'=])(?:--?|\/)(?:api[-_]?key|token|secret|password|authorization|credential)(?:=|:|\s|$)|\bbearer\s+\S+/iu
const CREDENTIAL_ASSIGNMENT = /(?:^|[\s,;{])["']?(?:[A-Za-z][A-Za-z0-9_.-]{0,63}[._-])?(?:api[-_]?key|access[-_]?token|refresh[-_]?token|auth[-_]?token|token|secret|password|authorization|credential)["']?\s*[:=]\s*["']?\S+/iu
const HTTP_URL_IN_TEXT = /https?:\/\/[^\s"']+/giu

function containsInlineCredential(value: string) {
  if (INLINE_CREDENTIAL_ARGUMENT.test(value) || CREDENTIAL_ASSIGNMENT.test(value)) return true
  for (const candidate of value.match(HTTP_URL_IN_TEXT) ?? []) {
    try {
      const parsed = new URL(candidate.replace(/[).,;]+$/u, ''))
      if (parsed.username || parsed.password || parsed.search || parsed.hash) return true
    } catch {
      return true
    }
  }
  return false
}

function cleanLine(value: string, maximum: number) {
  return value.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').replace(/\s+/gu, ' ').trim().slice(0, maximum)
}

function uniqueSorted(values: string[]) {
  return [...new Set(values)].sort((left, right) => left.localeCompare(right))
}

export function normalizeHermesMcpEditDraft(snapshot: HermesMcpConfiguredServerSnapshot): HermesMcpEditDraft {
  return {
    transport: snapshot.transport,
    url: snapshot.url,
    command: snapshot.command,
    args: [...snapshot.args],
    environmentVariableNames: [...snapshot.environmentVariableNames],
    auth: snapshot.auth,
    enabled: snapshot.enabled,
  }
}

export function validateHermesMcpEditDraft(draft: HermesMcpEditDraft): HermesMcpEditValidationIssue[] {
  const issues = new Set<HermesMcpEditValidationIssue>()
  if (!['http', 'stdio', 'unknown'].includes(draft.transport)) issues.add('invalid-transport')
  if (draft.args.length > HERMES_MCP_REVIEW_LIMITS.args) issues.add('too-many-arguments')
  if (draft.environmentVariableNames.length > HERMES_MCP_REVIEW_LIMITS.environmentNames) {
    issues.add('too-many-environment-names')
  }
  if (draft.transport === 'http') {
    let parsed: URL | null = null
    try { parsed = new URL(draft.url) } catch { /* handled below */ }
    if (
      !parsed ||
      !['http:', 'https:'].includes(parsed.protocol) ||
      Boolean(parsed.username || parsed.password || parsed.search || parsed.hash) ||
      draft.url.length > HERMES_MCP_REVIEW_LIMITS.url
    ) {
      issues.add('invalid-http-url')
    }
  }
  if (draft.transport === 'stdio') {
    if (
      !draft.command.trim() ||
      draft.command.length > HERMES_MCP_REVIEW_LIMITS.command ||
      CONTROL_OR_DIRECTIONAL.test(draft.command) ||
      containsInlineCredential(draft.command)
    ) issues.add('invalid-command')
  }
  if (draft.args.some((argument) =>
    argument.length > HERMES_MCP_REVIEW_LIMITS.argument ||
    CONTROL_OR_DIRECTIONAL.test(argument) ||
    containsInlineCredential(argument))) {
    issues.add('invalid-argument')
  }
  if (draft.environmentVariableNames.some((name) => !ENVIRONMENT_NAME.test(name))) issues.add('invalid-environment-name')
  return [...issues]
}

export function canonicalHermesMcpEdit(edit: HermesMcpSafeEdit) {
  return JSON.stringify({
    transport: edit.transport,
    url: edit.url.trim(),
    command: edit.command.trim(),
    args: [...edit.args],
    environmentVariableNames: uniqueSorted(edit.environmentVariableNames),
    auth: edit.auth,
    enabled: edit.enabled,
  })
}

export function canonicalHermesMcpReviewBinding(request: HermesMcpEditorReviewRequest) {
  return JSON.stringify({
    contractVersion: request.contractVersion,
    serverId: request.serverId,
    revision: request.revision,
    edit: canonicalHermesMcpEdit(request.edit),
  })
}

function displayValue(value: string) {
  const safe = value.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').replace(/\s+/gu, ' ').trim()
  return safe || '(none)'
}

function displayList(values: string[]) {
  return displayValue(values.map((value) => cleanLine(value, HERMES_MCP_REVIEW_LIMITS.argument)).join(', '))
}

export function describeHermesMcpEditChanges(
  snapshot: HermesMcpConfiguredServerSnapshot,
  edit: HermesMcpEditDraft,
): HermesMcpReviewChange[] {
  const fields: Array<[string, string, string]> = [
    ['Transport', snapshot.transport, edit.transport],
    ['URL', snapshot.url, edit.url],
    ['Command', snapshot.command, edit.command],
    ['Arguments', displayList(snapshot.args), displayList(edit.args)],
    ['Environment names', displayList(snapshot.environmentVariableNames), displayList(edit.environmentVariableNames)],
    ['Authentication', snapshot.auth ?? '(none)', edit.auth ?? '(none)'],
    ['Enabled', String(snapshot.enabled), String(edit.enabled)],
  ]
  return fields.flatMap(([field, beforeValue, afterValue]) => {
    const before = displayValue(beforeValue)
    const after = displayValue(afterValue)
    if (before === after) return []
    return [{ field, before, after, kind: before === '(none)' ? 'added' : after === '(none)' ? 'removed' : 'changed' }]
  })
}

export function riskForHermesMcpEdit(
  snapshot: HermesMcpConfiguredServerSnapshot,
  edit: HermesMcpEditDraft,
): HermesMcpEditorRisk {
  if (snapshot.transport !== edit.transport) return 'transport'
  if (
    edit.transport === 'stdio' &&
    (snapshot.command !== edit.command ||
      JSON.stringify(snapshot.args) !== JSON.stringify(edit.args) ||
      JSON.stringify(uniqueSorted(snapshot.environmentVariableNames)) !== JSON.stringify(uniqueSorted(edit.environmentVariableNames)))
  ) {
    return 'command'
  }
  if (snapshot.url !== edit.url || snapshot.auth !== edit.auth) return 'source'
  return 'none'
}

export function isOpaqueHermesMcpReviewHandle(value: string) {
  return /^mcp-review:[A-Za-z0-9_-]{32,128}$/u.test(value)
}

export function hermesMcpEditorReasonText(reason: HermesMcpEditorReason) {
  const messages: Record<HermesMcpEditorReason, string> = {
    ready: 'Review prepared. Confirm the risk summary before committing.',
    'validation-error': 'The proposed configuration is invalid. Review the highlighted fields and prepare a new review.',
    unavailable: 'The secure MCP editor is unavailable. No changes were made.',
    'stale-revision': 'The configured server changed. Refresh it and prepare a new review.',
    'stale-review': 'This review was replaced by a newer review. Prepare it again.',
    'expired-review': 'This review expired. Prepare it again.',
    'unknown-review': 'This review is no longer available. Prepare it again.',
    'secret-rebind-required': 'This change needs a credential rebind in the native Connections vault. No changes were made.',
    'risk-confirmation-required': 'Risk confirmation was required. No changes were made.',
    'commit-failed': 'The secure commit failed. No success was recorded.',
    committed: 'The MCP server configuration was updated.',
  }
  return messages[reason]
}
