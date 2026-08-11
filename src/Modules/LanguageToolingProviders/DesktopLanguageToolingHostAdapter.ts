import type { LanguageToolingHostAdapter, LanguageToolingHostRequest } from './contracts'

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

type DesktopWindow = Window & {
  __HERMES_DESKTOP_HOST__?: { capabilities?: { languageTooling?: boolean; languageToolingVersion?: number } }
  chrome?: { webview?: WebViewBridge }
}

export type DesktopLanguageToolingResult = {
  succeeded: boolean
  code: string
  message: string
  sessionId?: string
  diagnostics: readonly {
    filePath: string
    severity: 'info' | 'warning' | 'error'
    code: string
    message: string
    startLine: number
    startColumn: number
    endLine: number
    endColumn: number
  }[]
  artifacts: readonly string[]
}

export type DesktopLanguageToolingPublication = {
  requestId: string
  revision: number
  providerId: 'gcc' | 'arduino'
  targetPath: string
  result: DesktopLanguageToolingResult
}

const identifier = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
function isSafeRelativePath(value: string) {
  if (!value || value.length > 2_048 || value !== value.trim() || value.includes('\\')
    || value.startsWith('/') || /^[A-Za-z]:/.test(value) || /[\u0000-\u001f\u007f<>:"|?*]/.test(value)) return false
  const segments = value.split('/')
  return segments.every((segment) => segment && segment !== '.' && segment !== '..'
    && !segment.endsWith('.') && !segment.endsWith(' '))
}

export class DesktopLanguageToolingHostAdapter implements LanguageToolingHostAdapter {
  available() {
    if (typeof window === 'undefined') return false
    const desktop = window as DesktopWindow
    return desktop.__HERMES_DESKTOP_HOST__?.capabilities?.languageTooling === true
      && desktop.__HERMES_DESKTOP_HOST__.capabilities.languageToolingVersion === 1
      && Boolean(desktop.chrome?.webview)
  }

  request(request: LanguageToolingHostRequest, signal: AbortSignal): Promise<DesktopLanguageToolingResult> {
    const bridge = this.bridge()
    if (!bridge) return Promise.resolve(failure('native-unavailable', 'The trusted language-tooling host is unavailable.'))
    if (signal.aborted) return Promise.resolve(failure('cancelled', 'The language-tooling request was cancelled.'))
    const wire = this.toWire(request)
    return new Promise((resolve) => {
      let settled = false
      const finish = (value: DesktopLanguageToolingResult) => {
        if (settled) return
        settled = true
        window.clearTimeout(timeout)
        signal.removeEventListener('abort', abort)
        bridge.removeEventListener('message', receive)
        resolve(value)
      }
      const abort = () => {
        bridge.postMessage({ type: 'developerServices.languageTooling.cancel', version: 1, requestId: requestId('cancel'), targetRequestId: request.requestId })
        finish(failure('cancelled', 'The language-tooling request was cancelled.'))
      }
      const receive = (event: MessageEvent) => {
        const raw = event.data as Record<string, unknown>
        if (raw?.type !== 'developerServices.languageTooling.result' || raw.version !== 1 || raw.requestId !== request.requestId) return
        finish(normalizeResult(raw))
      }
      const timeout = window.setTimeout(() => abort(), 6 * 60_000)
      signal.addEventListener('abort', abort, { once: true })
      bridge.addEventListener('message', receive)
      try { bridge.postMessage(wire) }
      catch { finish(failure('native-unavailable', 'The trusted language-tooling host is unavailable.')) }
    })
  }

  private bridge() {
    if (typeof window === 'undefined') return null
    const desktop = window as DesktopWindow
    return this.available() ? desktop.chrome?.webview ?? null : null
  }

  private toWire(request: LanguageToolingHostRequest) {
    const common = { version: 1, requestId: request.requestId, providerId: request.providerId }
    if (request.operation === 'inspect-provider') return { type: 'developerServices.languageTooling.inspect', ...common }
    if (request.operation === 'start-language-session') return { type: 'developerServices.languageTooling.session.start', ...common, documentPath: request.documentPath }
    if (request.operation === 'stop-language-session') return { type: 'developerServices.languageTooling.session.stop', ...common, sessionId: request.sessionId }
    if (request.operation === 'inspect-project') return { type: 'developerServices.languageTooling.project.inspect', ...common, projectPath: request.projectPath }
    if (request.operation === 'compile') return { type: 'developerServices.languageTooling.compile', ...common, targetPath: request.targetPath, mode: request.mode, ...(request.boardFqbn ? { boardFqbn: request.boardFqbn } : {}) }
    if (request.operation === 'run-tests') return { type: 'developerServices.languageTooling.tests', ...common, targetPath: request.targetPath, ...(request.selection ? { selection: request.selection } : {}) }
    throw new Error('This desktop build does not expose that language-tooling operation.')
  }
}

export function createLanguageToolingRequestId(prefix = 'tooling') {
  return requestId(prefix)
}

function requestId(prefix: string) {
  const suffix = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  return `${prefix}-${suffix}`
}

function normalizeResult(raw: Record<string, unknown>): DesktopLanguageToolingResult {
  const code = safeText(raw.code, 96, 'request-failed')
  const message = safeText(raw.message, 512, 'The language-tooling request failed.')
  if (raw.succeeded !== true || !raw.result || typeof raw.result !== 'object' || Array.isArray(raw.result))
    return failure(code, message)
  const result = raw.result as Record<string, unknown>
  const diagnostics = Array.isArray(result.diagnostics) ? result.diagnostics.slice(0, 2_000).map(normalizeDiagnostic).filter((item): item is NonNullable<typeof item> => item !== null) : []
  const artifacts = Array.isArray(result.artifacts) ? result.artifacts.slice(0, 128).filter((item): item is string => typeof item === 'string' && isSafeRelativePath(item)) : []
  const sessionId = typeof result.sessionId === 'string' && identifier.test(result.sessionId) ? result.sessionId : undefined
  return { succeeded: result.succeeded === true, code: safeText(result.code, 96, code), message: safeText(result.message, 512, message), diagnostics, artifacts, ...(sessionId ? { sessionId } : {}) }
}

function normalizeDiagnostic(value: unknown): DesktopLanguageToolingResult['diagnostics'][number] | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const raw = value as Record<string, unknown>
  if (typeof raw.filePath !== 'string' || (raw.filePath && !isSafeRelativePath(raw.filePath))) return null
  const severity = raw.severity === 'error' || raw.severity === 'warning' ? raw.severity : 'info'
  const positions = ['startLine', 'startColumn', 'endLine', 'endColumn'].map((name) => raw[name])
  if (positions.some((item) => typeof item !== 'number' || !Number.isSafeInteger(item) || item < 1 || item > 1_000_000)) return null
  return { filePath: raw.filePath, severity, code: safeText(raw.code, 96, 'diagnostic'), message: safeText(raw.message, 512, 'A tooling diagnostic was reported.'), startLine: positions[0] as number, startColumn: positions[1] as number, endLine: positions[2] as number, endColumn: positions[3] as number }
}

function safeText(value: unknown, maximum: number, fallback: string) {
  if (typeof value !== 'string') return fallback
  const cleaned = value.replace(/[\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/g, ' ').trim()
  return cleaned ? cleaned.slice(0, maximum) : fallback
}

function failure(code: string, message: string): DesktopLanguageToolingResult {
  return { succeeded: false, code, message, diagnostics: [], artifacts: [] }
}
