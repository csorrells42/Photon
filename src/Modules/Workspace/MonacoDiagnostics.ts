import type { editor } from 'monaco-editor'
import type {
  DeveloperBuildResult,
  DeveloperDiagnostic,
} from '../DeveloperServices/DesktopDeveloperServicesClient'

export const monacoMsbuildDiagnosticOwner = 'photos-developer-diagnostics-msbuild-v2'
export const monacoRoslynDiagnosticOwner = 'photos-developer-diagnostics-roslyn-v2'
export const monacoGccDiagnosticOwner = 'photos-developer-diagnostics-gcc-v1'
export const monacoArduinoDiagnosticOwner = 'photos-developer-diagnostics-arduino-v1'
export const monacoDiagnosticOwners = [monacoMsbuildDiagnosticOwner, monacoRoslynDiagnosticOwner, monacoGccDiagnosticOwner, monacoArduinoDiagnosticOwner] as const

export const maximumMonacoMarkersPerOwner = 500
export const maximumMonacoDiagnosticMessageLength = 1_024

type MonacoDiagnosticOwner = typeof monacoDiagnosticOwners[number]

export type MonacoDiagnosticsState = {
  watermark: number
  result: DeveloperBuildResult | null
}

export type MonacoProblemSummary = {
  errors: number
  warnings: number
  information: number
  omitted: number
}

export type MonacoDiagnosticProjection = {
  markers: Record<MonacoDiagnosticOwner, editor.IMarkerData[]>
  summary: MonacoProblemSummary
}

export type MonacoDiagnosticModel = {
  getLineCount: () => number
  getLineMaxColumn: (lineNumber: number) => number
}

export type MonacoMarkerSeverities = {
  Error: number
  Warning: number
  Info: number
}

export function initialMonacoDiagnosticsState(): MonacoDiagnosticsState {
  return { watermark: -1, result: null }
}

export function advanceMonacoDiagnosticsState(
  current: MonacoDiagnosticsState,
  candidate: DeveloperBuildResult | null,
): MonacoDiagnosticsState {
  if (candidate === null) return initialMonacoDiagnosticsState()
  const revision = candidate.revision
  if (!Number.isFinite(revision) || !Number.isInteger(revision) || revision < 0 || revision <= current.watermark) {
    return current
  }

  if (candidate.stale || candidate.wasCancelled) {
    return { watermark: revision, result: current.result }
  }
  return { watermark: revision, result: candidate }
}

export function normalizeMonacoDiagnosticPath(value: string): string | null {
  const candidate = value.trim().replaceAll('\\', '/')
  if (!candidate || candidate.length > 2_048 || candidate.startsWith('/') || /^[a-zA-Z]:/.test(candidate)
    || /[\u0000-\u001f\u007f]/.test(candidate)) return null

  const components = candidate.split('/')
  if (components.some((component) => !component || component === '.' || component === '..'
    || component.endsWith('.') || component.endsWith(' ') || /[<>:"|?*]/.test(component))) return null
  return components.join('/')
}

function diagnosticOwner(source: string): MonacoDiagnosticOwner | null {
  const normalized = source.trim().toLowerCase()
  if (normalized === 'msbuild') return monacoMsbuildDiagnosticOwner
  if (normalized === 'roslyn') return monacoRoslynDiagnosticOwner
  if (normalized === 'gcc') return monacoGccDiagnosticOwner
  if (normalized === 'arduino') return monacoArduinoDiagnosticOwner
  return null
}

function boundedPositiveInteger(value: number, fallback = 1) {
  return Number.isFinite(value) ? Math.max(1, Math.trunc(value)) : fallback
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value))
}

function boundedMessage(value: string) {
  const sanitized = value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '\uFFFD')
  return (sanitized || 'Developer diagnostic').slice(0, maximumMonacoDiagnosticMessageLength)
}

