import {
  HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
  type HermesExtensionSettingsSnapshot,
} from '../HermesExtensionSettings/contracts'
import {
  HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION,
  liveAdapterLimits,
  type HermesExtensionSettingsLiveAdapterOptions,
  type HermesExtensionSettingsLiveReadRequest,
  type HermesExtensionSettingsLiveReadResult,
  type LiveReadState,
  type LiveSourceId,
  type LiveSourceReport,
  type LiveSourceState,
  type UnsupportedMutationKind,
  type UnsupportedMutationResult,
} from './contracts'
import {
  buildLiveAdvancedModelSettings,
  liveObject,
  liveSafeId,
  liveSafeText,
  mergeLiveSkillContents,
  normalizeLiveMcpCatalogNames,
  normalizeLiveMcpServers,
  normalizeLiveModelOptions,
  normalizeLiveSkillContent,
  normalizeLiveSkillInventory,
  normalizeLiveToolsetInventory,
  normalizeLiveWebToolset,
} from './normalization'

export type HermesExtensionSettingsLiveFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

interface SourceResult {
  value?: unknown
  report: LiveSourceReport
}

class ResponseTooLargeError extends Error {}

function route(source: LiveSourceId) {
  const routes: Record<LiveSourceId, string> = {
    skills: '/api/skills',
    'skill-contents': '/api/skills/content',
    toolsets: '/api/tools/toolsets',
    'web-toolset-config': '/api/tools/toolsets/web/config',
    'mcp-servers': '/api/mcp/servers',
    'mcp-catalog': '/api/mcp/catalog',
    'model-options': '/api/model/options',
    'model-info': '/api/model/info',
    'model-auxiliary': '/api/model/auxiliary',
    'model-moa': '/api/model/moa',
  }
  return routes[source]
}

function report(source: LiveSourceId, state: LiveSourceState, itemCount: number, message: string): LiveSourceReport {
  return { source, route: route(source), state, itemCount, message: liveSafeText(message, 512) }
}

function isAbort(reason: unknown) {
  return reason instanceof DOMException
    ? reason.name === 'AbortError'
    : reason instanceof Error && reason.name === 'AbortError'
}

async function boundedResponseText(response: Response) {
  const declared = Number(response.headers.get('Content-Length'))
  if (Number.isFinite(declared) && declared > liveAdapterLimits.responseBytes) {
    throw new ResponseTooLargeError('Response exceeded the adapter byte limit.')
  }

  if (!response.body) {
    const buffer = await response.arrayBuffer()
    if (buffer.byteLength > liveAdapterLimits.responseBytes) throw new ResponseTooLargeError('Response exceeded the adapter byte limit.')
    return new TextDecoder().decode(buffer)
  }

  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  while (true) {
    const next = await reader.read()
    if (next.done) break
    total += next.value.byteLength
    if (total > liveAdapterLimits.responseBytes) {
      await reader.cancel().catch(() => undefined)
      throw new ResponseTooLargeError('Response exceeded the adapter byte limit.')
    }
    chunks.push(next.value)
  }
  const merged = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    merged.set(chunk, offset)
    offset += chunk.byteLength
  }
  return new TextDecoder().decode(merged)
}

function emptySnapshot(state: HermesExtensionSettingsSnapshot['state'] = 'unavailable'): HermesExtensionSettingsSnapshot {
  return {
    contractVersion: HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
    state,
    skills: [],
    toolsetProviders: [],
    models: [],
    toolsets: {
      search: { providerId: '', backendId: '', specialtyModelId: '' },
      extract: { providerId: '', backendId: '', specialtyModelId: '' },
    },
    auxiliaryTasks: [],
    taskOverrideKinds: [],
    moaPresets: [],
    modelSettings: {
      defaultModelId: '',
      auxiliaryModels: [],
      taskOverrides: [],
      moa: { enabled: false, presetId: '', referenceModelIds: [], aggregatorModelId: '' },
      providers: [],
    },
    mcpServers: [],
    notices: [],
  }
}

export class HermesExtensionSettingsLiveAdapter {
  private readonly fetcher: HermesExtensionSettingsLiveFetch
  private readonly reviews
  private readonly now
  private readonly inFlight = new Set<string>()
  private readonly recent = new Set<string>()
  private readonly recentOrder: string[] = []

