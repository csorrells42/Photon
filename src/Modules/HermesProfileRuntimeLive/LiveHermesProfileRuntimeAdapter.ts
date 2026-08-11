import { BoundedSemaphore } from './BoundedSemaphore';
import {
  HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
  HERMES_PROFILE_RUNTIME_LIVE_BOUNDS as B,
  type AccountStatus,
  type ActiveProfileState,
  type AuthProviderMetadata,
  type ConfigurationMetadata,
  type ContextDocument,
  type HermesProfileRuntimeLiveAdapterContract,
  type LiveAdapterOptions,
  type LiveAdapterResult,
  type LiveCapability,
  type LiveCapabilityId,
  type LiveError,
  type LiveRequestIdentity,
  type LiveSection,
  type LiveTransportBody,
  type ProfileInventoryItem,
  type ProfileRuntimeLiveSnapshot,
  type ProviderStatus,
} from './contracts';
import {
  LIVE_CAPABILITIES,
  LiveNormalizationError,
  assertResponseIdentity,
  assertValidRequest,
  capability,
  normalizeAccount,
  normalizeActiveProfile,
  normalizeAuthProviders,
  normalizeConfigurationMetadata,
  normalizeProfiles,
  normalizeSoul,
  revisionFor,
  signedOutAccount,
} from './normalization';

type Collected<T> = {
  readonly section: LiveSection<T>;
  readonly error?: LiveNormalizationError;
  readonly notice?: string;
};

const JSON_HEADERS = Object.freeze({ Accept: 'application/json' });
const SAFE_PROVIDER_STATUS_REASON =
  'Provider connection status is unavailable until Hermes exposes a renderer-safe projection.';

function unavailableProviderStatuses(): Collected<readonly ProviderStatus[]> {
  const error = new LiveNormalizationError('unavailable', SAFE_PROVIDER_STATUS_REASON);
  return {
    section: { availability: 'unavailable', value: null, message: SAFE_PROVIDER_STATUS_REASON },
    error,
    notice: `Provider status: ${SAFE_PROVIDER_STATUS_REASON}`,
  };
}

export class LiveHermesProfileRuntimeAdapter implements HermesProfileRuntimeLiveAdapterContract {
  public static readonly adapterVersion = HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION;

  private readonly semaphore: BoundedSemaphore;
  private readonly maxPendingLoads: number;
  private readonly correlations = new Map<string, { profileId: string; active: boolean }>();
  private pendingLoads = 0;

  public constructor(private readonly options: LiveAdapterOptions) {
    if (typeof options.transport !== 'function') throw new Error('An injected live transport is required.');
    const concurrency = boundedOption(
      options.maxConcurrency,
      B.defaultTransportConcurrency,
      1,
      B.maxTransportConcurrency,
      'maxConcurrency',
    );
    this.maxPendingLoads = boundedOption(
      options.maxPendingLoads,
      B.maxPendingLoads,
      1,
      B.maxPendingLoads,
      'maxPendingLoads',
    );
    this.semaphore = new BoundedSemaphore(concurrency);
  }

  public async load(
    request: LiveRequestIdentity,
    signal: AbortSignal,
  ): Promise<LiveAdapterResult<ProfileRuntimeLiveSnapshot>> {
    const rejected = this.begin<ProfileRuntimeLiveSnapshot>(request, signal);
    if (rejected) return rejected;

    try {
      const profile = encodeURIComponent(request.profileId);
      const [profiles, activeProfile, contextDocuments, configurationMetadata, providerStatuses, authProviders, account] =
        await Promise.all([
          this.collect('Profile inventory', () =>
            this.readAndNormalize('/api/profiles', request, signal, B.responseBytes, normalizeProfiles),
          ),
          this.collect('Active profile', () =>
            this.readAndNormalize('/api/profiles/active', request, signal, B.responseBytes, normalizeActiveProfile),
          ),
          this.collect('SOUL document', () =>
            this.readAndNormalize(
              `/api/profiles/${profile}/soul`,
              request,
              signal,
              B.documentBytes,
              normalizeSoul,
            ),
          ),
          this.collect('Configuration metadata', () =>
            this.readAndNormalize(
              `/api/config/schema?profile=${profile}`,
              request,
              signal,
              B.responseBytes,
              normalizeConfigurationMetadata,
            ),
          ),
          // The broad OAuth DTO includes status.token_preview. Dropping that
          // field after parsing is too late because it has already crossed
          // into WebView memory. Fail closed until a renderer-safe projection
          // exists at the server or host boundary.
          Promise.resolve(unavailableProviderStatuses()),
          this.collect('Auth providers', () =>
            this.readAndNormalize('/api/auth/providers', request, signal, B.responseBytes, normalizeAuthProviders),
          ),
          this.collectAccount(request, signal),
        ]);

      if (signal.aborted) return this.cancelled(request);

      const collected = [
        profiles,
        activeProfile,
        contextDocuments,
        configurationMetadata,
        providerStatuses,
        authProviders,
        account,
      ] as const;
      const identityError = collected
        .map((item) => item.error)
        .find(
          (error) =>
            error?.code === 'cross-profile-response' || error?.code === 'cross-correlation-response',
        );
      if (identityError) return this.failure(request, identityError);

      if (profiles.section.availability !== 'available' || profiles.section.value === null) {
        return this.failure(
          request,
          profiles.error ?? new LiveNormalizationError('malformed-response', 'Profile inventory is unavailable.'),
        );
      }
      if (!profiles.section.value.some((item) => item.profileId === request.profileId)) {
        return this.failure(
          request,
          new LiveNormalizationError(
            'cross-profile-response',
            'The requested profile was absent from the returned inventory.',
          ),
        );
      }

      const snapshot: ProfileRuntimeLiveSnapshot = {
        profiles: profiles.section,
        activeProfile: activeProfile.section,
        contextDocuments: contextDocuments.section,
        configurationMetadata: configurationMetadata.section,
        providerStatuses: providerStatuses.section,
        authProviders: authProviders.section,
        account: account.section,
        capabilities: LIVE_CAPABILITIES,
      };
      const revision = revisionFor(snapshot);
      if (request.expectedRevision !== undefined && request.expectedRevision !== revision) {
        return this.failure(
          request,
          new LiveNormalizationError('stale-response', 'The response revision does not match the expected revision.'),
          revision,
        );
      }

      const notices = collected
        .map((item) => item.notice)
        .filter((item): item is string => item !== undefined)
        .slice(0, B.notices);
      const status = collected.some((item) => item.section.availability !== 'available') ? 'partial' : 'ready';
      return {
        ...request,
        revision,
        status,
        value: snapshot,
        notices,
      };
    } catch (error) {
      if (signal.aborted || isAbort(error)) return this.cancelled(request);
      return this.failure(request, asLiveError(error));
    } finally {
      this.finish(request.correlationId);
    }
  }

