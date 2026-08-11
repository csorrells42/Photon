import {
  HERMES_PROFILE_RUNTIME_LIVE_BOUNDS as B,
  type AccountStatus,
  type ActiveProfileState,
  type AuthProviderMetadata,
  type ConfigurationFieldKind,
  type ConfigurationFieldMetadata,
  type ConfigurationMetadata,
  type ContextDocument,
  type LiveCapability,
  type LiveCapabilityId,
  type LiveErrorCode,
  type LiveRequestIdentity,
  type ProfileInventoryItem,
  type ProfileRuntimeLiveSnapshot,
  type ProviderStatus,
} from './contracts';

export class LiveNormalizationError extends Error {
  public constructor(public readonly code: LiveErrorCode, message: string) {
    super(message);
    this.name = 'LiveNormalizationError';
  }
}

type UnknownRecord = Record<string, unknown>;

const SECRET_FIELD_KEY = /(?:api[-_]?key|token|password|secret|credential)/i;

export function assertValidRequest(request: LiveRequestIdentity): void {
  if (request.adapterVersion !== 'hermes-profile-runtime-live/v1') {
    throw new LiveNormalizationError('invalid-request', 'Unsupported live adapter contract version.');
  }
  boundedRequiredString(request.profileId, 'profileId', B.identifierCharacters);
  boundedRequiredString(request.correlationId, 'correlationId', B.identifierCharacters);
  if (request.expectedRevision !== undefined) {
    boundedRequiredString(request.expectedRevision, 'expectedRevision', B.identifierCharacters);
  }
}

export function assertResponseIdentity(raw: unknown, request: LiveRequestIdentity): void {
  if (!isRecord(raw)) return;
  const responseProfile = firstString(raw, ['profileId', 'profile_id', 'profile']);
  if (responseProfile !== null && responseProfile !== request.profileId) {
    throw new LiveNormalizationError('cross-profile-response', 'The upstream response identified a different profile.');
  }
  const responseCorrelation = firstString(raw, ['correlationId', 'correlation_id']);
  if (responseCorrelation !== null && responseCorrelation !== request.correlationId) {
    throw new LiveNormalizationError(
      'cross-correlation-response',
      'The upstream response identified a different correlation.',
    );
  }
}

export function normalizeProfiles(raw: unknown): readonly ProfileInventoryItem[] {
  const root = record(raw, 'profile inventory');
  const values = boundedArray(root.profiles, 'profiles', B.profiles);
  return values.map((entry, index) => {
    const item = record(entry, `profiles[${index}]`);
    return {
      profileId: boundedRequiredString(item.name, 'profile name', B.identifierCharacters),
      description: boundedOptionalString(item.description, 'profile description', B.descriptionCharacters),
      isDefault: boolean(item.is_default, 'profile is_default'),
      modelId: boundedOptionalString(item.model, 'profile model', B.labelCharacters),
      providerId: boundedOptionalString(item.provider, 'profile provider', B.identifierCharacters),
      skillCount: boundedInteger(item.skill_count, 'profile skill_count', 0, 100_000),
      gatewayRunning: boolean(item.gateway_running, 'profile gateway_running'),
      descriptionAuto: boolean(item.description_auto, 'profile description_auto'),
      hasAlias: boolean(item.has_alias, 'profile has_alias'),
    };
  });
}

export function normalizeActiveProfile(raw: unknown): ActiveProfileState {
  const root = record(raw, 'active profile');
  return {
    stickyProfileId: boundedRequiredString(root.active, 'active profile', B.identifierCharacters),
    currentProcessProfileId: boundedRequiredString(root.current, 'current profile', B.identifierCharacters),
  };
}

export function normalizeSoul(raw: unknown): readonly ContextDocument[] {
  const root = record(raw, 'soul document');
  const exists = boolean(root.exists, 'soul exists');
  const content = boundedRequiredString(root.content, 'soul content', B.documentCharacters, true);
  return [{ kind: 'soul', exists, content }];
}