  constructor(
    fetcher: HermesExtensionSettingsLiveFetch = (...arguments_) => globalThis.fetch(...arguments_),
    options: HermesExtensionSettingsLiveAdapterOptions = {},
  ) {
    this.fetcher = fetcher
    this.reviews = (options.workbenchReviews ?? []).slice(0, liveAdapterLimits.collection)
    this.now = options.now ?? (() => new Date())
  }

  async read(request: HermesExtensionSettingsLiveReadRequest): Promise<HermesExtensionSettingsLiveReadResult> {
    const identity = this.identity(request)
    if (!identity.ok) return this.terminalResult(identity.correlationId, identity.profileId, 'error', identity.message)
    const key = `${identity.profileId ?? '(default)'}::${identity.correlationId}`
    if (this.inFlight.has(key) || this.recent.has(key)) {
      return this.terminalResult(identity.correlationId, identity.profileId, 'duplicate', 'Duplicate correlation identity was rejected.')
    }
    this.inFlight.add(key)

    try {
      if (request.signal?.aborted) {
        return this.terminalResult(identity.correlationId, identity.profileId, 'cancelled', 'Read cancelled before dispatch.')
      }

      const scoped = { ...request, profileId: identity.profileId ?? undefined, correlationId: identity.correlationId }
      const [
        skillsSource,
        toolsetsSource,
        webSource,
        mcpServersSource,
        mcpCatalogSource,
        modelOptionsSource,
        modelInfoSource,
        auxiliarySource,
        moaSource,
      ] = await Promise.all([
        this.readJson('skills', scoped),
        this.readJson('toolsets', scoped),
        this.readJson('web-toolset-config', scoped),
        this.readJson('mcp-servers', scoped),
        this.readJson('mcp-catalog', scoped),
        this.readJson('model-options', scoped, { explicit_only: 'true', include_unconfigured: 'false', refresh: 'false' }),
        this.readJson('model-info', scoped),
        this.readJson('model-auxiliary', scoped),
        this.readJson('model-moa', scoped),
      ])

      const skills = normalizeLiveSkillInventory(skillsSource.value, this.reviews)
      this.refine(skillsSource.report, skills.length, skillsSource.value !== undefined && !Array.isArray(skillsSource.value))
      const contentSource = await this.readSkillContents(skills, scoped)
      const mergedSkills = mergeLiveSkillContents(skills, contentSource.contents)

      const toolsetInventory = normalizeLiveToolsetInventory(toolsetsSource.value)
      this.refine(toolsetsSource.report, toolsetInventory.itemCount, toolsetsSource.value !== undefined && !Array.isArray(toolsetsSource.value))
      const web = normalizeLiveWebToolset(webSource.value, this.reviews)
      const webMalformed = webSource.value !== undefined && !Array.isArray(liveObject(webSource.value).providers)
      this.refine(webSource.report, web.providers.length, webMalformed)
      if (toolsetsSource.report.state === 'ready' && (!toolsetInventory.webAdvertised || !toolsetInventory.webAvailable)) {
        webSource.report.state = 'partial'
        webSource.report.message = 'The web toolset is not advertised as available.'
      }

      const catalogNames = normalizeLiveMcpCatalogNames(mcpCatalogSource.value)
      this.refine(mcpCatalogSource.report, catalogNames.size, mcpCatalogSource.value !== undefined && !Array.isArray(liveObject(mcpCatalogSource.value).entries))
      const mcpServers = normalizeLiveMcpServers(mcpServersSource.value, catalogNames, this.reviews)
      this.refine(mcpServersSource.report, mcpServers.length, mcpServersSource.value !== undefined && !Array.isArray(liveObject(mcpServersSource.value).servers))

      const modelOptions = normalizeLiveModelOptions(modelOptionsSource.value, modelInfoSource.value)
      this.refine(modelOptionsSource.report, modelOptions.models.length, modelOptionsSource.value !== undefined && !Array.isArray(liveObject(modelOptionsSource.value).providers))
      this.refine(modelInfoSource.report, modelInfoSource.value === undefined ? 0 : 1, modelInfoSource.value !== undefined && Object.keys(liveObject(modelInfoSource.value)).length === 0)
      const advanced = buildLiveAdvancedModelSettings(modelOptions, auxiliarySource.value, moaSource.value)
      const auxiliaryMalformed = auxiliarySource.value !== undefined && !Array.isArray(liveObject(auxiliarySource.value).tasks)
      this.refine(auxiliarySource.report, advanced.settings.auxiliaryModels.length, auxiliaryMalformed)
      const moaMalformed = moaSource.value !== undefined && Object.keys(liveObject(liveObject(moaSource.value).presets)).length === 0
      this.refine(moaSource.report, advanced.presets.length, moaMalformed)

      const sources = [
        skillsSource.report,
        contentSource.report,
        toolsetsSource.report,
        webSource.report,
        mcpServersSource.report,
        mcpCatalogSource.report,
        modelOptionsSource.report,
        modelInfoSource.report,
        auxiliarySource.report,
        moaSource.report,
      ]
      const state = this.aggregateState(sources, request.signal)
      const snapshotState: HermesExtensionSettingsSnapshot['state'] = state === 'ready'
        ? 'ready'
        : state === 'partial'
          ? 'partial'
          : state === 'error'
            ? 'error'
            : 'unavailable'
      const snapshot: HermesExtensionSettingsSnapshot = {
        contractVersion: HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
        state: snapshotState,
        skills: mergedSkills,
        toolsetProviders: web.providers,
        models: modelOptions.models,
        toolsets: web.selections,
        auxiliaryTasks: ['vision', 'compression', 'title-generation'],
        taskOverrideKinds: [],
        moaPresets: advanced.presets,
        modelSettings: advanced.settings,
        mcpServers,
        notices: sources.filter((source) => source.state !== 'ready').map((source) => ({
          id: liveSafeId(source.source),
          level: source.state === 'error' ? 'error' as const : 'warning' as const,
          message: `${source.source}: ${source.message}`,
        })).slice(0, 32),
      }

      return {
        contractVersion: HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION,
        correlationId: identity.correlationId,
        profileId: identity.profileId,
        state,
        snapshot,
        sources,
        completedAt: this.now().toISOString(),
      }
    } finally {
      this.inFlight.delete(key)
      this.remember(key)
    }
  }

