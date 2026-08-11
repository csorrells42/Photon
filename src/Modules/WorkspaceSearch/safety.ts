import {
  WORKSPACE_SEARCH_LIMITS,
  WORKSPACE_SEARCH_PROTOCOL_VERSION,
  type WorkspaceSearchMatchRange,
  type WorkspaceSearchMode,
  type WorkspaceSearchProviderProgress,
  type WorkspaceSearchResult,
} from './contracts'

const CONTROL_OR_DIRECTIONAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/u
const CONTROL_OR_DIRECTIONAL_GLOBAL = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u2069\ufeff]/gu
const BLOCKED_SEGMENTS = new Set([
  '.git', '.cache', '.next', '.nuxt', '.pnpm-store', '.yarn', 'artifacts', 'bin',
  'build', 'cache', 'caches', 'coverage', 'data', 'debug', 'dist', 'logs',
  'node_modules', 'obj', 'out', 'release', 'runtime', 'target', 'temp', 'tmp', 'vault',
])
const BLOCKED_FILE_NAMES = new Set([
  'auth.json', 'credentials.json', 'secrets.json', 'id_dsa', 'id_ecdsa', 'id_ed25519', 'id_rsa',
])
const BLOCKED_EXTENSIONS = new Set([
  '.cer', '.cert', '.crt', '.der', '.jks', '.key', '.keystore', '.p12', '.pem', '.pfx', '.pkcs12', '.pub',
])
const WINDOWS_INVALID_SEGMENT = /[<>:"|?*]/u
const WINDOWS_RESERVED_NAME = /^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/iu

type RecordValue = Record<string, unknown>
export type NormalizedWorkspaceSearchOutput = {
  results: WorkspaceSearchResult[]
  truncated: boolean
}

function record(value: unknown): RecordValue | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as RecordValue : null
}

function boundedInteger(value: unknown, minimum: number, maximum: number) {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum ? value : null
}

function compareOrdinal(left: string, right: string) {
  return left < right ? -1 : left > right ? 1 : 0
}

export function normalizeWorkspaceSearchQuery(value: string) {
  return value.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').slice(0, WORKSPACE_SEARCH_LIMITS.queryCharacters)
}

export function normalizeWorkspaceSearchMessage(value: string) {
  return value.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').slice(0, WORKSPACE_SEARCH_LIMITS.statusMessageCharacters)
}

export function isSafeWorkspaceSearchPath(value: unknown): value is string {
  if (typeof value !== 'string' || !value || value.length > WORKSPACE_SEARCH_LIMITS.pathCharacters) return false
  if (CONTROL_OR_DIRECTIONAL.test(value) || value.includes('%')) return false
  if (/^(?:[a-z]:|[\\/]{1,2})/iu.test(value)) return false
  const segments = value.replaceAll('\\', '/').split('/')
  if (segments.some((segment) => !segment || segment === '.' || segment === '..')) return false
  return segments.every((segment) => {
    const canonical = segment.toLowerCase()
    const dot = canonical.lastIndexOf('.')
    const extension = dot >= 0 ? canonical.slice(dot) : ''
    return !WINDOWS_INVALID_SEGMENT.test(segment)
      && !/[. ]$/u.test(segment)
      && !WINDOWS_RESERVED_NAME.test(canonical)
      && !BLOCKED_SEGMENTS.has(canonical)
      && !BLOCKED_FILE_NAMES.has(canonical)
      && canonical !== '.env'
      && !canonical.startsWith('.env.')
      && !BLOCKED_EXTENSIONS.has(extension)
  })
}

