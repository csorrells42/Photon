import { describe, expect, it } from 'vitest'
import type { DeveloperBuildResult, DeveloperDiagnostic } from '../DeveloperServices/DesktopDeveloperServicesClient'
import {
  advanceMonacoDiagnosticsState,
  initialMonacoDiagnosticsState,
  maximumMonacoDiagnosticMessageLength,
  maximumMonacoMarkersPerOwner,
  monacoArduinoDiagnosticOwner,
  monacoGccDiagnosticOwner,
  monacoMsbuildDiagnosticOwner,
  monacoProblemSummaryLabel,
  monacoRoslynDiagnosticOwner,
  normalizeMonacoDiagnosticPath,
  projectMonacoDiagnostics,
} from './MonacoDiagnostics'

const model = {
  getLineCount: () => 3,
  getLineMaxColumn: (lineNumber: number) => [1, 6, 4, 1][lineNumber] ?? 1,
}
const severities = { Error: 8, Warning: 4, Info: 2 }

function diagnostic(overrides: Partial<DeveloperDiagnostic> = {}): DeveloperDiagnostic {
  return {
    filePath: 'src/Program.cs',
    severity: 'error',
    code: 'CS1002',
    message: '; expected',
    source: 'msbuild',
    range: { start: { line: 2, column: 2 }, end: { line: 2, column: 2 } },
    ...overrides,
  }
}

function result(revision: number, diagnostics: DeveloperDiagnostic[] = [], overrides: Partial<DeveloperBuildResult> = {}): DeveloperBuildResult {
  return {
    requestId: `analyze:${revision}`,
    revision,
    operation: 'analyze',
    stale: false,
    workspaceRoot: 'C:\\workspace',
    succeeded: true,
    wasCancelled: false,
    diagnostics,
    output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
    ...overrides,
  }
}

describe('Monaco diagnostics revision gate', () => {
  it('accepts only strictly newer finite nonnegative integer revisions', () => {
    const accepted = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(3, [diagnostic()]))
    expect(accepted).toMatchObject({ watermark: 3, result: { revision: 3 } })
    expect(advanceMonacoDiagnosticsState(accepted, result(3))).toBe(accepted)
    expect(advanceMonacoDiagnosticsState(accepted, result(2))).toBe(accepted)
    expect(advanceMonacoDiagnosticsState(accepted, result(Number.NaN))).toBe(accepted)
    expect(advanceMonacoDiagnosticsState(accepted, result(3.5))).toBe(accepted)
    expect(advanceMonacoDiagnosticsState(accepted, result(-1))).toBe(accepted)
  })

  it('advances the watermark for stale or cancelled results without replacing markers', () => {
    const accepted = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(4, [diagnostic()]))
    const stale = advanceMonacoDiagnosticsState(accepted, result(8, [], { stale: true }))
    expect(stale.watermark).toBe(8)
    expect(stale.result).toBe(accepted.result)
    expect(advanceMonacoDiagnosticsState(stale, result(7))).toBe(stale)

    const cancelled = advanceMonacoDiagnosticsState(stale, result(9, [], { wasCancelled: true }))
    expect(cancelled).toMatchObject({ watermark: 9, result: { revision: 4 } })
  })

  it('lets a newer valid empty result clear markers and null reset the stream', () => {
    const accepted = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(1, [diagnostic()]))
    const cleared = advanceMonacoDiagnosticsState(accepted, result(2))
    expect(cleared).toMatchObject({ watermark: 2, result: { revision: 2, diagnostics: [] } })
    expect(advanceMonacoDiagnosticsState(cleared, null)).toEqual(initialMonacoDiagnosticsState())
  })
})

