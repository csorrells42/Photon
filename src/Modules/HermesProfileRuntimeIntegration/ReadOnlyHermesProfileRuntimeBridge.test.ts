import { describe, expect, it, vi } from 'vitest'
import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type ProfileImportInspection,
} from '../HermesProfileRuntime/contracts'
import { createRequestContext } from '../HermesProfileRuntime/runtimeSafety'
import {
  HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
  type HermesProfileRuntimeLiveAdapterContract,
  type LiveAdapterResult,
  type LiveRequestIdentity,
  type LiveTransportResponse,
  type ProfileRuntimeLiveSnapshot,
} from '../HermesProfileRuntimeLive'
import {
  HermesProfileRuntimeBridgeError,
  HermesProfileRuntimeReadOnlyError,
  ReadOnlyHermesProfileRuntimeBridge,
  createProductionHermesProfileRuntimeBridge,
  createSameOriginProfileRuntimeTransport,
  getHermesProfileRuntimeIntegrationCapabilities,
  type SameOriginProfileRuntimeFetch,
} from './index'

const PROFILE = 'alpha'

function available<T>(value: T) {
  return { availability: 'available' as const, value }
}

function liveSnapshot(): ProfileRuntimeLiveSnapshot {
  return {
    profiles: available([
      {
        profileId: PROFILE,
        description: 'Alpha profile',
        isDefault: true,
        modelId: 'source-model-not-an-intent',
        providerId: 'provider-safe',
        skillCount: 3,
        gatewayRunning: true,
        descriptionAuto: false,
        hasAlias: true,
      },
      {
        profileId: 'beta',
        description: null,
        isDefault: false,
        modelId: null,
        providerId: null,
        skillCount: 0,
        gatewayRunning: false,
        descriptionAuto: false,
        hasAlias: false,
      },
    ]),
    activeProfile: available({ stickyProfileId: 'beta', currentProcessProfileId: PROFILE }),
    contextDocuments: available([
      { kind: 'soul' as const, exists: true, content: '<script>source-confirmed text only</script>' },
    ]),
    configurationMetadata: available({
      categoryOrder: ['General'],
      fields: [
        {
          key: 'safe_text', label: 'Safe text', description: 'Metadata only', category: 'General',
          kind: 'text', searchable: true, clearable: true, sensitive: false, options: [],
        },
        {
          key: 'safe_choice', label: 'Safe choice', description: null, category: 'General',
          kind: 'select', searchable: true, clearable: true, sensitive: false,
          options: [{ value: 'one', label: 'One' }],
        },
        {
          key: 'api_key', label: 'API key', description: 'private-config-description', category: 'Secrets',
          kind: 'secret', searchable: false, clearable: true, sensitive: true,
          options: [{ value: 'private-config-option', label: 'Private' }],
        },
        {
          key: 'private_token', label: 'Token', description: null, category: null,
          kind: 'text', searchable: false, clearable: false, sensitive: false, options: [],
        },
        {
          key: 'unknown_field', label: 'Unknown', description: null, category: null,
          kind: 'unknown', searchable: false, clearable: false, sensitive: false, options: [],
        },
      ],
    }),
    providerStatuses: available([
      {
        providerId: 'provider-safe', label: 'Provider Safe', flow: 'oauth', connected: true,
        expiresAt: 'private-provider-expiry', hasRefreshToken: true,
        tokenPreview: 'private-provider-token', sourcePath: 'C:/private/provider.json',
      } as never,
    ]),
    authProviders: available([
      { providerId: 'local', label: 'Local', supportsPassword: true, privateHint: 'private-auth-hint' } as never,
    ]),
    account: available({
      signedIn: true,
      displayName: 'Private Account Name',
      providerId: 'local',
      expiresAt: 'private-account-expiry',
      userId: 'private-user-id',
      email: 'private@example.invalid',
      orgId: 'private-org-id',
    } as never),
    capabilities: [],
  }
}

function liveResult(
  request: LiveRequestIdentity,
  snapshot: ProfileRuntimeLiveSnapshot | null = liveSnapshot(),
  status: LiveAdapterResult<ProfileRuntimeLiveSnapshot>['status'] = 'ready',
  revision: string | null = 'opaque-revision-a',
): LiveAdapterResult<ProfileRuntimeLiveSnapshot> {
  return {
    ...request,
    revision,
    status,
    value: snapshot,
    notices: [],
  }
}

