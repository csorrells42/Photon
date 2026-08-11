import { describe, expect, it, vi } from 'vitest'
import { announceHermesAuthChanged, subscribeHermesAuthChanged } from './HermesAuthEvents'

describe('Hermes auth events', () => {
  it('notifies every active subscriber of the new authentication state', () => {
    const first = vi.fn()
    const second = vi.fn()
    const target = new EventTarget()
    const removeFirst = subscribeHermesAuthChanged(first, target)
    const removeSecond = subscribeHermesAuthChanged(second, target)

    announceHermesAuthChanged('signed-in', target)
    removeFirst()
    announceHermesAuthChanged('signed-out', target)
    removeSecond()

    expect(first).toHaveBeenCalledTimes(1)
    expect(first).toHaveBeenCalledWith('signed-in')
    expect(second).toHaveBeenNthCalledWith(1, 'signed-in')
    expect(second).toHaveBeenNthCalledWith(2, 'signed-out')
  })
})