function markerForDiagnostic(
  diagnostic: DeveloperDiagnostic,
  model: MonacoDiagnosticModel,
  severities: MonacoMarkerSeverities,
): editor.IMarkerData {
  const lineCount = Math.max(1, boundedPositiveInteger(model.getLineCount()))
  const startLineNumber = clamp(boundedPositiveInteger(diagnostic.range.start.line), 1, lineCount)
  let endLineNumber = clamp(boundedPositiveInteger(diagnostic.range.end.line), startLineNumber, lineCount)
  const startMaximum = Math.max(1, boundedPositiveInteger(model.getLineMaxColumn(startLineNumber)))
  let startColumn = clamp(boundedPositiveInteger(diagnostic.range.start.column), 1, startMaximum)
  const endMaximum = Math.max(1, boundedPositiveInteger(model.getLineMaxColumn(endLineNumber)))
  let endColumn = clamp(boundedPositiveInteger(diagnostic.range.end.column), 1, endMaximum)

  if (endLineNumber === startLineNumber && endColumn <= startColumn) {
    if (startColumn < startMaximum) endColumn = startColumn + 1
    else if (startColumn > 1) {
      startColumn -= 1
      endColumn = startColumn + 1
    } else {
      endColumn = startColumn
    }
  }
  if (endLineNumber < startLineNumber) endLineNumber = startLineNumber

  return {
    severity: diagnostic.severity === 'error'
      ? severities.Error
      : diagnostic.severity === 'warning'
        ? severities.Warning
        : severities.Info,
    code: diagnostic.code.slice(0, 128) || undefined,
    source: diagnostic.source.trim().toLowerCase(),
    message: boundedMessage(diagnostic.message),
    startLineNumber,
    startColumn,
    endLineNumber,
    endColumn,
  }
}

function markerIdentity(marker: editor.IMarkerData) {
  return [marker.severity, marker.code ?? '', marker.message, marker.startLineNumber, marker.startColumn,
    marker.endLineNumber, marker.endColumn].join('\u0000')
}

export function projectMonacoDiagnostics(
  state: MonacoDiagnosticsState,
  workspacePath: string,
  model: MonacoDiagnosticModel,
  severities: MonacoMarkerSeverities,
): MonacoDiagnosticProjection {
  const markers: Record<MonacoDiagnosticOwner, editor.IMarkerData[]> = {
    [monacoMsbuildDiagnosticOwner]: [],
    [monacoRoslynDiagnosticOwner]: [],
    [monacoGccDiagnosticOwner]: [],
    [monacoArduinoDiagnosticOwner]: [],
  }
  const summary: MonacoProblemSummary = { errors: 0, warnings: 0, information: 0, omitted: 0 }
  const expectedPath = normalizeMonacoDiagnosticPath(workspacePath)?.toLowerCase()
  if (!state.result || !expectedPath) return { markers, summary }

  const seen: Record<MonacoDiagnosticOwner, Set<string>> = {
    [monacoMsbuildDiagnosticOwner]: new Set<string>(),
    [monacoRoslynDiagnosticOwner]: new Set<string>(),
    [monacoGccDiagnosticOwner]: new Set<string>(),
    [monacoArduinoDiagnosticOwner]: new Set<string>(),
  }

  for (const diagnostic of state.result.diagnostics) {
    const normalizedPath = normalizeMonacoDiagnosticPath(diagnostic.filePath)
    if (!normalizedPath) {
      summary.omitted += 1
      continue
    }
    if (normalizedPath.toLowerCase() !== expectedPath) continue

    const owner = diagnosticOwner(diagnostic.source)
    if (!owner || (diagnostic.severity !== 'error' && diagnostic.severity !== 'warning' && diagnostic.severity !== 'info')) {
      summary.omitted += 1
      continue
    }
    const marker = markerForDiagnostic(diagnostic, model, severities)
    const identity = markerIdentity(marker)
    if (seen[owner].has(identity)) continue
    seen[owner].add(identity)
    if (markers[owner].length >= maximumMonacoMarkersPerOwner) {
      summary.omitted += 1
      continue
    }

    markers[owner].push(marker)
    if (diagnostic.severity === 'error') summary.errors += 1
    else if (diagnostic.severity === 'warning') summary.warnings += 1
    else summary.information += 1
  }

  return { markers, summary }
}

export function monacoProblemSummaryLabel(summary: MonacoProblemSummary) {
  return `${summary.errors} error${summary.errors === 1 ? '' : 's'} · ${summary.warnings} warning${summary.warnings === 1 ? '' : 's'} · ${summary.information} info · ${summary.omitted} omitted`
}