function fakeLive(
  implementation?: (
    request: LiveRequestIdentity,
    signal: AbortSignal,
  ) => Promise<LiveAdapterResult<ProfileRuntimeLiveSnapshot>>,
): HermesProfileRuntimeLiveAdapterContract & { load: ReturnType<typeof vi.fn> } {
  const load = vi.fn(implementation ?? (async (request) => liveResult(request)))
  return {
    load,
    unavailable: vi.fn(async (request, capabilityId) => ({
      ...request,
      revision: null,
      status: 'unavailable' as const,
      value: { capabilityId, availability: 'unavailable' as const, reason: 'Unavailable.' },
      notices: ['Unavailable.'],
    })),
  }
}

function response(): LiveTransportResponse {
  return {
    ok: true,
    status: 200,
    headers: { get: () => '0' },
    body: null,
  }
}

describe('ReadOnlyHermesProfileRuntimeBridge mapping', () => {
  it('maps ready live truth without inventing absent mutable fields', async () => {
    const live = fakeLive()
    const bridge = new ReadOnlyHermesProfileRuntimeBridge(live)
    const context = createRequestContext(PROFILE, 'ready:1')

    const result = await bridge.load(context, new AbortController().signal)

    expect(result.profileId).toBe(PROFILE)
    expect(result.correlationId).toBe('ready:1')
    expect(result.value.availability).toBe('ready')
    expect(result.value.profiles.map((profile) => [profile.id, profile.isActive])).toEqual([
      [PROFILE, false],
      ['beta', true],
    ])
    expect(result.value.detail).toContain('Sticky profile: beta; current-process profile: alpha.')
    expect(result.value.documents).toEqual([
      { kind: 'soul', text: '<script>source-confirmed text only</script>', maximumCharacters: 65_536 },
    ])
    expect(result.value.documents.map((document) => document.kind)).toEqual(['soul'])
    expect(result.value.intent).toEqual({ modelId: null, projectPath: null, worktreePath: null, note: '' })
    expect(result.value.terminalBackends).toEqual([])
    expect(result.value.computerUse).toMatchObject({ availability: 'unavailable', permissions: [] })
    expect(result.value.configuration).toEqual({})
    expect(result.value.configurationSchema.map((field) => [field.key, field.disposition])).toEqual([
      ['safe_text', 'delegated'],
      ['safe_choice', 'delegated'],
    ])
    expect(result.value.providers[0]).toMatchObject({
      id: 'provider-safe',
      oauth: 'connected',
      credentialPool: 'delegated',
      configuredCredentialSlots: 0,
      customEndpoint: { state: 'delegated', origin: null },
    })
    expect(live.load).toHaveBeenCalledWith(
      {
        adapterVersion: HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
        profileId: PROFILE,
        correlationId: 'ready:1',
      },
      expect.any(AbortSignal),
    )
  })

  it('preserves partial notices and maps error and unavailable reads honestly', async () => {
    const partial: ProfileRuntimeLiveSnapshot = {
      ...liveSnapshot(),
      providerStatuses: {
        availability: 'error',
        value: null,
        message: 'Provider status route returned HTTP 500.',
      },
    }
    const partialLive = fakeLive(async (request) => ({
      ...liveResult(request, partial, 'partial'),
      notices: ['Provider status: route returned HTTP 500.'],
    }))
    const partialResult = await new ReadOnlyHermesProfileRuntimeBridge(partialLive).load(
      createRequestContext(PROFILE, 'partial:1'),
      new AbortController().signal,
    )
    expect(partialResult.value.availability).toBe('partial')
    expect(partialResult.value.providers).toEqual([])
    expect(partialResult.value.detail).toContain('Provider status: route returned HTTP 500.')

    for (const [status, code, expected] of [
      ['error', 'transport-error', 'error'],
      ['unavailable', 'unavailable', 'unavailable'],
    ] as const) {
      const live = fakeLive(async (request) => ({
        ...request,
        revision: null,
        status,
        value: null,
        notices: ['Bounded live notice.'],
        error: { code, message: 'Bounded live failure.' },
      }))
      const result = await new ReadOnlyHermesProfileRuntimeBridge(live).load(
        createRequestContext(PROFILE, `failure:${status}`),
        new AbortController().signal,
      )
      expect(result.value.availability).toBe(expected)
      expect(result.value.profiles).toEqual([])
      expect(result.value.detail).toContain('Bounded live failure.')
      expect(result.value.detail).toContain('Bounded live notice.')
    }
  })

  it('redacts credential-shaped live errors and notices before renderer exposure', async () => {
    const live = fakeLive(async (request) => ({
      ...request,
      revision: null,
      status: 'error',
      value: null,
      notices: ['token=notice-secret-value\r\n\t\u202e\u200b<script>& ordinary punctuation: ok.'],
      error: { code: 'transport-error', message: 'Authorization: Bearer renderer-secret-value' },
    }))

    const result = await new ReadOnlyHermesProfileRuntimeBridge(live).load(
      createRequestContext(PROFILE, 'redaction:1'),
      new AbortController().signal,
    )

    expect(result.value.detail).toContain('[redacted]')
    expect(result.value.detail).not.toContain('notice-secret-value')
    expect(result.value.detail).not.toContain('renderer-secret-value')
    expect(result.value.detail).not.toMatch(/[\r\n\t\u202e\u200b<>&]/)
    expect(result.value.detail).toContain('ordinary punctuation: ok.')
  })

  it('omits sensitive metadata, all values, and provider/account private fields', async () => {
    const result = await new ReadOnlyHermesProfileRuntimeBridge(fakeLive()).load(
      createRequestContext(PROFILE, 'privacy:1'),
      new AbortController().signal,
    )
    const exposed = JSON.stringify(result.value)

    for (const forbidden of [
      'api_key',
      'private_token',
      'private-config-description',
      'private-config-option',
      'private-provider-expiry',
      'private-provider-token',
      'C:/private/provider.json',
      'private-auth-hint',
      'Private Account Name',
      'private-account-expiry',
      'private-user-id',
      'private@example.invalid',
      'private-org-id',
      'source-model-not-an-intent',
    ]) expect(exposed).not.toContain(forbidden)
  })

  it('keeps stable renderer revisions and passes only the exact associated live revision', async () => {
    let revision = 'opaque-revision-a'
    const requests: LiveRequestIdentity[] = []
    const live = fakeLive(async (request) => {
      requests.push(request)
      return liveResult(request, liveSnapshot(), 'ready', revision)
    })
    const bridge = new ReadOnlyHermesProfileRuntimeBridge(live)

    const first = await bridge.load(createRequestContext(PROFILE, 'revision:1'), new AbortController().signal)
    const repeated = await bridge.load(createRequestContext(PROFILE, 'revision:2'), new AbortController().signal)
    const checked = await bridge.load(
      createRequestContext(PROFILE, 'revision:3', first.revision),
      new AbortController().signal,
    )
    expect(first.revision).toBe(1)
    expect(repeated.revision).toBe(1)
    expect(checked.revision).toBe(1)
    expect(requests[2]?.expectedRevision).toBe('opaque-revision-a')

    revision = 'opaque-revision-b'
    const changed = await bridge.load(createRequestContext(PROFILE, 'revision:4'), new AbortController().signal)
    expect(changed.revision).toBe(2)
    const callsBeforeStale = live.load.mock.calls.length
    await expect(bridge.load(
      createRequestContext(PROFILE, 'revision:stale', first.revision),
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'stale-response', correlationId: 'revision:stale' })
    expect(live.load).toHaveBeenCalledTimes(callsBeforeStale)
  })

  it('discards an older concurrent load before it can replace the current revision', async () => {
    let firstRequest: LiveRequestIdentity | undefined
    let secondRequest: LiveRequestIdentity | undefined
    let resolveFirst!: (result: LiveAdapterResult<ProfileRuntimeLiveSnapshot>) => void
    let resolveSecond!: (result: LiveAdapterResult<ProfileRuntimeLiveSnapshot>) => void
    const live = fakeLive((request) => {
      if (!firstRequest) {
        firstRequest = request
        return new Promise((resolve) => { resolveFirst = resolve })
      }
      if (!secondRequest) {
        secondRequest = request
        return new Promise((resolve) => { resolveSecond = resolve })
      }
      return Promise.resolve(liveResult(request, liveSnapshot(), 'ready', 'newer-revision'))
    })
    const bridge = new ReadOnlyHermesProfileRuntimeBridge(live)

    const older = bridge.load(createRequestContext(PROFILE, 'race:older'), new AbortController().signal)
    await vi.waitFor(() => expect(firstRequest).toBeDefined())
    const newer = bridge.load(createRequestContext(PROFILE, 'race:newer'), new AbortController().signal)
    await vi.waitFor(() => expect(secondRequest).toBeDefined())

    resolveSecond(liveResult(secondRequest!, liveSnapshot(), 'ready', 'newer-revision'))
    const current = await newer
    resolveFirst(liveResult(firstRequest!, liveSnapshot(), 'ready', 'older-revision'))
    await expect(older).rejects.toMatchObject({ code: 'stale-response', correlationId: 'race:older' })

    await bridge.load(
      createRequestContext(PROFILE, 'race:checked', current.revision),
      new AbortController().signal,
    )
    expect(live.load.mock.calls[2]?.[0]).toMatchObject({ expectedRevision: 'newer-revision' })
  })

  it('tracks latest loads independently for different profiles', async () => {
    const betaSnapshot: ProfileRuntimeLiveSnapshot = {
      ...liveSnapshot(),
      profiles: available([
        ...(liveSnapshot().profiles.value ?? []),
        {
          profileId: 'gamma', description: null, isDefault: false, modelId: null, providerId: null,
          skillCount: 0, gatewayRunning: false, descriptionAuto: false, hasAlias: false,
        },
      ]),
    }
    const live = fakeLive(async (request) => liveResult(
      request,
      request.profileId === PROFILE ? liveSnapshot() : betaSnapshot,
      'ready',
      `${request.profileId}-revision`,
    ))
    const bridge = new ReadOnlyHermesProfileRuntimeBridge(live)

    const [alpha, beta] = await Promise.all([
      bridge.load(createRequestContext(PROFILE, 'profiles:alpha'), new AbortController().signal),
      bridge.load(createRequestContext('beta', 'profiles:beta'), new AbortController().signal),
    ])

    expect(alpha.profileId).toBe(PROFILE)
    expect(beta.profileId).toBe('beta')
    expect(alpha.revision).toBe(1)
    expect(beta.revision).toBe(1)
  })

  it('fails closed for cross-profile and malformed results and never injects profile-main', async () => {
    const crossLive = fakeLive(async (request) => ({ ...liveResult(request), profileId: 'beta' }))
    await expect(new ReadOnlyHermesProfileRuntimeBridge(crossLive).load(
      createRequestContext(PROFILE, 'cross:1'),
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'cross-profile-response' })

    const malformedLive = fakeLive(async (request) => liveResult(request, null, 'ready', 'opaque'))
    await expect(new ReadOnlyHermesProfileRuntimeBridge(malformedLive).load(
      createRequestContext(PROFILE, 'malformed:1'),
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'malformed-live-result' })

    const noFake = await new ReadOnlyHermesProfileRuntimeBridge(fakeLive()).load(
      createRequestContext(PROFILE, 'no-fake:1'),
      new AbortController().signal,
    )
    expect(JSON.stringify(noFake.value)).not.toContain('profile-main')
    const missingRequested = fakeLive(async (request) => liveResult(request))
    await expect(new ReadOnlyHermesProfileRuntimeBridge(missingRequested).load(
      createRequestContext('profile-main', 'no-fake:2'),
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'cross-profile-response' })
  })

  it('preserves cancellation, duplicate correlation identity, and the original AbortSignal', async () => {
    const signal = new AbortController().signal
    const cancelledLive = fakeLive(async (request, receivedSignal) => {
      expect(receivedSignal).toBe(signal)
      return {
        ...request,
        revision: null,
        status: 'cancelled',
        value: null,
        notices: [],
        error: { code: 'cancelled', message: 'The request was cancelled.' },
      }
    })
    await expect(new ReadOnlyHermesProfileRuntimeBridge(cancelledLive).load(
      createRequestContext(PROFILE, 'cancel:1'),
      signal,
    )).rejects.toMatchObject({ name: 'AbortError' })

    const duplicateLive = fakeLive(async (request) => ({
      ...request,
      revision: null,
      status: 'error',
      value: null,
      notices: [],
      error: { code: 'duplicate-correlation', message: 'Correlation already used.' },
    }))
    await expect(new ReadOnlyHermesProfileRuntimeBridge(duplicateLive).load(
      createRequestContext(PROFILE, 'duplicate:1'),
      signal,
    )).rejects.toEqual(expect.objectContaining({
      code: 'duplicate-correlation',
      profileId: PROFILE,
      correlationId: 'duplicate:1',
    }))
  })
})

