import type { TimeRange } from './contracts'

export const desktopUsageProtocolVersion = 3

export type DesktopOrganizationProvider = 'openai-api' | 'anthropic-api' | 'google-ai-studio'

export type OpenRouterUsageData = {
  usage: number
  usageDaily?: number
  usageWeekly?: number
  usageMonthly?: number
  limit?: number
  limitRemaining?: number
  limitReset?: 'daily' | 'weekly' | 'monthly'
  isFreeTier: boolean
  collectedAt: string
}

export type DesktopUsageResult =
  | { kind: 'success'; data: OpenRouterUsageData }
  | { kind: 'failure'; code: string; message: string; retryable: boolean }
  | { kind: 'unavailable' }

export type NativeCapabilityState = 'Supported' | 'SetupRequired' | 'Unavailable' | 'PermissionDenied' | 'Stale' | 'Partial' | 'Error'

export type NativeUsageObservation = {
  kind: 'Usage' | 'Cost' | 'Budget' | 'Quota'
  metric: string
  value: number
  unit: string
  currency?: string
  freshness: {
    collectedAt: string
    dataThrough?: string
    state: 'Current' | 'Delayed' | 'Stale' | 'Unknown'
    note: string
  }
}

export type DesktopProviderUsageData = {
  capabilities: { capability: string; state: NativeCapabilityState; reason: string }[]
  observations: NativeUsageObservation[]
  errors: { code: string; message: string; retryable: boolean }[]
  collectedAt: string
}

export type DesktopProviderUsageResult =
  | { kind: 'success'; data: DesktopProviderUsageData }
  | { kind: 'failure'; code: string; message: string; retryable: boolean }
  | { kind: 'unavailable' }

type DesktopUsageFrame = {
  type?: unknown
  version?: unknown
  requestId?: unknown
  provider?: unknown
  collectedAt?: unknown
  data?: unknown
  code?: unknown
  message?: unknown
  retryable?: unknown
}

type WebViewBridge = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
}

type DesktopWindow = Window & {
  __HERMES_DESKTOP_HOST__?: { capabilities?: { usage?: boolean; usageVersion?: number } }
  chrome?: { webview?: WebViewBridge }
}

function optionalNonNegative(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined
}

export function normalizeOpenRouterUsageFrame(frame: DesktopUsageFrame): OpenRouterUsageData | null {
  if (frame.type !== 'usage.collect.result'
      || frame.version !== desktopUsageProtocolVersion
      || frame.provider !== 'openrouter'
      || typeof frame.collectedAt !== 'string'
      || Number.isNaN(Date.parse(frame.collectedAt))
      || !frame.data
      || typeof frame.data !== 'object') return null

  const data = frame.data as Record<string, unknown>
  const usage = optionalNonNegative(data.usage)
  if (usage === undefined || typeof data.isFreeTier !== 'boolean') return null
  const limitReset = data.limitReset === 'daily' || data.limitReset === 'weekly' || data.limitReset === 'monthly'
    ? data.limitReset
    : undefined

  return {
    usage,
    ...(optionalNonNegative(data.usageDaily) !== undefined ? { usageDaily: optionalNonNegative(data.usageDaily) } : {}),
    ...(optionalNonNegative(data.usageWeekly) !== undefined ? { usageWeekly: optionalNonNegative(data.usageWeekly) } : {}),
    ...(optionalNonNegative(data.usageMonthly) !== undefined ? { usageMonthly: optionalNonNegative(data.usageMonthly) } : {}),
    ...(optionalNonNegative(data.limit) !== undefined ? { limit: optionalNonNegative(data.limit) } : {}),
    ...(optionalNonNegative(data.limitRemaining) !== undefined ? { limitRemaining: optionalNonNegative(data.limitRemaining) } : {}),
    ...(limitReset ? { limitReset } : {}),
    isFreeTier: data.isFreeTier,
    collectedAt: frame.collectedAt,
  }
}

function boundedString(value: unknown, maximum = 512) {
  return typeof value === 'string' && value.length > 0 && value.length <= maximum ? value : null
}

function validInstant(value: unknown) {
  return typeof value === 'string' && value.length <= 64 && !Number.isNaN(Date.parse(value)) ? value : null
}

