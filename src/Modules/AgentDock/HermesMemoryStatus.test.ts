import { describe, expect, it } from 'vitest'
import { memoryStatusForConnection, normalizeHermesMemoryStatus } from './HermesMemoryStatus'

describe('HermesMemoryStatus', () => {
  it('reports that memory status is available after connection without a checking flicker', () => {
    expect(memoryStatusForConnection('closed')).toEqual({
      state: 'unavailable',
      label: 'Memory status available after connection',
      title: 'Connect to Hermes to verify the authenticated memory provider.',
    })
    expect(memoryStatusForConnection('open').state).toBe('checking')
  })

  it('reports ready only for the exact active configured Mem0 provider', () => {
    expect(normalizeHermesMemoryStatus({ active: 'mem0', providers: [{ name: 'mem0', available: true, configured: true, status: 'ready' }] }))
      .toMatchObject({ state: 'ready', label: 'Memory ready · Mem0 local' })
  })

  it('reports honest bounded unavailable states', () => {
    expect(normalizeHermesMemoryStatus({ active: '', providers: [{ name: 'mem0', available: true, configured: true, status: 'ready' }] }).label).toContain('Not active')
    expect(normalizeHermesMemoryStatus({ active: 'mem0', providers: [{ name: 'mem0', available: false, configured: false, status: 'needs_config' }] }).label).toContain('Setup required')
    expect(normalizeHermesMemoryStatus({ active: 'mem0', providers: [] }).label).toContain('Mem0 missing')
    expect(normalizeHermesMemoryStatus(null).state).toBe('unavailable')
  })
})
