import { describe, expect, it, vi } from 'vitest'
import {
  HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
  type AdvancedModelSettings,
  type ExtensionWriteIntent,
  type HermesExtensionSettingsSnapshot,
} from '../HermesExtensionSettings/contracts'
import {
  HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION,
  type HermesExtensionSettingsLiveReadRequest,
  type HermesExtensionSettingsLiveReadResult,
} from './contracts'
import {
  HermesExtensionSettingsLiveController,
  diagnoseVisionReadiness,
  type HermesExtensionSettingsLiveControllerOptions,
} from './HermesExtensionSettingsLiveController'
import type { HermesExtensionSettingsLiveFetch } from './HermesExtensionSettingsLiveAdapter'

function snapshot(): HermesExtensionSettingsSnapshot {
  return {
    contractVersion: HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
    state: 'ready',
    skills: [],
    toolsetProviders: [],
    models: [
      {
        id: 'openrouter::vendor/text-main',
        label: 'Text main',
        providerId: 'openrouter',
        specialties: ['general'],
        costTier: 'standard',
        available: true,
      },
      {
        id: 'openrouter::vendor/vision/model',
        label: 'Vision model',
        providerId: 'openrouter',
        specialties: ['general', 'vision'],
        costTier: 'standard',
        available: true,
      },
      {
        id: 'nous::vendor/title-model',
        label: 'Title model',
        providerId: 'nous',
        specialties: ['general'],
        costTier: 'standard',
        available: true,
      },
      {
        id: 'openrouter::vendor/alternate-main',
        label: 'Alternate main',
        providerId: 'openrouter',
        specialties: ['general'],
        costTier: 'standard',
        available: true,
      },
      {
        id: 'openrouter::vendor/unavailable',
        label: 'Unavailable',
        providerId: 'openrouter',
        specialties: ['vision'],
        costTier: 'standard',
        available: false,
      },
    ],
    toolsets: {
      search: { providerId: '', backendId: '', specialtyModelId: '' },
      extract: { providerId: '', backendId: '', specialtyModelId: '' },
    },
    auxiliaryTasks: ['vision', 'compression', 'title-generation'],
    taskOverrideKinds: [],
    moaPresets: [],
    modelSettings: {
      defaultModelId: 'openrouter::vendor/text-main',
      auxiliaryModels: [
        { task: 'compression', modelId: 'openrouter::vendor/text-main' },
        { task: 'title-generation', modelId: 'openrouter::vendor/text-main' },
      ],
      taskOverrides: [],
      moa: { enabled: false, presetId: '', referenceModelIds: [], aggregatorModelId: '' },
      providers: [],
    },
    mcpServers: [],
    notices: [],
  }
}

function settings(base: HermesExtensionSettingsSnapshot): AdvancedModelSettings {
  return structuredClone(base.modelSettings)
}

function liveResult(
  value: HermesExtensionSettingsSnapshot,
  request: HermesExtensionSettingsLiveReadRequest,
): HermesExtensionSettingsLiveReadResult {
  return {
    contractVersion: HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION,
    correlationId: request.correlationId,
    profileId: request.profileId ?? null,
    state: value.state === 'ready' ? 'ready' : 'partial',
    snapshot: structuredClone(value),
    sources: [],
    completedAt: '2026-08-09T20:00:00.000Z',
  }
}

function createReadAdapter(...values: HermesExtensionSettingsSnapshot[]) {
  let index = 0
  const requests: HermesExtensionSettingsLiveReadRequest[] = []
  return {
    requests,
    read: vi.fn(async (request: HermesExtensionSettingsLiveReadRequest) => {
      requests.push(request)
      const value = values[Math.min(index, values.length - 1)]
      index += 1
      if (!value) throw new Error('No staged live snapshot.')
      return liveResult(value, request)
    }),
  }
}

function acknowledgement(body: Record<string, unknown>) {
  return body.scope === 'main'
    ? { ok: true, scope: 'main', provider: body.provider, model: body.model }
    : { ok: true, scope: 'auxiliary', tasks: [body.task], provider: body.provider, model: body.model }
}

