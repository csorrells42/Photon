import { describe, expect, it } from 'vitest'
import { diagnosticsForWorkspacePath, toMonacoMarkers } from './DeveloperDiagnostics'
import type { DeveloperDiagnostic } from './DesktopDeveloperServicesClient'

const diagnostic: DeveloperDiagnostic = {
  filePath: 'C:\\workspace\\src\\Program.cs',
  severity: 'error',
  code: 'CS1002',
  message: '; expected',
  source: 'msbuild',
  range: { start: { line: 4, column: 8 }, end: { line: 4, column: 8 } },
}

describe('developer diagnostics', () => {
  it('matches only the open workspace-relative file', () => {
    expect(diagnosticsForWorkspacePath([diagnostic], 'C:\\workspace', 'src/Program.cs')).toEqual([diagnostic])
    expect(diagnosticsForWorkspacePath([diagnostic], 'C:\\workspace', 'src/Other.cs')).toEqual([])
  })

  it('creates a visible one-character marker for point diagnostics', () => {
    expect(toMonacoMarkers([diagnostic], { Error: 8, Warning: 4, Info: 2 })).toEqual([expect.objectContaining({
      severity: 8,
      startLineNumber: 4,
      startColumn: 8,
      endLineNumber: 4,
      endColumn: 9,
      code: 'CS1002',
    })])
  })
})
