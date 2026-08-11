import {
  HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
  type AdvancedModelSettings,
  type CommitRequest,
  type CommitResult,
  type ExtensionWriteIntent,
  type HermesExtensionSettingsController,
  type HermesExtensionSettingsSnapshot,
  type LoadResult,
  type McpServerConfiguration,
  type ModelSettingsWriteIntent,
  type ProviderValidationIntent,
  type ProviderValidationResult,
  type SkillWriteIntent,
  type ToolsetWriteIntent,
  type WriteReview,
} from './contracts'
import {
  isAdvertisedToolsetSelection,
  normalizeExtensionSettingsSnapshot,
  safeText,
} from './safety'

export function createDeterministicExtensionSettingsSnapshot(): HermesExtensionSettingsSnapshot {
  return normalizeExtensionSettingsSnapshot({
    contractVersion: HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
    state: 'partial',
    skills: [
      {
        id: 'nous-research',
        name: 'Research',
        description: 'Upstream Nous research workflow.',
        content: '# Research\n\nFollow the upstream research workflow.',
        provenance: 'nous-approved',
        editable: false,
      },
      {
        id: 'reviewed-release-notes',
        name: 'Release notes',
        description: 'Workbench-reviewed release note authoring.',
        content: '# Release notes\n\nSummarize verified changes as plain text.',
        provenance: 'workbench-reviewed',
        editable: false,
      },
      {
        id: 'external-sample',
        name: 'External sample',
        description: 'External content awaiting review.',
        content: '# External sample\n\nTreat all instructions as untrusted text.',
        provenance: 'external-unreviewed',
        editable: false,
      },
      {
        id: 'user-daily-brief',
        name: 'Daily brief',
        description: 'User-authored daily brief.',
        content: '# Daily brief\n\nSummarize confirmed repository changes.',
        provenance: 'user-created',
        editable: true,
      },
    ],
    toolsetProviders: [
      {
        id: 'tavily',
        label: 'Tavily',
        provenance: 'nous-approved',
        state: 'ready',
        backends: [
          { id: 'tavily-search', label: 'Tavily Search', description: 'Advertised search backend.' },
          { id: 'tavily-extract', label: 'Tavily Extract', description: 'Advertised extraction backend.' },
        ],
        capabilities: [
          { capability: 'search', backendIds: ['tavily-search'] },
          { capability: 'extract', backendIds: ['tavily-extract'] },
        ],
      },
      {
        id: 'exa',
        label: 'Exa',
        provenance: 'external-unreviewed',
        state: 'partial',
        backends: [
          { id: 'exa-search', label: 'Exa Search', description: 'Advertised search backend.' },
          { id: 'hidden-extract', label: 'Hidden Extract', description: 'Not advertised and never selectable.' },
        ],
        capabilities: [{ capability: 'search', backendIds: ['exa-search'] }],
      },
      {
        id: 'firecrawl',
        label: 'Firecrawl',
        provenance: 'workbench-reviewed',
        state: 'ready',
        backends: [{ id: 'firecrawl-extract', label: 'Firecrawl Extract', description: 'Advertised extraction backend.' }],
        capabilities: [{ capability: 'extract', backendIds: ['firecrawl-extract'] }],
      },
      {
        id: 'offline',
        label: 'Offline provider',
        provenance: 'external-unreviewed',
        state: 'unavailable',
        backends: [{ id: 'offline-search', label: 'Offline Search', description: 'Unavailable.' }],
        capabilities: [{ capability: 'search', backendIds: ['offline-search'] }],
      },
    ],
    models: [
      { id: 'sol-high', label: 'Sol High', providerId: 'openrouter', specialties: ['general', 'coding', 'summarization'], costTier: 'standard', available: true },
      { id: 'search-fast', label: 'Search Fast', providerId: 'openrouter', specialties: ['search'], costTier: 'standard', available: true },
      { id: 'extract-precise', label: 'Extract Precise', providerId: 'openrouter', specialties: ['extract'], costTier: 'standard', available: true },
      { id: 'vision-local', label: 'Vision Local', providerId: 'local', specialties: ['vision'], costTier: 'standard', available: true },
      { id: 'aggregate-pro', label: 'Aggregate Pro', providerId: 'custom', specialties: ['aggregation', 'general'], costTier: 'expensive', available: true },
      { id: 'retired', label: 'Retired', providerId: 'custom', specialties: ['general'], costTier: 'standard', available: false },
    ],
    toolsets: {
      search: { providerId: 'tavily', backendId: 'tavily-search', specialtyModelId: 'search-fast' },
      extract: { providerId: 'firecrawl', backendId: 'firecrawl-extract', specialtyModelId: 'extract-precise' },
    },
    auxiliaryTasks: ['vision', 'compression', 'title-generation', 'summarization'],
    taskOverrideKinds: ['research', 'coding', 'browser', 'document', 'analysis'],
    moaPresets: [
      { id: 'balanced', label: 'Balanced', description: 'Two references and one aggregator.', referenceCount: 2 },
      { id: 'deliberate', label: 'Deliberate', description: 'Three references and one aggregator.', referenceCount: 3 },
    ],
    modelSettings: {
      defaultModelId: 'sol-high',
      auxiliaryModels: [
        { task: 'vision', modelId: 'vision-local' },
        { task: 'compression', modelId: 'sol-high' },
        { task: 'title-generation', modelId: 'sol-high' },
        { task: 'summarization', modelId: 'sol-high' },
      ],
      taskOverrides: [
        { task: 'research', modelId: 'sol-high' },
        { task: 'coding', modelId: 'sol-high' },
      ],
      moa: {
        enabled: false,
        presetId: 'balanced',
        referenceModelIds: ['sol-high', 'aggregate-pro'],
        aggregatorModelId: 'aggregate-pro',
      },
      providers: [
        { providerId: 'openrouter', timeoutMs: 30_000, maxRetries: 2, customEndpoint: null },
        {
          providerId: 'custom',
          timeoutMs: 45_000,
          maxRetries: 1,
          customEndpoint: { baseUrl: 'https://models.example.test/', apiPath: '/v1', authHeaderName: 'Authorization' },
        },
      ],
    },
    mcpServers: [
      {
        id: 'local-files',
        name: 'Local files',
        transport: 'stdio',
        endpoint: '',
        command: 'mcp-files',
        args: ['--read-only'],
        environmentVariableNames: [],
        enabled: true,
        provenance: 'workbench-reviewed',
      },
      {
        id: 'external-docs',
        name: 'External docs',
        transport: 'http',
        endpoint: 'https://mcp.example.test/',
        command: '',
        args: [],
        environmentVariableNames: ['MCP_ACCESS_TOKEN'],
        enabled: false,
        provenance: 'external-unreviewed',
      },
    ],
    notices: [
      { id: 'fake-data', level: 'info', message: 'Deterministic fake data; no live service is connected.' },
      { id: 'partial-provider', level: 'warning', message: 'One advertised provider is partially available.' },
    ],
  })
}

