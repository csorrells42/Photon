import { collectDesktopOpenRouterUsage, collectDesktopProviderUsage } from './DesktopUsageBridge'
import type { DesktopOrganizationProvider, DesktopProviderUsageData, DesktopProviderUsageResult, DesktopUsageResult, OpenRouterUsageData } from './DesktopUsageBridge'
import { credentialDisplayName, listDesktopCredentials } from './DesktopCredentialBridge'
import type { DesktopCredentialListResult, DesktopCredentialMetadata } from './DesktopCredentialBridge'
import type { ProviderUsageAdapter, UsageAdapterResult } from './ProviderUsageAdapter'
import {
  providerDefinitions,
  usageIntelligenceContractVersion,
  type ProviderId,
  type ProviderConnectionState,
  type ProviderUsage,
  type UsageCollectionRequest,
  type UsageCredentialBreakdown,
} from './contracts'
import { createSpendSummary, normalizeUsageSnapshot } from './usageNormalization'

type NativeCollector = (credentialId: string, signal?: AbortSignal) => Promise<DesktopUsageResult>
type OrganizationCollector = (provider: DesktopOrganizationProvider, credentialId: string, period: UsageCollectionRequest['period'], signal?: AbortSignal) => Promise<DesktopProviderUsageResult>
type CredentialLister = (signal?: AbortSignal) => Promise<DesktopCredentialListResult>

const productionUsageBaseline: ProviderUsageAdapter = {
  async collect(request) {
    const requested = request.providers ? new Set<ProviderId>(request.providers) : null
    const providers: ProviderUsage[] = providerDefinitions
      .filter((definition) => !requested || requested.has(definition.id))
      .map((definition) => ({
        provider: definition.id,
        displayName: definition.displayName,
        readiness: definition.readiness,
        state: definition.supportsProgrammaticUsage ? 'not-configured' : 'unavailable',
        statusMessage: definition.supportsProgrammaticUsage
          ? 'No verified native usage evidence has been collected yet.'
          : definition.summary,
        trend: [],
        provenance: {
          kind: 'host-collector',
          label: 'Trusted host collector',
          detail: 'No provider value is shown until the trusted desktop host returns verified evidence.',
        },
      }))
    return {
      kind: 'success',
      snapshot: normalizeUsageSnapshot({
        contractVersion: usageIntelligenceContractVersion,
        generatedAt: new Date().toISOString(),
        period: request.period,
        providers,
        spendSummary: createSpendSummary(providers),
        notices: ['No synthetic usage data is shown in the production Workbench.'],
      }),
    }
  },
}

type NamedUsageResult = {
  credentialId: string
  result: Exclude<DesktopUsageResult, { kind: 'unavailable' }>
}

type NamedProviderResult = {
  credentialId: string
  result: Exclude<DesktopProviderUsageResult, { kind: 'unavailable' }>
}

function money(amount: number, label: string) {
  return { value: { amount, currency: 'USD' }, quality: 'reported' as const, label }
}

function usageSummary(data: OpenRouterUsageData) {
  const parts = [
    data.usageDaily === undefined ? null : `$${data.usageDaily.toFixed(2)} today`,
    data.usageWeekly === undefined ? null : `$${data.usageWeekly.toFixed(2)} this week`,
    data.usageMonthly === undefined ? null : `$${data.usageMonthly.toFixed(2)} this month`,
  ].filter(Boolean)
  return parts.length ? `OpenRouter reports ${parts.join(', ')} for this key.` : 'OpenRouter returned live usage for this key.'
}

function failureState(result: Extract<DesktopUsageResult, { kind: 'failure' }>): ProviderConnectionState {
  return result.code === 'not-configured'
    ? 'not-configured'
    : result.code === 'unavailable' || result.code === 'rate-limited'
      ? 'warning'
      : 'error'
}

