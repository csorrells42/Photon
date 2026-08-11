import { afterEach, describe, expect, it } from 'vitest'
import { DesktopSourceControlClient } from './DesktopSourceControlClient'
import {
  MAXIMUM_SOURCE_CONTROL_FRAME_CHARACTERS,
  normalizeSourceControlFrame,
} from './contracts'

class FakeBridge {
  messages: unknown[] = []
  listener: ((event: MessageEvent) => void) | undefined
  added = 0
  removed = 0

  postMessage(message: unknown) { this.messages.push(message) }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) {
    this.listener = listener
    this.added++
  }
  removeEventListener(_type: 'message', listener: (event: MessageEvent) => void) {
    if (this.listener === listener) this.listener = undefined
    this.removed++
  }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

function installBridge(bridge: FakeBridge) {
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { chrome: { webview: bridge } },
  })
}

function requestId(bridge: FakeBridge) {
  return (bridge.messages.at(-1) as { requestId: string }).requestId
}

function descriptionFrame(id: string) {
  return {
    type: 'sourceControl.describe.result',
    version: 1,
    requestId: id,
    value: {
      git: { state: 'available' },
      gitExtensions: { state: 'unavailable', message: 'Optional tool unavailable.' },
      operations: ['describe', 'getStatus'],
      gitExtensionsSurfaces: [],
    },
  }
}

afterEach(() => {
  Reflect.deleteProperty(globalThis, 'window')
})

describe('desktop source-control protocol', () => {
  it('rejects malformed, wrong-version, cyclic, and oversized frames', () => {
    expect(normalizeSourceControlFrame(null)).toBeNull()
    expect(normalizeSourceControlFrame({ type: 'sourceControl.describe.result', version: 99, requestId: 'x' })).toBeNull()
    const cyclic: Record<string, unknown> = { version: 1 }
    cyclic.self = cyclic
    expect(normalizeSourceControlFrame(cyclic)).toBeNull()
    expect(normalizeSourceControlFrame({
      ...descriptionFrame('large'),
      padding: 'x'.repeat(MAXIMUM_SOURCE_CONTROL_FRAME_CHARACTERS + 1),
    })).toBeNull()
  })

  it('bounds entries and discards unknown fields while preserving text paths', () => {
    const frame = normalizeSourceControlFrame({
      type: 'sourceControl.status.result', version: 1, requestId: 'status:1',
      value: {
        succeeded: true,
        snapshot: {
          repositoryId: 'opaque', displayName: 'Repo', entryCount: 1,
          branch: { head: 'main', ahead: 0, behind: 0, stashCount: 0 },
          groups: {
            staged: [{ path: '<script>alert(1)</script>', kind: 'modified', staged: true, secret: 'discard' }],
            unstaged: [], untracked: [], conflicted: [], renamed: [], deleted: [], submodules: [],
          },
        },
      },
    })
    expect(frame).toMatchObject({ type: 'status', value: { snapshot: { groups: { staged: [{ path: '<script>alert(1)</script>' }] } } } })
    expect(JSON.stringify(frame)).not.toContain('discard')
  })

  it('correlates request identifiers and expected response kinds', async () => {
    const bridge = new FakeBridge()
    installBridge(bridge)
    const client = new DesktopSourceControlClient()
    const pending = client.describe()
    const id = requestId(bridge)
    let settled = false
    void pending.then(() => { settled = true })

    bridge.emit(descriptionFrame('wrong-id'))
    bridge.emit({ type: 'sourceControl.repository.resolve.result', version: 1, requestId: id, value: { succeeded: false } })
    await Promise.resolve()
    expect(settled).toBe(false)

    bridge.emit(descriptionFrame(id))
    await expect(pending).resolves.toMatchObject({ git: { state: 'available' } })
    expect(bridge.added).toBe(1)
    client.close()
  })

  it('removes its listener and rejects pending requests on close', async () => {
    const bridge = new FakeBridge()
    installBridge(bridge)
    const client = new DesktopSourceControlClient()
    const pending = client.resolveRepository('.')
    expect(bridge.messages).toHaveLength(1)
    client.close()
    await expect(pending).rejects.toThrow('disconnected')
    expect(bridge.removed).toBe(1)
    expect(bridge.listener).toBeUndefined()
  })

  it('emits the exact repository-resolve and cancel protocol messages', () => {
    const bridge = new FakeBridge()
    installBridge(bridge)
    const client = new DesktopSourceControlClient()
    void client.resolveRepository('nested/repo').catch(() => undefined)
    expect(bridge.messages[0]).toMatchObject({ type: 'sourceControl.repository.resolve', workspaceRelativePath: 'nested/repo', version: 1 })
    const status = client.status('opaque-id')
    void status.promise.catch(() => undefined)
    status.cancel()
    expect(bridge.messages.at(-1)).toMatchObject({ type: 'sourceControl.cancel', targetRequestId: status.requestId, version: 1 })
    client.close()
  })

  it('fails non-fatally when no desktop bridge exists', async () => {
    Object.defineProperty(globalThis, 'window', { configurable: true, value: {} })
    const client = new DesktopSourceControlClient()
    expect(client.available).toBe(false)
    await expect(client.describe()).rejects.toThrow('Hermes desktop app')
  })
})