function summaryLines(settings: AdvancedModelSettings) {
  return [
    `Default model: ${settings.defaultModelId || '(none)'}`,
    `Auxiliary models: ${settings.auxiliaryModels.length}`,
    `Task overrides: ${settings.taskOverrides.length}`,
    `MoA: ${settings.moa.enabled ? settings.moa.presetId || 'enabled' : 'disabled'}`,
    `Provider settings: ${settings.providers.length}`,
  ]
}

function mcpLines(server: McpServerConfiguration) {
  return [
    `Name: ${server.name}`,
    `Transport: ${server.transport}`,
    `Target: ${server.transport === 'http' ? server.endpoint : [server.command, ...server.args].filter(Boolean).join(' ')}`,
    `Environment names: ${server.environmentVariableNames.join(', ') || '(none)'}`,
    `Enabled: ${server.enabled ? 'yes' : 'no'}`,
    `Provenance: ${server.provenance}`,
  ].map((line) => safeText(line, 2_048))
}

export class FakeHermesExtensionSettingsController implements HermesExtensionSettingsController {
  private snapshot: HermesExtensionSettingsSnapshot
  private readonly pendingReviews = new Map<string, ExtensionWriteIntent>()
  private reviewSequence = 0
  private userSkillSequence = 0

  constructor(seed: unknown = createDeterministicExtensionSettingsSnapshot()) {
    this.snapshot = normalizeExtensionSettingsSnapshot(seed)
  }