function keyBreakdown(entry: NamedUsageResult): UsageCredentialBreakdown {
  const displayName = credentialDisplayName(entry.credentialId)
  if (entry.result.kind === 'success') {
    const data = entry.result.data
    const displayedUsage = data.usageWeekly ?? data.usage
    return {
      credentialId: entry.credentialId,
      displayName,
      state: 'connected',
      statusMessage: usageSummary(data),
      spend: money(displayedUsage, data.usageWeekly === undefined ? 'Total key usage' : 'Weekly key usage'),
      ...(data.limitRemaining === undefined ? {} : { remaining: money(data.limitRemaining, 'Key limit remaining') }),
      billingPeriod: {
        label: data.limitReset
          ? `${data.limitReset[0].toUpperCase()}${data.limitReset.slice(1)} key limit`
          : data.limit === undefined ? 'No key limit reported' : 'Key spending limit',
      },
      provenance: {
        kind: 'host-collector',
        label: 'Live OpenRouter key collector',
        collectedAt: data.collectedAt,
        detail: 'Reported for this named key by the official OpenRouter GET /api/v1/key endpoint through the native desktop host.',
      },
    }
  }

  return {
    credentialId: entry.credentialId,
    displayName,
    state: failureState(entry.result),
    statusMessage: entry.result.message,
    billingPeriod: { label: entry.result.retryable ? 'Refresh to try again' : 'Credential attention required' },
    provenance: {
      kind: 'host-collector',
      label: 'Native OpenRouter collector',
      collectedAt: new Date().toISOString(),
      detail: 'The desktop host returned a sanitized per-key collector state; no provider secret entered the renderer.',
    },
  }
}

function openRouterNotConfigured(message = 'Add one or more named OpenRouter keys in the native credential vault.') : ProviderUsage {
  return {
    provider: 'openrouter',
    displayName: 'OpenRouter',
    readiness: 'ready',
    state: 'not-configured',
    statusMessage: message,
    billingPeriod: { label: 'No named key profiles configured' },
    trend: [],
    credentialBreakdown: [],
    provenance: {
      kind: 'host-collector',
      label: 'Native OpenRouter collector',
      collectedAt: new Date().toISOString(),
      detail: 'The desktop collector is available, but no OpenRouter credential metadata was found.',
    },
  }
}

export function openRouterProviderFromResults(results: NamedUsageResult[]): ProviderUsage {
  if (!results.length) return openRouterNotConfigured()
  const breakdown = results.map(keyBreakdown)
  const successful = results.filter((entry): entry is NamedUsageResult & { result: Extract<DesktopUsageResult, { kind: 'success' }> } => entry.result.kind === 'success')
  const connectedCount = successful.length
  const attentionCount = results.length - connectedCount

  if (!successful.length) {
    const state = breakdown.some((entry) => entry.state === 'warning') ? 'warning' : 'error'
    return {
      provider: 'openrouter',
      displayName: 'OpenRouter',
      readiness: 'ready',
      state,
      statusMessage: `${results.length} named key profile${results.length === 1 ? '' : 's'} configured; each needs attention.`,
      billingPeriod: { label: 'No live key usage available' },
      trend: [],
      credentialBreakdown: breakdown,
      provenance: {
        kind: 'host-collector',
        label: 'Native OpenRouter key collectors',
        collectedAt: new Date().toISOString(),
        detail: 'Every named key returned a sanitized failure state; secrets remained native-only.',
      },
    }
  }

  const allReportWeekly = successful.every((entry) => entry.result.data.usageWeekly !== undefined)
  const spend = successful.reduce((total, entry) => total + (allReportWeekly ? entry.result.data.usageWeekly ?? 0 : entry.result.data.usage), 0)
  const allReportRemaining = successful.every((entry) => entry.result.data.limitRemaining !== undefined)
  const remaining = allReportRemaining
    ? successful.reduce((total, entry) => total + (entry.result.data.limitRemaining ?? 0), 0)
    : undefined
  const collectedAt = successful
    .map((entry) => entry.result.data.collectedAt)
    .sort((left, right) => right.localeCompare(left))[0]

  return {
    provider: 'openrouter',
    displayName: 'OpenRouter',
    readiness: 'ready',
    state: attentionCount ? 'warning' : 'connected',
    statusMessage: `${connectedCount} of ${results.length} named key profile${results.length === 1 ? '' : 's'} reporting${attentionCount ? `; ${attentionCount} needs attention` : ''}.`,
    spend: money(spend, allReportWeekly ? 'Combined weekly key usage' : 'Combined total key usage'),
    ...(remaining === undefined ? {} : { remaining: money(remaining, 'Combined key limits remaining') }),
    billingPeriod: { label: `${results.length} independently managed key profile${results.length === 1 ? '' : 's'}` },
    trend: [],
    credentialBreakdown: breakdown,
    provenance: {
      kind: 'host-collector',
      label: 'Live OpenRouter key roll-up',
      collectedAt,
      detail: 'A sum of same-period values reported independently by each named key through the native desktop host.',
    },
  }
}

export function openRouterProviderFromResult(result: Exclude<DesktopUsageResult, { kind: 'unavailable' }>): ProviderUsage {
  return openRouterProviderFromResults([{ credentialId: 'primary', result }])
}