  createSkill(): Promise<UnsupportedMutationResult> {
    return this.unsupported('skill-create')
  }

  editSkill(): Promise<UnsupportedMutationResult> {
    return this.unsupported('skill-edit')
  }

  installSkill(): Promise<UnsupportedMutationResult> {
    return this.unsupported('skill-install')
  }

  configureToolset(): Promise<UnsupportedMutationResult> {
    return this.unsupported('toolset-configure')
  }

  configureModels(): Promise<UnsupportedMutationResult> {
    return this.unsupported('model-configure')
  }

  configureMcp(): Promise<UnsupportedMutationResult> {
    return this.unsupported('mcp-configure')
  }

  private async unsupported(kind: UnsupportedMutationKind): Promise<UnsupportedMutationResult> {
    return {
      status: 'unavailable',
      kind,
      message: 'This versioned adapter is read-only; no source-confirmed mutation is implemented.',
    }
  }

  private identity(request: HermesExtensionSettingsLiveReadRequest) {
    const correlationId = liveSafeText(request.correlationId, liveAdapterLimits.identifier)
    const profileId = request.profileId === undefined ? null : liveSafeText(request.profileId, liveAdapterLimits.identifier)
    if (!/^[A-Za-z0-9._:-]{1,128}$/.test(correlationId)) {
      return { ok: false as const, correlationId, profileId, message: 'A valid correlation identity is required.' }
    }
    if (profileId !== null && !/^[A-Za-z0-9._:-]{1,128}$/.test(profileId)) {
      return { ok: false as const, correlationId, profileId, message: 'The profile identity is invalid.' }
    }
    return { ok: true as const, correlationId, profileId, message: '' }
  }

  private terminalResult(
    correlationId: string,
    profileId: string | null,
    state: Extract<LiveReadState, 'error' | 'cancelled' | 'duplicate'>,
    message: string,
  ): HermesExtensionSettingsLiveReadResult {
    const snapshot = emptySnapshot(state === 'error' ? 'error' : 'unavailable')
    snapshot.notices = [{ id: liveSafeId(state), level: state === 'error' ? 'error' : 'warning', message: liveSafeText(message, 512) }]
    return {
      contractVersion: HERMES_EXTENSION_SETTINGS_LIVE_CONTRACT_VERSION,
      correlationId,
      profileId,
      state,
      snapshot,
      sources: [],
      completedAt: this.now().toISOString(),
    }
  }

