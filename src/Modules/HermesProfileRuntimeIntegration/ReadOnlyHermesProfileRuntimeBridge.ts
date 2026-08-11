import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type AdapterResponse,
  type ApplyImportRequest,
  type ApplyProfileMutationRequest,
  type ConfirmPermissionGrantRequest,
  type ConfigurationFieldSchema,
  type ExportProfileRequest,
  type HermesProfileRuntimeAdapter,
  type PreviewImportRequest,
  type PreviewPermissionGrantRequest,
  type PreviewProfileMutationRequest,
  type ProfileExport,
  type ProfileMutationResult,
  type ProfileRuntimeSnapshot,
  type ProfileSelectionResult,
  type RequestContext,
  type SaveConfigurationRequest,
  type SaveDocumentRequest,
  type SaveIntentRequest,
  type SelectActiveProfileRequest,
  type SelectTerminalBackendRequest,
  type OperationPreview,
} from '../HermesProfileRuntime/contracts'
import { PROFILE_RUNTIME_LIMITS } from '../HermesProfileRuntime/runtimeSafety'
import {
  HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
  HERMES_PROFILE_RUNTIME_LIVE_BOUNDS,
  LiveHermesProfileRuntimeAdapter,
  type ConfigurationFieldMetadata,
  type HermesProfileRuntimeLiveAdapterContract,
  type LiveAdapterResult,
  type LiveErrorCode,
  type LiveRequestIdentity,
  type LiveSection,
  type ProfileInventoryItem,
  type ProfileRuntimeLiveSnapshot,
  type ProviderStatus,
} from '../HermesProfileRuntimeLive'
import {
  HermesProfileRuntimeBridgeError,
  HermesProfileRuntimeReadOnlyError,
  type HermesProfileRuntimeBridgeErrorCode,
  type HermesProfileRuntimeReadOnlyOperation,
} from './contracts'
import {
  createSameOriginProfileRuntimeTransport,
  type SameOriginProfileRuntimeFetch,
} from './sameOriginTransport'

const MAX_RENDERER_REVISION = 2_147_483_647
const MAX_REVISIONS_PER_PROFILE = 256
const MAX_DETAIL_CHARACTERS = 4_096
const MAX_NOTICE_CHARACTERS = 2_048
const PROFILE_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/
const CORRELATION_ID = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const SECRET_CONFIGURATION_KEY = /(?:secret|token|password|api[-_]?key|credential|private[-_]?key|authorization|cookie|session[-_]?key)/i
const CREDENTIAL_TEXT = /(?:\bAuthorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._~+\/-]{8,}|\bBearer\s+[A-Za-z0-9._~+\/-]{8,}|\bsk-[A-Za-z0-9_-]{8,}|\b(?:api[_-]?key|token|password|authorization)\s*[:=]\s*\S+)/gi
const DISPLAY_UNSAFE = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff<>&]/g

type ProfileRevisionState = {
  readonly revisions: Map<string, number>
  currentLiveRevision: string
  currentRendererRevision: number
  nextRendererRevision: number
}

class RendererRevisionRegistry {
  private readonly profiles = new Map<string, ProfileRevisionState>()

  public expectedLiveRevision(context: RequestContext): string | undefined {
    if (context.expectedRevision === undefined) return undefined
    const state = this.profiles.get(context.profileId)
    if (!state || state.currentRendererRevision !== context.expectedRevision) {
      throw bridgeError(
        'stale-response',
        'The renderer revision is stale or is not known to this bridge instance. No transport request was made.',
        context,
      )
    }
    return state.currentLiveRevision
  }