  public async unavailable(
    request: LiveRequestIdentity,
    capabilityId: LiveCapabilityId,
    signal: AbortSignal,
  ): Promise<LiveAdapterResult<LiveCapability>> {
    const rejected = this.begin<LiveCapability>(request, signal);
    if (rejected) return rejected;
    try {
      const item = capability(capabilityId);
      if (item.availability === 'available') {
        return this.failure(
          request,
          new LiveNormalizationError('invalid-request', 'This capability is available through the read snapshot.'),
        );
      }
      return {
        ...request,
        revision: null,
        status: 'unavailable',
        value: item,
        notices: [item.reason ?? 'Capability unavailable.'],
        error: { code: 'unavailable', message: item.reason ?? 'Capability unavailable.' },
      };
    } finally {
      this.finish(request.correlationId);
    }
  }

  private begin<T>(
    request: LiveRequestIdentity,
    signal: AbortSignal,
  ): LiveAdapterResult<T> | null {
    try {
      assertValidRequest(request);
    } catch (error) {
      return this.failure(request, asLiveError(error));
    }
    if (signal.aborted) return this.cancelled(request);
    if (this.correlations.has(request.correlationId)) {
      return this.failure(
        request,
        new LiveNormalizationError('duplicate-correlation', 'The correlation identifier has already been used.'),
      );
    }
    if (this.pendingLoads >= this.maxPendingLoads) {
      return this.failure(
        request,
        new LiveNormalizationError('busy', 'The live adapter pending-load bound has been reached.'),
      );
    }
    this.pendingLoads += 1;
    this.correlations.set(request.correlationId, { profileId: request.profileId, active: true });
    return null;
  }

  private finish(correlationId: string): void {
    const entry = this.correlations.get(correlationId);
    if (entry?.active) {
      this.correlations.set(correlationId, { ...entry, active: false });
      this.pendingLoads -= 1;
    }
    while (this.correlations.size > B.correlationHistory) {
      const completed = [...this.correlations].find(([, value]) => !value.active);
      if (!completed) break;
      this.correlations.delete(completed[0]);
    }
  }

  private async collect<T>(label: string, operation: () => Promise<T>): Promise<Collected<T>> {
    try {
      return { section: { availability: 'available', value: await operation() } };
    } catch (error) {
      if (isAbort(error)) throw error;
      const normalized = asLiveError(error);
      const availability = normalized.code === 'unavailable' ? 'unavailable' : 'error';
      return {
        section: { availability, value: null, message: normalized.message },
        error: normalized,
        notice: `${label}: ${normalized.message}`,
      };
    }
  }

  private async collectAccount(
    request: LiveRequestIdentity,
    signal: AbortSignal,
  ): Promise<Collected<AccountStatus>> {
    try {
      const raw = await this.readJson('/api/auth/me', request, signal, B.responseBytes, true);
      if (raw === SIGNED_OUT) {
        return { section: { availability: 'available', value: signedOutAccount() } };
      }
      return { section: { availability: 'available', value: normalizeAccount(raw) } };
    } catch (error) {
      if (isAbort(error)) throw error;
      const normalized = asLiveError(error);
      return {
        section: {
          availability: normalized.code === 'unavailable' ? 'unavailable' : 'error',
          value: null,
          message: normalized.message,
        },
        error: normalized,
        notice: `Account status: ${normalized.message}`,
      };
    }
  }