function credentialInventoryProvider(
  provider: ProviderUsage,
  entries: DesktopCredentialMetadata[],
): ProviderUsage {
  if (!entries.length) return provider
  const updatedAt = entries
    .map((entry) => entry.updatedAt)
    .filter((value): value is string => Boolean(value))
    .sort((left, right) => right.localeCompare(left))[0]
  const provenance = {
    kind: 'manual' as const,
    label: 'Native key inventory',
    ...(updatedAt ? { collectedAt: updatedAt } : {}),
    detail: 'Only safe credential profile names are visible. Gemini key-level usage is not inferred because no supported key usage collector is connected.',
  }
  return {
    ...provider,
    state: 'unavailable',
    statusMessage: `${entries.length} named Gemini key profile${entries.length === 1 ? '' : 's'} stored securely; key-level usage reporting is unavailable.`,
    billingPeriod: { label: 'Credential inventory only' },
    trend: [],
    credentialBreakdown: entries.map((entry) => ({
      credentialId: entry.credentialId,
      displayName: credentialDisplayName(entry.credentialId),
      state: 'unavailable',
      statusMessage: 'Stored securely; no supported key-level Gemini usage feed is connected.',
      billingPeriod: { label: 'Usage unavailable by key' },
      provenance,
    })),
    provenance,
  }
}

function vaultFailureProvider(message: string): ProviderUsage {
  return {
    ...openRouterNotConfigured(message),
    state: 'warning',
    billingPeriod: { label: 'Credential inventory unavailable' },
  }
}

function organizationNotConfigured(provider: DesktopOrganizationProvider, displayName: string): ProviderUsage {
  return {
    provider,
    displayName,
    readiness: 'ready',
    state: 'not-configured',
    statusMessage: `Add one or more named ${displayName} Admin API keys in the native credential vault.`,
    billingPeriod: { label: 'No named organization key profiles configured' },
    trend: [],
    credentialBreakdown: [],
    provenance: {
      kind: 'host-collector',
      label: `Native ${displayName} collector`,
      collectedAt: new Date().toISOString(),
      detail: 'The trusted desktop collector is ready; credentials remain in Windows Credential Manager.',
    },
  }
}

function providerFailureState(code: string): ProviderConnectionState {
  if (code === 'not-configured' || code === 'configuration') return 'not-configured'
  if (/unauthorized|forbidden|permission/i.test(code)) return 'error'
  return 'warning'
}

function metricsFromProviderData(data: DesktopProviderUsageData) {
  const costs = data.observations.filter((item) => item.kind === 'Cost' && item.metric === 'actual-cost' && item.currency)
  const currencies = [...new Set(costs.map((item) => item.currency as string))]
  const spend = currencies.length === 1
    ? { value: { amount: costs.reduce((total, item) => total + item.value, 0), currency: currencies[0] }, quality: 'reported' as const, label: 'Organization cost' }
    : undefined
  const metric = (name: string) => data.observations.filter((item) => item.kind === 'Usage' && item.metric === name).reduce((total, item) => total + item.value, 0)
  const hasMetric = (name: string) => data.observations.some((item) => item.kind === 'Usage' && item.metric === name)
  const openAiInput = metric('input-tokens')
  const anthropicInput = metric('uncached-input-tokens') + metric('cache-write-input-tokens') + metric('cache-read-input-tokens')
  const input = hasMetric('input-tokens') ? openAiInput : anthropicInput
  const output = metric('output-tokens')
  const hasTokens = hasMetric('input-tokens') || hasMetric('uncached-input-tokens') || hasMetric('output-tokens')
  const requestMetric = hasMetric('model-requests') ? 'model-requests' : 'requests'
  const requestCount = metric(requestMetric)
  return {
    spend,
    ...(hasMetric(requestMetric) ? { requests: { value: requestCount, quality: 'reported' as const, label: 'Model requests' } } : {}),
    ...(hasTokens ? { tokens: { input, output, quality: 'reported' as const } } : {}),
  }
}