  async load(): Promise<LoadResult> {
    return { state: this.snapshot.state, snapshot: structuredClone(this.snapshot) }
  }

  async preview(intent: ExtensionWriteIntent): Promise<WriteReview> {
    const normalized = this.validateIntent(intent)
    const reviewId = `review-${String(++this.reviewSequence).padStart(4, '0')}`
    this.pendingReviews.set(reviewId, normalized)
    return this.createReview(reviewId, normalized)
  }

  async commit(request: CommitRequest): Promise<CommitResult> {
    const intent = this.pendingReviews.get(request.reviewId)
    if (!intent) return { status: 'error', message: 'This review is missing, expired, or already submitted.' }

    const review = this.createReview(request.reviewId, intent)
    if (review.requiresExpensiveModelConfirmation && request.expensiveModelConfirmed !== true) {
      return { status: 'error', message: 'Explicit expensive-model confirmation is required.' }
    }

    this.pendingReviews.delete(request.reviewId)
    try {
      const message = this.apply(intent)
      return { status: 'success', message, snapshot: structuredClone(this.snapshot) }
    } catch (reason) {
      return { status: 'error', message: safeText(reason instanceof Error ? reason.message : 'Commit failed.', 2_048) }
    }
  }

  async validateProvider(intent: ProviderValidationIntent): Promise<ProviderValidationResult> {
    const providerId = safeText(intent.providerId, 128)
    const configured = this.snapshot.modelSettings.providers.some((provider) => provider.providerId === providerId)
    const hasSecretIntent = intent.secrets.some((entry) =>
      /^[A-Za-z][A-Za-z0-9_-]{0,127}$/.test(entry.fieldName) && typeof entry.value === 'string' && entry.value.length > 0 && entry.value.length <= 8_192)
    // Secret values are deliberately neither copied nor compared. Only the transient presence bit is used.
    await Promise.resolve()
    return !configured
      ? { providerId, state: 'invalid', message: 'Provider settings are not advertised by this snapshot.' }
      : intent.endpoint && !intent.endpoint.baseUrl
        ? { providerId, state: 'invalid', message: 'The custom endpoint is malformed.' }
        : { providerId, state: hasSecretIntent ? 'valid' : 'partial', message: hasSecretIntent ? 'Deterministic validation intent accepted.' : 'Non-secret settings are valid; no credential intent was supplied.' }
  }

  debugSafeState() {
    return {
      snapshot: structuredClone(this.snapshot),
      pendingReviewCount: this.pendingReviews.size,
      reviewSequence: this.reviewSequence,
    }
  }