  public bind(context: RequestContext, liveRevision: string): number {
    assertBoundedText(liveRevision, 'Live revision', HERMES_PROFILE_RUNTIME_LIVE_BOUNDS.identifierCharacters, true)
    let state = this.profiles.get(context.profileId)
    if (!state) {
      if (this.profiles.size >= PROFILE_RUNTIME_LIMITS.profiles) {
        throw bridgeError('revision-capacity', 'The bounded per-profile revision registry is full.', context)
      }
      state = {
        revisions: new Map(),
        currentLiveRevision: liveRevision,
        currentRendererRevision: 0,
        nextRendererRevision: 1,
      }
      this.profiles.set(context.profileId, state)
    }

    const known = state.revisions.get(liveRevision)
    if (known !== undefined) {
      state.currentLiveRevision = liveRevision
      state.currentRendererRevision = known
      return known
    }
    if (state.revisions.size >= MAX_REVISIONS_PER_PROFILE || state.nextRendererRevision > MAX_RENDERER_REVISION) {
      throw bridgeError('revision-capacity', 'The bounded renderer revision history is full.', context)
    }

    const rendererRevision = state.nextRendererRevision
    state.nextRendererRevision += 1
    state.revisions.set(liveRevision, rendererRevision)
    state.currentLiveRevision = liveRevision
    state.currentRendererRevision = rendererRevision
    return rendererRevision
  }

  public currentRendererRevision(profileId: string): number {
    return this.profiles.get(profileId)?.currentRendererRevision ?? 0
  }
}

export type ProductionHermesProfileRuntimeBridgeOptions = {
  readonly fetch: SameOriginProfileRuntimeFetch
  readonly maxConcurrency?: number
  readonly maxPendingLoads?: number
}

export function createProductionHermesProfileRuntimeBridge(
  options: ProductionHermesProfileRuntimeBridgeOptions,
): ReadOnlyHermesProfileRuntimeBridge {
  const liveAdapter = new LiveHermesProfileRuntimeAdapter({
    transport: createSameOriginProfileRuntimeTransport(options.fetch),
    ...(options.maxConcurrency === undefined ? {} : { maxConcurrency: options.maxConcurrency }),
    ...(options.maxPendingLoads === undefined ? {} : { maxPendingLoads: options.maxPendingLoads }),
  })
  return new ReadOnlyHermesProfileRuntimeBridge(liveAdapter)
}

export class ReadOnlyHermesProfileRuntimeBridge implements HermesProfileRuntimeAdapter {
  private readonly revisions = new RendererRevisionRegistry()
  private readonly loadGenerations = new Map<string, number>()

  public constructor(private readonly liveAdapter: HermesProfileRuntimeLiveAdapterContract) {
    if (!liveAdapter || typeof liveAdapter.load !== 'function') {
      throw new Error('A Hermes profile-runtime live adapter is required.')
    }
  }

