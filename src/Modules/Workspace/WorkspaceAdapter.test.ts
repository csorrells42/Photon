import { describe, expect, it, vi } from 'vitest'
import { WorkspaceAdapter } from './WorkspaceAdapter'

describe('read-only workspace adapter', () => {
  it('keeps the browser receiver when it uses the native fetch implementation', async () => {
    const fetcher = vi.fn(function (this: typeof globalThis) {
      if (this !== globalThis) throw new TypeError('Illegal invocation')
      return Promise.resolve(new Response(JSON.stringify({ root: 'HermesAgent', path: '', entries: [] }), { status: 200 }))
    })
    vi.stubGlobal('fetch', fetcher)

    try {
      await expect(new WorkspaceAdapter().readDirectory()).resolves.toMatchObject({ root: 'HermesAgent' })
      expect(fetcher).toHaveBeenCalledOnce()
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('encodes paths before calling the local bridge', async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify({ path: 'src/My File.ts', content: 'ok', size: 2, modifiedAt: '2026-08-10T00:00:00.000Z', sha256: 'a'.repeat(64) }), { status: 200 }))
    const adapter = new WorkspaceAdapter(fetcher as typeof fetch)

    await expect(adapter.readFile('src/My File.ts')).resolves.toMatchObject({ content: 'ok' })
    expect(fetcher).toHaveBeenCalledWith('/workbench-api/workspace/file?path=src%2FMy%20File.ts', { credentials: 'same-origin' })
  })

  it('surfaces the bridge safety message on a rejected path', async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify({ error: 'Path escapes the Hermes workspace.' }), { status: 403 }))
    const adapter = new WorkspaceAdapter(fetcher as typeof fetch)

    await expect(adapter.readDirectory('../secrets')).rejects.toThrow('escapes the Hermes workspace')
  })
})
