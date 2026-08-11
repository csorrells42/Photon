import type { AdapterResponse, ProfileId, RequestContext } from './contracts'
import { assertMatchingResponse, createRequestContext } from './runtimeSafety'

export class DuplicateProfileRuntimeOperationError extends Error {
  constructor(readonly operationKey: string) {
    super(`Operation ${operationKey} is already pending.`)
    this.name = 'DuplicateProfileRuntimeOperationError'
  }
}

export class HermesProfileRuntimeCoordinator {
  private sequence = 0
  private readonly pending = new Map<string, AbortController>()

  get pendingKeys(): readonly string[] { return [...this.pending.keys()] }

  cancel(operationKey: string): boolean {
    const controller = this.pending.get(operationKey)
    if (!controller) return false
    this.pending.delete(operationKey)
    controller.abort()
    return true
  }

  cancelAll(): void {
    const controllers = [...this.pending.values()]
    this.pending.clear()
    for (const controller of controllers) controller.abort()
  }

  async execute<T>(
    operationKey: string,
    profileId: ProfileId,
    expectedRevision: number | undefined,
    operation: (context: RequestContext, signal: AbortSignal) => Promise<AdapterResponse<T>>,
  ): Promise<T> {
    if (this.pending.has(operationKey)) throw new DuplicateProfileRuntimeOperationError(operationKey)
    const controller = new AbortController()
    const correlationId = `profile-runtime:${++this.sequence}`
    const context = createRequestContext(profileId, correlationId, expectedRevision)
    this.pending.set(operationKey, controller)
    try {
      const response = assertMatchingResponse(await operation(context, controller.signal), context)
      return response.value
    } finally {
      if (this.pending.get(operationKey) === controller) this.pending.delete(operationKey)
    }
  }
}
