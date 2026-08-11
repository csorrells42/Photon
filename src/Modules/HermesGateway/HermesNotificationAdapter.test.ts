import { describe, expect, it } from 'vitest'
import {
  HERMES_NOTIFICATION_ADAPTER_VERSION,
  HERMES_NOTIFICATION_LIMIT,
  mergeHermesNotification,
  normalizeHermesNotification,
  removeExpiredHermesNotifications,
} from './HermesNotificationAdapter'
import type { HermesNotification } from './HermesNotificationAdapter'

describe('Hermes notification adapter v1', () => {
  it('normalizes severity, glyphs, detail, sticky lifetime, and bounded identity', () => {
    expect(HERMES_NOTIFICATION_ADAPTER_VERSION).toBe(1)
    expect(normalizeHermesNotification({
      type: 'notification.show',
      payload: { key: 'credits.usage', level: 'warn', kind: 'sticky', text: '⚠ You used $75 · $100 cap' },
    }, 1_000)).toEqual({
      id: 'credits.usage',
      key: 'credits.usage',
      message: 'You used $75',
      detail: '$100 cap',
      level: 'warning',
      sticky: true,
      receivedAt: 1_000,
    })
  })

  it('bounds ttl notices and expires them without touching sticky notices', () => {
    const ttl = normalizeHermesNotification({
      type: 'notification.show', payload: { id: 'short', kind: 'ttl', ttl_ms: 5, text: 'Temporary' },
    }, 2_000)
    expect(ttl?.expiresAt).toBe(3_000)
    const sticky = normalizeHermesNotification({ type: 'notification.show', payload: { key: 'keep', text: 'Keep' } }, 2_000)
    expect(removeExpiredHermesNotifications([ttl!, sticky!], 3_001)).toEqual([sticky])
  })

  it('replaces a repeated key and clears only the matching notice', () => {
    const first = mergeHermesNotification([], {
      type: 'notification.show', payload: { key: 'credits.usage', text: '50 percent' },
    }, 1)
    const replaced = mergeHermesNotification(first, {
      type: 'notification.show', payload: { key: 'credits.usage', level: 'warn', text: '75 percent' },
    }, 2)
    const withOther = mergeHermesNotification(replaced, {
      type: 'notification.show', payload: { key: 'other', text: 'Other notice' },
    }, 3)
    const cleared = mergeHermesNotification(withOther, {
      type: 'notification.clear', payload: { key: 'credits.usage' },
    }, 4)

    expect(replaced).toHaveLength(1)
    expect(replaced[0]).toMatchObject({ message: '75 percent', level: 'warning' })
    expect(cleared).toHaveLength(1)
    expect(cleared[0].key).toBe('other')
  })

  it('ignores malformed notices and caps the visible history', () => {
    const unchanged: HermesNotification[] = [{ id: 'existing', message: 'Existing', level: 'info', sticky: true, receivedAt: 1 }]
    expect(mergeHermesNotification(unchanged, { type: 'notification.show', payload: { text: 'x'.repeat(2_001) } })).toBe(unchanged)

    let notices = unchanged
    for (let index = 0; index < HERMES_NOTIFICATION_LIMIT + 5; index += 1) {
      notices = mergeHermesNotification(notices, {
        type: 'notification.show', payload: { key: `notice-${index}`, text: `Notice ${index}` },
      }, index + 10)
    }
    expect(notices).toHaveLength(HERMES_NOTIFICATION_LIMIT)
    expect(notices[0].key).toBe(`notice-${HERMES_NOTIFICATION_LIMIT + 4}`)
  })
})
