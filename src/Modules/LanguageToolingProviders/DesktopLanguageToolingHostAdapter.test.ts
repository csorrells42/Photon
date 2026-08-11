import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DesktopLanguageToolingHostAdapter } from './DesktopLanguageToolingHostAdapter'

type Listener = (event: MessageEvent) => void

function installHost() {
  const listeners = new Set<Listener>()
  const postMessage = vi.fn()
  const hostWindow = {
    setTimeout,
    clearTimeout,
    __HERMES_DESKTOP_HOST__: {
      capabilities: { languageTooling: true, languageToolingVersion: 1 },
    },
    chrome: {
      webview: {
        postMessage,
        addEventListener: (_type: 'message', listener: Listener) => listeners.add(listener),
        removeEventListener: (_type: 'message', listener: Listener) => listeners.delete(listener),
      },
    },
  }
  vi.stubGlobal('window', hostWindow)
  return {
    postMessage,
    reply(data: unknown) {
      for (const listener of listeners) listener({ data } as MessageEvent)
    },
  }
}

describe('DesktopLanguageToolingHostAdapter', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('posts only the typed compile fields and normalizes a bounded result', async () => {
    const host = installHost()
    const adapter = new DesktopLanguageToolingHostAdapter()
    const promise = adapter.request({
      contract: 'language-tooling-providers/v1',
      operation: 'compile',
      requestId: 'req-1',
      workspaceId: 'renderer-value-is-not-forwarded',
      providerId: 'arduino',
      targetPath: 'sketch/blink.ino',
      mode: 'check',
      boardFqbn: 'arduino:avr:uno',
    }, new AbortController().signal)

    expect(host.postMessage).toHaveBeenCalledWith({
      type: 'developerServices.languageTooling.compile',
      version: 1,
      requestId: 'req-1',
      providerId: 'arduino',
      targetPath: 'sketch/blink.ino',
      mode: 'check',
      boardFqbn: 'arduino:avr:uno',
    })
    expect(JSON.stringify(host.postMessage.mock.calls[0])).not.toMatch(/workspaceId|executable|argv|environment/i)

    host.reply({
      type: 'developerServices.languageTooling.result',
      version: 1,
      requestId: 'req-1',
      succeeded: true,
      code: 'ok',
      message: 'Complete',
      result: {
        succeeded: true,
        code: 'compiled',
        message: 'Compiled',
        diagnostics: [{ filePath: 'sketch/blink.ino', severity: 'warning', code: 'W1', message: 'Check pin', startLine: 1, startColumn: 2, endLine: 1, endColumn: 3 }],
        artifacts: ['out/blink.hex', '../escape.hex', 'C:/escape.hex', 'out//duplicate.hex', 'out/trailing.'],
      },
    })

    await expect(promise).resolves.toMatchObject({
      succeeded: true,
      code: 'compiled',
      diagnostics: [{ filePath: 'sketch/blink.ino', severity: 'warning' }],
      artifacts: ['out/blink.hex'],
    })
  })

  it('fails closed when the exact desktop capability is absent', async () => {
    installHost()
    ;(window as unknown as { __HERMES_DESKTOP_HOST__?: unknown }).__HERMES_DESKTOP_HOST__ = { capabilities: { languageTooling: true, languageToolingVersion: 2 } }
    const result = await new DesktopLanguageToolingHostAdapter().request({
      contract: 'language-tooling-providers/v1',
      operation: 'inspect-provider',
      requestId: 'req-2',
      workspaceId: 'ignored',
      providerId: 'gcc',
    }, new AbortController().signal)
    expect(result).toMatchObject({ succeeded: false, code: 'native-unavailable' })
  })

  it('cancels only the exact active request', async () => {
    const host = installHost()
    const controller = new AbortController()
    const promise = new DesktopLanguageToolingHostAdapter().request({
      contract: 'language-tooling-providers/v1',
      operation: 'inspect-provider',
      requestId: 'req-3',
      workspaceId: 'ignored',
      providerId: 'gcc',
    }, controller.signal)
    controller.abort()
    await expect(promise).resolves.toMatchObject({ succeeded: false, code: 'cancelled' })
    expect(host.postMessage).toHaveBeenLastCalledWith(expect.objectContaining({
      type: 'developerServices.languageTooling.cancel',
      version: 1,
      targetRequestId: 'req-3',
    }))
  })
})
