import { afterEach, describe, expect, it, vi } from 'vitest'
import { collectDesktopOpenRouterUsage, normalizeOpenRouterUsageFrame, normalizeProviderUsageFrame } from './DesktopUsageBridge'

afterEach(() => vi.unstubAllGlobals())

describe('desktop usage bridge', () => {
  it('keeps only sanitized numeric OpenRouter fields', () => {
    const normalized = normalizeOpenRouterUsageFrame({
      type: 'usage.collect.result',
      version: 3,
      requestId: 'request',
      provider: 'openrouter',
      collectedAt: '2026-08-09T12:00:00.000Z',
      data: {
        usage: 25.5,
        usageWeekly: 4.25,
        limitRemaining: 74.5,
        limitReset: 'monthly',
        isFreeTier: false,
        label: 'sk-or-v1-secret',
        creatorUserId: 'user-secret',
      },
    })

    expect(normalized).toEqual({ usage: 25.5, usageWeekly: 4.25, limitRemaining: 74.5, limitReset: 'monthly', isFreeTier: false, collectedAt: '2026-08-09T12:00:00.000Z' })
    expect(JSON.stringify(normalized)).not.toMatch(/sk-or|creator|user-secret/i)
  })

  it('rejects malformed, negative, or non-finite usage', () => {
    const base = { type: 'usage.collect.result', version: 3, provider: 'openrouter', collectedAt: '2026-08-09T12:00:00.000Z' }
    expect(normalizeOpenRouterUsageFrame({ ...base, data: { usage: -1, isFreeTier: false } })).toBeNull()
    expect(normalizeOpenRouterUsageFrame({ ...base, data: { usage: Number.POSITIVE_INFINITY, isFreeTier: false } })).toBeNull()
    expect(normalizeOpenRouterUsageFrame({ ...base, data: { usage: 1, isFreeTier: 'false' } })).toBeNull()
  })

  it('sends the selected named credential id without placing a secret in the renderer message', async () => {
    const listeners = new Set<(event: MessageEvent) => void>()
    let sent: Record<string, unknown> | undefined
    const webview = {
      postMessage(message: unknown) {
        sent = message as Record<string, unknown>
        queueMicrotask(() => {
          for (const listener of listeners) listener({ data: {
            type: 'usage.collect.result', version: 3, requestId: sent?.requestId, provider: 'openrouter',
            collectedAt: '2026-08-09T12:00:00.000Z', data: { usage: 1, isFreeTier: false },
          } } as MessageEvent)
        })
      },
      addEventListener(_type: 'message', listener: (event: MessageEvent) => void) { listeners.add(listener) },
      removeEventListener(_type: 'message', listener: (event: MessageEvent) => void) { listeners.delete(listener) },
    }
    vi.stubGlobal('window', {
      __HERMES_DESKTOP_HOST__: { capabilities: { usage: true, usageVersion: 3 } },
      chrome: { webview },
      setTimeout,
      clearTimeout,
    })
    vi.stubGlobal('crypto', { randomUUID: () => '00000000-0000-4000-8000-000000000001' })

    await expect(collectDesktopOpenRouterUsage('scarlett')).resolves.toMatchObject({ kind: 'success' })
    expect(sent).toMatchObject({ type: 'usage.collect', provider: 'openrouter', credentialId: 'scarlett' })
    expect(JSON.stringify(sent)).not.toMatch(/secret|api.?key|authorization/i)
  })

  it('keeps only bounded provider-neutral observations and sanitized errors', () => {
    const normalized = normalizeProviderUsageFrame({
      type: 'usage.collect.provider.result', version: 3, provider: 'openai-api',
      data: {
        collectedAt: '2026-08-09T12:00:00.000Z',
        capabilities: [{ capability: 'organization-costs', state: 'Supported', reason: 'Available.' }],
        observations: [{ kind: 'Cost', metric: 'actual-cost', value: 12.5, unit: 'currency', currency: 'USD', freshness: { collectedAt: '2026-08-09T12:00:00.000Z', state: 'Current', note: 'Reported.' }, accountId: 'discard-me' }],
        errors: [{ code: 'partial', message: 'One capability lagged.', retryable: true, rawBody: 'discard-me' }],
        credential: 'discard-me',
      },
    }, 'openai-api')
    expect(normalized?.observations[0]).toMatchObject({ kind: 'Cost', metric: 'actual-cost', value: 12.5, currency: 'USD' })
    expect(JSON.stringify(normalized)).not.toContain('discard-me')
  })
})
