import { describe, expect, it } from 'vitest'
import { normalizeConsoleFrame } from './HermesConsoleClient'

describe('Hermes Console compatibility adapter', () => {
  it('normalizes ready and output frames without inventing fields', () => {
    expect(normalizeConsoleFrame({ type: 'ready', profile: 'current', prompt: 'hermes> ' }))
      .toEqual({ type: 'ready', profile: 'current', prompt: 'hermes> ', id: undefined, stream: undefined, data: undefined, message: undefined, command: undefined, status: undefined })
    expect(normalizeConsoleFrame({ type: 'output', id: 4, stream: 'stdout', data: 'ok' }))
      .toMatchObject({ type: 'output', id: 4, stream: 'stdout', data: 'ok' })
  })

  it('preserves confirmation and completion metadata', () => {
    expect(normalizeConsoleFrame({ type: 'confirm_required', command: 'cron delete 3', message: 'Delete it?' }))
      .toMatchObject({ type: 'confirm_required', command: 'cron delete 3', message: 'Delete it?' })
    expect(normalizeConsoleFrame({ type: 'complete', status: 'cancelled', prompt: 'hermes> ' }))
      .toMatchObject({ type: 'complete', status: 'cancelled' })
  })

  it('rejects non-object frames and safely labels future frame types', () => {
    expect(normalizeConsoleFrame('output')).toBeNull()
    expect(normalizeConsoleFrame({ type: 'future-event', data: 'x' }))?.toMatchObject({ type: 'unknown', data: 'x' })
  })
})
