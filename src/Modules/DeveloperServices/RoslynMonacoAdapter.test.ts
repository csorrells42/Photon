import { describe, expect, it } from 'vitest'
import { applyWorkspaceTextEdits, collectWorkspaceTextEdits, projectedLocations } from './RoslynMonacoAdapter'

describe('RoslynMonacoAdapter', () => {
  it('projects bounded workspace-relative definitions and references', () => {
    expect(projectedLocations([
      { uri: 'src/One.cs', range: { start: { line: 2, character: 3 }, end: { line: 2, character: 7 } } },
      { uri: '../escape.cs', range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } } },
    ])).toEqual([{ path: 'src/One.cs', range: { startLineNumber: 3, startColumn: 4, endLineNumber: 3, endColumn: 8 } }])
  })

  it('collects both LSP workspace edit encodings without executable commands', () => {
    const edits = collectWorkspaceTextEdits({
      changes: { 'src/One.cs': [{ range: { start: { line: 0, character: 1 }, end: { line: 0, character: 4 } }, newText: '<Name>' }] },
      documentChanges: [{ textDocument: { uri: 'src/Two.cs', version: 4 }, edits: [{ range: { start: { line: 1, character: 0 }, end: { line: 1, character: 2 } }, newText: 'Two' }] }],
      command: { command: 'must-not-run' },
    })
    expect([...edits.keys()]).toEqual(['src/One.cs', 'src/Two.cs'])
    expect(edits.get('src/One.cs')?.[0].newText).toBe('<Name>')
  })

  it('applies multiple edits from the end and rejects overlap', () => {
    const edits = [
      { range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 4 }, newText: 'ONE' },
      { range: { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 4 }, newText: 'TWO' },
    ]
    expect(applyWorkspaceTextEdits('one\ntwo\n', edits)).toBe('ONE\nTWO\n')
    expect(() => applyWorkspaceTextEdits('abcd', [
      { range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 4 }, newText: 'x' },
      { range: { startLineNumber: 1, startColumn: 2, endLineNumber: 1, endColumn: 3 }, newText: 'y' },
    ])).toThrow('overlapping')
  })
})
