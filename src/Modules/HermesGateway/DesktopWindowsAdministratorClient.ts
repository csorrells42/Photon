export type WindowsAdministratorResult = {
  succeeded: boolean
  code: string
  message: string
  exitCode?: number
  output: string
  outputTruncated: boolean
}

type WebViewBridge = {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

let sequence = 0

function bridge(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: WebViewBridge } }).chrome?.webview ?? null
}

function requestId() {
  sequence = (sequence + 1) % 1_000_000
  return `windows-administrator:${Date.now().toString(36)}:${sequence.toString(36)}`
}

function text(value: unknown, maximum: number) {
  return typeof value === 'string' ? value.slice(0, maximum) : ''
}

export function runWindowsAdministratorOperation(
  script: string,
  reason: string,
): Promise<WindowsAdministratorResult> {
  const host = bridge()
  if (!host) return Promise.resolve({
    succeeded: false,
    code: 'native-unavailable',
    message: 'Windows Administrator access is available only in the trusted desktop host.',
    output: '',
    outputTruncated: false,
  })

  const id = requestId()
  return new Promise((resolve) => {
    const timeout = window.setTimeout(() => finish({
      succeeded: false,
      code: 'timeout',
      message: 'The Windows Administrator operation did not return before the desktop timeout.',
      output: '',
      outputTruncated: false,
    }), 31 * 60 * 1_000)
    const listener = (event: MessageEvent) => {
      const frame = event.data as Record<string, unknown> | null
      if (!frame
        || frame.type !== 'developerServices.windowsAdministrator.run.result'
        || frame.version !== 1
        || frame.requestId !== id) return
      finish({
        succeeded: frame.succeeded === true,
        code: text(frame.code, 96) || 'administrator-operation-failed',
        message: text(frame.message, 1_024) || 'The Windows Administrator operation did not complete.',
        ...(typeof frame.exitCode === 'number' && Number.isSafeInteger(frame.exitCode)
          ? { exitCode: frame.exitCode }
          : {}),
        output: text(frame.output, 256 * 1_024),
        outputTruncated: frame.outputTruncated === true,
      })
    }
    const finish = (result: WindowsAdministratorResult) => {
      window.clearTimeout(timeout)
      host.removeEventListener('message', listener)
      resolve(result)
    }
    host.addEventListener('message', listener)
    try {
      host.postMessage({
        type: 'developerServices.windowsAdministrator.run',
        version: 1,
        requestId: id,
        script,
        reason,
      })
    } catch {
      finish({
        succeeded: false,
        code: 'native-unavailable',
        message: 'The trusted desktop host could not request Windows Administrator consent.',
        output: '',
        outputTruncated: false,
      })
    }
  })
}