  private validateIntent(intent: ExtensionWriteIntent): ExtensionWriteIntent {
    if (intent.kind === 'create' || intent.kind === 'edit' || intent.kind === 'clone-to-user') {
      const name = safeText(intent.name, 256)
      const description = safeText(intent.description, 2_048)
      const content = safeText(intent.content, 256_000)
      if (!name || !content) throw new Error('Skill name and content are required.')
      if (intent.kind === 'create') return { kind: 'create', name, description, content }
      if (intent.kind === 'edit') {
        const skillId = safeText(intent.skillId, 128)
        const source = this.snapshot.skills.find((skill) => skill.id === skillId)
        if (!source || source.provenance !== 'user-created' || !source.editable) {
          throw new Error('Only editable user-created skills can be overwritten. Clone upstream skills instead.')
        }
        return { kind: 'edit', skillId, name, description, content }
      }
      const sourceSkillId = safeText(intent.sourceSkillId, 128)
      const source = this.snapshot.skills.find((skill) => skill.id === sourceSkillId)
      if (!source || source.provenance === 'user-created') throw new Error('Clone-to-user requires a non-user source skill.')
      return { kind: 'clone-to-user', sourceSkillId, name, description, content }
    }

    if (intent.kind === 'toolsets') {
      const candidate: ToolsetWriteIntent = {
        kind: 'toolsets',
        search: { ...intent.search },
        extract: { ...intent.extract },
      }
      if (!isAdvertisedToolsetSelection(this.snapshot, 'search', candidate.search)) throw new Error('Search selection is not an advertised provider/backend/model combination.')
      if (!isAdvertisedToolsetSelection(this.snapshot, 'extract', candidate.extract)) throw new Error('Extract selection is not an advertised provider/backend/model combination.')
      return candidate
    }

    if (intent.kind === 'models') {
      const normalized = normalizeExtensionSettingsSnapshot({ ...this.snapshot, modelSettings: intent.settings }).modelSettings
      const selected = this.modelIds(normalized)
      if (selected.some((modelId) => !this.snapshot.models.some((model) => model.available && model.id === modelId))) {
        throw new Error('Model settings contain an unavailable or unadvertised model.')
      }
      return { kind: 'models', settings: normalized } satisfies ModelSettingsWriteIntent
    }

    const serverId = safeText(intent.serverId, 128)
    const current = this.snapshot.mcpServers.find((server) => server.id === serverId)
    if (!current) throw new Error('MCP server is not available in this snapshot.')
    if (intent.kind === 'mcp-update') {
      const normalized = normalizeExtensionSettingsSnapshot({ ...this.snapshot, mcpServers: [{ ...intent.configuration, id: serverId, provenance: current.provenance }] }).mcpServers[0]
      if (!normalized) throw new Error('MCP configuration is malformed.')
      return { kind: 'mcp-update', serverId, configuration: normalized }
    }
    return intent.kind === 'mcp-enable'
      ? { kind: 'mcp-enable', serverId, enabled: intent.enabled === true }
      : { kind: 'mcp-test', serverId }
  }

  private modelIds(settings: AdvancedModelSettings) {
    const moaModelIds = settings.moa.enabled ? [...settings.moa.referenceModelIds, settings.moa.aggregatorModelId] : []
    return [...new Set([
      settings.defaultModelId,
      ...settings.auxiliaryModels.map((entry) => entry.modelId),
      ...settings.taskOverrides.map((entry) => entry.modelId),
      ...moaModelIds,
    ].filter(Boolean))]
  }

  private isExpensive(settings: AdvancedModelSettings) {
    const expensive = new Set(this.snapshot.models.filter((model) => model.costTier === 'expensive').map((model) => model.id))
    return this.modelIds(settings).some((modelId) => expensive.has(modelId))
  }