export function normalizeConfigurationMetadata(raw: unknown): ConfigurationMetadata {
  const root = record(raw, 'configuration schema');
  const fieldsRecord = record(root.fields, 'configuration fields');
  const entries = Object.entries(fieldsRecord);
  if (entries.length > B.configurationFields) {
    throw new LiveNormalizationError('oversized-response', 'Configuration field count exceeds the adapter bound.');
  }

  const fields = entries.map(([key, value], index): ConfigurationFieldMetadata => {
    const field = record(value, `configuration field ${index}`);
    const kind = normalizeFieldKind(field.type);
    const sensitive = kind === 'secret' || SECRET_FIELD_KEY.test(key);
    return {
      key: boundedRequiredString(key, 'configuration key', B.identifierCharacters),
      label:
        boundedOptionalString(field.label, 'configuration label', B.labelCharacters) ??
        boundedRequiredString(key, 'configuration key', B.identifierCharacters),
      description: boundedOptionalString(field.description, 'configuration description', B.descriptionCharacters),
      category: boundedOptionalString(field.category, 'configuration category', B.labelCharacters),
      kind,
      searchable: optionalBoolean(field.searchable, false, 'configuration searchable'),
      clearable: optionalBoolean(field.clearable, false, 'configuration clearable'),
      sensitive,
      options: sensitive ? [] : normalizeOptions(field.options),
    };
  });

  const categoryOrder =
    root.category_order === undefined
      ? []
      : boundedArray(root.category_order, 'category order', B.configurationFields).map((value) =>
          boundedRequiredString(value, 'configuration category', B.labelCharacters),
        );

  return { categoryOrder, fields };
}

export function normalizeProviderStatuses(raw: unknown): readonly ProviderStatus[] {
  const root = record(raw, 'provider status');
  return boundedArray(root.providers, 'providers', B.providers).map((entry, index) => {
    const provider = record(entry, `providers[${index}]`);
    const status = record(provider.status, `providers[${index}].status`);
    return {
      providerId: boundedRequiredString(provider.id, 'provider id', B.identifierCharacters),
      label: boundedRequiredString(provider.name, 'provider name', B.labelCharacters),
      flow: boundedOptionalString(provider.flow, 'provider flow', B.labelCharacters),
      connected: boolean(status.logged_in, 'provider logged_in'),
      expiresAt: boundedOptionalString(status.expires_at, 'provider expiry', B.labelCharacters),
      hasRefreshToken: optionalBoolean(status.has_refresh_token, false, 'provider has_refresh_token'),
    };
  });
}

export function normalizeAuthProviders(raw: unknown): readonly AuthProviderMetadata[] {
  const root = record(raw, 'auth providers');
  return boundedArray(root.providers, 'auth providers', B.authProviders).map((entry, index) => {
    const provider = record(entry, `auth providers[${index}]`);
    return {
      providerId: boundedRequiredString(provider.name, 'auth provider name', B.identifierCharacters),
      label: boundedRequiredString(provider.display_name, 'auth provider display name', B.labelCharacters),
      supportsPassword: boolean(provider.supports_password, 'auth provider supports_password'),
    };
  });
}

export function normalizeAccount(raw: unknown): AccountStatus {
  const root = record(raw, 'account');
  boundedRequiredString(root.user_id, 'account user_id', B.identifierCharacters);
  return {
    signedIn: true,
    displayName: boundedOptionalString(root.display_name, 'account display name', B.labelCharacters),
    providerId: boundedOptionalString(root.provider, 'account provider', B.identifierCharacters),
    expiresAt: boundedOptionalString(root.expires_at, 'account expiry', B.labelCharacters),
  };
}

export function signedOutAccount(): AccountStatus {
  return { signedIn: false, displayName: null, providerId: null, expiresAt: null };
}

export function revisionFor(snapshot: ProfileRuntimeLiveSnapshot): string {
  const text = JSON.stringify(snapshot);
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return `fnv1a32-${(hash >>> 0).toString(16).padStart(8, '0')}`;
}

const AVAILABLE_CAPABILITIES: readonly LiveCapabilityId[] = [
  'read-profile-inventory',
  'read-active-profile',
  'read-soul-document',
  'read-configuration-metadata',
  'read-account-status',
];

const UNAVAILABLE_REASONS: Readonly<Partial<Record<LiveCapabilityId, string>>> = {
  'read-provider-status': 'The upstream OAuth status DTO contains a secret-derived token preview; a renderer-safe projection is required.',
  'read-persona-document': 'No source-confirmed profile-scoped persona read route exists.',
  'read-context-document': 'No source-confirmed profile-scoped context read route exists.',
  'select-active-profile': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'create-profile': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'rename-profile': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'delete-profile': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'save-document': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'save-configuration': 'Mutation acknowledgement, conflicts, and ownership are not proven for this lane.',
  'grant-permission': 'No source-confirmed profile-scoped permission grant route exists.',
};

