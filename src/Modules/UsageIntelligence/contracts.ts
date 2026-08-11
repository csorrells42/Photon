/**
 * Stable renderer-facing schema for usage data collected by a trusted host or
 * backend. Values in this module never contain credentials or account ids.
 */
export const usageIntelligenceContractVersion = 'usage-intelligence/v1' as const

export type ProviderId =
  | 'openai-api'
  | 'chatgpt-subscription'
  | 'openrouter'
  | 'google-cloud'
  | 'google-ai-studio'
  | 'anthropic-api'

export type IntegrationReadiness = 'ready' | 'partial' | 'manual-only' | 'unsupported'

export type ProviderConnectionState =
  | 'connected'
  | 'warning'
  | 'error'
  | 'unavailable'
  | 'not-configured'

export type MetricQuality = 'reported' | 'estimated' | 'delayed' | 'unavailable' | 'not-comparable'

export type ProvenanceKind = 'host-collector' | 'manual' | 'synthetic'

export interface TimeRange {
  start: string
  end: string
  label: string
}

export interface MoneyValue {
  amount: number
  currency: string
}

export interface MoneyMetric {
  value: MoneyValue
  quality: MetricQuality
  label: string
}

export interface NumericMetric {
  value: number
  quality: MetricQuality
  label: string
}

export interface TokenMetric {
  input: number
  output: number
  quality: MetricQuality
}

export interface BillingPeriod {
  label: string
  endsAt?: string
  resetsAt?: string
}

export interface UsageProvenance {
  kind: ProvenanceKind
  label: string
  collectedAt?: string
  detail: string
}

export interface UsageTrendPoint {
  at: string
  label: string
  spend?: MoneyMetric
  requests?: NumericMetric
  tokens?: TokenMetric
}

export interface UsageCredentialBreakdown {
  /** Safe native-vault identifier such as `ali` or `scarlett`; never a secret or provider account id. */
  credentialId: string
  displayName: string
  state: ProviderConnectionState
  statusMessage: string
  spend?: MoneyMetric
  remaining?: MoneyMetric
  billingPeriod?: BillingPeriod
  provenance: UsageProvenance
}

export interface ProviderUsage {
  provider: ProviderId
  displayName: string
  readiness: IntegrationReadiness
  state: ProviderConnectionState
  statusMessage: string
  spend?: MoneyMetric
  remaining?: MoneyMetric
  requests?: NumericMetric
  tokens?: TokenMetric
  billingPeriod?: BillingPeriod
  trend: UsageTrendPoint[]
  provenance: UsageProvenance
  credentialBreakdown?: UsageCredentialBreakdown[]
}

export interface SpendSummary {
  total?: MoneyMetric
  includedProviders: ProviderId[]
  excludedProviders: ProviderId[]
  comparisonNote: string
}

export interface UsageDashboardSnapshot {
  contractVersion: typeof usageIntelligenceContractVersion
  generatedAt: string
  period: TimeRange
  providers: ProviderUsage[]
  spendSummary: SpendSummary
  notices: string[]
}

/**
 * A host-resolved opaque handle. The renderer may pass it to a host bridge but
 * must never resolve, render, store, or log credential material.
 */
export interface CredentialReference {
  credentialRef: string
}

export interface UsageCollectionRequest {
  period: TimeRange
  providers?: ProviderId[]
  credentials?: Partial<Record<ProviderId, CredentialReference>>
}

export interface ProviderDefinition {
  id: ProviderId
  displayName: string
  readiness: IntegrationReadiness
  supportsProgrammaticUsage: boolean
  summary: string
}

export const providerDefinitions: readonly ProviderDefinition[] = [
  {
    id: 'openai-api',
    displayName: 'OpenAI API',
    readiness: 'ready',
    supportsProgrammaticUsage: true,
    summary: 'Organization usage and cost reporting can be collected by a trusted host.',
  },
  {
    id: 'chatgpt-subscription',
    displayName: 'ChatGPT subscription',
    readiness: 'unsupported',
    supportsProgrammaticUsage: false,
    summary: 'No supported public programmatic subscription-usage interface is assumed.',
  },
  {
    id: 'openrouter',
    displayName: 'OpenRouter',
    readiness: 'ready',
    supportsProgrammaticUsage: true,
    summary: 'Managed-key credit totals can be collected by a trusted host.',
  },
  {
    id: 'google-cloud',
    displayName: 'Google Cloud / APIs',
    readiness: 'partial',
    supportsProgrammaticUsage: true,
    summary: 'Budgets and exported billing data are available, with reporting delay and setup requirements.',
  },
  {
    id: 'google-ai-studio',
    displayName: 'Google AI Studio / Gemini API',
    readiness: 'partial',
    supportsProgrammaticUsage: false,
    summary: 'AI Studio dashboards are official; host collection should use the applicable Cloud Billing path where permitted.',
  },
  {
    id: 'anthropic-api',
    displayName: 'Anthropic API / Claude',
    readiness: 'ready',
    supportsProgrammaticUsage: true,
    summary: 'Organization usage and cost reporting can be collected with an Admin API key in the trusted host.',
  },
]