export function normalizeProviderUsageFrame(
  frame: DesktopUsageFrame,
  provider: DesktopOrganizationProvider,
): DesktopProviderUsageData | null {
  if (frame.type !== 'usage.collect.provider.result'
      || frame.version !== desktopUsageProtocolVersion
      || frame.provider !== provider
      || !frame.data
      || typeof frame.data !== 'object') return null
  const data = frame.data as Record<string, unknown>
  const collectedAt = validInstant(data.collectedAt)
  if (!collectedAt || !Array.isArray(data.capabilities) || !Array.isArray(data.observations) || !Array.isArray(data.errors)
      || data.capabilities.length > 16 || data.observations.length > 32 || data.errors.length > 16) return null

  const capabilities = data.capabilities.map((item) => {
    if (!item || typeof item !== 'object') return null
    const raw = item as Record<string, unknown>
    const capability = boundedString(raw.capability, 128)
    const reason = boundedString(raw.reason)
    const states: NativeCapabilityState[] = ['Supported', 'SetupRequired', 'Unavailable', 'PermissionDenied', 'Stale', 'Partial', 'Error']
    const state = states.find((candidate) => candidate === raw.state)
    return capability && reason && state ? { capability, state, reason } : null
  })
  const observations = data.observations.map((item): NativeUsageObservation | null => {
    if (!item || typeof item !== 'object') return null
    const raw = item as Record<string, unknown>
    const kinds: NativeUsageObservation['kind'][] = ['Usage', 'Cost', 'Budget', 'Quota']
    const kind = kinds.find((candidate) => candidate === raw.kind)
    const metric = boundedString(raw.metric, 128)
    const unit = boundedString(raw.unit, 64)
    const value = optionalNonNegative(raw.value)
    const currency = raw.currency === null || raw.currency === undefined ? undefined : boundedString(raw.currency, 8)
    const freshnessRaw = raw.freshness
    if (!freshnessRaw || typeof freshnessRaw !== 'object') return null
    const freshnessRecord = freshnessRaw as Record<string, unknown>
    const freshnessStates: NativeUsageObservation['freshness']['state'][] = ['Current', 'Delayed', 'Stale', 'Unknown']
    const freshnessState = freshnessStates.find((candidate) => candidate === freshnessRecord.state)
    const freshnessCollectedAt = validInstant(freshnessRecord.collectedAt)
    const dataThrough = freshnessRecord.dataThrough === null || freshnessRecord.dataThrough === undefined
      ? undefined
      : validInstant(freshnessRecord.dataThrough)
    const note = boundedString(freshnessRecord.note)
    if (!kind || !metric || !unit || value === undefined || currency === null || !freshnessState || !freshnessCollectedAt || dataThrough === null || !note) return null
    return { kind, metric, value, unit, ...(currency ? { currency } : {}), freshness: { collectedAt: freshnessCollectedAt, ...(dataThrough ? { dataThrough } : {}), state: freshnessState, note } }
  })
  const errors = data.errors.map((item) => {
    if (!item || typeof item !== 'object') return null
    const raw = item as Record<string, unknown>
    const code = boundedString(raw.code, 128)
    const message = boundedString(raw.message)
    return code && message && typeof raw.retryable === 'boolean' ? { code, message, retryable: raw.retryable } : null
  })
  if (capabilities.some((item) => item === null) || observations.some((item) => item === null) || errors.some((item) => item === null)) return null
  return {
    capabilities: capabilities as DesktopProviderUsageData['capabilities'],
    observations: observations as NativeUsageObservation[],
    errors: errors as DesktopProviderUsageData['errors'],
    collectedAt,
  }
}

export function desktopUsageWebView(): WebViewBridge | null {
  if (typeof window === 'undefined') return null
  const desktop = window as DesktopWindow
  const capabilities = desktop.__HERMES_DESKTOP_HOST__?.capabilities
  if (capabilities?.usage !== true || capabilities.usageVersion !== desktopUsageProtocolVersion) return null
  return desktop.chrome?.webview ?? null
}

