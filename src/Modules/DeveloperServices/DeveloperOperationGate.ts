import type {
  DeveloperBuildResult,
  DeveloperOperation,
  DeveloperOperationHandle,
  DeveloperServicesDescription,
} from './DesktopDeveloperServicesClient'

export type DeveloperOperationIdentity = Pick<DeveloperOperationHandle, 'requestId' | 'revision' | 'operation'>
export type DeveloperOperationSettlement = 'accepted' | 'stale' | 'foreign'

export class DeveloperOperationGate {
  private activeOperation: DeveloperOperationIdentity | null = null
  private launching = false
  private cancellationRequested = false
  private descriptionGeneration = 0

  get active(): Readonly<DeveloperOperationIdentity> | null {
    return this.activeOperation
  }

  get cancelling() {
    return this.activeOperation !== null && this.cancellationRequested
  }

  start(operation: DeveloperOperation, launch: () => DeveloperOperationHandle): DeveloperOperationHandle | null {
    if (this.launching || this.activeOperation) return null
    this.launching = true
    try {
      const handle = launch()
      if (handle.operation !== operation || !handle.requestId || !Number.isInteger(handle.revision)) {
        throw new Error('Developer services returned an invalid operation identity.')
      }
      this.activeOperation = identityOf(handle)
      this.cancellationRequested = false
      return handle
    } finally {
      this.launching = false
    }
  }

  requestCancellation(): string | null {
    if (!this.activeOperation || this.cancellationRequested) return null
    this.cancellationRequested = true
    return this.activeOperation.requestId
  }

  settle(result: DeveloperBuildResult): DeveloperOperationSettlement {
    if (!this.matches(result)) return 'foreign'
    this.activeOperation = null
    this.cancellationRequested = false
    return result.stale ? 'stale' : 'accepted'
  }

  fail(identity: DeveloperOperationIdentity): boolean {
    if (!this.matches(identity)) return false
    this.activeOperation = null
    this.cancellationRequested = false
    return true
  }

  matches(identity: DeveloperOperationIdentity): boolean {
    return this.activeOperation?.requestId === identity.requestId
      && this.activeOperation.revision === identity.revision
      && this.activeOperation.operation === identity.operation
  }

  beginDescription(): number {
    this.descriptionGeneration += 1
    return this.descriptionGeneration
  }

  isCurrentDescription(generation: number) {
    return generation === this.descriptionGeneration
  }

  invalidateDescription() {
    this.descriptionGeneration += 1
  }
}

export function canRunDeveloperOperation(
  description: DeveloperServicesDescription | null,
  operation: DeveloperOperation,
  targetPath: string,
): boolean {
  if (!description || !description.targets.includes(targetPath)) return false
  const extension = targetPath.split('.').pop()?.toLowerCase()
  return description.providers.some((provider) => {
    if (provider.availability.state !== 'available' || !provider.build.supported) return false
    const targetKinds = provider.build.targetKinds.map((kind) => kind.replace(/^\./, '').toLowerCase())
    const supportsTarget = !targetKinds.length || (extension !== undefined && targetKinds.includes(extension))
    return supportsTarget && (operation === 'build' || provider.build.producesDiagnostics)
  })
}

export function operationUnavailableMessage(operation: DeveloperOperation) {
  return operation === 'build'
    ? 'The selected target is not available from a ready build provider.'
    : 'The selected target is not available from a ready diagnostics provider.'
}

function identityOf(handle: DeveloperOperationHandle): DeveloperOperationIdentity {
  return { requestId: handle.requestId, revision: handle.revision, operation: handle.operation }
}
