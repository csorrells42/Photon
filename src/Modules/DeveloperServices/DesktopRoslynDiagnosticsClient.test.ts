import { afterEach, describe, expect, it, vi } from 'vitest'
import { DesktopRoslynDiagnosticsClient, normalizeRoslynFrame } from './DesktopRoslynDiagnosticsClient'

const sessionId = 'a'.repeat(48)

class FakeBridge {
  readonly posted: unknown[] = []
  private listener?: (event: MessageEvent) => void
  postMessage = (message: unknown) => { this.posted.push(message) }
  addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listener = listener }
  removeEventListener = () => { this.listener = undefined }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

afterEach(() => vi.unstubAllGlobals())

describe('live Roslyn diagnostics contract', () => {
  it('accepts only session- and revision-bound workspace-relative diagnostics', () => {
    expect(normalizeRoslynFrame({
      type: 'developerServices.language.diagnostics', version: 2, sessionId,
      documentPath: 'src/Program.cs', revision: 7,
      diagnostics: [{
        filePath: 'src/Program.cs', severity: 'error', code: 'CS1002', message: '; expected', source: 'roslyn',
        range: { start: { line: 3, column: 5 }, end: { line: 3, column: 6 } },
      }],
    })).toMatchObject({ type: 'diagnostics', value: { sessionId, documentPath: 'src/Program.cs', revision: 7, diagnostics: [{ code: 'CS1002' }] } })
  })

  it('rejects foreign paths, sessions, revisions, and non-Roslyn sources', () => {
    const valid = {
      type: 'developerServices.language.diagnostics', version: 2, sessionId,
      documentPath: 'Program.cs', revision: 1,
      diagnostics: [{ filePath: 'Program.cs', severity: 'warning', code: '', message: 'warning', source: 'roslyn', range: { start: { line: 1, column: 1 }, end: { line: 1, column: 2 } } }],
    }
    expect(normalizeRoslynFrame({ ...valid, sessionId: 'foreign' })).toBeNull()
    expect(normalizeRoslynFrame({ ...valid, documentPath: '../Program.cs' })).toBeNull()
    expect(normalizeRoslynFrame({ ...valid, revision: -1 })).toBeNull()
    expect(normalizeRoslynFrame({ ...valid, diagnostics: [{ ...valid.diagnostics[0], source: 'server-command' }] })).toBeNull()
  })

  it('projects only typed document operations and binds acknowledgements to the exact request', async () => {
    const webview = new FakeBridge()
    vi.stubGlobal('window', { chrome: { webview } })
    const client = new DesktopRoslynDiagnosticsClient()
    const opened = client.open('src/Program.cs', 'class Program {}', 1)
    const request = webview.posted[0] as Record<string, unknown>
    expect(Object.keys(request).sort()).toEqual(['documentPath', 'requestId', 'revision', 'text', 'type', 'version'])
    expect(JSON.stringify(request)).not.toMatch(/command|executable|arguments|environment/i)

    webview.emit({
      type: 'developerServices.language.open.result', version: 2, requestId: request.requestId,
      sessionId, documentPath: 'src/Program.cs', revision: 1,
    })
    await expect(opened).resolves.toEqual({ sessionId, documentPath: 'src/Program.cs', revision: 1 })
  })
})
