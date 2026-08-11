export const SOURCE_CONTROL_PROTOCOL_VERSION = 1
export const MAXIMUM_SOURCE_CONTROL_FRAME_CHARACTERS = 2 * 1024 * 1024
export const MAXIMUM_STATUS_ENTRIES = 10_000

export type AvailabilityState = 'available' | 'unavailable' | 'error'

export type SourceControlAvailability = {
  state: AvailabilityState
  code?: string
  message?: string
  version?: string
}

export type SourceControlDescription = {
  protocolVersion: 1
  git: SourceControlAvailability
  gitExtensions: SourceControlAvailability
  operations: string[]
  gitExtensionsSurfaces: string[]
}

export type SourceControlError = { code: string; message: string; retryable: boolean }

export type RepositoryResolution = {
  protocolVersion: 1
  requestId: string
  succeeded: boolean
  repositoryId?: string
  displayName?: string
  error?: SourceControlError
}

export type GitChangeKind =
  | 'modified' | 'added' | 'deleted' | 'renamed' | 'copied'
  | 'typeChanged' | 'untracked' | 'conflicted' | 'submodule'

export type GitChangeEntry = {
  path: string
  originalPath?: string
  kind: GitChangeKind
  staged: boolean
  unstaged: boolean
  untracked: boolean
  conflicted: boolean
  deleted: boolean
  renamed: boolean
  submodule: boolean
}

export type GitStatusGroups = {
  staged: GitChangeEntry[]
  unstaged: GitChangeEntry[]
  untracked: GitChangeEntry[]
  conflicted: GitChangeEntry[]
  renamed: GitChangeEntry[]
  deleted: GitChangeEntry[]
  submodules: GitChangeEntry[]
}

export type GitStatusSnapshot = {
  protocolVersion: 1
  requestId: string
  repositoryId: string
  displayName: string
  branch: {
    head?: string
    upstream?: string
    ahead: number
    behind: number
    stashCount: number
    detached: boolean
    unborn: boolean
  }
  groups: GitStatusGroups
  entryCount: number
  truncated: boolean
  observedAtUtc?: string
}

export type GitStatusResult = { succeeded: boolean; snapshot?: GitStatusSnapshot; error?: SourceControlError }
export type GitExtensionsOpenResult = { succeeded: boolean; error?: SourceControlError }

export type SourceControlFrame =
  | { type: 'description'; requestId: string; value: SourceControlDescription }
  | { type: 'repository'; requestId: string; value: RepositoryResolution }
  | { type: 'status'; requestId: string; value: GitStatusResult }
  | { type: 'gitExtensions'; requestId: string; value: GitExtensionsOpenResult }
  | { type: 'error'; requestId: string; value: SourceControlError }

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function boundedText(value: unknown, maximum = 4_096) {
  return typeof value === 'string' && value.length <= maximum ? value : ''
}

function optionalText(value: unknown, maximum = 4_096) {
  const normalized = boundedText(value, maximum)
  return normalized || undefined
}

function nonnegativeInteger(value: unknown) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : 0
}

function stringArray(value: unknown, maximum: number, maximumItem = 128) {
  return Array.isArray(value)
    ? value.slice(0, maximum).map((item) => boundedText(item, maximumItem)).filter(Boolean)
    : []
}

function normalizeAvailability(value: unknown): SourceControlAvailability {
  const raw = record(value)
  const state: AvailabilityState = raw?.available === true
    ? 'available'
    : raw?.state === 'available' || raw?.state === 'error'
    ? raw.state
    : 'unavailable'
  return {
    state,
    code: optionalText(raw?.code, 128),
    message: optionalText(raw?.message, 1_024),
    version: optionalText(raw?.version, 128),
  }
}

function normalizeError(value: unknown): SourceControlError | undefined {
  const raw = record(value)
  const code = boundedText(raw?.code, 128)
  const message = boundedText(raw?.message, 1_024)
  return raw && code && message ? { code, message, retryable: raw.retryable === true } : undefined
}

const kinds = new Set<GitChangeKind>([
  'modified', 'added', 'deleted', 'renamed', 'copied',
  'typeChanged', 'untracked', 'conflicted', 'submodule',
])

function normalizeEntry(value: unknown): GitChangeEntry | null {
  const raw = record(value)
  const path = boundedText(raw?.path, 32 * 1024)
  const kind = boundedText(raw?.kind, 32) as GitChangeKind
  if (!raw || !path || !kinds.has(kind)) return null
  return {
    path,
    originalPath: optionalText(raw.originalPath, 32 * 1024),
    kind,
    staged: raw.staged === true,
    unstaged: raw.unstaged === true,
    untracked: raw.untracked === true,
    conflicted: raw.conflicted === true,
    deleted: raw.deleted === true,
    renamed: raw.renamed === true,
    submodule: raw.submodule === true,
  }
}

