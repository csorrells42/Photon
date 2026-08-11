export const HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION = 'hermes-profile-runtime-live/v1' as const;

export const HERMES_PROFILE_RUNTIME_LIVE_BOUNDS = Object.freeze({
  responseBytes: 512 * 1024,
  documentBytes: 128 * 1024,
  profiles: 64,
  documents: 3,
  configurationFields: 128,
  configurationOptions: 64,
  providers: 64,
  authProviders: 32,
  notices: 32,
  identifierCharacters: 128,
  labelCharacters: 256,
  descriptionCharacters: 2_048,
  documentCharacters: 131_072,
  correlationHistory: 512,
  maxTransportConcurrency: 6,
  defaultTransportConcurrency: 3,
  maxPendingLoads: 16,
});

export type LiveAdapterVersion = typeof HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION;

export interface LiveRequestIdentity {
  readonly adapterVersion: LiveAdapterVersion;
  readonly profileId: string;
  readonly correlationId: string;
  readonly expectedRevision?: string;
}

export type LiveResultStatus = 'ready' | 'partial' | 'unavailable' | 'error' | 'cancelled';
export type LiveErrorCode =
  | 'cancelled'
  | 'duplicate-correlation'
  | 'invalid-request'
  | 'malformed-response'
  | 'oversized-response'
  | 'cross-profile-response'
  | 'cross-correlation-response'
  | 'stale-response'
  | 'transport-error'
  | 'http-error'
  | 'busy'
  | 'unavailable';

export interface LiveError {
  readonly code: LiveErrorCode;
  readonly message: string;
}

export interface LiveAdapterResult<T> extends LiveRequestIdentity {
  readonly revision: string | null;
  readonly status: LiveResultStatus;
  readonly value: T | null;
  readonly notices: readonly string[];
  readonly error?: LiveError;
}

export type SectionAvailability = 'available' | 'partial' | 'unavailable' | 'error';

export interface LiveSection<T> {
  readonly availability: SectionAvailability;
  readonly value: T | null;
  readonly message?: string;
}

export interface ProfileInventoryItem {
  readonly profileId: string;
  readonly description: string | null;
  readonly isDefault: boolean;
  readonly modelId: string | null;
  readonly providerId: string | null;
  readonly skillCount: number;
  readonly gatewayRunning: boolean;
  readonly descriptionAuto: boolean;
  readonly hasAlias: boolean;
}

export interface ActiveProfileState {
  readonly stickyProfileId: string;
  readonly currentProcessProfileId: string;
}

export type ContextDocumentKind = 'soul';

export interface ContextDocument {
  readonly kind: ContextDocumentKind;
  readonly exists: boolean;
  readonly content: string;
}

export type ConfigurationFieldKind = 'boolean' | 'number' | 'select' | 'secret' | 'text' | 'unknown';

export interface ConfigurationOption {
  readonly value: string;
  readonly label: string;
}

export interface ConfigurationFieldMetadata {
  readonly key: string;
  readonly label: string;
  readonly description: string | null;
  readonly category: string | null;
  readonly kind: ConfigurationFieldKind;
  readonly searchable: boolean;
  readonly clearable: boolean;
  readonly sensitive: boolean;
  readonly options: readonly ConfigurationOption[];
}

export interface ConfigurationMetadata {
  readonly categoryOrder: readonly string[];
  readonly fields: readonly ConfigurationFieldMetadata[];
}

export interface ProviderStatus {
  readonly providerId: string;
  readonly label: string;
  readonly flow: string | null;
  readonly connected: boolean;
  readonly expiresAt: string | null;
  readonly hasRefreshToken: boolean;
}

export interface AuthProviderMetadata {
  readonly providerId: string;
  readonly label: string;
  readonly supportsPassword: boolean;
}

export interface AccountStatus {
  readonly signedIn: boolean;
  readonly displayName: string | null;
  readonly providerId: string | null;
  readonly expiresAt: string | null;
}

export type LiveCapabilityId =
  | 'read-profile-inventory'
  | 'read-active-profile'
  | 'read-soul-document'
  | 'read-persona-document'
  | 'read-context-document'
  | 'read-configuration-metadata'
  | 'read-provider-status'
  | 'read-account-status'
  | 'select-active-profile'
  | 'create-profile'
  | 'rename-profile'
  | 'delete-profile'
  | 'save-document'
  | 'save-configuration'
  | 'grant-permission';

export interface LiveCapability {
  readonly capabilityId: LiveCapabilityId;
  readonly availability: 'available' | 'unavailable';
  readonly reason: string | null;
}

export interface ProfileRuntimeLiveSnapshot {
  readonly profiles: LiveSection<readonly ProfileInventoryItem[]>;
  readonly activeProfile: LiveSection<ActiveProfileState>;
  readonly contextDocuments: LiveSection<readonly ContextDocument[]>;
  readonly configurationMetadata: LiveSection<ConfigurationMetadata>;
  readonly providerStatuses: LiveSection<readonly ProviderStatus[]>;
  readonly authProviders: LiveSection<readonly AuthProviderMetadata[]>;
  readonly account: LiveSection<AccountStatus>;
  readonly capabilities: readonly LiveCapability[];
}

export interface LiveTransportBodyReader {
  read(): Promise<{ readonly done: boolean; readonly value?: Uint8Array }>;
  cancel(reason?: unknown): Promise<void>;
  releaseLock?(): void;
}

export interface LiveTransportBody {
  getReader(): LiveTransportBodyReader;
}

export interface LiveTransportResponse {
  readonly ok: boolean;
  readonly status: number;
  readonly headers: { get(name: string): string | null };
  readonly body: LiveTransportBody | null;
}

export type LiveTransport = (
  url: string,
  init: { readonly method: 'GET'; readonly signal: AbortSignal; readonly headers: Readonly<Record<string, string>> },
) => Promise<LiveTransportResponse>;

export interface LiveAdapterOptions {
  readonly transport: LiveTransport;
  readonly maxConcurrency?: number;
  readonly maxPendingLoads?: number;
}

export interface HermesProfileRuntimeLiveAdapterContract {
  load(request: LiveRequestIdentity, signal: AbortSignal): Promise<LiveAdapterResult<ProfileRuntimeLiveSnapshot>>;
  unavailable(
    request: LiveRequestIdentity,
    capabilityId: LiveCapabilityId,
    signal: AbortSignal,
  ): Promise<LiveAdapterResult<LiveCapability>>;
}