function providerBreakdown(provider: DesktopOrganizationProvider, entry: NamedProviderResult): UsageCredentialBreakdown {
  const displayName = credentialDisplayName(entry.credentialId)
  if (entry.result.kind === 'failure') {
    return {
      credentialId: entry.credentialId,
      displayName,
      state: providerFailureState(entry.result.code),
      statusMessage: entry.result.message,
      billingPeriod: { label: entry.result.retryable ? 'Refresh to try again' : 'Credential attention required' },
      provenance: { kind: 'host-collector', label: 'Native organization collector', collectedAt: new Date().toISOString(), detail: 'The desktop host returned a sanitized collector failure; no provider response or credential entered the renderer.' },
    }
  }
  const metrics = metricsFromProviderData(entry.result.data)
  const setup = entry.result.data.capabilities.some((item) => item.state === 'SetupRequired')
  const denied = entry.result.data.capabilities.some((item) => item.state === 'PermissionDenied' || item.state === 'Error')
  const attention = entry.result.data.errors.length > 0 || entry.result.data.capabilities.some((item) => item.state === 'Stale' || item.state === 'Partial')
  const state: ProviderConnectionState = denied ? 'error' : setup ? 'not-configured' : attention ? 'warning' : entry.result.data.observations.length ? 'connected' : 'unavailable'
  const firstReason = entry.result.data.errors[0]?.message ?? entry.result.data.capabilities[0]?.reason ?? 'No supported observation was returned.'
  return {
    credentialId: entry.credentialId,
    displayName,
    state,
    statusMessage: state === 'connected' ? 'Official organization usage data collected.' : firstReason,
    ...(metrics.spend ? { spend: metrics.spend } : {}),
    billingPeriod: { label: 'Selected dashboard period' },
    provenance: { kind: 'host-collector', label: 'Official organization usage collector', collectedAt: entry.result.data.collectedAt, detail: `${provider} observations were normalized by the trusted desktop host.` },
  }
}

export function organizationProviderFromResults(
  provider: DesktopOrganizationProvider,
  displayName: string,
  results: NamedProviderResult[],
): ProviderUsage {
  if (!results.length) return organizationNotConfigured(provider, displayName)
  const successful = results.filter((entry): entry is NamedProviderResult & { result: Extract<DesktopProviderUsageResult, { kind: 'success' }> } => entry.result.kind === 'success')
  const merged: DesktopProviderUsageData = {
    capabilities: successful.flatMap((entry) => entry.result.data.capabilities),
    observations: successful.flatMap((entry) => entry.result.data.observations),
    errors: successful.flatMap((entry) => entry.result.data.errors),
    collectedAt: successful.map((entry) => entry.result.data.collectedAt).sort((left, right) => right.localeCompare(left))[0] ?? new Date().toISOString(),
  }
  const metrics = metricsFromProviderData(merged)
  const breakdown = results.map((entry) => providerBreakdown(provider, entry))
  const connected = breakdown.filter((entry) => entry.state === 'connected').length
  const state: ProviderConnectionState = connected === results.length ? 'connected' : connected > 0 ? 'warning' : breakdown.some((entry) => entry.state === 'error') ? 'error' : breakdown.some((entry) => entry.state === 'not-configured') ? 'not-configured' : 'warning'
  return {
    provider,
    displayName,
    readiness: 'ready',
    state,
    statusMessage: `${connected} of ${results.length} named organization profile${results.length === 1 ? '' : 's'} reporting${connected === results.length ? '.' : '; review profile details.'}`,
    ...(metrics.spend ? { spend: metrics.spend } : {}),
    ...(metrics.requests ? { requests: metrics.requests } : {}),
    ...(metrics.tokens ? { tokens: metrics.tokens } : {}),
    billingPeriod: { label: 'Selected dashboard period' },
    trend: [],
    credentialBreakdown: breakdown,
    provenance: { kind: 'host-collector', label: `Live ${displayName} organization collector`, collectedAt: merged.collectedAt, detail: 'Official provider API observations were normalized and combined only across compatible metrics for the selected period.' },
  }
}

function geminiProviderFromResult(
  fallback: ProviderUsage,
  result: Exclude<DesktopProviderUsageResult, { kind: 'unavailable' }>,
  entries: DesktopCredentialMetadata[],
) {
  const project = organizationProviderFromResults('google-ai-studio', 'Google AI Studio / Gemini API', [
    { credentialId: 'application-default', result },
  ])
  const inventory = credentialInventoryProvider(fallback, entries)
  return {
    ...project,
    credentialBreakdown: inventory.credentialBreakdown,
    statusMessage: result.kind === 'success'
      ? `Project-level Gemini Monitoring is connected${entries.length ? `; ${entries.length} named API key profile${entries.length === 1 ? '' : 's'} remain inventory-only` : ''}.`
      : entries.length
        ? `Project-level Gemini Monitoring needs setup; ${entries.length} named API key profile${entries.length === 1 ? '' : 's'} ${entries.length === 1 ? 'is' : 'are'} stored securely as inventory only.`
        : project.statusMessage,
    provenance: {
      ...project.provenance,
      detail: 'Official Cloud Monitoring project aggregates were normalized by the trusted desktop host. No API-key attribution is inferred.',
    },
  } satisfies ProviderUsage
}

