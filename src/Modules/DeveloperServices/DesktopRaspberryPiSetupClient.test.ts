import { describe, expect, it } from 'vitest'
import { configureRaspberryPiTarget } from './DesktopRaspberryPiSetupClient'

class FakeWebView {
  sent: unknown[] = []
  listener: ((event: MessageEvent) => void) | null = null
  postMessage(message: unknown) { this.sent.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { this.listener = listener }
  removeEventListener() { this.listener = null }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

describe('DesktopRaspberryPiSetupClient', () => {
  it('requests native-only setup and accepts only its correlated opaque result', async () => {
    const webview = new FakeWebView()
    Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview }, setTimeout, clearTimeout } })
    const pending = configureRaspberryPiTarget()
    const request = webview.sent[0] as { type: string; version: number; requestId: string }
    expect(request).toMatchObject({ type: 'developerServices.raspberryPi.configure', version: 1 })
    webview.emit({ type: 'developerServices.raspberryPi.configure.result', version: 1, requestId: 'wrong', succeeded: true, code: 'configured', message: 'wrong' })
    webview.emit({ type: 'developerServices.raspberryPi.configure.result', version: 1, requestId: request.requestId, succeeded: true, code: 'configured', message: 'saved', targetId: 'shop-pi' })
    await expect(pending).resolves.toEqual({ succeeded: true, code: 'configured', message: 'saved', targetId: 'shop-pi' })
  })

  it('fails closed when the desktop host is absent', async () => {
    Object.defineProperty(globalThis, 'window', { configurable: true, value: {} })
    await expect(configureRaspberryPiTarget()).resolves.toMatchObject({ succeeded: false, code: 'native-unavailable' })
  })
})