  private async readJson(
    source: LiveSourceId,
    request: HermesExtensionSettingsLiveReadRequest,
    parameters: Record<string, string> = {},
  ): Promise<SourceResult> {
    if (request.signal?.aborted) return { report: report(source, 'cancelled', 0, 'Read cancelled.') }
    const search = new URLSearchParams(parameters)
    if (request.profileId) search.set('profile', request.profileId)
    const url = `${route(source)}${search.size ? `?${search.toString()}` : ''}`
    try {
      const response = await this.fetcher(url, {
        method: 'GET',
        credentials: 'include',
        signal: request.signal,
        headers: {
          Accept: 'application/json',
          'X-Hermes-Correlation-Id': request.correlationId,
        },
      })
      if (!response.ok) {
        const state: LiveSourceState = [401, 403, 404, 501, 503].includes(response.status) ? 'unavailable' : 'error'
        return { report: report(source, state, 0, `Source returned HTTP ${response.status}.`) }
      }
      const text = await boundedResponseText(response)
      const value = JSON.parse(text) as unknown
      return { value, report: report(source, 'ready', 0, 'Source read completed.') }
    } catch (reason) {
      if (isAbort(reason) || request.signal?.aborted) return { report: report(source, 'cancelled', 0, 'Read cancelled.') }
      if (reason instanceof ResponseTooLargeError) return { report: report(source, 'error', 0, 'Source response exceeded the byte limit.') }
      return { report: report(source, 'error', 0, 'Source response was unavailable or malformed.') }
    }
  }

  private async readSkillContents(
    skills: HermesExtensionSettingsSnapshot['skills'],
    request: HermesExtensionSettingsLiveReadRequest,
  ) {
    if (skills.length === 0) {
      return {
        contents: [] as { id: string; content: string }[],
        report: report('skill-contents', 'ready', 0, 'No skill content reads were required.'),
      }
    }
    const selected = skills.slice(0, liveAdapterLimits.skillContents)
    const contents: { id: string; content: string }[] = []
    let failed = 0
    let cancelled = false
    for (let index = 0; index < selected.length; index += 4) {
      const batch = selected.slice(index, index + 4)
      const rows = await Promise.all(batch.map((skill) => this.readJson('skill-contents', request, { name: skill.name })))
      for (const row of rows) {
        if (row.report.state === 'cancelled') cancelled = true
        if (row.report.state !== 'ready') {
          failed += 1
          continue
        }
        const normalized = normalizeLiveSkillContent(row.value)
        if (normalized) contents.push(normalized)
        else failed += 1
      }
      if (cancelled) break
    }
    const truncated = skills.length > selected.length
    const state: LiveSourceState = cancelled
      ? 'cancelled'
      : failed > 0 || truncated
        ? contents.length > 0 ? 'partial' : 'unavailable'
        : 'ready'
    const message = cancelled
      ? 'Skill content reads were cancelled.'
      : truncated
        ? `Skill content reads were capped at ${liveAdapterLimits.skillContents}.`
        : failed
          ? `${failed} skill content response(s) were unavailable or malformed.`
          : 'Skill content reads completed.'
    return { contents, report: report('skill-contents', state, contents.length, message) }
  }

  private refine(source: LiveSourceReport, itemCount: number, malformed: boolean) {
    source.itemCount = Math.max(0, Math.min(liveAdapterLimits.collection, itemCount))
    if (source.state === 'ready' && malformed) {
      source.state = 'partial'
      source.message = 'Malformed source fields were ignored.'
    }
  }

  private aggregateState(sources: readonly LiveSourceReport[], signal?: AbortSignal): LiveReadState {
    if (signal?.aborted || sources.some((source) => source.state === 'cancelled')) return 'cancelled'
    if (sources.every((source) => source.state === 'ready')) return 'ready'
    const useful = sources.some((source) => source.state === 'ready' || source.state === 'partial')
    if (useful) return 'partial'
    return sources.some((source) => source.state === 'error') ? 'error' : 'unavailable'
  }

  private remember(key: string) {
    this.recent.add(key)
    this.recentOrder.push(key)
    while (this.recentOrder.length > liveAdapterLimits.recentCorrelations) {
      const oldest = this.recentOrder.shift()
      if (oldest) this.recent.delete(oldest)
    }
  }
}