function response(value: unknown, status = 200, declaredLength?: number): Response {
  const text = JSON.stringify(value)
  return new Response(text, {
    status,
    headers: declaredLength === undefined ? { 'Content-Type': 'application/json' } : {
      'Content-Type': 'application/json',
      'Content-Length': String(declaredLength),
    },
  })
}

function successfulWriteFetch() {
  const requests: Array<{ input: RequestInfo | URL; init?: RequestInit; body: Record<string, unknown> }> = []
  const fetcher: HermesExtensionSettingsLiveFetch = vi.fn(async (input, init) => {
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>
    requests.push({ input, init, body })
    return response(acknowledgement(body))
  })
  return { fetcher, requests }
}

function controller(
  values: HermesExtensionSettingsSnapshot[],
  writeFetch: HermesExtensionSettingsLiveFetch,
  options: Partial<Omit<HermesExtensionSettingsLiveControllerOptions, 'readAdapter' | 'writeFetch'>> = {},
) {
  const readAdapter = createReadAdapter(...values)
  return {
    readAdapter,
    value: new HermesExtensionSettingsLiveController({
      readAdapter,
      writeFetch,
      profileId: 'labs',
      ...options,
    }),
  }
}

async function loadAndPreview(
  value: HermesExtensionSettingsLiveController,
  base: HermesExtensionSettingsSnapshot,
  mutate: (draft: AdvancedModelSettings) => void,
) {
  expect((await value.load()).snapshot).toBeDefined()
  const draft = settings(base)
  mutate(draft)
  return value.preview({ kind: 'models', settings: draft })
}