export const LIVE_CAPABILITIES: readonly LiveCapability[] = [
  ...AVAILABLE_CAPABILITIES.map((capabilityId) => ({ capabilityId, availability: 'available' as const, reason: null })),
  ...Object.entries(UNAVAILABLE_REASONS).map(([capabilityId, reason]) => ({
    capabilityId: capabilityId as LiveCapabilityId,
    availability: 'unavailable' as const,
    reason: reason ?? 'Unavailable.',
  })),
];

export function capability(capabilityId: LiveCapabilityId): LiveCapability {
  return (
    LIVE_CAPABILITIES.find((item) => item.capabilityId === capabilityId) ?? {
      capabilityId,
      availability: 'unavailable',
      reason: 'Capability is not implemented by this adapter version.',
    }
  );
}

function normalizeOptions(raw: unknown): readonly { value: string; label: string }[] {
  if (raw === undefined || raw === null) return [];
  return boundedArray(raw, 'configuration options', B.configurationOptions).map((entry) => {
    if (typeof entry === 'string') {
      const value = boundedRequiredString(entry, 'configuration option', B.labelCharacters);
      return { value, label: value };
    }
    const option = record(entry, 'configuration option');
    const value = boundedRequiredString(option.value, 'configuration option value', B.labelCharacters);
    return {
      value,
      label: boundedOptionalString(option.label, 'configuration option label', B.labelCharacters) ?? value,
    };
  });
}

function normalizeFieldKind(raw: unknown): ConfigurationFieldKind {
  if (typeof raw !== 'string') return 'unknown';
  switch (raw.toLowerCase()) {
    case 'bool':
    case 'boolean':
      return 'boolean';
    case 'int':
    case 'integer':
    case 'float':
    case 'number':
      return 'number';
    case 'select':
    case 'choice':
      return 'select';
    case 'secret':
    case 'password':
      return 'secret';
    case 'str':
    case 'string':
    case 'text':
      return 'text';
    default:
      return 'unknown';
  }
}

function record(value: unknown, name: string): UnknownRecord {
  if (!isRecord(value)) throw new LiveNormalizationError('malformed-response', `${name} must be an object.`);
  return value;
}

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function boundedArray(value: unknown, name: string, maximum: number): readonly unknown[] {
  if (!Array.isArray(value)) throw new LiveNormalizationError('malformed-response', `${name} must be an array.`);
  if (value.length > maximum) {
    throw new LiveNormalizationError('oversized-response', `${name} exceeds the collection bound.`);
  }
  return value;
}

function boundedRequiredString(
  value: unknown,
  name: string,
  maximum: number,
  allowEmpty = false,
): string {
  if (typeof value !== 'string' || (!allowEmpty && value.length === 0)) {
    throw new LiveNormalizationError('malformed-response', `${name} must be a string.`);
  }
  if (value.length > maximum) throw new LiveNormalizationError('oversized-response', `${name} exceeds its bound.`);
  return value;
}

function boundedOptionalString(value: unknown, name: string, maximum: number): string | null {
  if (value === undefined || value === null || value === '') return null;
  return boundedRequiredString(value, name, maximum);
}

function boolean(value: unknown, name: string): boolean {
  if (typeof value !== 'boolean') throw new LiveNormalizationError('malformed-response', `${name} must be boolean.`);
  return value;
}

function optionalBoolean(value: unknown, fallback: boolean, name: string): boolean {
  if (value === undefined || value === null) return fallback;
  return boolean(value, name);
}

function boundedInteger(value: unknown, name: string, minimum: number, maximum: number): number {
  if (!Number.isInteger(value) || (value as number) < minimum || (value as number) > maximum) {
    throw new LiveNormalizationError('malformed-response', `${name} must be a bounded integer.`);
  }
  return value as number;
}

function firstString(recordValue: UnknownRecord, keys: readonly string[]): string | null {
  for (const key of keys) {
    if (key in recordValue) {
      if (typeof recordValue[key] !== 'string') {
        throw new LiveNormalizationError('malformed-response', `${key} must be a string when present.`);
      }
      return recordValue[key] as string;
    }
  }
  return null;
}