describe('read-only closure and production transport', () => {
  it('rejects every non-load method before any production fetch', async () => {
    const fetch = vi.fn<SameOriginProfileRuntimeFetch>(async () => response())
    const bridge = createProductionHermesProfileRuntimeBridge({ fetch })
    const context = createRequestContext(PROFILE, 'mutation:1', 1)
    const signal = new AbortController().signal
    const inspection: ProfileImportInspection = {
      data: {
        format: HERMES_PROFILE_RUNTIME_CONTRACT,
        name: 'Import',
        documents: {},
        intent: { modelId: null, projectPath: null, worktreePath: null, note: '' },
        configuration: {},
      },
      warnings: [],
      characterCount: 2,
    }
    const operations = [
      bridge.selectActiveProfile({ ...context, targetProfileId: 'beta' }, signal),
      bridge.previewProfileMutation({ ...context, mutation: { kind: 'delete' } }, signal),
      bridge.applyProfileMutation({ ...context, mutation: { kind: 'delete' }, previewId: 'preview' }, signal),
      bridge.saveDocument({ ...context, document: 'soul', text: 'text' }, signal),
      bridge.saveIntent({ ...context, intent: { modelId: null, projectPath: null, worktreePath: null, note: '' } }, signal),
      bridge.selectTerminalBackend({ ...context, backendId: 'backend' }, signal),
      bridge.previewPermissionGrant({ ...context, intent: { permissionId: 'permission', duration: 'session', rationale: 'test' } }, signal),
      bridge.confirmPermissionGrant({ ...context, intent: { permissionId: 'permission', duration: 'session', rationale: 'test' }, previewId: 'preview', confirmationPhrase: 'confirm' }, signal),
      bridge.saveConfiguration({ ...context, values: {} }, signal),
      bridge.previewImport({ ...context, inspection }, signal),
      bridge.applyImport({ ...context, inspection, previewId: 'preview', confirmationPhrase: 'confirm' }, signal),
      bridge.exportProfile(context, signal),
    ]

    for (const operation of operations) {
      await expect(operation).rejects.toBeInstanceOf(HermesProfileRuntimeReadOnlyError)
    }
    expect(fetch).not.toHaveBeenCalled()
    const capability = getHermesProfileRuntimeIntegrationCapabilities()
    expect(capability).toMatchObject({ label: 'Live read-only beta', canLoad: true, mutationsEnabled: false })
    expect(capability.disabledOperations).toHaveLength(12)
  })

  it('uses credentials include, GET only, the caller signal, and no secret-bearing headers', async () => {
    const fetch = vi.fn<SameOriginProfileRuntimeFetch>(async () => response())
    const transport = createSameOriginProfileRuntimeTransport(fetch)
    const signal = new AbortController().signal

    await transport('/api/profiles?profile=alpha', {
      method: 'GET',
      signal,
      headers: { Accept: 'application/json' },
    })
    expect(fetch).toHaveBeenCalledWith('/api/profiles?profile=alpha', {
      method: 'GET',
      credentials: 'include',
      signal,
      headers: { Accept: 'application/json' },
    })
    const headers = fetch.mock.calls[0]?.[1].headers ?? {}
    expect(Object.keys(headers)).toEqual(['Accept'])
    expect(JSON.stringify(headers)).not.toMatch(/authorization|cookie|token|secret|credential/i)

    await expect(transport('https://example.invalid/api/profiles', {
      method: 'GET', signal, headers: { Accept: 'application/json' },
    })).rejects.toThrow(/same-origin/)
    await expect(transport('/api/profiles', {
      method: 'POST', signal, headers: { Accept: 'application/json' },
    } as never)).rejects.toThrow(/GET only/)
    await expect(transport('/api/profiles', {
      method: 'GET', signal, headers: { Accept: 'application/json', Authorization: 'secret' },
    })).rejects.toThrow(/headers/)
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('exports typed bridge errors without exposing live internals', () => {
    const error = new HermesProfileRuntimeBridgeError('live-read-error', 'Read failed.', PROFILE, 'typed:1')
    expect(error).toMatchObject({ code: 'live-read-error', profileId: PROFILE, correlationId: 'typed:1' })
  })
})
