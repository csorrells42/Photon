import { describe, expect, it, vi } from 'vitest'
import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type ApplyImportRequest,
  type ConfirmPermissionGrantRequest,
  type PreviewImportRequest,
  type PreviewPermissionGrantRequest,
  type ProfileRuntimeSnapshot,
  type RequestContext,
  type SaveConfigurationRequest,
} from '../HermesProfileRuntime/contracts'
import { createRequestContext } from '../HermesProfileRuntime/runtimeSafety'
import {
  HERMES_PROFILE_RUNTIME_MUTATION_ROUTES,
  HermesProfileRuntimeMutationBridge,
  HermesProfileRuntimeMutationBridgeError,
  type HermesProfileRuntimeMutationFetch,
} from './index'

const PROFILE = 'alpha'
const NOW = 1_800_000_000_000

function context(correlationId: string, expectedRevision?: number, profileId = PROFILE): RequestContext {
  return createRequestContext(profileId, correlationId, expectedRevision)
}

function snapshot(overrides: Partial<ProfileRuntimeSnapshot> = {}): ProfileRuntimeSnapshot {
  return {
    contract: HERMES_PROFILE_RUNTIME_CONTRACT,
    profileId: PROFILE,
    revision: 7,
    availability: 'ready',
    detail: 'Profile runtime ready.',
    profiles: [{ id: PROFILE, name: 'Alpha', description: 'Primary profile', isActive: true, isDeleteProtected: true }],
    documents: [{ kind: 'soul', text: 'Be direct and helpful.', maximumCharacters: 65_536 }],
    intent: { modelId: 'openai::gpt-5', projectPath: null, worktreePath: null, note: '' },
    terminalBackends: [{
      id: 'native', label: 'Native terminal', description: 'Workbench terminal', selected: true,
      availability: 'ready', detail: 'Ready.',
    }],
    computerUse: { availability: 'unavailable', detail: 'Not available here.', permissions: [] },
    providers: [],
    configurationSchema: [],
    configuration: {},
    ...overrides,
  }
}

function envelope<T>(request: RequestContext, value: T, revision = 7) {
  return {
    contract: request.contract,
    profileId: request.profileId,
    correlationId: request.correlationId,
    ...(request.expectedRevision === undefined ? {} : { expectedRevision: request.expectedRevision }),
    revision,
    value,
  }
}

function json(value: unknown, status = 200, extraHeaders: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json', ...extraHeaders },
  })
}

function bridge(fetchImplementation: HermesProfileRuntimeMutationFetch, now: () => number = () => NOW) {
  return new HermesProfileRuntimeMutationBridge({ fetch: fetchImplementation, now })
}

function previewValue(profileId = PROFILE) {
  return {
    previewId: 'preview-1',
    operation: 'rename',
    profileId,
    title: 'Rename profile',
    summary: 'Rename Alpha to Beta.',
    consequences: ['The profile display name changes.'],
    destructive: false,
    confirmationPhrase: null,
    expiresAt: new Date(NOW + 60_000).toISOString(),
  }
}

