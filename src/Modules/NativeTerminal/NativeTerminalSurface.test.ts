import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('@xterm/xterm', () => ({ Terminal: class {} }))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class {} }))
vi.mock('@xterm/xterm/css/xterm.css', () => ({}))

import { createNativeTerminalRestartController } from './NativeTerminalSurface'

describe('native terminal restart lifecycle', () => {
  afterEach(() => vi.useRealTimers())

  it('does not restart a terminal after its surface is disposed', () => {
    vi.useFakeTimers()
    const client = { start: vi.fn(), stop: vi.fn() }
    const terminal = { cols: 120, rows: 36, reset: vi.fn() }
    const restart = createNativeTerminalRestartController()

    restart.restart(client, terminal)
    restart.dispose()
    vi.advanceTimersByTime(180)

    expect(client.stop).toHaveBeenCalledOnce()
    expect(terminal.reset).toHaveBeenCalledOnce()
    expect(client.start).not.toHaveBeenCalled()
  })

  it('keeps only the newest pending restart', () => {
    vi.useFakeTimers()
    const client = { start: vi.fn(), stop: vi.fn() }
    const terminal = { cols: 100, rows: 30, reset: vi.fn() }
    const restart = createNativeTerminalRestartController()

    restart.restart(client, terminal)
    restart.restart(client, terminal)
    vi.advanceTimersByTime(180)

    expect(client.stop).toHaveBeenCalledTimes(2)
    expect(client.start).toHaveBeenCalledOnce()
    expect(client.start).toHaveBeenCalledWith(100, 30)
  })
})