export class DesktopUsageAdapter implements ProviderUsageAdapter {
  constructor(
    private readonly nativeCollector: NativeCollector = collectDesktopOpenRouterUsage,
    private readonly fallback: ProviderUsageAdapter = productionUsageBaseline,
    private readonly credentialLister: CredentialLister = listDesktopCredentials,
    private readonly organizationCollector: OrganizationCollector = collectDesktopProviderUsage,
  ) {}

  async collect(request: UsageCollectionRequest, signal?: AbortSignal): Promise<UsageAdapterResult> {
    const fallback = await this.fallback.collect(request, signal)
    if (fallback.kind !== 'success') return fallback

    const credentialList = await this.credentialLister(signal)
    if (credentialList.kind === 'unavailable') return fallback
    if (credentialList.kind === 'failure') {
      const providers = fallback.snapshot.providers.map((provider) => provider.provider === 'openrouter'
        ? vaultFailureProvider(credentialList.message)
        : provider)
      return {
        kind: 'success',
        snapshot: normalizeUsageSnapshot({
          ...fallback.snapshot,
          generatedAt: new Date().toISOString(),
          providers,
          notices: [credentialList.message, ...fallback.snapshot.notices.slice(1)],
        }),
      }
    }

    const openRouterIds = [...new Set(credentialList.entries
      .filter((entry) => entry.provider === 'openrouter')
      .map((entry) => entry.credentialId))]
      .sort((left, right) => left.localeCompare(right))
    const openRouterResults = await Promise.all(openRouterIds.map(async (credentialId): Promise<NamedUsageResult> => {
      const result = await this.nativeCollector(credentialId, signal)
      return {
        credentialId,
        result: result.kind === 'unavailable'
          ? { kind: 'failure', code: 'unavailable', message: 'The native OpenRouter collector is unavailable.', retryable: true }
          : result,
      }
    }))
    const openRouter = openRouterProviderFromResults(openRouterResults)
    const organizationResults = await Promise.all(([
      ['openai-api', 'OpenAI API'],
      ['anthropic-api', 'Anthropic API / Claude'],
    ] as const).map(async ([provider, displayName]) => {
      const ids = [...new Set(credentialList.entries.filter((entry) => entry.provider === provider).map((entry) => entry.credentialId))]
        .sort((left, right) => left.localeCompare(right))
      const results = await Promise.all(ids.map(async (credentialId): Promise<NamedProviderResult> => {
        const result = await this.organizationCollector(provider, credentialId, request.period, signal)
        return { credentialId, result: result.kind === 'unavailable' ? { kind: 'failure', code: 'unavailable', message: 'The native organization collector is unavailable.', retryable: true } : result }
      }))
      return organizationProviderFromResults(provider, displayName, results)
    }))
    const organizations = new Map(organizationResults.map((provider) => [provider.provider, provider]))
    const geminiEntries = credentialList.entries.filter((entry) => entry.provider === 'google-ai-studio')
    const rawGemini = await this.organizationCollector('google-ai-studio', 'application-default', request.period, signal)
    const geminiResult: Exclude<DesktopProviderUsageResult, { kind: 'unavailable' }> = rawGemini.kind === 'unavailable'
      ? { kind: 'failure', code: 'unavailable', message: 'The native Google Monitoring collector is unavailable.', retryable: true }
      : rawGemini
    const providers = fallback.snapshot.providers.map((provider) => {
      if (provider.provider === 'openrouter') return openRouter
      if (provider.provider === 'openai-api' || provider.provider === 'anthropic-api') return organizations.get(provider.provider) ?? provider
      if (provider.provider === 'google-ai-studio') return geminiProviderFromResult(provider, geminiResult, geminiEntries)
      return provider
    })
    const notices = [
      openRouterIds.length
        ? `OpenRouter is live per named key (${openRouterIds.length} configured); synthetic providers remain excluded from live totals.`
        : 'The native OpenRouter collector is ready; add named key profiles to begin live collection.',
      'Gemini usage is collected only as a Google Cloud project aggregate; named API keys remain secure inventory and are never assigned inferred usage.',
      'OpenAI and Anthropic organization collectors use Admin API keys from Windows Credential Manager; consumer subscription allowances remain unsupported.',
    ]
    return {
      kind: 'success',
      snapshot: normalizeUsageSnapshot({
        ...fallback.snapshot,
        generatedAt: new Date().toISOString(),
        providers,
        notices,
      }),
    }
  }
}

export const desktopUsageAdapter = new DesktopUsageAdapter()
