import { describe, expect, it } from 'vitest'
import { DesktopRoslynLanguageClient, normalizeRoslynLanguageFrame } from './DesktopRoslynLanguageClient'

const sessionId = 'a'.repeat(48)

class FakeWebView {
  sent: unknown[] = []
  listener: ((event: MessageEvent) => void) | null = null
  postMessage(message: unknown) { this.sent.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listener = listener }
  removeEventListener() { this.listener = null }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

describe('DesktopRoslynLanguageClient', () => {
  it('normalizes bounded, revision-bound results and rejects malformed output', () => {
    expect(normalizeRoslynLanguageFrame({
      type: 'developerServices.language.result', version: 2, requestId: 'r:1', sessionId,
      documentPath: 'src/App.cs', revision: 7, operation: 'completion', result: { items: [{ label: '<tag>' }] },
    })).toMatchObject({ type: 'result', value: { operation: 'completion', documentPath: 'src/App.cs' } })
    expect(normalizeRoslynLanguageFrame({
      type: 'developerServices.language.result', version: 2, requestId: 'r:1', sessionId,
      documentPath: '../App.cs', revision: 7, operation: 'completion', result: {},
    })).toBeNull()
    expect(normalizeRoslynLanguageFrame({
      type: 'developerServices.language.result', version: 2, requestId: 'r:1', sessionId,
      documentPath: 'App.cs', revision: 7, operation: 'execute-command', result: {},
    })).toBeNull()
  })

  it('correlates one typed request and cancels without replay', async () => {
    const webview = new FakeWebView()
    Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview } } })
    const client = new DesktopRoslynLanguageClient()
    const controller = new AbortController()
    const pending = client.request({ sessionId, documentPath: 'Program.cs', revision: 3 }, {
      operation: 'hover', line: 4, character: 2,
    }, controller.signal)
    expect(webview.sent[0]).toMatchObject({ type: 'developerServices.language.request', operation: 'hover', line: 4, character: 2 })
    const requestId = (webview.sent[0] as { requestId: string }).requestId
    webview.emit({
      type: 'developerServices.language.result', version: 2, requestId, sessionId,
      documentPath: 'Program.cs', revision: 3, operation: 'hover', result: { contents: '<b>text</b>' },
    })
    await expect(pending).resolves.toMatchObject({ result: { contents: '<b>text</b>' } })

    const cancelled = client.request({ sessionId, documentPath: 'Program.cs', revision: 3 }, {
      operation: 'references', line: 1, character: 1,
    }, controller.signal)
    controller.abort()
    await expect(cancelled).rejects.toThrow('cancelled')
    expect(webview.sent.filter((item) => (item as { type?: string }).type === 'developerServices.language.request')).toHaveLength(2)
    expect(webview.sent.at(-1)).toMatchObject({ type: 'developerServices.language.cancel' })
  })
})
