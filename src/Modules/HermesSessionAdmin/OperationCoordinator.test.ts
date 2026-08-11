import { describe, expect, it, vi } from 'vitest'
import { DuplicatePendingOperationError, SessionAdminOperationCoordinator, restoreFocus } from './OperationCoordinator'

describe('SessionAdminOperationCoordinator', () => {
  it('tracks correlated pending state, rejects duplicates, and finishes only the matching request', () => {
    const coordinator = new SessionAdminOperationCoordinator()
    const pending = coordinator.begin('delete-preview', 'correlation:1')
    expect(coordinator.snapshot()).toEqual([pending])
    expect(() => coordinator.begin('delete-preview', 'correlation:2')).toThrow(DuplicatePendingOperationError)
    expect(coordinator.finish('delete-preview', 'wrong')).toBe(false)
    expect(coordinator.isCurrent('delete-preview', 'correlation:1')).toBe(true)
    expect(coordinator.isCurrent('delete-preview', 'wrong')).toBe(false)
    expect(coordinator.snapshot()).toHaveLength(1)
    expect(coordinator.finish('delete-preview', 'correlation:1')).toBe(true)
    expect(coordinator.snapshot()).toHaveLength(0)
  })

  it('cancels one or all in-flight operations', () => {
    const coordinator = new SessionAdminOperationCoordinator()
    const first = coordinator.begin('export', 'correlation:1')
    const second = coordinator.begin('statistics', 'correlation:2')
    expect(coordinator.cancel('export')).toBe(true)
    expect(first.controller.signal.aborted).toBe(true)
    expect(second.controller.signal.aborted).toBe(false)
    coordinator.cancelAll()
    expect(second.controller.signal.aborted).toBe(true)
    expect(coordinator.snapshot()).toHaveLength(0)
    expect(coordinator.finish('statistics', 'correlation:2')).toBe(false)
  })

  it('restores focus through the narrow focusable contract', () => {
    const target = { focus: vi.fn() }
    restoreFocus(target)
    restoreFocus(null)
    expect(target.focus).toHaveBeenCalledOnce()
  })
})
