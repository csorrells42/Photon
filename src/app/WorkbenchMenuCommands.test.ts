import { describe, expect, it, vi } from 'vitest'
import {
  canExecuteWorkbenchEditCommand,
  executeWorkbenchEditCommand,
  isWorkbenchEditTarget,
  requestWorkbenchExit,
  type WorkbenchCommandTarget,
} from './WorkbenchMenuCommands'

function editableTarget(overrides: Partial<WorkbenchCommandTarget> = {}): WorkbenchCommandTarget {
  return { tagName: 'TEXTAREA', isConnected: true, focus: vi.fn(), closest: vi.fn(() => null), ...overrides }
}

describe('WorkbenchMenuCommands', () => {
  it('routes exit to the desktop host exactly once and otherwise uses a browser fallback', () => {
    const postMessage = vi.fn()
    const desktopClose = vi.fn()
    expect(requestWorkbenchExit({ chrome: { webview: { postMessage } }, close: desktopClose })).toBe('desktop')
    expect(postMessage).toHaveBeenCalledOnce()
    expect(postMessage).toHaveBeenCalledWith({ type: 'window.close' })
    expect(desktopClose).not.toHaveBeenCalled()

    const browserClose = vi.fn()
    expect(requestWorkbenchExit({ close: browserClose })).toBe('browser')
    expect(browserClose).toHaveBeenCalledOnce()
  })

  it('restores the captured editable target and delegates cut, copy, and paste without reading values', () => {
    const target = editableTarget()
    const execCommand = vi.fn(() => true)
    for (const command of ['cut', 'copy', 'paste'] as const) {
      expect(executeWorkbenchEditCommand(command, target, { execCommand })).toEqual(expect.objectContaining({ ok: true }))
    }
    expect(target.focus).toHaveBeenCalledTimes(3)
    expect(execCommand.mock.calls).toEqual([['cut'], ['copy'], ['paste']])
  })

  it('allows copy but refuses mutation for read-only, noneditable, and terminal surfaces', () => {
    const execCommand = vi.fn(() => true)
    const readOnly = editableTarget({ readOnly: true })
    expect(executeWorkbenchEditCommand('copy', readOnly, { execCommand }).ok).toBe(true)
    expect(executeWorkbenchEditCommand('cut', readOnly, { execCommand }).ok).toBe(false)
    expect(executeWorkbenchEditCommand('paste', { tagName: 'BUTTON' }, { execCommand }).ok).toBe(false)
    const terminal = editableTarget({ closest: vi.fn((selector) => selector.includes('.xterm')) })
    expect(executeWorkbenchEditCommand('paste', terminal, { execCommand }).ok).toBe(false)
  })

  it('recognizes text controls, contenteditable, and Monaco without treating buttons as editors', () => {
    expect(isWorkbenchEditTarget(editableTarget())).toBe(true)
    expect(isWorkbenchEditTarget({ tagName: 'INPUT', type: 'text', isConnected: true })).toBe(true)
    expect(isWorkbenchEditTarget({ tagName: 'DIV', isContentEditable: true, isConnected: true })).toBe(true)
    expect(isWorkbenchEditTarget({ tagName: 'DIV', isConnected: true, closest: () => ({}) })).toBe(true)
    expect(isWorkbenchEditTarget({ tagName: 'BUTTON', isConnected: true })).toBe(false)
  })

  it('returns an honest failure when the browser command is unavailable', () => {
    expect(executeWorkbenchEditCommand('copy', null, { execCommand: () => false })).toEqual({
      ok: false,
      message: 'Copy was not accepted by this surface.',
    })
  })

  it('disables commands that the active target or browser cannot support', () => {
    const target = editableTarget()
    expect(canExecuteWorkbenchEditCommand('paste', target, {})).toBe(false)
    expect(canExecuteWorkbenchEditCommand('paste', target, { execCommand: () => true })).toBe(true)
    expect(canExecuteWorkbenchEditCommand('cut', { tagName: 'BUTTON' }, { execCommand: () => true })).toBe(false)
    expect(canExecuteWorkbenchEditCommand('copy', null, { execCommand: () => true })).toBe(true)
  })
})
