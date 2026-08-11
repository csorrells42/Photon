import type { HermesGatewayEvent } from './HermesGatewayClient'

export const HERMES_NOTIFICATION_ADAPTER_VERSION = 1
export const HERMES_NOTIFICATION_LIMIT = 20

export type HermesNotificationLevel = 'info' | 'warning' | 'error' | 'success'

export type HermesNotification = {
  id: string
  key?: string
  message: string
  detail?: string
  level: HermesNotificationLevel
  sticky: boolean
  receivedAt: number
  expiresAt?: number
}

const DEFAULT_TTL_MS = 6_000
const MAX_TTL_MS = 24 * 60 * 60 * 1_000
const LEADING_GLYPH = /^[•⚠✕✗✓]\uFE0F?\s*/u

function boundedText(value: unknown, maximum: number) {
  if (typeof value !== 'string') return ''
  const text = value.trim()
  return text.length <= maximum ? text : ''
}

function splitDetail(text: string): [string, string | undefined] {
  const separator = text.indexOf(' · ')
  return separator < 0
    ? [text, undefined]
    : [text.slice(0, separator), text.slice(separator + 3) || undefined]
}

function noticeLevel(value: unknown): HermesNotificationLevel {
  if (value === 'warn' || value === 'warning') return 'warning'
  if (value === 'error' || value === 'success') return value
  return 'info'
}

export function normalizeHermesNotification(event: HermesGatewayEvent, now = Date.now()): HermesNotification | null {
  if (event.type !== 'notification.show') return null
  const payload = event.payload ?? {}
  const rawText = boundedText(payload.text, 2_000)
  if (!rawText) return null

  const [message, detail] = splitDetail(rawText.replace(LEADING_GLYPH, ''))
  if (!message) return null
  const key = boundedText(payload.key, 128)
  const wireId = boundedText(payload.id, 128)
  const sticky = payload.kind !== 'ttl'
  const requestedTtl = typeof payload.ttl_ms === 'number' && Number.isFinite(payload.ttl_ms)
    ? Math.round(payload.ttl_ms)
    : DEFAULT_TTL_MS
  const ttl = Math.min(MAX_TTL_MS, Math.max(1_000, requestedTtl))

  return {
    id: key || wireId || `notice-${now}`,
    ...(key ? { key } : {}),
    message,
    ...(detail ? { detail } : {}),
    level: noticeLevel(payload.level),
    sticky,
    receivedAt: now,
    ...(!sticky ? { expiresAt: now + ttl } : {}),
  }
}

export function mergeHermesNotification(
  current: HermesNotification[],
  event: HermesGatewayEvent,
  now = Date.now(),
): HermesNotification[] {
  if (event.type === 'notification.clear') {
    const key = boundedText(event.payload?.key, 128)
    return key ? current.filter((notice) => notice.key !== key && notice.id !== key) : current
  }

  const notice = normalizeHermesNotification(event, now)
  if (!notice) return current
  return [notice, ...current.filter((candidate) => candidate.id !== notice.id)].slice(0, HERMES_NOTIFICATION_LIMIT)
}

export function removeExpiredHermesNotifications(current: HermesNotification[], now = Date.now()) {
  return current.filter((notice) => notice.expiresAt === undefined || notice.expiresAt > now)
}