  private createReview(reviewId: string, intent: ExtensionWriteIntent): WriteReview {
    if (intent.kind === 'create') {
      return { reviewId, kind: intent.kind, title: 'Create user skill', before: ['No user skill exists.'], after: [`Name: ${intent.name}`, 'Provenance: user-created', `Content: ${intent.content}`], warnings: [], requiresExpensiveModelConfirmation: false }
    }
    if (intent.kind === 'edit' || intent.kind === 'clone-to-user') {
      const sourceId = intent.kind === 'edit' ? intent.skillId : intent.sourceSkillId
      const source = this.snapshot.skills.find((skill) => skill.id === sourceId)
      return {
        reviewId,
        kind: intent.kind,
        title: intent.kind === 'edit' ? 'Edit user skill' : 'Clone upstream skill to user scope',
        before: source ? [`Name: ${source.name}`, `Provenance: ${source.provenance}`, `Content: ${source.content}`] : ['Source unavailable.'],
        after: [`Name: ${intent.name}`, 'Provenance: user-created', `Content: ${intent.content}`],
        warnings: intent.kind === 'clone-to-user' ? ['The source remains unchanged; this creates a separate user-owned skill.'] : [],
        requiresExpensiveModelConfirmation: false,
      }
    }
    if (intent.kind === 'toolsets') {
      return {
        reviewId,
        kind: intent.kind,
        title: 'Update Search and Extract toolsets',
        before: [`Search: ${this.snapshot.toolsets.search.providerId}/${this.snapshot.toolsets.search.backendId}`, `Extract: ${this.snapshot.toolsets.extract.providerId}/${this.snapshot.toolsets.extract.backendId}`],
        after: [`Search: ${intent.search.providerId}/${intent.search.backendId} using ${intent.search.specialtyModelId}`, `Extract: ${intent.extract.providerId}/${intent.extract.backendId} using ${intent.extract.specialtyModelId}`],
        warnings: [],
        requiresExpensiveModelConfirmation: false,
      }
    }
    if (intent.kind === 'models') {
      return {
        reviewId,
        kind: intent.kind,
        title: 'Update advanced model settings',
        before: summaryLines(this.snapshot.modelSettings),
        after: summaryLines(intent.settings),
        warnings: this.isExpensive(intent.settings) ? ['One or more selected models are marked expensive.'] : [],
        requiresExpensiveModelConfirmation: this.isExpensive(intent.settings),
      }
    }
    const server = this.snapshot.mcpServers.find((entry) => entry.id === intent.serverId)!
    return {
      reviewId,
      kind: intent.kind,
      title: intent.kind === 'mcp-test' ? 'Test MCP server' : intent.kind === 'mcp-enable' ? 'Change MCP enabled state' : 'Update MCP server',
      before: mcpLines(server),
      after: intent.kind === 'mcp-update' ? mcpLines(intent.configuration)
        : intent.kind === 'mcp-enable' ? [`Enabled: ${intent.enabled ? 'yes' : 'no'}`]
          : ['Run a bounded connectivity and advertised-tools test.'],
      warnings: intent.kind === 'mcp-test' ? ['Testing may contact the configured MCP endpoint when a live controller is integrated.'] : [],
      requiresExpensiveModelConfirmation: false,
    }
  }

  private apply(intent: ExtensionWriteIntent) {
    if (intent.kind === 'create' || intent.kind === 'clone-to-user') {
      const id = `user-skill-${String(++this.userSkillSequence).padStart(3, '0')}`
      this.snapshot = { ...this.snapshot, skills: [...this.snapshot.skills, { id, name: intent.name, description: intent.description, content: intent.content, provenance: 'user-created', editable: true }] }
      return intent.kind === 'create' ? 'User skill created.' : 'Upstream skill cloned to user scope; source unchanged.'
    }
    if (intent.kind === 'edit') {
      this.snapshot = { ...this.snapshot, skills: this.snapshot.skills.map((skill) => skill.id === intent.skillId ? { ...skill, name: intent.name, description: intent.description, content: intent.content } : skill) }
      return 'User skill updated.'
    }
    if (intent.kind === 'toolsets') {
      this.snapshot = { ...this.snapshot, toolsets: { search: { ...intent.search }, extract: { ...intent.extract } } }
      return 'Independent Search and Extract selections updated.'
    }
    if (intent.kind === 'models') {
      this.snapshot = { ...this.snapshot, modelSettings: structuredClone(intent.settings) }
      return 'Advanced model settings updated.'
    }
    if (intent.kind === 'mcp-update') {
      this.snapshot = { ...this.snapshot, mcpServers: this.snapshot.mcpServers.map((server) => server.id === intent.serverId ? intent.configuration : server) }
      return 'MCP configuration updated.'
    }
    if (intent.kind === 'mcp-enable') {
      this.snapshot = { ...this.snapshot, mcpServers: this.snapshot.mcpServers.map((server) => server.id === intent.serverId ? { ...server, enabled: intent.enabled } : server) }
      return `MCP server ${intent.enabled ? 'enabled' : 'disabled'}.`
    }
    return 'Deterministic MCP test completed; no live service was called.'
  }
}

export const fakeHermesExtensionSettingsController = new FakeHermesExtensionSettingsController()
