export type WorkbenchEditCommand = 'cut' | 'copy' | 'paste'

export type WorkbenchCommandTarget = {
  tagName?: string
  type?: string
  disabled?: boolean
  readOnly?: boolean
  isContentEditable?: boolean
  isConnected?: boolean
  focus?: (options?: { preventScroll?: boolean }) => void
  closest?: (selector: string) => unknown
}

export type WorkbenchCommandDocument = {
  execCommand?: (command: string) => boolean
}

export type WorkbenchExitTarget = {
  chrome?: { webview?: { postMessage: (message: unknown) => void } }
  close?: () => void
}

export type WorkbenchCommandResult = {
  ok: boolean
  message: string
}

const TEXT_INPUT_TYPES = new Set(['', 'text', 'search', 'url', 'tel', 'email', 'password'])

export function isWorkbenchEditTarget(target: WorkbenchCommandTarget | null | undefined) {
  if (!target || target.isConnected === false) return false
  const tagName = target.tagName?.toLowerCase()
  if (tagName === 'textarea') return true
  if (tagName === 'input') return TEXT_INPUT_TYPES.has((target.type ?? '').toLowerCase())
  return Boolean(target.isContentEditable || target.closest?.('.monaco-editor'))
}

function canMutateTarget(target: WorkbenchCommandTarget | null | undefined) {
  if (!isWorkbenchEditTarget(target) || target?.disabled || target?.readOnly) return false
  if (target?.closest?.('.xterm, [data-no-global-edit-commands="true"]')) return false
  return true
}

export function canExecuteWorkbenchEditCommand(
  command: WorkbenchEditCommand,
  target: WorkbenchCommandTarget | null | undefined,
  documentTarget: WorkbenchCommandDocument,
) {
  if (typeof documentTarget.execCommand !== 'function') return false
  return command === 'copy' || canMutateTarget(target)
}

export function executeWorkbenchEditCommand(
  command: WorkbenchEditCommand,
  target: WorkbenchCommandTarget | null | undefined,
  documentTarget: WorkbenchCommandDocument,
): WorkbenchCommandResult {
  if (!canExecuteWorkbenchEditCommand(command, target, documentTarget)) {
    const label = `${command[0].toUpperCase()}${command.slice(1)}`
    return { ok: false, message: `${label} is unavailable for the active surface.` }
  }
  if (target?.isConnected !== false) target?.focus?.({ preventScroll: true })
  try {
    if (!documentTarget.execCommand?.(command)) {
      return { ok: false, message: `${command[0].toUpperCase()}${command.slice(1)} was not accepted by this surface.` }
    }
    return { ok: true, message: `${command[0].toUpperCase()}${command.slice(1)} completed.` }
  } catch {
    return { ok: false, message: `${command[0].toUpperCase()}${command.slice(1)} is unavailable in this host.` }
  }
}

export function requestWorkbenchExit(target: WorkbenchExitTarget): 'desktop' | 'browser' {
  const host = target.chrome?.webview
  if (host) {
    host.postMessage({ type: 'window.close' })
    return 'desktop'
  }
  target.close?.()
  return 'browser'
}