describe('Monaco diagnostics projection', () => {
  it('matches safe relative paths case-insensitively with normalized slashes and stable source owners', () => {
    const state = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(1, [
      diagnostic({ filePath: 'SRC\\Program.cs', source: 'MSBuild' }),
      diagnostic({ filePath: 'src/program.cs', source: 'Roslyn', severity: 'warning', code: 'CS0168' }),
      diagnostic({ filePath: 'src/program.cs', source: 'other', severity: 'info' }),
      diagnostic({ filePath: 'src/Other.cs', source: 'msbuild' }),
    ]))
    const projection = projectMonacoDiagnostics(state, 'src/Program.cs', model, severities)

    expect(projection.markers[monacoMsbuildDiagnosticOwner]).toHaveLength(1)
    expect(projection.markers[monacoRoslynDiagnosticOwner]).toHaveLength(1)
    expect(projection.markers[monacoMsbuildDiagnosticOwner][0]).toMatchObject({ source: 'msbuild', severity: 8 })
    expect(projection.markers[monacoRoslynDiagnosticOwner][0]).toMatchObject({ source: 'roslyn', severity: 4 })
    expect(projection.summary).toEqual({ errors: 1, warnings: 1, information: 0, omitted: 1 })
  })

  it('rejects unsafe paths, coalesces duplicates, bounds messages, and clamps ranges to the model', () => {
    const longMessage = 'x'.repeat(maximumMonacoDiagnosticMessageLength + 200)
    const clamped = diagnostic({
      message: longMessage,
      range: { start: { line: 99, column: 99 }, end: { line: -5, column: -5 } },
    })
    const state = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(2, [
      clamped,
      clamped,
      diagnostic({ filePath: '../src/Program.cs' }),
      diagnostic({ filePath: 'C:\\workspace\\src\\Program.cs' }),
      diagnostic({ filePath: 'src/Program.cs:secret' }),
    ]))
    const projection = projectMonacoDiagnostics(state, 'SRC\\PROGRAM.CS', model, severities)
    const markers = projection.markers[monacoMsbuildDiagnosticOwner]

    expect(markers).toHaveLength(1)
    expect(markers[0].message).toHaveLength(maximumMonacoDiagnosticMessageLength)
    expect(markers[0]).toMatchObject({ startLineNumber: 3, startColumn: 1, endLineNumber: 3, endColumn: 1 })
    expect(projection.summary).toEqual({ errors: 1, warnings: 0, information: 0, omitted: 3 })
  })

  it('caps each owner independently and reports overflow omissions', () => {
    const diagnostics = Array.from({ length: maximumMonacoMarkersPerOwner + 1 }, (_, index) => [
      diagnostic({ source: 'msbuild', message: `msbuild-${index}` }),
      diagnostic({ source: 'roslyn', severity: 'info', message: `roslyn-${index}` }),
    ]).flat()
    const state = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(3, diagnostics))
    const projection = projectMonacoDiagnostics(state, 'src/Program.cs', model, severities)

    expect(projection.markers[monacoMsbuildDiagnosticOwner]).toHaveLength(maximumMonacoMarkersPerOwner)
    expect(projection.markers[monacoRoslynDiagnosticOwner]).toHaveLength(maximumMonacoMarkersPerOwner)
    expect(projection.summary).toEqual({
      errors: maximumMonacoMarkersPerOwner,
      warnings: 0,
      information: maximumMonacoMarkersPerOwner,
      omitted: 2,
    })
    expect(monacoProblemSummaryLabel(projection.summary)).toContain('2 omitted')
  })

  it('keeps GCC and Arduino markers in independent owners', () => {
    const state = advanceMonacoDiagnosticsState(initialMonacoDiagnosticsState(), result(4, [
      diagnostic({ source: 'gcc', filePath: 'src/main.cpp', code: 'gcc-1' }),
      diagnostic({ source: 'arduino', filePath: 'src/main.cpp', severity: 'warning', code: 'avr-1' }),
    ]))
    const projection = projectMonacoDiagnostics(state, 'src/main.cpp', model, severities)
    expect(projection.markers[monacoGccDiagnosticOwner]).toMatchObject([{ source: 'gcc', code: 'gcc-1' }])
    expect(projection.markers[monacoArduinoDiagnosticOwner]).toMatchObject([{ source: 'arduino', code: 'avr-1' }])
    expect(projection.summary).toEqual({ errors: 1, warnings: 1, information: 0, omitted: 0 })
  })

  it('normalizes only safe workspace-relative paths', () => {
    expect(normalizeMonacoDiagnosticPath('src\\Program.cs')).toBe('src/Program.cs')
    expect(normalizeMonacoDiagnosticPath('/src/Program.cs')).toBeNull()
    expect(normalizeMonacoDiagnosticPath('..\\Program.cs')).toBeNull()
    expect(normalizeMonacoDiagnosticPath('src//Program.cs')).toBeNull()
    expect(normalizeMonacoDiagnosticPath('src/Program.cs:stream')).toBeNull()
  })
})
