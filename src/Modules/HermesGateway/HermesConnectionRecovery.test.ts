import { describe, expect, it, vi } from 'vitest'
import {
  canStartHermesChat,
  HERMES_RECONNECT_DELAYS_MS,
  HermesSessionOpenGeneration,
  isHermesAuthenticationError,
  isHermesMissingSessionError,
  reconnectDelayMs,
  resumeHermesSession,
  shouldAcceptHermesSessionEvent,
  shouldRecoverHermesSession,
} from './HermesConnectionRecovery'

describe('Hermes connection recovery', () => {
  it('uses a bounded reconnect ladder long enough for a local container restart', () => {
    expect(HERMES_RECONNECT_DELAYS_MS).toEqual([400, 1_200, 2_500, 5_000, 10_000])
    expect(reconnectDelayMs(-1)).toBe(400)
    expect(reconnectDelayMs(1)).toBe(1_200)
    expect(reconnectDelayMs(99)).toBe(10_000)
  })

  it('does not treat a manual refresh of an open gateway as session recovery', () => {
    expect(shouldRecoverHermesSession(true, true)).toBe(false)
    expect(shouldRecoverHermesSession(true, false)).toBe(true)
    expect(shouldRecoverHermesSession(false, false)).toBe(false)
  })

  it('only permits a new chat when the gateway is genuinely open', () => {
    expect(canStartHermesChat(false, true)).toBe(true)
    expect(canStartHermesChat(false, false)).toBe(false)
    expect(canStartHermesChat(true, true)).toBe(false)
  })

  it('drops every session frame during a transition and rejects the prior session after commit', () => {
    expect(shouldAcceptHermesSessionEvent(true, null, 'runtime-a')).toBe(false)
    expect(shouldAcceptHermesSessionEvent(true, null, 'runtime-b')).toBe(false)
    expect(shouldAcceptHermesSessionEvent(true, null)).toBe(false)
    expect(shouldAcceptHermesSessionEvent(false, 'runtime-b', 'runtime-a')).toBe(false)
    expect(shouldAcceptHermesSessionEvent(false, 'runtime-b', 'runtime-b')).toBe(true)
    expect(shouldAcceptHermesSessionEvent(false, 'runtime-b')).toBe(true)
  })

  it('allows only the newest session-open request to publish results', () => {
    const gate = new HermesSessionOpenGeneration()
    const first = gate.begin()
    const second = gate.begin()
    expect(gate.isCurrent(first)).toBe(false)
    expect(gate.isCurrent(second)).toBe(true)
    gate.invalidate()
    expect(gate.isCurrent(second)).toBe(false)
  })

  it('distinguishes authentication and missing-session failures', () => {
    const typedAuthError = new Error('Sign in to Hermes inside Workbench, then reconnect.')
    typedAuthError.name = 'HermesAuthenticationError'
    expect(isHermesAuthenticationError(typedAuthError)).toBe(true)
    expect(isHermesAuthenticationError(new Error('Sign in to Hermes from Workbench Account to continue.'))).toBe(true)
    expect(isHermesAuthenticationError(new Error('Hermes could not reconnect. Check Docker or sign in.'))).toBe(false)
    expect(isHermesAuthenticationError(new Error('Could not connect to the Hermes gateway.'))).toBe(false)
    expect(isHermesMissingSessionError(new Error('Session abc was not found'))).toBe(true)
    expect(isHermesMissingSessionError(new Error('Hermes gateway connection closed.'))).toBe(false)
  })

  it('rebinds the durable session on its original profile without replaying a prompt', async () => {
    const request = vi.fn(async () => ({ session_id: 'runtime-new', stored_session_id: 'stored-1' }))

    await expect(resumeHermesSession(request, ' stored-1 ', ' project ')).resolves.toEqual({
      runtimeSessionId: 'runtime-new',
      storedSessionId: 'stored-1',
    })
    expect(request).toHaveBeenCalledWith('session.resume', {
      session_id: 'stored-1',
      cols: 96,
      source: 'desktop',
      omit_messages: true,
      profile: 'project',
    })
    expect(request).toHaveBeenCalledTimes(1)
  })

  it('rejects an invalid resume response instead of silently creating a new chat', async () => {
    const request = vi.fn(async () => ({}))
    await expect(resumeHermesSession(request, 'stored-1')).rejects.toThrow('runtime session ID')
    expect(request).toHaveBeenCalledTimes(1)
  })
})
