import { describe, expect, it } from 'vitest'
import { normalizeCodexHostFrame } from './CodexAppServerClient'

describe('Codex app-server compatibility adapter', () => {
  it('normalizes ready and protocol frames', () => {
    expect(normalizeCodexHostFrame({ type: 'codex.ready', version: 1, processId: 42, executable: 'codex.exe', initialized: true }))
      .toMatchObject({ type: 'ready', version: 1, processId: 42, executable: 'codex.exe', initialized: true })
    expect(normalizeCodexHostFrame({ type: 'codex.protocol', version: 1, payload: { method: 'turn/started', params: { turn: { id: 'turn-1' } } } }))
      .toMatchObject({ type: 'protocol', payload: { method: 'turn/started' } })
  })

  it('preserves unavailable, exit, and error details', () => {
    expect(normalizeCodexHostFrame({ type: 'codex.unavailable', message: 'Install Codex' })).toMatchObject({ type: 'unavailable', message: 'Install Codex' })
    expect(normalizeCodexHostFrame({ type: 'codex.exit', exitCode: 7 })).toMatchObject({ type: 'exit', exitCode: 7 })
    expect(normalizeCodexHostFrame({ type: 'codex.error', message: 'failed' })).toMatchObject({ type: 'error', message: 'failed' })
  })

  it('rejects invalid frames and labels future host messages', () => {
    expect(normalizeCodexHostFrame('ready')).toBeNull()
    expect(normalizeCodexHostFrame({ type: 'codex.future', payload: {} })).toMatchObject({ type: 'unknown' })
  })
})