  public async load(
    context: RequestContext,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    validateRequest(context)
    if (signal.aborted) throw abortError()
    if (!this.loadGenerations.has(context.profileId) && this.loadGenerations.size >= PROFILE_RUNTIME_LIMITS.profiles) {
      throw bridgeError('revision-capacity', 'The bounded per-profile load registry is full.', context)
    }
    const generation = (this.loadGenerations.get(context.profileId) ?? 0) + 1
    this.loadGenerations.set(context.profileId, generation)

    const expectedLiveRevision = this.revisions.expectedLiveRevision(context)
    const liveRequest: LiveRequestIdentity = {
      adapterVersion: HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION,
      profileId: context.profileId,
      correlationId: context.correlationId,
      ...(expectedLiveRevision === undefined ? {} : { expectedRevision: expectedLiveRevision }),
    }
    let result: LiveAdapterResult<ProfileRuntimeLiveSnapshot>
    try {
      result = await this.liveAdapter.load(liveRequest, signal)
    } catch {
      if (signal.aborted) throw abortError()
      if (this.loadGenerations.get(context.profileId) !== generation) {
        throw bridgeError('stale-response', 'A newer profile-runtime read completed first; this older result was discarded.', context)
      }
      throw bridgeError('live-read-error', 'The live profile-runtime read failed.', context)
    }
    if (signal.aborted || result.status === 'cancelled') throw abortError()
    if (this.loadGenerations.get(context.profileId) !== generation) {
      throw bridgeError('stale-response', 'A newer profile-runtime read completed first; this older result was discarded.', context)
    }
    validateResultEnvelope(result, liveRequest)

    if (result.status === 'ready' || result.status === 'partial') {
      if (result.value === null || result.revision === null) {
        throw bridgeError('malformed-live-result', 'A successful live result omitted its snapshot or revision.', context)
      }
      const snapshot = mapLiveSnapshot(context, result.value, result.status, result.notices)
      const revision = this.revisions.bind(context, result.revision)
      const value: ProfileRuntimeSnapshot = { ...snapshot, revision }
      return { ...context, revision, value }
    }

    if (result.error === undefined) {
      throw bridgeError('malformed-live-result', 'A failed live result omitted its error.', context)
    }
    const fatalCode = fatalBridgeCode(result.error.code)
    if (fatalCode) throw bridgeError(fatalCode, boundedErrorMessage(result.error.message, context), context)
    if (result.value !== null || result.revision !== null) {
      throw bridgeError('malformed-live-result', 'A failed live result contained an unexpected snapshot or revision.', context)
    }

    const availability = result.status === 'unavailable' || result.error.code === 'unavailable'
      ? 'unavailable'
      : 'error'
    const revision = this.revisions.currentRendererRevision(context.profileId)
    const value = unavailableSnapshot(
      context,
      revision,
      availability,
      [result.error.message, ...result.notices],
    )
    return { ...context, revision, value }
  }

