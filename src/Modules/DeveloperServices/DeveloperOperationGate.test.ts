import { describe, expect, it, vi } from 'vitest'
import type {
  DeveloperBuildResult,
  DeveloperOperation,
  DeveloperOperationHandle,
  DeveloperServicesDescription,
} from './DesktopDeveloperServicesClient'
import { canRunDeveloperOperation, DeveloperOperationGate } from './DeveloperOperationGate'

function handle(operation: DeveloperOperation, requestId = `${operation}:1`, revision = 1): DeveloperOperationHandle {
  return { operation, requestId, revision, promise: new Promise(() => undefined), cancel: vi.fn() }
}

function result(operation: DeveloperOperation, requestId = `${operation}:1`, revision = 1, stale = false): DeveloperBuildResult {
  return {
    operation, requestId, revision, stale, workspaceRoot: 'C:\\workspace', succeeded: true, wasCancelled: false,
    diagnostics: [], output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
  }
}

const description: DeveloperServicesDescription = {
  protocolVersion: 2,
  requestId: 'describe:1',
  workspaceRoot: 'C:\\workspace',
  targets: ['Photon.slnx', 'src/App.csproj'],
  availability: { state: 'available' },
  providers: [{
    contractVersion: 1,
    providerId: 'dotnet',
    displayName: '.NET SDK',
    providerVersion: '8',
    languageIds: ['csharp'],
    projectKinds: ['slnx', 'csproj'],
    build: { supported: true, producesDiagnostics: true, supportsCancellation: true, targetKinds: ['slnx', 'csproj'] },
    lsp: { supported: false },
    dap: { supported: false },
    availability: { state: 'available' },
  }],
  languageTooling: [],
}

describe('DeveloperOperationGate', () => {
  it('guards duplicate starts synchronously and retains kind plus revision identity', () => {
    const gate = new DeveloperOperationGate()
    const first = gate.start('build', () => handle('build'))
    const duplicate = vi.fn(() => handle('analyze', 'analyze:2', 2))
    expect(first).not.toBeNull()
    expect(gate.start('analyze', duplicate)).toBeNull()
    expect(duplicate).not.toHaveBeenCalled()
    expect(gate.active).toEqual({ operation: 'build', requestId: 'build:1', revision: 1 })
  })

  it('accepts only the exact latest operation identity', () => {
    const gate = new DeveloperOperationGate()
    gate.start('analyze', () => handle('analyze', 'analyze:9', 9))
    expect(gate.settle(result('build', 'analyze:9', 9))).toBe('foreign')
    expect(gate.settle(result('analyze', 'analyze:9', 8))).toBe('foreign')
    expect(gate.settle(result('analyze', 'analyze:9', 9))).toBe('accepted')
    expect(gate.active).toBeNull()
  })

  it('retires an exact stale result without publishing it', () => {
    const gate = new DeveloperOperationGate()
    gate.start('build', () => handle('build'))
    expect(gate.settle(result('build', 'build:1', 1, true))).toBe('stale')
    expect(gate.active).toBeNull()
  })

  it('reports cancellation honestly until the operation settles', () => {
    const gate = new DeveloperOperationGate()
    gate.start('build', () => handle('build'))
    expect(gate.requestCancellation()).toBe('build:1')
    expect(gate.cancelling).toBe(true)
    expect(gate.requestCancellation()).toBeNull()
    expect(gate.active).not.toBeNull()
    gate.settle({ ...result('build'), wasCancelled: true, succeeded: false })
    expect(gate.cancelling).toBe(false)
  })

  it('uses monotonically increasing description generations', () => {
    const gate = new DeveloperOperationGate()
    const first = gate.beginDescription()
    const second = gate.beginDescription()
    expect(gate.isCurrentDescription(first)).toBe(false)
    expect(gate.isCurrentDescription(second)).toBe(true)
    gate.invalidateDescription()
    expect(gate.isCurrentDescription(second)).toBe(false)
  })

  it('revalidates target and provider capabilities for each operation', () => {
    expect(canRunDeveloperOperation(description, 'build', 'Photon.slnx')).toBe(true)
    expect(canRunDeveloperOperation(description, 'analyze', 'src/App.csproj')).toBe(true)
    expect(canRunDeveloperOperation(description, 'build', 'missing.csproj')).toBe(false)
    expect(canRunDeveloperOperation({ ...description, providers: [{ ...description.providers[0], availability: { state: 'unavailable' } }] }, 'build', 'Photon.slnx')).toBe(false)
    expect(canRunDeveloperOperation({ ...description, providers: [{ ...description.providers[0], build: { ...description.providers[0].build, producesDiagnostics: false } }] }, 'analyze', 'Photon.slnx')).toBe(false)
  })
})
