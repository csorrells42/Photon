import { describe, expect, it } from 'vitest'
import { normalizeNativeTerminalFrame } from './DesktopHostTerminalClient'

describe('native terminal compatibility adapter', () => {
  it('normalizes ready and output frames', () => {
    expect(normalizeNativeTerminalFrame({ type: 'terminal.ready', version: 1, sessionId: 'abc', processId: 42, shell: 'PowerShell', cwd: 'C:\\work' }))
      .toMatchObject({ type: 'ready', version: 1, sessionId: 'abc', processId: 42, cwd: 'C:\\work' })
    expect(normalizeNativeTerminalFrame({ type: 'terminal.output', version: 1, data: '\u001b[32mok\u001b[0m' }))
      .toMatchObject({ type: 'output', data: '\u001b[32mok\u001b[0m' })
  })

  it('preserves exits and errors', () => {
    expect(normalizeNativeTerminalFrame({ type: 'terminal.exit', exitCode: 7 })).toMatchObject({ type: 'exit', exitCode: 7 })
    expect(normalizeNativeTerminalFrame({ type: 'terminal.error', message: 'failed' })).toMatchObject({ type: 'error', message: 'failed' })
  })

  it('rejects invalid frames and safely labels future messages', () => {
    expect(normalizeNativeTerminalFrame('output')).toBeNull()
    expect(normalizeNativeTerminalFrame({ type: 'terminal.future', data: 'x' })).toMatchObject({ type: 'unknown', data: 'x' })
  })
})