function normalizeEntries(value: unknown) {
  return Array.isArray(value)
    ? value.slice(0, MAXIMUM_STATUS_ENTRIES).map(normalizeEntry).filter((item): item is GitChangeEntry => item !== null)
    : []
}

function normalizeStatus(value: unknown, requestId: string): GitStatusResult | null {
  const raw = record(value)
  if (!raw) return null
  const error = normalizeError(raw.error)
  if (raw.succeeded !== true) return { succeeded: false, error }
  const snapshot = record(raw.snapshot)
  const branch = record(snapshot?.branch)
  const groups = record(snapshot?.groups)
  const repositoryId = boundedText(snapshot?.repositoryId, 128)
  if (!snapshot || !branch || !groups || !repositoryId) return null
  const entryGroups: GitStatusGroups = {
    staged: normalizeEntries(groups.staged),
    unstaged: normalizeEntries(groups.unstaged),
    untracked: normalizeEntries(groups.untracked),
    conflicted: normalizeEntries(groups.conflicted),
    renamed: normalizeEntries(groups.renamed),
    deleted: normalizeEntries(groups.deleted),
    submodules: normalizeEntries(groups.submodules),
  }
  const uniqueEntries = new Set(Object.values(entryGroups).flat().map((entry) => `${entry.path}\0${entry.originalPath ?? ''}`))
  if (uniqueEntries.size > MAXIMUM_STATUS_ENTRIES) return null
  return {
    succeeded: true,
    snapshot: {
      protocolVersion: SOURCE_CONTROL_PROTOCOL_VERSION,
      requestId,
      repositoryId,
      displayName: boundedText(snapshot.displayName, 512) || 'Repository',
      branch: {
        head: optionalText(branch.head, 4_096),
        upstream: optionalText(branch.upstream, 4_096),
        ahead: nonnegativeInteger(branch.ahead),
        behind: nonnegativeInteger(branch.behind),
        stashCount: nonnegativeInteger(branch.stashCount),
        detached: branch.detached === true,
        unborn: branch.unborn === true,
      },
      groups: entryGroups,
      entryCount: Math.min(nonnegativeInteger(snapshot.entryCount), MAXIMUM_STATUS_ENTRIES),
      truncated: snapshot.truncated === true,
      observedAtUtc: optionalText(snapshot.observedAtUtc, 128),
    },
  }
}

function withinFrameBound(value: unknown) {
  try {
    return JSON.stringify(value).length <= MAXIMUM_SOURCE_CONTROL_FRAME_CHARACTERS
  } catch {
    return false
  }
}

export function normalizeSourceControlFrame(value: unknown): SourceControlFrame | null {
  if (!withinFrameBound(value)) return null
  const raw = record(value)
  if (!raw || raw.version !== SOURCE_CONTROL_PROTOCOL_VERSION) return null
  const requestId = boundedText(raw.requestId, 128)
  if (!requestId || !/^[A-Za-z0-9_:-]+$/.test(requestId)) return null
  switch (raw.type) {
    case 'sourceControl.describe.result': {
      const description = record(raw.value) ?? raw
      return {
        type: 'description', requestId, value: {
          protocolVersion: SOURCE_CONTROL_PROTOCOL_VERSION,
          git: normalizeAvailability(description.git),
          gitExtensions: normalizeAvailability(description.gitExtensions),
          operations: stringArray(description.operations, 32),
          gitExtensionsSurfaces: stringArray(description.gitExtensionsSurfaces, 16),
        },
      }
    }
    case 'sourceControl.repository.resolve.result': {
      const result = record(raw.value) ?? raw
      const repository = record(result.repository) ?? result
      const succeeded = result.succeeded === true
      const repositoryId = optionalText(repository.repositoryId, 128)
      if (succeeded && !repositoryId) return null
      return {
        type: 'repository', requestId, value: {
          protocolVersion: SOURCE_CONTROL_PROTOCOL_VERSION,
          requestId,
          succeeded,
          repositoryId,
          displayName: optionalText(repository.displayName, 512),
          error: normalizeError(result.error),
        },
      }
    }
    case 'sourceControl.status.result': {
      const result = normalizeStatus(raw.value ?? raw, requestId)
      return result ? { type: 'status', requestId, value: result } : null
    }
    case 'sourceControl.gitExtensions.result': {
      const result = record(raw.value) ?? raw
      return {
        type: 'gitExtensions', requestId, value: {
          succeeded: result.started === true || result.succeeded === true,
          error: normalizeError(result.error),
        },
      }
    }
    case 'sourceControl.error': {
      const error = normalizeError(raw)
      return error ? { type: 'error', requestId, value: error } : null
    }
    default: return null
  }
}
