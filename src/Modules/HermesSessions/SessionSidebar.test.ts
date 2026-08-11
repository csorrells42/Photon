import { describe, expect, it } from 'vitest'
import { removeSessionByIdentity, sessionIdentity, updateSessionByIdentity } from './SessionSidebar'
import type { HermesSession } from './HermesSessionApi'

const session = (profile: string, title: string): HermesSession => ({
  id: 'shared-id', profile, title, preview: null, model: null, cwd: null,
  is_active: false, started_at: 1, last_active: 1, message_count: 1, tool_call_count: 0,
})

describe('SessionSidebar profile-scoped identity', () => {
  it('uses distinct keys for the same session id in different profiles', () => {
    expect(sessionIdentity(session('ali', 'Ali'))).not.toBe(sessionIdentity(session('scarlett', 'Scarlett')))
  })

  it('updates and removes only the targeted profile row', () => {
    const sessions = [session('ali', 'Ali'), session('scarlett', 'Scarlett')]
    const renamed = updateSessionByIdentity(sessions, sessions[0], (item) => ({ ...item, title: 'Renamed' }))
    expect(renamed.map((item) => item.title)).toEqual(['Renamed', 'Scarlett'])
    expect(removeSessionByIdentity(renamed, sessions[0]).map((item) => item.profile)).toEqual(['scarlett'])
  })
})