describe('HermesExtensionSettingsLiveController staged payload', () => {
  it('load returns the injected live normalized snapshot and carries profile identity', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base], writes.fetcher)

    const result = await staged.value.load()

    expect(result.state).toBe('ready')
    expect(result.snapshot?.modelSettings.defaultModelId).toBe('openrouter::vendor/text-main')
    expect(staged.readAdapter.requests[0]).toMatchObject({ profileId: 'labs' })
    expect(staged.readAdapter.requests[0].correlationId).toMatch(/^live-model-read-/)
    expect(writes.requests).toHaveLength(0)
  })

  it('writes one vision assignment with vendor slashes preserved and no api_key', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base, base, base], writes.fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.auxiliaryModels.push({ task: 'vision', modelId: 'openrouter::vendor/vision/model' })
    })

    const result = await staged.value.commit({ reviewId: review.reviewId, confirmed: true })

    expect(result.status).toBe('success')
    expect(writes.requests).toHaveLength(1)
    expect(String(writes.requests[0].input)).toBe('/api/model/set?profile=labs')
    expect(writes.requests[0].init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(writes.requests[0].body).toEqual({
      scope: 'auxiliary',
      provider: 'openrouter',
      model: 'vendor/vision/model',
      task: 'vision',
    })
    expect(writes.requests[0].body).not.toHaveProperty('api_key')
  })

  it('maps title-generation to title_generation', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base, base, base], writes.fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.auxiliaryModels = draft.auxiliaryModels.map((item) =>
        item.task === 'title-generation' ? { ...item, modelId: 'nous::vendor/title-model' } : item)
    })

    expect((await staged.value.commit({ reviewId: review.reviewId, confirmed: true })).status).toBe('success')
    expect(writes.requests[0].body).toMatchObject({
      scope: 'auxiliary',
      task: 'title_generation',
      provider: 'nous',
      model: 'vendor/title-model',
    })
  })

  it('writes one main model assignment', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base, base, base], writes.fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    expect((await staged.value.commit({ reviewId: review.reviewId, confirmed: true })).status).toBe('success')
    expect(writes.requests[0].body).toEqual({
      scope: 'main',
      provider: 'openrouter',
      model: 'vendor/alternate-main',
    })
  })

  it('rejects unadvertised and unavailable models before writes', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base], writes.fetcher)
    await staged.value.load()

    for (const modelId of ['openrouter::vendor/missing', 'openrouter::vendor/unavailable']) {
      const draft = settings(base)
      draft.auxiliaryModels.push({ task: 'vision', modelId })
      await expect(staged.value.preview({ kind: 'models', settings: draft })).rejects.toThrow(/unavailable|advertised/)
    }
    expect(writes.requests).toHaveLength(0)
  })

  it('rejects multi-slot, MoA, provider, task override, and non-model intents before writes', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base], writes.fetcher)
    await staged.value.load()

    const multi = settings(base)
    multi.defaultModelId = 'openrouter::vendor/alternate-main'
    multi.auxiliaryModels.push({ task: 'vision', modelId: 'openrouter::vendor/vision/model' })
    await expect(staged.value.preview({ kind: 'models', settings: multi })).rejects.toThrow(/Exactly one/)

    const moa = settings(base)
    moa.moa.enabled = true
    await expect(staged.value.preview({ kind: 'models', settings: moa })).rejects.toThrow(/MoA/)

    const providers = settings(base)
    providers.providers.push({ providerId: 'custom', timeoutMs: 10_000, maxRetries: 0, customEndpoint: null })
    await expect(staged.value.preview({ kind: 'models', settings: providers })).rejects.toThrow(/Provider/)

    const overrides = settings(base)
    overrides.taskOverrides.push({ task: 'coding', modelId: 'openrouter::vendor/text-main' })
    await expect(staged.value.preview({ kind: 'models', settings: overrides })).rejects.toThrow(/override/)

    const nonModel: ExtensionWriteIntent = {
      kind: 'toolsets',
      search: { providerId: '', backendId: '', specialtyModelId: '' },
      extract: { providerId: '', backendId: '', specialtyModelId: '' },
    }
    await expect(staged.value.preview(nonModel)).rejects.toThrow(/only one advanced-model/)
    expect(writes.requests).toHaveLength(0)
  })

  it('rejects a stale re-read without POST', async () => {
    const base = snapshot()
    const stale = snapshot()
    stale.modelSettings.defaultModelId = 'openrouter::vendor/alternate-main'
    const writes = successfulWriteFetch()
    const staged = controller([base, stale], writes.fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'nous::vendor/title-model'
    })

    const result = await staged.value.commit({ reviewId: review.reviewId, confirmed: true })

    expect(result.status).toBe('error')
    expect(result.message).toMatch(/stale/)
    expect(writes.requests).toHaveLength(0)
  })

  it('consumes a successful review so duplicate commits write once', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base, base, base], writes.fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    expect((await staged.value.commit({ reviewId: review.reviewId, confirmed: true })).status).toBe('success')
    expect((await staged.value.commit({ reviewId: review.reviewId, confirmed: true })).status).toBe('error')
    expect(writes.requests).toHaveLength(1)
  })

  it('serializes distinct reviewed assignments so cancel and refresh ordering stay unambiguous', async () => {
    const base = snapshot()
    const afterMain = snapshot()
    afterMain.modelSettings.defaultModelId = 'openrouter::vendor/alternate-main'
    const writes = successfulWriteFetch()
    const staged = controller([base, base, afterMain], writes.fetcher)
    await staged.value.load()
    const mainDraft = settings(base)
    mainDraft.defaultModelId = 'openrouter::vendor/alternate-main'
    const auxiliaryDraft = settings(base)
    auxiliaryDraft.auxiliaryModels.push({ task: 'vision', modelId: 'openrouter::vendor/vision/model' })
    const mainReview = await staged.value.preview({ kind: 'models', settings: mainDraft })
    const auxiliaryReview = await staged.value.preview({ kind: 'models', settings: auxiliaryDraft })

    const first = staged.value.commit({ reviewId: mainReview.reviewId, confirmed: true })
    const concurrent = await staged.value.commit({ reviewId: auxiliaryReview.reviewId, confirmed: true })
    const committed = await first

    expect(committed.status).toBe('success')
    expect(concurrent).toMatchObject({ status: 'error', message: expect.stringMatching(/already committing/) })
    expect(writes.requests).toHaveLength(1)
  })

  it('rejects an oversized response without retrying', async () => {
    const base = snapshot()
    const fetcher: HermesExtensionSettingsLiveFetch = vi.fn(async () =>
      new Response('x'.repeat(64 * 1024 + 1), { status: 200 }))
    const staged = controller([base, base], fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    const result = await staged.value.commit({ reviewId: review.reviewId, confirmed: true })

    expect(result.status).toBe('error')
    expect(result.message).toMatch(/64 KiB/)
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('times out a bounded write and does not retry', async () => {
    const base = snapshot()
    let observedSignal: AbortSignal | undefined
    const fetcher: HermesExtensionSettingsLiveFetch = vi.fn((_input, init) =>
      new Promise<Response>((_resolve, reject) => {
        observedSignal = init?.signal
        init?.signal?.addEventListener('abort', () => {
          const error = new Error('aborted')
          error.name = 'AbortError'
          reject(error)
        }, { once: true })
      }))
    const staged = controller([base, base], fetcher, { writeTimeoutMs: 10 })
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    const result = await staged.value.commit({ reviewId: review.reviewId, confirmed: true })

    expect(result.status).toBe('error')
    expect(result.message).toMatch(/timed out/)
    expect(observedSignal?.aborted).toBe(true)
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('supports explicit abort of one pending write without retrying', async () => {
    const base = snapshot()
    let started!: () => void
    const didStart = new Promise<void>((resolve) => { started = resolve })
    const fetcher: HermesExtensionSettingsLiveFetch = vi.fn((_input, init) =>
      new Promise<Response>((_resolve, reject) => {
        started()
        init?.signal?.addEventListener('abort', () => {
          const error = new Error('aborted')
          error.name = 'AbortError'
          reject(error)
        }, { once: true })
      }))
    const staged = controller([base, base], fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    const pending = staged.value.commit({ reviewId: review.reviewId, confirmed: true })
    await didStart
    expect(staged.value.cancelPendingWrite()).toBe(true)
    const result = await pending

    expect(result.status).toBe('error')
    expect(result.message).toMatch(/cancelled/)
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('does not resend confirm_required and only sends pre-reviewed expensive confirmation', async () => {
    const base = snapshot()
    const confirmFetcher: HermesExtensionSettingsLiveFetch = vi.fn(async () =>
      response({ ok: false, confirm_required: true, confirm_message: 'raw pricing text' }))
    const standard = controller([base, base], confirmFetcher)
    const standardReview = await loadAndPreview(standard.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    const required = await standard.value.commit({ reviewId: standardReview.reviewId, confirmed: true })
    expect(required.status).toBe('unavailable')
    expect(required.message).not.toContain('raw pricing text')
    expect((await standard.value.commit({ reviewId: standardReview.reviewId, confirmed: true })).status).toBe('error')
    expect(confirmFetcher).toHaveBeenCalledTimes(1)
    expect(JSON.parse(String((confirmFetcher as ReturnType<typeof vi.fn>).mock.calls[0][1]?.body)))
      .not.toHaveProperty('confirm_expensive_model')

    const learnedReview = await standard.value.preview({
      kind: 'models',
      settings: { ...base.modelSettings, defaultModelId: 'openrouter::vendor/alternate-main' },
    })
    expect(learnedReview.requiresExpensiveModelConfirmation).toBe(true)
    expect((await standard.value.commit({
      reviewId: learnedReview.reviewId,
      confirmed: true,
    })).status).toBe('error')
    const retried = await standard.value.commit({
      reviewId: learnedReview.reviewId,
      confirmed: true,
      expensiveModelConfirmed: true,
    })
    expect(retried.status).toBe('unavailable')
    expect(confirmFetcher).toHaveBeenCalledTimes(2)
    expect(JSON.parse(String((confirmFetcher as ReturnType<typeof vi.fn>).mock.calls[1][1]?.body)))
      .toHaveProperty('confirm_expensive_model', true)

    const expensiveBase = snapshot()
    const expensive = expensiveBase.models.find((model) => model.id === 'openrouter::vendor/alternate-main')
    if (!expensive) throw new Error('fixture missing')
    expensive.costTier = 'expensive'
    const writes = successfulWriteFetch()
    const preReviewed = controller([expensiveBase, expensiveBase, expensiveBase], writes.fetcher)
    const expensiveReview = await loadAndPreview(preReviewed.value, expensiveBase, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })
    expect(expensiveReview.requiresExpensiveModelConfirmation).toBe(true)

    expect((await preReviewed.value.commit({
      reviewId: expensiveReview.reviewId,
      confirmed: true,
    })).status).toBe('error')
    expect(writes.requests).toHaveLength(0)

    expect((await preReviewed.value.commit({
      reviewId: expensiveReview.reviewId,
      confirmed: true,
      expensiveModelConfirmed: true,
    })).status).toBe('success')
    expect(writes.requests).toHaveLength(1)
    expect(writes.requests[0].body.confirm_expensive_model).toBe(true)
  })

  it('never serializes, returns, or sends provider secret validation input', async () => {
    const base = snapshot()
    const writes = successfulWriteFetch()
    const staged = controller([base], writes.fetcher)
    const stringify = vi.spyOn(JSON, 'stringify')
    const secret = 'do-not-retain-secret-value'

    const result = await staged.value.validateProvider({
      providerId: 'openrouter',
      endpoint: null,
      secrets: [{ fieldName: 'api_key', value: secret }],
    })

    expect(result).toEqual({
      providerId: 'openrouter',
      state: 'unavailable',
      message: 'Live provider credential validation is unavailable in this first model controller.',
    })
    expect(stringify).not.toHaveBeenCalled()
    stringify.mockRestore()
    expect(writes.requests).toHaveLength(0)
    expect(String(result)).not.toContain(secret)
  })

  it('reports native, auxiliary fallback, and missing vision readiness purely', () => {
    const native = snapshot()
    const nativeMain = native.models.find((model) => model.id === native.modelSettings.defaultModelId)
    if (!nativeMain) throw new Error('fixture missing')
    nativeMain.specialties.push('vision')
    expect(diagnoseVisionReadiness(native).state).toBe('native-ready')

    const fallback = snapshot()
    fallback.modelSettings.auxiliaryModels.push({
      task: 'vision',
      modelId: 'openrouter::vendor/vision/model',
    })
    expect(diagnoseVisionReadiness(fallback)).toMatchObject({
      state: 'fallback-ready',
      auxiliaryVisionModelId: 'openrouter::vendor/vision/model',
    })

    const missing = snapshot()
    expect(diagnoseVisionReadiness(missing).state).toBe('configuration-required')
    expect(diagnoseVisionReadiness(missing)).toEqual(diagnoseVisionReadiness(missing))
  })

  it('fails safe for malformed or empty live snapshots', async () => {
    const empty = snapshot()
    empty.models = []
    const writes = successfulWriteFetch()
    const staged = controller([empty, empty], writes.fetcher)

    const loaded = await staged.value.load()

    expect(loaded.state).toBe('error')
    expect(loaded.snapshot).toBeUndefined()
    const intent = { kind: 'models', settings: settings(snapshot()) } as const
    await expect(staged.value.preview(intent)).rejects.toThrow(/malformed|empty/)
    expect(writes.requests).toHaveLength(0)
    expect(diagnoseVisionReadiness(empty).state).toBe('configuration-required')
  })

  it('rejects structurally malformed live model fields before diagnostics or writes', async () => {
    const malformed = snapshot()
    malformed.models[0].specialties = undefined as never
    const writes = successfulWriteFetch()
    const staged = controller([malformed], writes.fetcher)

    const loaded = await staged.value.load()

    expect(loaded).toMatchObject({ state: 'error' })
    expect(loaded.snapshot).toBeUndefined()
    expect(diagnoseVisionReadiness(malformed).state).toBe('configuration-required')
    expect(writes.requests).toHaveLength(0)
  })

  it('fails closed on a mismatched acknowledgement', async () => {
    const base = snapshot()
    const fetcher: HermesExtensionSettingsLiveFetch = vi.fn(async () =>
      response({ ok: true, scope: 'main', provider: 'wrong', model: 'wrong' }))
    const staged = controller([base, base], fetcher)
    const review = await loadAndPreview(staged.value, base, (draft) => {
      draft.defaultModelId = 'openrouter::vendor/alternate-main'
    })

    const result = await staged.value.commit({ reviewId: review.reviewId, confirmed: true })

    expect(result.status).toBe('error')
    expect(result.message).toMatch(/mismatched/)
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})

