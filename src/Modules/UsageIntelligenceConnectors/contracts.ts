export const connectorProviderIds = ['google-cloud-billing', 'google-gemini-api', 'openai-chatgpt-codex', 'anthropic-claude', 'google-antigravity'] as const;
export type ConnectorProviderId = (typeof connectorProviderIds)[number];
export type ConnectorCategory = 'cloud-billing' | 'api-usage' | 'subscription-link';
export type ObservationQuality = 'reported' | 'estimated' | 'delayed' | 'unavailable';
export type ConnectorAvailability = 'available' | 'linked' | 'link-required' | 'setup-required' | 'unavailable';
export type SubscriptionLinkState = 'linked' | 'link-required' | 'unavailable';
export interface ConnectorCapabilityDefinition { readonly id: string; readonly label: string; readonly description: string; readonly supportsProgrammaticUsage: boolean; readonly usageScope: 'billing-account' | 'project' | 'none'; readonly supportedQualities: readonly ObservationQuality[]; }
export interface ConnectorDefinition { readonly providerId: ConnectorProviderId; readonly displayName: string; readonly category: ConnectorCategory; readonly capabilities: readonly ConnectorCapabilityDefinition[]; }
export interface NativeBrokerReference { readonly kind: 'native-broker-ref'; readonly id: string; }
export interface NamedKeyProfileDto { readonly keyProfileId: string; readonly displayName: string; readonly brokerRef: NativeBrokerReference; }
export interface ConnectorAccountProfileDto { readonly providerId: ConnectorProviderId; readonly accountProfileId: string; readonly displayName: string; readonly assistantId?: string; readonly keyProfiles: readonly NamedKeyProfileDto[]; readonly brokerRef: NativeBrokerReference; }
export interface UsageObservationDto { readonly metricId: string; readonly label: string; readonly quality: ObservationQuality; readonly value?: number; readonly unit?: string; readonly observedAt?: string; readonly availableAt?: string; readonly note?: string; }
export interface ConnectorProfileStatusDto { readonly providerId: ConnectorProviderId; readonly accountProfileId: string; readonly assistantId?: string; readonly availability: ConnectorAvailability; readonly statusLabel: string; readonly detail?: string; readonly linkState?: SubscriptionLinkState; readonly observations: readonly UsageObservationDto[]; }
export interface ConnectorDashboardEntryDto { readonly definition: ConnectorDefinition; readonly profiles: readonly ConnectorProfileStatusDto[]; }
export interface ConnectorDashboardDto { readonly generatedAt: string; readonly entries: readonly ConnectorDashboardEntryDto[]; }
export interface ReadConnectorProfileStatusRequest { readonly providerId: ConnectorProviderId; readonly accountProfileId: string; readonly assistantId?: string; readonly brokerRef: NativeBrokerReference; readonly keyProfiles: readonly NamedKeyProfileDto[]; }
export type SubscriptionProviderId = Extract<ConnectorProviderId, 'openai-chatgpt-codex' | 'anthropic-claude' | 'google-antigravity'>;
export interface StartSubscriptionLinkRequest { readonly providerId: SubscriptionProviderId; readonly accountProfileId: string; readonly brokerRef: NativeBrokerReference; }
/** Trusted native seam. This renderer module performs no provider I/O. */
export interface NativeUsageConnectorBroker { listAccountProfiles(): Promise<unknown>; readProfileStatus(request: ReadConnectorProfileStatusRequest): Promise<unknown>; startSubscriptionLink(request: StartSubscriptionLinkRequest): Promise<unknown>; }
