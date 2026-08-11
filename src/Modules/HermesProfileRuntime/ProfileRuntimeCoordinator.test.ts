import { describe, expect, it } from 'vitest'
import { HERMES_PROFILE_RUNTIME_CONTRACT, type AdapterResponse, type RequestContext } from './contracts'
import { DuplicateProfileRuntimeOperationError, HermesProfileRuntimeCoordinator } from './ProfileRuntimeCoordinator'

describe('HermesProfileRuntimeCoordinator', () => {
  it('prevents duplicate pending operations and clears the key afterward', async () => {
    const coordinator = new HermesProfileRuntimeCoordinator()
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    const operation = (context: RequestContext): Promise<AdapterResponse<string>> => gate.then(() => ({ ...context, revision: 1, value: 'done' }))
    const first = coordinator.execute('save', 'profile-main', 1, operation)
    await expect(coordinator.execute('save', 'profile-main', 1, operation)).rejects.toBeInstanceOf(DuplicateProfileRuntimeOperationError)
    expect(coordinator.pendingKeys).toEqual(['save'])
    release()
    await expect(first).resolves.toBe('done')
    expect(coordinator.pendingKeys).toEqual([])
  })

  it('supports cancellation and rejects a late cross-profile result', async () => {
    const coordinator = new HermesProfileRuntimeCoordinator()
    const pending = coordinator.execute('slow', 'profile-main', undefined, (context, signal) => new Promise<AdapterResponse<string>>((resolve, reject) => {
      signal.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true })
      setTimeout(() => resolve({ ...context, revision: 1, value: 'late' }), 100)
    }))
    expect(coordinator.cancel('slow')).toBe(true)
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' })

    await expect(coordinator.execute('forged', 'profile-main', undefined, async (context) => ({
      ...context,
      contract: HERMES_PROFILE_RUNTIME_CONTRACT,
      profileId: 'profile-lab',
      revision: 1,
      value: 'forged',
    }))).rejects.toThrow(/cross-profile/i)
  })

  it('releases a cancelled operation key before the aborted operation settles', async () => {
    const coordinator = new HermesProfileRuntimeCoordinator()
    let rejectCancelled!: (reason: unknown) => void
    const cancelled = coordinator.execute('load', 'profile-main', undefined, (_context, signal) => new Promise<AdapterResponse<string>>((_resolve, reject) => {
      rejectCancelled = reject
      signal.addEventListener('abort', () => undefined, { once: true })
    }))

    expect(coordinator.cancel('load')).toBe(true)
    expect(coordinator.pendingKeys).toEqual([])
    await expect(coordinator.execute('load', 'profile-main', undefined, async (context) => ({
      ...context,
      revision: 1,
      value: 'replacement',
    }))).resolves.toBe('replacement')

    rejectCancelled(new DOMException('cancelled', 'AbortError'))
    await expect(cancelled).rejects.toMatchObject({ name: 'AbortError' })
    expect(coordinator.pendingKeys).toEqual([])
  })
})
