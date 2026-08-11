import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
  HERMES_PROFILE_RUNTIME_LIVE_BOUNDS,
  LiveHermesProfileRuntimeAdapter,
  type LiveRequestIdentity,
  type LiveTransport,
  type LiveTransportResponse,
} from './index';

const PROFILE = 'alpha';

function request(correlationId: string, expectedRevision?: string): LiveRequestIdentity {
  return {
    adapterVersion: HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
    profileId: PROFILE,
    correlationId,
    ...(expectedRevision === undefined ? {} : { expectedRevision }),
  };
}

function jsonResponse(value: unknown, status = 200, extraHeaders: Record<string, string> = {}): LiveTransportResponse {
  const bytes = new TextEncoder().encode(JSON.stringify(value));
  const headers = new Map<string, string>(
    Object.entries({ 'content-length': String(bytes.byteLength), ...extraHeaders }).map(([key, item]) => [
      key.toLowerCase(),
      item,
    ]),
  );
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (name) => headers.get(name.toLowerCase()) ?? null },
    body: {
      getReader: () => {
        let sent = false;
        return {
          read: async () => {
            if (sent) return { done: true };
            sent = true;
            return { done: false, value: bytes };
          },
          cancel: async () => {
            sent = true;
          },
          releaseLock: () => undefined,
        };
      },
    },
  };
}

function fixtures(): Record<string, LiveTransportResponse> {
  return {
    '/api/profiles': jsonResponse({
      profiles: [
        {
          name: PROFILE,
          path: 'C:/private/profile',
          is_default: true,
          model: 'model-safe',
          provider: 'provider-safe',
          has_env: true,
          skill_count: 2,
          gateway_running: false,
          description: 'Primary',
          description_auto: false,
          distribution_source: 'C:/private/distribution',
          has_alias: true,
        },
      ],
    }),
    '/api/profiles/active': jsonResponse({ active: PROFILE, current: 'worker-profile' }),
    '/api/profiles/alpha/soul': jsonResponse({
      content: '<script>untrusted text only</script>',
      exists: true,
    }),
    '/api/config/schema?profile=alpha': jsonResponse({
      category_order: ['Model'],
      fields: {
        api_key: {
          label: 'API key',
          description: 'Credential metadata only',
          category: 'Model',
          type: 'secret',
          searchable: false,
          clearable: true,
          value: 'CONFIG_SECRET_VALUE',
          default: 'CONFIG_SECRET_DEFAULT',
          env_key: 'SUPER_SECRET_ENV',
          options: ['MUST_NOT_ESCAPE'],
        },
        model: {
          label: 'Model',
          description: 'Model selection',
          category: 'Model',
          type: 'select',
          options: [{ value: 'safe-model', label: 'Safe model', internal: 'ignored' }],
        },
      },
    }),
    '/api/providers/oauth?profile=alpha': jsonResponse({
      providers: [
        {
          id: 'provider-safe',
          name: 'Provider Safe',
          flow: 'oauth',
          cli_command: 'secret-command',
          disconnect_command: 'secret-disconnect',
          docs_url: 'https://private.invalid',
          status: {
            logged_in: true,
            source: 'environment',
            source_label: 'C:/private/auth.json',
            token_preview: 'TOKEN_SECRET_VALUE',
            expires_at: '2030-01-01T00:00:00Z',
            has_refresh_token: true,
          },
        },
      ],
    }),
    '/api/auth/providers': jsonResponse({
      providers: [{ name: 'local', display_name: 'Local', supports_password: true }],
    }),
    '/api/auth/me': jsonResponse({
      user_id: 'private-user-id',
      email: 'private@example.invalid',
      display_name: 'Engineer',
      org_id: 'private-org',
      provider: 'local',
      expires_at: '2030-01-01T00:00:00Z',
    }),
  };
}