  public selectActiveProfile(
    _request: SelectActiveProfileRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileSelectionResult>> {
    return rejectReadOnly('select-active-profile')
  }

  public previewProfileMutation(
    _request: PreviewProfileMutationRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<OperationPreview>> {
    return rejectReadOnly('preview-profile-mutation')
  }

  public applyProfileMutation(
    _request: ApplyProfileMutationRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileMutationResult>> {
    return rejectReadOnly('apply-profile-mutation')
  }

  public saveDocument(
    _request: SaveDocumentRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('save-document')
  }

  public saveIntent(
    _request: SaveIntentRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('save-intent')
  }

  public selectTerminalBackend(
    _request: SelectTerminalBackendRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('select-terminal-backend')
  }

  public previewPermissionGrant(
    _request: PreviewPermissionGrantRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<OperationPreview>> {
    return rejectReadOnly('preview-permission-grant')
  }

  public confirmPermissionGrant(
    _request: ConfirmPermissionGrantRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('confirm-permission-grant')
  }

  public saveConfiguration(
    _request: SaveConfigurationRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('save-configuration')
  }

  public previewImport(
    _request: PreviewImportRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<OperationPreview>> {
    return rejectReadOnly('preview-import')
  }

  public applyImport(
    _request: ApplyImportRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectReadOnly('apply-import')
  }

  public exportProfile(
    _request: ExportProfileRequest,
    _signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileExport>> {
    return rejectReadOnly('export-profile')
  }
}

function mapLiveSnapshot(
  context: RequestContext,
  live: ProfileRuntimeLiveSnapshot,
  liveStatus: 'ready' | 'partial',
  notices: readonly string[],
): Omit<ProfileRuntimeSnapshot, 'revision'> {
  validateLiveSnapshot(live, context, liveStatus)

  const profileItems = live.profiles.value as readonly ProfileInventoryItem[]
  const active = live.activeProfile.value
  const stickyProfileId = active?.stickyProfileId ?? null
  const currentProcessProfileId = active?.currentProcessProfileId ?? null
  const profiles = profileItems.map((profile) => ({
    id: profile.profileId,
    name: profile.profileId,
    description: profile.description ?? '',
    isActive: stickyProfileId === profile.profileId,
    // The live contract exposes no delete-protection fact. Read-only mode fails safe.
    isDeleteProtected: true,
  }))

  const documents = live.contextDocuments.value
    ?.filter((document) => document.kind === 'soul')
    .map((document) => ({
      kind: 'soul' as const,
      text: document.exists ? document.content : '',
      maximumCharacters: PROFILE_RUNTIME_LIMITS.documentCharacters,
    })) ?? []

  const configurationSchema = mapConfigurationSchema(live.configurationMetadata)
  const providers = mapProviderStatuses(live.providerStatuses)
  const accountDetail = accountDisposition(live.account)
  const activeDetail = stickyProfileId && currentProcessProfileId
    ? stickyProfileId === currentProcessProfileId
      ? `Sticky and current-process profile: ${stickyProfileId}.`
      : `Sticky profile: ${stickyProfileId}; current-process profile: ${currentProcessProfileId}.`
    : 'Sticky/current-process profile distinction is unavailable.'

  return {
    contract: HERMES_PROFILE_RUNTIME_CONTRACT,
    profileId: context.profileId,
    availability: liveStatus,
    detail: joinDetail([
      'Live read-only beta.',
      activeDetail,
      accountDetail,
      'Persona, context, intents, terminal backends, computer-use permissions, configuration values, credential slots, endpoint origins, and mutations are unavailable or delegated.',
      ...notices.map(displaySafeText),
    ]),
    profiles,
    documents,
    intent: { modelId: null, projectPath: null, worktreePath: null, note: '' },
    terminalBackends: [],
    computerUse: {
      availability: 'unavailable',
      detail: 'No source-confirmed profile-scoped computer-use permission read is available.',
      permissions: [],
    },
    providers,
    configurationSchema,
    configuration: {},
  }
}

function mapConfigurationSchema(
  section: LiveSection<import('../HermesProfileRuntimeLive').ConfigurationMetadata>,
): ConfigurationFieldSchema[] {
  if (section.value === null) return []
  const seen = new Set<string>()
  const mapped: ConfigurationFieldSchema[] = []
  for (const field of section.value.fields) {
    validateConfigurationField(field)
    if (seen.has(field.key)) throw new Error('Live configuration metadata contains a duplicate key.')
    seen.add(field.key)
    if (field.sensitive || field.kind === 'secret' || field.kind === 'unknown' || field.kind === 'number') continue
    if (SECRET_CONFIGURATION_KEY.test(field.key)) continue

    const kind: ConfigurationFieldSchema['kind'] = field.kind === 'boolean'
      ? 'boolean'
      : field.kind === 'select'
        ? 'enum'
        : 'string'
    mapped.push({
      key: field.key,
      label: field.label,
      description: field.description ?? 'Read-only live schema metadata; no value is exposed.',
      kind,
      required: false,
      ...(kind === 'string' ? { maximumLength: PROFILE_RUNTIME_LIMITS.fieldText } : {}),
      ...(kind === 'enum' ? { options: field.options.map((option) => option.value) } : {}),
      disposition: 'delegated',
    })
  }
  return mapped
}

function mapProviderStatuses(
  section: LiveSection<readonly ProviderStatus[]>,
): ProfileRuntimeSnapshot['providers'] {
  if (section.value === null) return []
  const seen = new Set<string>()
  return section.value.map((provider) => {
    validateProvider(provider)
    if (seen.has(provider.providerId)) throw new Error('Live provider status contains a duplicate provider identifier.')
    seen.add(provider.providerId)
    return {
      id: provider.providerId,
      label: provider.label,
      oauth: provider.connected ? 'connected' as const : 'disconnected' as const,
      credentialPool: 'delegated' as const,
      configuredCredentialSlots: 0,
      customEndpoint: { state: 'delegated' as const, origin: null },
      disposition: 'delegated' as const,
      detail: 'OAuth disposition is live. Credential-slot count and custom endpoint origin are not exposed; zero is a non-factual renderer sentinel while the field is delegated.',
    }
  })
}

function accountDisposition(section: LiveSection<import('../HermesProfileRuntimeLive').AccountStatus>): string {
  if (section.value === null) return 'Account disposition is unavailable.'
  return section.value.signedIn
    ? 'Account disposition: signed in; identity and private account fields are omitted.'
    : 'Account disposition: signed out.'
}

function unavailableSnapshot(
  context: RequestContext,
  revision: number,
  availability: 'error' | 'unavailable',
  details: readonly string[],
): ProfileRuntimeSnapshot {
  return {
    contract: HERMES_PROFILE_RUNTIME_CONTRACT,
    profileId: context.profileId,
    revision,
    availability,
    detail: joinDetail(['Live read-only beta.', ...details.map(displaySafeText)]),
    profiles: [],
    documents: [],
    intent: { modelId: null, projectPath: null, worktreePath: null, note: '' },
    terminalBackends: [],
    computerUse: {
      availability: 'unavailable',
      detail: 'No live profile runtime snapshot is available.',
      permissions: [],
    },
    providers: [],
    configurationSchema: [],
    configuration: {},
  }
}

function validateRequest(context: RequestContext): void {
  if (!context || context.contract !== HERMES_PROFILE_RUNTIME_CONTRACT) {
    throw bridgeError('invalid-request', 'The renderer contract version is incompatible.', context)
  }
  if (!PROFILE_ID.test(context.profileId) || !CORRELATION_ID.test(context.correlationId)) {
    throw bridgeError('invalid-request', 'Profile or correlation identity is malformed.', context)
  }
  if (
    context.expectedRevision !== undefined
    && (!Number.isSafeInteger(context.expectedRevision) || context.expectedRevision < 0)
  ) {
    throw bridgeError('invalid-request', 'Expected renderer revision is malformed.', context)
  }
}

function validateResultEnvelope(
  result: LiveAdapterResult<ProfileRuntimeLiveSnapshot>,
  request: LiveRequestIdentity,
): void {
  if (!result || typeof result !== 'object') throw bridgeError('malformed-live-result', 'Live adapter returned no result.', request)
  if (result.adapterVersion !== HERMES_PROFILE_RUNTIME_LIVE_ADAPTER_VERSION) {
    throw bridgeError('malformed-live-result', 'Live adapter returned an incompatible version.', request)
  }
  if (result.profileId !== request.profileId) {
    throw bridgeError('cross-profile-response', 'Live adapter returned a cross-profile result.', request)
  }
  if (result.correlationId !== request.correlationId) {
    throw bridgeError('cross-correlation-response', 'Live adapter returned a cross-correlation result.', request)
  }
  if (!['ready', 'partial', 'unavailable', 'error', 'cancelled'].includes(result.status)) {
    throw bridgeError('malformed-live-result', 'Live adapter returned an unknown status.', request)
  }
  if (!Array.isArray(result.notices) || result.notices.length > HERMES_PROFILE_RUNTIME_LIVE_BOUNDS.notices) {
    throw bridgeError('malformed-live-result', 'Live adapter returned malformed notices.', request)
  }
  for (const notice of result.notices) assertBoundedText(notice, 'Live notice', MAX_NOTICE_CHARACTERS, false)
}

function validateLiveSnapshot(
  live: ProfileRuntimeLiveSnapshot,
  context: RequestContext,
  liveStatus: 'ready' | 'partial',
): void {
  if (!live || typeof live !== 'object') throw bridgeError('malformed-live-result', 'Live snapshot is malformed.', context)
  const sections = [
    live.profiles,
    live.activeProfile,
    live.contextDocuments,
    live.configurationMetadata,
    live.providerStatuses,
    live.authProviders,
    live.account,
  ] as const
  for (const section of sections) validateSection(section, context)
  if (liveStatus === 'ready' && sections.some((section) => section.availability !== 'available')) {
    throw bridgeError('malformed-live-result', 'A ready live result contains a non-available section.', context)
  }
  if (live.profiles.availability !== 'available' || !Array.isArray(live.profiles.value)) {
    throw bridgeError('malformed-live-result', 'Live profile inventory is unavailable or malformed.', context)
  }
  if (live.profiles.value.length > PROFILE_RUNTIME_LIMITS.profiles) {
    throw bridgeError('oversized-response', 'Live profile inventory exceeds the renderer bound.', context)
  }
  const ids = new Set<string>()
  for (const profile of live.profiles.value) {
    validateProfile(profile, context)
    if (ids.has(profile.profileId)) throw bridgeError('malformed-live-result', 'Live profile inventory contains a duplicate identifier.', context)
    ids.add(profile.profileId)
  }
  if (!ids.has(context.profileId)) {
    throw bridgeError('cross-profile-response', 'Requested profile is absent from the live inventory.', context)
  }
  if (live.activeProfile.value !== null) {
    const { stickyProfileId, currentProcessProfileId } = live.activeProfile.value
    if (!PROFILE_ID.test(stickyProfileId) || !PROFILE_ID.test(currentProcessProfileId)) {
      throw bridgeError('malformed-live-result', 'Live active-profile identity is malformed.', context)
    }
    if (!ids.has(stickyProfileId) || !ids.has(currentProcessProfileId)) {
      throw bridgeError('cross-profile-response', 'Live active-profile state refers outside the returned inventory.', context)
    }
  }
  if (live.contextDocuments.value !== null) {
    if (!Array.isArray(live.contextDocuments.value) || live.contextDocuments.value.length > PROFILE_RUNTIME_LIMITS.documents) {
      throw bridgeError('malformed-live-result', 'Live context documents are malformed.', context)
    }
    const soulCount = live.contextDocuments.value.filter((document) => document.kind === 'soul').length
    if (soulCount !== live.contextDocuments.value.length || soulCount > 1) {
      throw bridgeError('malformed-live-result', 'Live context documents contain unsupported or duplicate kinds.', context)
    }
    for (const document of live.contextDocuments.value) {
      if (typeof document.exists !== 'boolean' || typeof document.content !== 'string') {
        throw bridgeError('malformed-live-result', 'Live SOUL document is malformed.', context)
      }
      if (document.content.length > PROFILE_RUNTIME_LIMITS.documentCharacters) {
        throw bridgeError('oversized-response', 'Live SOUL document exceeds the renderer character bound.', context)
      }
    }
  }
  if (live.configurationMetadata.value !== null) {
    if (!Array.isArray(live.configurationMetadata.value.fields) || live.configurationMetadata.value.fields.length > PROFILE_RUNTIME_LIMITS.fields) {
      throw bridgeError('malformed-live-result', 'Live configuration metadata is malformed.', context)
    }
  }
  if (live.providerStatuses.value !== null) {
    if (!Array.isArray(live.providerStatuses.value) || live.providerStatuses.value.length > PROFILE_RUNTIME_LIMITS.providers) {
      throw bridgeError('malformed-live-result', 'Live provider status is malformed.', context)
    }
  }
}

function validateSection(section: LiveSection<unknown>, context: RequestContext): void {
  if (!section || typeof section !== 'object' || !['available', 'partial', 'unavailable', 'error'].includes(section.availability)) {
    throw bridgeError('malformed-live-result', 'Live snapshot contains a malformed section.', context)
  }
  if (section.availability === 'available' && section.value === null) {
    throw bridgeError('malformed-live-result', 'An available live section omitted its value.', context)
  }
  if (section.message !== undefined) assertBoundedText(section.message, 'Live section message', MAX_NOTICE_CHARACTERS, false)
}

function validateProfile(profile: ProfileInventoryItem, context: RequestContext): void {
  if (!profile || typeof profile !== 'object' || !PROFILE_ID.test(profile.profileId)) {
    throw bridgeError('malformed-live-result', 'Live profile inventory contains a malformed identifier.', context)
  }
  if (profile.description !== null) assertBoundedText(profile.description, 'Profile description', 2_048, false)
}

function validateConfigurationField(field: ConfigurationFieldMetadata): void {
  if (!field || typeof field !== 'object') throw new Error('Live configuration field is malformed.')
  assertBoundedText(field.key, 'Configuration key', 128, true)
  assertBoundedText(field.label, 'Configuration label', 256, true)
  if (field.description !== null) assertBoundedText(field.description, 'Configuration description', 2_048, false)
  if (!['boolean', 'number', 'select', 'secret', 'text', 'unknown'].includes(field.kind)) {
    throw new Error('Live configuration field kind is malformed.')
  }
  if (!Array.isArray(field.options) || field.options.length > HERMES_PROFILE_RUNTIME_LIVE_BOUNDS.configurationOptions) {
    throw new Error('Live configuration options are malformed.')
  }
  for (const option of field.options) {
    assertBoundedText(option.value, 'Configuration option', 256, true)
    assertBoundedText(option.label, 'Configuration option label', 256, true)
  }
}

function validateProvider(provider: ProviderStatus): void {
  if (!provider || typeof provider !== 'object') throw new Error('Live provider status is malformed.')
  assertBoundedText(provider.providerId, 'Provider identity', 128, true)
  assertBoundedText(provider.label, 'Provider label', 256, true)
  if (typeof provider.connected !== 'boolean') throw new Error('Live provider connection status is malformed.')
}

function fatalBridgeCode(code: LiveErrorCode): HermesProfileRuntimeBridgeErrorCode | null {
  switch (code) {
    case 'cancelled': return 'cancelled'
    case 'duplicate-correlation': return 'duplicate-correlation'
    case 'invalid-request': return 'invalid-request'
    case 'malformed-response': return 'malformed-live-result'
    case 'oversized-response': return 'oversized-response'
    case 'cross-profile-response': return 'cross-profile-response'
    case 'cross-correlation-response': return 'cross-correlation-response'
    case 'stale-response': return 'stale-response'
    case 'busy': return 'busy'
    default: return null
  }
}

function rejectReadOnly<T>(operation: HermesProfileRuntimeReadOnlyOperation): Promise<T> {
  return Promise.reject(new HermesProfileRuntimeReadOnlyError(operation))
}

function bridgeError(
  code: HermesProfileRuntimeBridgeErrorCode,
  message: string,
  identity: Pick<RequestContext, 'profileId' | 'correlationId'> | undefined,
): HermesProfileRuntimeBridgeError {
  return new HermesProfileRuntimeBridgeError(
    code,
    message,
    identity?.profileId ?? '(invalid)',
    identity?.correlationId ?? '(invalid)',
  )
}

function abortError(): Error {
  const error = new Error('The profile runtime read was cancelled.')
  error.name = 'AbortError'
  return error
}

function boundedErrorMessage(value: string, context: RequestContext): string {
  assertBoundedText(value, 'Live error', MAX_NOTICE_CHARACTERS, true)
  const safe = displaySafeText(value)
  if (!safe) throw bridgeError('malformed-live-result', 'A live error contained no display-safe text.', context)
  return safe
}

function displaySafeText(value: string): string {
  return value
    .replace(CREDENTIAL_TEXT, '[redacted]')
    .replace(DISPLAY_UNSAFE, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

function assertBoundedText(value: unknown, label: string, maximum: number, required: boolean): asserts value is string {
  if (typeof value !== 'string' || (required && value.length === 0) || value.length > maximum) {
    throw new Error(`${label} is malformed or exceeds its bound.`)
  }
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(value)) {
    throw new Error(`${label} contains forbidden control characters.`)
  }
}

function joinDetail(parts: readonly string[]): string {
  const unique: string[] = []
  for (const part of parts) {
    assertBoundedText(part, 'Profile runtime detail', MAX_NOTICE_CHARACTERS, false)
    const normalized = part.trim()
    if (!normalized || unique.includes(normalized)) continue
    const candidate = [...unique, normalized].join(' ')
    if (candidate.length > MAX_DETAIL_CHARACTERS) break
    unique.push(normalized)
  }
  return unique.join(' ')
}