describe('HermesProfileRuntimeMutationBridge transport and identity', () => {
  it('loads a bounded snapshot only from the fixed versioned same-origin route', async () => {
    const request = context('load:1')
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async (_url, _init) => json(envelope(request, snapshot())))

    const result = await bridge(fetch).load(request, new AbortController().signal)

    expect(result.value.profileId).toBe(PROFILE)
    expect(result.revision).toBe(7)
    expect(fetch).toHaveBeenCalledTimes(1)
    const [url, init] = fetch.mock.calls[0]!
    expect(url).toBe(`${HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.snapshot}?contract=hermes-profile-runtime%2Fv1&profileId=alpha&correlationId=load%3A1`)
    expect(init).toMatchObject({ method: 'GET', credentials: 'include', headers: { Accept: 'application/json' } })
    expect(init.body).toBeUndefined()
    expect(url).not.toContain('/api/profiles')
  })

  it.each([
    ['profileId', 'other'],
    ['correlationId', 'other:1'],
    ['contract', 'hermes-profile-runtime/v0'],
    ['expectedRevision', 99],
  ])('rejects mismatched %s binding', async (key, value) => {
    const request = context(`mismatch:${key}`, 7)
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async () => json({ ...envelope(request, snapshot()), [key]: value }))
    await expect(bridge(fetch).selectTerminalBackend(
      { ...request, backendId: 'native' },
      new AbortController().signal,
    )).rejects.toMatchObject({ code: key === 'expectedRevision' ? 'stale-response' : 'malformed-response' })
  })

  it('rejects secret-shaped, native-path, malformed, and oversized renderer responses', async () => {
    const secretRequest = context('hostile:secret')
    const secretSnapshot = snapshot({
      providers: [{
        id: 'openai', label: 'OpenAI', oauth: 'connected', credentialPool: 'configured', configuredCredentialSlots: 1,
        customEndpoint: { state: 'not_configured', origin: null }, disposition: 'delegated', detail: 'Ready.',
        apiKey: 'sk-private',
      } as never],
    })
    await expect(bridge(async () => json(envelope(secretRequest, secretSnapshot))).load(
      secretRequest,
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'malformed-response' })

    const pathRequest = context('hostile:path')
    await expect(bridge(async () => json(envelope(pathRequest, snapshot({ detail: 'Loaded C:\\Users\\Chris\\profile.json' })))).load(
      pathRequest,
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'malformed-response' })

    const malformedRequest = context('hostile:json')
    await expect(bridge(async () => new Response('{', { headers: { 'Content-Type': 'application/json' } })).load(
      malformedRequest,
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'malformed-response' })

    const largeRequest = context('hostile:large')
    await expect(bridge(async () => json({}, 200, { 'Content-Length': String(512 * 1024 + 1) })).load(
      largeRequest,
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'response-too-large' })
  })

  it('makes concurrent loads latest-wins and rejects correlation replay', async () => {
    const resolvers: Array<(response: Response) => void> = []
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(() => new Promise((resolve) => resolvers.push(resolve)))
    const adapter = bridge(fetch)
    const firstRequest = context('load:first')
    const secondRequest = context('load:second')
    const first = adapter.load(firstRequest, new AbortController().signal)
    const second = adapter.load(secondRequest, new AbortController().signal)
    resolvers[1]!(json(envelope(secondRequest, snapshot())))
    await expect(second).resolves.toMatchObject({ correlationId: 'load:second' })
    resolvers[0]!(json(envelope(firstRequest, snapshot())))
    await expect(first).rejects.toMatchObject({ code: 'stale-response' })
    await expect(adapter.load(secondRequest, new AbortController().signal)).rejects.toMatchObject({ code: 'correlation-replay' })
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('fails cancelled requests before transport', async () => {
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>()
    const controller = new AbortController()
    controller.abort()
    await expect(bridge(fetch).load(context('cancel:1'), controller.signal)).rejects.toMatchObject({ code: 'aborted' })
    expect(fetch).not.toHaveBeenCalled()
  })
})