function normalizeRanges(value: unknown, previewLength: number): WorkspaceSearchMatchRange[] | null {
  if (!Array.isArray(value) || value.length > WORKSPACE_SEARCH_LIMITS.matchRanges) return null
  const ranges = value.map((item) => {
    const raw = record(item)
    const start = boundedInteger(raw?.start, 0, previewLength)
    const end = boundedInteger(raw?.end, 0, previewLength)
    return raw && start !== null && end !== null && start < end ? { start, end } : null
  })
  if (ranges.some((range) => range === null)) return null
  return (ranges as WorkspaceSearchMatchRange[])
    .sort((left, right) => left.start - right.start || left.end - right.end)
    .filter((range, index, sorted) => index === 0 || range.start >= sorted[index - 1].end)
}

function normalizeResult(value: unknown): Omit<WorkspaceSearchResult, 'id'> | null {
  const raw = record(value)
  if (!raw || raw.pathKind !== 'regular-file' || !isSafeWorkspaceSearchPath(raw.path)) return null
  const line = boundedInteger(raw.line, 1, Number.MAX_SAFE_INTEGER)
  const column = boundedInteger(raw.column, 1, Number.MAX_SAFE_INTEGER)
  if (line === null || column === null || typeof raw.preview !== 'string') return null
  const preview = raw.preview.replace(CONTROL_OR_DIRECTIONAL_GLOBAL, ' ').slice(0, WORKSPACE_SEARCH_LIMITS.previewCharacters)
  const matches = normalizeRanges(raw.matches, preview.length)
  return matches ? { path: raw.path.replaceAll('\\', '/'), pathKind: 'regular-file', line, column, preview, matches } : null
}

export function normalizeWorkspaceSearchProgress(value: unknown): WorkspaceSearchProviderProgress | null {
  const raw = record(value)
  const completedFiles = boundedInteger(raw?.completedFiles, 0, WORKSPACE_SEARCH_LIMITS.progressFiles)
  if (completedFiles === null) return null
  const totalFiles = raw?.totalFiles === undefined ? undefined : boundedInteger(raw.totalFiles, completedFiles, WORKSPACE_SEARCH_LIMITS.progressFiles)
  return raw?.totalFiles !== undefined && totalFiles === null ? null : { completedFiles, totalFiles: totalFiles ?? undefined }
}

export function normalizeWorkspaceSearchOutput(value: unknown, requestId: string, mode: WorkspaceSearchMode): NormalizedWorkspaceSearchOutput | null {
  const raw = record(value)
  if (!raw || raw.protocolVersion !== WORKSPACE_SEARCH_PROTOCOL_VERSION || raw.requestId !== requestId
    || !Array.isArray(raw.results) || raw.results.length > WORKSPACE_SEARCH_LIMITS.providerResults
    || typeof raw.truncated !== 'boolean') return null

  const normalized = raw.results.map(normalizeResult)
  const valid = normalized.filter((result): result is NonNullable<typeof result> => result !== null)
  if (mode === 'literal') {
    valid.sort((left, right) => {
      const leftCanonical = left.path.toLowerCase()
      const rightCanonical = right.path.toLowerCase()
      return compareOrdinal(leftCanonical, rightCanonical) || compareOrdinal(left.path, right.path)
        || left.line - right.line || left.column - right.column || compareOrdinal(left.preview, right.preview)
    })
  }

  const perFile = new Map<string, number>()
  const keys = new Set<string>()
  const results: WorkspaceSearchResult[] = []
  let omitted = normalized.length !== valid.length
  for (const result of valid) {
    const pathKey = result.path.toLowerCase()
    const count = perFile.get(pathKey) ?? 0
    const duplicateKey = `${pathKey}\0${result.line}\0${result.column}\0${result.preview}`
    if (count >= WORKSPACE_SEARCH_LIMITS.resultsPerFile || keys.has(duplicateKey)) { omitted = true; continue }
    if (results.length >= WORKSPACE_SEARCH_LIMITS.results) { omitted = true; break }
    perFile.set(pathKey, count + 1)
    keys.add(duplicateKey)
    results.push({ ...result, id: `workspace-search-result-${results.length + 1}` })
  }

  return {
    results,
    truncated: raw.truncated || omitted,
  }
}