function fixtureTransport(
  overrides: Record<string, LiveTransportResponse> = {},
  onCall?: (url: string, signal: AbortSignal) => void,
): LiveTransport {
  const values = { ...fixtures(), ...overrides };
  return vi.fn(async (url, init) => {
    onCall?.(url, init.signal);
    const value = values[url];
    if (!value) throw new Error(`Unexpected route: ${url}`);
    return value;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('LiveHermesProfileRuntimeAdapter', () => {
  it('returns a sanitized partial snapshot without requesting the broad OAuth DTO', async () => {
    const globalFetch = vi.fn(() => {
      throw new Error('Global fetch must never be called.');
    });
    vi.stubGlobal('fetch', globalFetch);
    const transport = fixtureTransport();
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport });

    const result = await adapter.load(request('normal-1'), new AbortController().signal);

    expect(result.status).toBe('partial');
    expect(result.profileId).toBe(PROFILE);
    expect(result.correlationId).toBe('normal-1');
    expect(result.revision).toMatch(/^fnv1a32-/);
    expect(result.value?.profiles.value?.[0]).toEqual({
      profileId: PROFILE,
      description: 'Primary',
      isDefault: true,
      modelId: 'model-safe',
      providerId: 'provider-safe',
      skillCount: 2,
      gatewayRunning: false,
      descriptionAuto: false,
      hasAlias: true,
    });
    expect(result.value?.contextDocuments.value?.[0].content).toBe('<script>untrusted text only</script>');
    expect(result.value?.configurationMetadata.value?.fields[0]).toMatchObject({
      key: 'api_key',
      kind: 'secret',
      sensitive: true,
      options: [],
    });
    expect(result.value?.account.value).toEqual({
      signedIn: true,
      displayName: 'Engineer',
      providerId: 'local',
      expiresAt: '2030-01-01T00:00:00Z',
    });
    expect(result.value?.providerStatuses).toEqual({
      availability: 'unavailable',
      value: null,
      message: 'Provider connection status is unavailable until Hermes exposes a renderer-safe projection.',
    });

    const exposed = JSON.stringify(result.value);
    for (const forbidden of [
      'C:/private/profile',
      'C:/private/distribution',
      'CONFIG_SECRET_VALUE',
      'CONFIG_SECRET_DEFAULT',
      'SUPER_SECRET_ENV',
      'MUST_NOT_ESCAPE',
      'secret-command',
      'secret-disconnect',
      'TOKEN_SECRET_VALUE',
      'C:/private/auth.json',
      'private-user-id',
      'private@example.invalid',
      'private-org',
      'has_env',
      'token_preview',
    ]) {
      expect(exposed).not.toContain(forbidden);
    }
    expect(globalFetch).not.toHaveBeenCalled();
    expect(transport).toHaveBeenCalledTimes(6);
    expect(transport).not.toHaveBeenCalledWith('/api/providers/oauth?profile=alpha', expect.anything());
  });

  it('does not touch a supplied broad OAuth response containing secret-derived data', async () => {
    const calls: string[] = [];
    const adapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({
        '/api/providers/oauth?profile=alpha': jsonResponse({ providers: [{ status: { token_preview: 'SECRET_SUFFIX' } }] }),
      }, (url) => calls.push(url)),
    });

    const result = await adapter.load(request('partial-1'), new AbortController().signal);

    expect(result.status).toBe('partial');
    expect(result.value?.profiles.availability).toBe('available');
    expect(calls).not.toContain('/api/providers/oauth?profile=alpha');
    expect(JSON.stringify(result)).not.toContain('SECRET_SUFFIX');
  });

  it('reports source-confirmed route absence as unavailable without hiding other results', async () => {
    const adapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({
        '/api/config/schema?profile=alpha': jsonResponse({}, 404),
      }),
    });

    const result = await adapter.load(request('route-unavailable-1'), new AbortController().signal);

    expect(result.status).toBe('partial');
    expect(result.value?.configurationMetadata.availability).toBe('unavailable');
  });

  it('returns unsupported destructive capabilities as unavailable without transport use', async () => {
    const transport = vi.fn<LiveTransport>();
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport });

    const result = await adapter.unavailable(request('unavailable-1'), 'delete-profile', new AbortController().signal);

    expect(result.status).toBe('unavailable');
    expect(result.value?.capabilityId).toBe('delete-profile');
    expect(result.profileId).toBe(PROFILE);
    expect(result.correlationId).toBe('unavailable-1');
    expect(transport).not.toHaveBeenCalled();
  });

  it('rejects malformed critical inventory data', async () => {
    const adapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({ '/api/profiles': jsonResponse({ profiles: 'not-an-array' }) }),
    });

    const result = await adapter.load(request('malformed-1'), new AbortController().signal);

    expect(result.status).toBe('error');
    expect(result.error?.code).toBe('malformed-response');
    expect(result.value).toBeNull();
  });

  it('carries AbortSignal to transport and returns a correlated cancellation', async () => {
    const seenSignals: AbortSignal[] = [];
    const transport: LiveTransport = vi.fn(
      async (_url, init) =>
        new Promise<LiveTransportResponse>((_resolve, reject) => {
          seenSignals.push(init.signal);
          const error = new Error('cancelled');
          error.name = 'AbortError';
          if (init.signal.aborted) {
            reject(error);
          } else {
            init.signal.addEventListener('abort', () => reject(error), { once: true });
          }
        }),
    );
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport, maxConcurrency: 2 });
    const controller = new AbortController();

    const pending = adapter.load(request('cancel-1'), controller.signal);
    await Promise.resolve();
    controller.abort();
    const result = await pending;

    expect(result.status).toBe('cancelled');
    expect(result.error?.code).toBe('cancelled');
    expect(result.correlationId).toBe('cancel-1');
    expect(seenSignals.length).toBeGreaterThan(0);
    expect(seenSignals.every((signal) => signal === controller.signal)).toBe(true);
  });

  it('rejects concurrent and replayed duplicate correlations without starting another load', async () => {
    let calls = 0;
    const transport: LiveTransport = vi.fn(
      async (_url, init) =>
        new Promise<LiveTransportResponse>((_resolve, reject) => {
          calls += 1;
          const error = new Error('cancelled');
          error.name = 'AbortError';
          init.signal.addEventListener('abort', () => reject(error), { once: true });
        }),
    );
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport, maxConcurrency: 2 });
    const controller = new AbortController();
    const first = adapter.load(request('duplicate-1'), controller.signal);
    await Promise.resolve();
    await Promise.resolve();
    const callsBeforeDuplicate = calls;

    const duplicate = await adapter.load(request('duplicate-1'), new AbortController().signal);

    expect(duplicate.error?.code).toBe('duplicate-correlation');
    expect(calls).toBe(callsBeforeDuplicate);
    controller.abort();
    await first;

    const replay = await adapter.load(request('duplicate-1'), new AbortController().signal);
    expect(replay.error?.code).toBe('duplicate-correlation');
  });

  it('rejects oversized response bodies and oversized collections', async () => {
    const oversizedBytes = new Uint8Array(HERMES_PROFILE_RUNTIME_LIVE_BOUNDS.responseBytes + 1);
    const tooLarge: LiveTransportResponse = {
      ok: true,
      status: 200,
      headers: { get: () => null },
      body: {
        getReader: () => {
          let sent = false;
          return {
            read: async () => {
              if (sent) return { done: true };
              sent = true;
              return { done: false, value: oversizedBytes };
            },
            cancel: async () => {
              sent = true;
            },
          };
        },
      },
    };
    const bodyAdapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({ '/api/profiles': tooLarge }),
    });

    const bodyResult = await bodyAdapter.load(request('oversized-body-1'), new AbortController().signal);
    expect(bodyResult.error?.code).toBe('oversized-response');

    const profiles = Array.from({ length: HERMES_PROFILE_RUNTIME_LIVE_BOUNDS.profiles + 1 }, (_, index) => ({
      name: `profile-${index}`,
      is_default: false,
      model: null,
      provider: null,
      skill_count: 0,
      gateway_running: false,
      description: null,
      description_auto: false,
      has_alias: false,
    }));
    const collectionAdapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({ '/api/profiles': jsonResponse({ profiles }) }),
    });
    const collectionResult = await collectionAdapter.load(
      request('oversized-collection-1'),
      new AbortController().signal,
    );
    expect(collectionResult.error?.code).toBe('oversized-response');
  });

  it('rejects cross-profile and cross-correlation response envelopes', async () => {
    const profileAdapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({
        '/api/profiles/alpha/soul': jsonResponse({
          profileId: 'beta',
          correlationId: 'cross-profile-1',
          content: 'wrong profile',
          exists: true,
        }),
      }),
    });
    const profileResult = await profileAdapter.load(
      request('cross-profile-1'),
      new AbortController().signal,
    );
    expect(profileResult.error?.code).toBe('cross-profile-response');
    expect(profileResult.value).toBeNull();

    const correlationAdapter = new LiveHermesProfileRuntimeAdapter({
      transport: fixtureTransport({
        '/api/config/schema?profile=alpha': jsonResponse({
          profileId: PROFILE,
          correlationId: 'different-correlation',
          fields: {},
          category_order: [],
        }),
      }),
    });
    const correlationResult = await correlationAdapter.load(
      request('cross-correlation-1'),
      new AbortController().signal,
    );
    expect(correlationResult.error?.code).toBe('cross-correlation-response');
  });

  it('rejects a stale expected revision while returning the observed revision', async () => {
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport: fixtureTransport() });

    const result = await adapter.load(request('stale-1', 'fnv1a32-deadbeef'), new AbortController().signal);

    expect(result.status).toBe('error');
    expect(result.error?.code).toBe('stale-response');
    expect(result.revision).toMatch(/^fnv1a32-/);
    expect(result.value).toBeNull();
  });

  it('bounds simultaneous injected transport work', async () => {
    let active = 0;
    let maximumActive = 0;
    const values = fixtures();
    const transport: LiveTransport = vi.fn(async (url) => {
      active += 1;
      maximumActive = Math.max(maximumActive, active);
      await new Promise((resolve) => setTimeout(resolve, 2));
      active -= 1;
      const value = values[url];
      if (!value) throw new Error('unexpected route');
      return value;
    });
    const adapter = new LiveHermesProfileRuntimeAdapter({ transport, maxConcurrency: 2 });

    const result = await adapter.load(request('concurrency-1'), new AbortController().signal);

    expect(result.status).toBe('partial');
    expect(maximumActive).toBeLessThanOrEqual(2);
  });
});