  private async readAndNormalize<T>(
    url: string,
    request: LiveRequestIdentity,
    signal: AbortSignal,
    maximumBytes: number,
    normalize: (raw: unknown) => T,
  ): Promise<T> {
    const raw = await this.readJson(url, request, signal, maximumBytes, false);
    if (raw === SIGNED_OUT) {
      throw new LiveNormalizationError('malformed-response', 'Unexpected signed-out response.');
    }
    return normalize(raw);
  }

  private async readJson(
    url: string,
    request: LiveRequestIdentity,
    signal: AbortSignal,
    maximumBytes: number,
    allowUnauthorized: boolean,
  ): Promise<unknown | typeof SIGNED_OUT> {
    const release = await this.semaphore.acquire(signal);
    try {
      const response = await this.options.transport(url, { method: 'GET', signal, headers: JSON_HEADERS });
      if (signal.aborted) throw abortException();
      if (allowUnauthorized && response.status === 401) return SIGNED_OUT;
      if (response.status === 404 || response.status === 405) {
        throw new LiveNormalizationError('unavailable', 'The source-confirmed route is unavailable in this runtime.');
      }
      if (!response.ok) {
        throw new LiveNormalizationError('http-error', `The route returned HTTP ${response.status}.`);
      }

      const contentLength = response.headers.get('content-length');
      if (contentLength !== null) {
        const parsed = Number(contentLength);
        if (!Number.isFinite(parsed) || parsed < 0) {
          throw new LiveNormalizationError('malformed-response', 'The response Content-Length is malformed.');
        }
        if (parsed > maximumBytes) {
          throw new LiveNormalizationError('oversized-response', 'The response exceeds the byte bound.');
        }
      }

      const bytes = await readBoundedBody(response.body, maximumBytes, signal);
      let raw: unknown;
      try {
        raw = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
      } catch {
        throw new LiveNormalizationError('malformed-response', 'The route returned malformed JSON.');
      }
      assertResponseIdentity(raw, request);
      return raw;
    } catch (error) {
      if (signal.aborted || isAbort(error)) throw abortException();
      if (error instanceof LiveNormalizationError) throw error;
      throw new LiveNormalizationError('transport-error', 'The injected transport failed.');
    } finally {
      release();
    }
  }

  private failure<T>(
    request: LiveRequestIdentity,
    error: LiveNormalizationError,
    revision: string | null = null,
  ): LiveAdapterResult<T> {
    return {
      ...request,
      revision,
      status: 'error',
      value: null,
      notices: [],
      error: { code: error.code, message: error.message },
    };
  }

  private cancelled<T>(request: LiveRequestIdentity): LiveAdapterResult<T> {
    const error: LiveError = { code: 'cancelled', message: 'The request was cancelled.' };
    return { ...request, revision: null, status: 'cancelled', value: null, notices: [], error };
  }
}

async function readBoundedBody(
  body: LiveTransportBody | null,
  maximumBytes: number,
  signal: AbortSignal,
): Promise<Uint8Array> {
  if (body === null) return new Uint8Array();
  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      if (signal.aborted) {
        await cancelReader(reader);
        throw abortException();
      }
      const chunk = await reader.read();
      if (chunk.done) break;
      if (!(chunk.value instanceof Uint8Array)) {
        await cancelReader(reader);
        throw new LiveNormalizationError('malformed-response', 'The response body contained an invalid chunk.');
      }
      total += chunk.value.byteLength;
      if (total > maximumBytes) {
        await cancelReader(reader);
        throw new LiveNormalizationError('oversized-response', 'The response exceeds the byte bound.');
      }
      chunks.push(chunk.value);
    }
  } finally {
    reader.releaseLock?.();
  }

  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return result;
}

async function cancelReader(reader: { cancel(reason?: unknown): Promise<void> }): Promise<void> {
  try {
    await reader.cancel();
  } catch {
    // Cancellation is best-effort; the bounded read still terminates locally.
  }
}
const SIGNED_OUT = Symbol('signed-out');

function boundedOption(
  value: number | undefined,
  fallback: number,
  minimum: number,
  maximum: number,
  name: string,
): number {
  const selected = value ?? fallback;
  if (!Number.isInteger(selected) || selected < minimum || selected > maximum) {
    throw new Error(`${name} must be an integer from ${minimum} through ${maximum}.`);
  }
  return selected;
}

function isAbort(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

function abortException(): Error {
  const error = new Error('Operation cancelled.');
  error.name = 'AbortError';
  return error;
}

function asLiveError(error: unknown): LiveNormalizationError {
  return error instanceof LiveNormalizationError
    ? error
    : new LiveNormalizationError('transport-error', 'The injected transport failed.');
}


