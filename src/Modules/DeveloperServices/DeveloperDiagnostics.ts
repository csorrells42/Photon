import type { editor } from 'monaco-editor'
import type { DeveloperDiagnostic } from './DesktopDeveloperServicesClient'
import { workspaceRelativePath } from './DesktopDeveloperServicesClient'

export const developerDiagnosticOwner = 'hermes-developer-services-v2'
const maximumSummaryDiagnostics = 2_000

export type DeveloperDiagnosticSummary = {
  total: number
  errors: number
  warnings: number
  information: number
  omitted: number
}

export function summarizeDeveloperDiagnostics(diagnostics: DeveloperDiagnostic[]): DeveloperDiagnosticSummary {
  const bounded = diagnostics.slice(0, maximumSummaryDiagnostics)
  return bounded.reduce<DeveloperDiagnosticSummary>((summary, diagnostic) => {
    summary.total += 1
    if (diagnostic.severity === 'error') summary.errors += 1
    else if (diagnostic.severity === 'warning') summary.warnings += 1
    else summary.information += 1
    return summary
  }, { total: 0, errors: 0, warnings: 0, information: 0, omitted: Math.max(0, diagnostics.length - maximumSummaryDiagnostics) })
}

export function diagnosticSummaryLabel(summary: DeveloperDiagnosticSummary) {
  const parts = [`${summary.errors} error${summary.errors === 1 ? '' : 's'}`, `${summary.warnings} warning${summary.warnings === 1 ? '' : 's'}`, `${summary.information} info`]
  if (summary.omitted) parts.push(`${summary.omitted} omitted`)
  return parts.join(' · ')
}

export function diagnosticsForWorkspacePath(
  diagnostics: DeveloperDiagnostic[],
  workspaceRoot: string,
  workspacePath: string,
) {
  const normalized = workspacePath.replaceAll('\\', '/').toLowerCase()
  return diagnostics.filter((diagnostic) => workspaceRelativePath(diagnostic.filePath, workspaceRoot)?.toLowerCase() === normalized)
}

export function toMonacoMarkers(
  diagnostics: DeveloperDiagnostic[],
  severity: { Error: number; Warning: number; Info: number },
): editor.IMarkerData[] {
  return diagnostics.map((diagnostic) => {
    const startLineNumber = Math.max(1, diagnostic.range.start.line)
    const startColumn = Math.max(1, diagnostic.range.start.column)
    const endLineNumber = Math.max(startLineNumber, diagnostic.range.end.line)
    const endColumn = endLineNumber === startLineNumber
      ? Math.max(startColumn + 1, diagnostic.range.end.column)
      : Math.max(1, diagnostic.range.end.column)
    return {
      severity: diagnostic.severity === 'error' ? severity.Error : diagnostic.severity === 'warning' ? severity.Warning : severity.Info,
      code: diagnostic.code || undefined,
      source: diagnostic.source,
      message: diagnostic.message,
      startLineNumber,
      startColumn,
      endLineNumber,
      endColumn,
    }
  })
}