export function collectDesktopOpenRouterUsage(credentialId = 'primary', signal?: AbortSignal): Promise<DesktopUsageResult> {
  const host = desktopUsageWebView()
  if (!host) return Promise.resolve({ kind: 'unavailable' })
  if (!/^[a-z0-9._-]{1,128}$/i.test(credentialId)) return Promise.resolve({ kind: 'failure', code: 'unexpected', message: 'The credential profile was invalid.', retryable: false })
  if (signal?.aborted) return Promise.resolve({ kind: 'failure', code: 'unavailable', message: 'Usage collection was cancelled.', retryable: true })
  const requestId = crypto.randomUUID()

  return new Promise((resolve) => {
    let settled = false
    const finish = (result: DesktopUsageResult) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      signal?.removeEventListener('abort', abort)
      host.removeEventListener('message', receive)
      resolve(result)
    }
    const abort = () => finish({ kind: 'failure', code: 'unavailable', message: 'Usage collection was cancelled.', retryable: true })
    const receive = (event: MessageEvent) => {
      const frame = event.data as DesktopUsageFrame
      if (frame?.requestId !== requestId || frame.version !== desktopUsageProtocolVersion || frame.provider !== 'openrouter') return
      if (frame.type === 'usage.collect.result') {
        const data = normalizeOpenRouterUsageFrame(frame)
        finish(data
          ? { kind: 'success', data }
          : { kind: 'failure', code: 'unexpected', message: 'The native host returned invalid OpenRouter usage data.', retryable: false })
      } else if (frame.type === 'usage.collect.error') {
        finish({
          kind: 'failure',
          code: typeof frame.code === 'string' ? frame.code : 'unexpected',
          message: typeof frame.message === 'string' ? frame.message : 'The native OpenRouter collector needs attention.',
          retryable: frame.retryable === true,
        })
      }
    }
    const timeout = window.setTimeout(() => finish({ kind: 'failure', code: 'unavailable', message: 'The native OpenRouter collector timed out.', retryable: true }), 15_000)
    signal?.addEventListener('abort', abort, { once: true })
    host.addEventListener('message', receive)
    try {
      host.postMessage({ type: 'usage.collect', version: desktopUsageProtocolVersion, requestId, provider: 'openrouter', credentialId })
    } catch {
      finish({ kind: 'failure', code: 'unavailable', message: 'The desktop usage bridge is unavailable.', retryable: true })
    }
  })
}

export function collectDesktopProviderUsage(
  provider: DesktopOrganizationProvider,
  credentialId: string,
  period: TimeRange,
  signal?: AbortSignal,
): Promise<DesktopProviderUsageResult> {
  const host = desktopUsageWebView()
  if (!host) return Promise.resolve({ kind: 'unavailable' })
  if (!/^[a-z0-9._-]{1,128}$/i.test(credentialId)
      || Number.isNaN(Date.parse(period.start))
      || Number.isNaN(Date.parse(period.end))) {
    return Promise.resolve({ kind: 'failure', code: 'unexpected', message: 'The provider usage request was invalid.', retryable: false })
  }
  if (signal?.aborted) return Promise.resolve({ kind: 'failure', code: 'unavailable', message: 'Usage collection was cancelled.', retryable: true })
  const requestId = crypto.randomUUID()

  return new Promise((resolve) => {
    let settled = false
    const finish = (result: DesktopProviderUsageResult) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      signal?.removeEventListener('abort', abort)
      host.removeEventListener('message', receive)
      resolve(result)
    }
    const abort = () => finish({ kind: 'failure', code: 'unavailable', message: 'Usage collection was cancelled.', retryable: true })
    const receive = (event: MessageEvent) => {
      const frame = event.data as DesktopUsageFrame
      if (frame?.requestId !== requestId || frame.version !== desktopUsageProtocolVersion || frame.provider !== provider) return
      if (frame.type === 'usage.collect.provider.result') {
        const data = normalizeProviderUsageFrame(frame, provider)
        finish(data
          ? { kind: 'success', data }
          : { kind: 'failure', code: 'unexpected', message: 'The native host returned invalid provider usage data.', retryable: false })
      } else if (frame.type === 'usage.collect.error') {
        finish({
          kind: 'failure',
          code: typeof frame.code === 'string' ? frame.code : 'unexpected',
          message: typeof frame.message === 'string' ? frame.message : 'The native provider collector needs attention.',
          retryable: frame.retryable === true,
        })
      }
    }
    const timeout = window.setTimeout(() => finish({ kind: 'failure', code: 'unavailable', message: 'The native provider collector timed out.', retryable: true }), 35_000)
    signal?.addEventListener('abort', abort, { once: true })
    host.addEventListener('message', receive)
    try {
      host.postMessage({
        type: 'usage.collect',
        version: desktopUsageProtocolVersion,
        requestId,
        provider,
        credentialId,
        periodStart: period.start,
        periodEnd: period.end,
      })
    } catch {
      finish({ kind: 'failure', code: 'unavailable', message: 'The desktop usage bridge is unavailable.', retryable: true })
    }
  })
}
