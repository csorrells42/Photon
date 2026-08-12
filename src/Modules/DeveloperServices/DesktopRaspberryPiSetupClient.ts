export type RaspberryPiSetupResult = {
  succeeded: boolean
  code: string
  message: string
  targetId?: string
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

let sequence = 0

function bridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function requestId() {
  sequence = (sequence + 1) % 1_000_000
  return `raspberry-pi-setup:${Date.now().toString(36)}:${sequence.toString(36)}`
}

function text(value: unknown, maximum: number) {
  return typeof value === 'string' ? value.slice(0, maximum) : ''
}

export function configureRaspberryPiTarget(): Promise<RaspberryPiSetupResult> {
  const host = bridge()
  if (!host) return Promise.resolve({ succeeded: false, code: 'native-unavailable', message: 'Raspberry Pi setup is available only in the trusted desktop host.' })
  const id = requestId()
  return new Promise((resolve) => {
    const timeout = window.setTimeout(() => finish({ succeeded: false, code: 'timeout', message: 'The native Raspberry Pi setup window did not respond.' }), 120_000)
    const listener = (event: MessageEvent) => {
      const frame = event.data as Record<string, unknown> | null
      if (!frame || frame.type !== 'developerServices.raspberryPi.configure.result' || frame.version !== 1 || frame.requestId !== id) return
      finish({
        succeeded: frame.succeeded === true,
        code: text(frame.code, 96) || 'setup-failed',
        message: text(frame.message, 512) || 'The trusted Raspberry Pi setup did not complete.',
        ...(typeof frame.targetId === 'string' && /^[A-Za-z0-9._-]{1,128}$/.test(frame.targetId) ? { targetId: frame.targetId } : {}),
      })
    }
    const finish = (result: RaspberryPiSetupResult) => {
      window.clearTimeout(timeout)
      host.removeEventListener('message', listener)
      resolve(result)
    }
    host.addEventListener('message', listener)
    try { host.postMessage({ type: 'developerServices.raspberryPi.configure', version: 1, requestId: id }) }
    catch { finish({ succeeded: false, code: 'native-unavailable', message: 'The trusted Raspberry Pi setup window could not open.' }) }
  })
}
