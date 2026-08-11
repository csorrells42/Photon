import { describe, expect, it } from 'vitest'
import { DesktopDotNetDebuggerController, normalizeDotNetDebugFrame } from './DesktopDotNetDebuggerClient'

class FakeWebView {
  sent: unknown[] = []
  listener: ((event: MessageEvent) => void) | null = null
  postMessage(message: unknown) { this.sent.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listener = listener }
  removeEventListener() { this.listener = null }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

describe('DesktopDotNetDebuggerController', () => {
  it('normalizes typed results/events and rejects arbitrary operations', () => {
    expect(normalizeDotNetDebugFrame({ type: 'developerServices.debug.result', version: 2, requestId: 'd:1', operation: 'threads', state: 'stopped', result: [] })).toMatchObject({ type: 'result', operation: 'threads' })
    expect(normalizeDotNetDebugFrame({ type: 'developerServices.debug.result', version: 2, requestId: 'd:1', operation: 'shell', state: 'stopped', result: [] })).toBeNull()
    expect(normalizeDotNetDebugFrame({ type: 'developerServices.debug.event', version: 2, event: 'stopped', state: 'stopped', body: { threadId: 7 } })).toMatchObject({ type: 'event', event: 'stopped' })
  })

  it('discovers targets and never launches or replays automatically', async () => {
    const webview = new FakeWebView()
    Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview }, dispatchEvent() {} } })
    const controller = new DesktopDotNetDebuggerController()
    const pending = controller.refreshTargets('Debug')
    const request = webview.sent[0] as { requestId: string }
    webview.emit({ type: 'developerServices.debug.result', version: 2, requestId: request.requestId, operation: 'targets', state: 'inactive', result: ['bin/Debug/net10.0/App.dll'] })
    await pending
    expect(controller.getSnapshot().selectedProgram).toBe('bin/Debug/net10.0/App.dll')
    expect(webview.sent).toHaveLength(1)
  })
})
