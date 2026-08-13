import { afterEach, describe, expect, it, vi } from 'vitest'
import { DeferredAppResourceDisposal } from './DeferredAppResourceDisposal'

describe('DeferredAppResourceDisposal', () => {
  afterEach(() => vi.useRealTimers())

  it('cancels the development Strict Mode cleanup when the effect is immediately re-established', () => {
    vi.useFakeTimers()
    const dispose = vi.fn()
    const gate = new DeferredAppResourceDisposal()

    gate.schedule(dispose)
    gate.cancel()
    vi.runAllTimers()

    expect(dispose).not.toHaveBeenCalled()
  })

  it('disposes exactly once after a real unmount', () => {
    vi.useFakeTimers()
    const dispose = vi.fn()
    const gate = new DeferredAppResourceDisposal()

    gate.schedule(dispose)
    vi.runAllTimers()

    expect(dispose).toHaveBeenCalledTimes(1)
  })
})
