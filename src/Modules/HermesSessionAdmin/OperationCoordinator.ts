export type PendingOperation = {
  key: string
  correlationId: string
  controller: AbortController
}

export class DuplicatePendingOperationError extends Error {
  constructor(readonly key: string) {
    super(`Operation ${key} is already pending.`)
    this.name = 'DuplicatePendingOperationError'
  }
}

export class SessionAdminOperationCoordinator {
  private readonly pending = new Map<string, PendingOperation>()

  begin(key: string, correlationId: string): PendingOperation {
    if (this.pending.has(key)) throw new DuplicatePendingOperationError(key)
    const operation = { key, correlationId, controller: new AbortController() }
    this.pending.set(key, operation)
    return operation
  }

  finish(key: string, correlationId: string): boolean {
    const current = this.pending.get(key)
    if (!current || current.correlationId !== correlationId) return false
    this.pending.delete(key)
    return true
  }

  isCurrent(key: string, correlationId: string): boolean {
    return this.pending.get(key)?.correlationId === correlationId
  }

  cancel(key: string): boolean {
    const current = this.pending.get(key)
    if (!current) return false
    current.controller.abort()
    return true
  }

  cancelAll(): void {
    for (const operation of this.pending.values()) operation.controller.abort()
    this.pending.clear()
  }

  snapshot(): ReadonlyArray<Readonly<PendingOperation>> {
    return [...this.pending.values()]
  }
}

export function restoreFocus(target: { focus(): void } | null | undefined): void {
  target?.focus()
}