describe('HermesProfileRuntimeMutationBridge writable subset', () => {
  it('binds a preview and consumes its exact profile/revision/mutation at most once', async () => {
    const previewRequest = { ...context('preview:1', 7), mutation: { kind: 'rename' as const, name: 'Beta' } }
    const commitRequest = { ...context('commit:1', 7), mutation: previewRequest.mutation, previewId: 'preview-1' }
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async (url) => url === HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.mutationPreview
      ? json(envelope(previewRequest, previewValue()))
      : json(envelope(commitRequest, { affectedProfileId: PROFILE, activeProfileId: PROFILE }, 8)))
    const adapter = bridge(fetch)

    await expect(adapter.previewProfileMutation(previewRequest, new AbortController().signal)).resolves.toMatchObject({
      value: { previewId: 'preview-1', operation: 'rename' },
    })
    await expect(adapter.applyProfileMutation(commitRequest, new AbortController().signal)).resolves.toMatchObject({ revision: 8 })
    await expect(adapter.applyProfileMutation(
      { ...commitRequest, correlationId: 'commit:replay' },
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'preview-replay' })
    expect(fetch).toHaveBeenCalledTimes(2)
    const commitBody = JSON.parse(String(fetch.mock.calls[1]![1].body))
    expect(commitBody).toEqual({
      contract: HERMES_PROFILE_RUNTIME_CONTRACT,
      profileId: PROFILE,
      correlationId: 'commit:1',
      expectedRevision: 7,
      mutation: { kind: 'rename', name: 'Beta' },
      previewId: 'preview-1',
    })
  })

  it('rejects cross-profile, stale-revision, changed-mutation, and expired preview commits without transport', async () => {
    let now = NOW
    const previewRequest = { ...context('preview:bound', 7), mutation: { kind: 'rename' as const, name: 'Beta' } }
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async () => json(envelope(previewRequest, previewValue())))
    const adapter = bridge(fetch, () => now)
    await adapter.previewProfileMutation(previewRequest, new AbortController().signal)

    for (const request of [
      { ...context('commit:profile', 7, 'other'), mutation: previewRequest.mutation, previewId: 'preview-1' },
      { ...context('commit:revision', 6), mutation: previewRequest.mutation, previewId: 'preview-1' },
      { ...context('commit:mutation', 7), mutation: { kind: 'rename' as const, name: 'Gamma' }, previewId: 'preview-1' },
    ]) {
      await expect(adapter.applyProfileMutation(request, new AbortController().signal)).rejects.toMatchObject({ code: 'preview-mismatch' })
    }
    now = NOW + 60_001
    await expect(adapter.applyProfileMutation(
      { ...context('commit:expired', 7), mutation: previewRequest.mutation, previewId: 'preview-1' },
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'preview-expired' })
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('maps stale and expired server error envelopes without leaking unbound errors', async () => {
    for (const [serverCode, expectedCode] of [
      ['stale-revision', 'stale-response'],
      ['preview-expired', 'preview-expired'],
    ] as const) {
      const request = context(`error:${serverCode}`, 7)
      const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async () => json({
        contract: request.contract,
        profileId: request.profileId,
        correlationId: request.correlationId,
        expectedRevision: request.expectedRevision,
        code: serverCode,
        message: 'Bounded safe failure.',
      }, 409))
      await expect(bridge(fetch).selectTerminalBackend(
        { ...request, backendId: 'native' },
        new AbortController().signal,
      )).rejects.toMatchObject({ code: expectedCode, message: 'Bounded safe failure.' })
    }
  })

  it('saves only SOUL atomically and never transports persona or context', async () => {
    const soulRequest = { ...context('soul:1', 7), document: 'soul' as const, text: 'Updated soul.' }
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async () => json(envelope(soulRequest, snapshot({
      revision: 8,
      documents: [{ kind: 'soul', text: 'Updated soul.', maximumCharacters: 65_536 }],
    }), 8)))
    const adapter = bridge(fetch)
    await expect(adapter.saveDocument(soulRequest, new AbortController().signal)).resolves.toMatchObject({ revision: 8 })
    expect(fetch.mock.calls[0]![0]).toBe(HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.soul)
    expect(JSON.parse(String(fetch.mock.calls[0]![1].body))).toMatchObject({ document: 'soul', text: 'Updated soul.' })

    await expect(adapter.saveDocument(
      { ...context('persona:1', 8), document: 'persona', text: 'No.' },
      new AbortController().signal,
    )).rejects.toMatchObject({ code: 'unsupported-operation' })
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('transports only provider/model intent and bounded terminal selection', async () => {
    const modelRequest = {
      ...context('model:1', 7),
      intent: { modelId: 'openrouter::deepseek/deepseek-r1', projectPath: null, worktreePath: null, note: '' },
    }
    const terminalRequest = { ...context('terminal:1', 8), backendId: 'native' }
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>(async (_url, init) => {
      const sent = JSON.parse(String(init.body))
      const request = context(sent.correlationId, sent.expectedRevision)
      return json(envelope(request, snapshot({ revision: sent.expectedRevision + 1 }), sent.expectedRevision + 1))
    })
    const adapter = bridge(fetch)
    await adapter.saveIntent(modelRequest, new AbortController().signal)
    await adapter.selectTerminalBackend(terminalRequest, new AbortController().signal)
    expect(JSON.parse(String(fetch.mock.calls[0]![1].body))).toEqual({
      contract: HERMES_PROFILE_RUNTIME_CONTRACT,
      profileId: PROFILE,
      correlationId: 'model:1',
      expectedRevision: 7,
      provider: 'openrouter',
      model: 'deepseek/deepseek-r1',
    })
    expect(JSON.parse(String(fetch.mock.calls[1]![1].body))).toMatchObject({ backendId: 'native' })

    for (const intent of [
      { ...modelRequest.intent, projectPath: 'C:\\Users\\Chris\\repo' },
      { ...modelRequest.intent, worktreePath: '/workspace/repo' },
      { ...modelRequest.intent, note: 'hidden context' },
    ]) {
      await expect(adapter.saveIntent(
        { ...context(`model:reject:${fetch.mock.calls.length}`, 8), intent },
        new AbortController().signal,
      )).rejects.toMatchObject({ code: 'unsupported-operation' })
    }
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('leaves permission, configuration, import, and export unavailable with zero transport', async () => {
    const fetch = vi.fn<HermesProfileRuntimeMutationFetch>()
    const adapter = bridge(fetch)
    const base = context('unsupported:base', 7)
    const requests: Array<Promise<unknown>> = [
      adapter.previewPermissionGrant({ ...base, intent: { permissionId: 'screen', duration: 'once', rationale: 'Need it.' } } as PreviewPermissionGrantRequest, new AbortController().signal),
      adapter.confirmPermissionGrant({ ...base, intent: { permissionId: 'screen', duration: 'once', rationale: 'Need it.' }, previewId: 'p', confirmationPhrase: 'confirm' } as ConfirmPermissionGrantRequest, new AbortController().signal),
      adapter.saveConfiguration({ ...base, values: {} } as SaveConfigurationRequest, new AbortController().signal),
      adapter.previewImport({ ...base, inspection: {} } as PreviewImportRequest, new AbortController().signal),
      adapter.applyImport({ ...base, inspection: {}, previewId: 'p', confirmationPhrase: 'confirm' } as ApplyImportRequest, new AbortController().signal),
      adapter.exportProfile(base, new AbortController().signal),
    ]
    for (const request of requests) {
      await expect(request).rejects.toBeInstanceOf(HermesProfileRuntimeMutationBridgeError)
      await expect(request).rejects.toMatchObject({ code: 'unsupported-operation' })
    }
    expect(fetch).not.toHaveBeenCalled()
  })
})
