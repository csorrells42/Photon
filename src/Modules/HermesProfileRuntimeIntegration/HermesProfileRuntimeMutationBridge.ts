import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type AdapterResponse,
  type ApplyImportRequest,
  type ApplyProfileMutationRequest,
  type ConfirmPermissionGrantRequest,
  type ExportProfileRequest,
  type HermesProfileRuntimeAdapter,
  type OperationPreview,
  type PreviewImportRequest,
  type PreviewPermissionGrantRequest,
  type PreviewProfileMutationRequest,
  type ProfileExport,
  type ProfileMutation,
  type ProfileMutationResult,
  type ProfileRuntimeSnapshot,
  type ProfileSelectionResult,
  type RequestContext,
  type SaveConfigurationRequest,
  type SaveDocumentRequest,
  type SaveIntentRequest,
  type SelectActiveProfileRequest,
  type SelectTerminalBackendRequest,
} from '../HermesProfileRuntime/contracts'
import {
  PROFILE_RUNTIME_LIMITS,
  assertNoSecretRoundTrip,
  assertSafeProfileId,
} from '../HermesProfileRuntime/runtimeSafety'

const MAX_RESPONSE_BYTES = 512 * 1024
const MAX_CORRELATIONS = 2_048
const MAX_PREVIEWS = 128
const MAX_PREVIEW_TTL_MS = 15 * 60 * 1_000
const CORRELATION_ID = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const SAFE_IDENTIFIER = /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/
const CONTROL_CHARACTERS = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/
const NATIVE_PATH = /(?:[A-Za-z]:[\\/]|\\\\[^\\]|\/(?:Users|home|opt|var|workspace)(?:\/|$))/i

export const HERMES_PROFILE_RUNTIME_MUTATION_ROUTES = Object.freeze({
  snapshot: '/api/workbench/profile-runtime/v1/snapshot',
  activeProfile: '/api/workbench/profile-runtime/v1/active-profile',
  mutationPreview: '/api/workbench/profile-runtime/v1/profile-mutation/preview',
  mutationCommit: '/api/workbench/profile-runtime/v1/profile-mutation/commit',
  soul: '/api/workbench/profile-runtime/v1/documents/soul',
  model: '/api/workbench/profile-runtime/v1/intent/model',
  terminal: '/api/workbench/profile-runtime/v1/terminal',
} as const)

export type HermesProfileRuntimeMutationFetch = (
  input: string,
  init: RequestInit & { readonly signal: AbortSignal },
) => Promise<Response>

export type HermesProfileRuntimeMutationBridgeErrorCode =
  | 'aborted'
  | 'correlation-replay'
  | 'error-envelope'
  | 'malformed-response'
  | 'preview-expired'
  | 'preview-mismatch'
  | 'preview-replay'
  | 'response-too-large'
  | 'stale-response'
  | 'transport-error'
  | 'unsupported-operation'

export class HermesProfileRuntimeMutationBridgeError extends Error {
  public constructor(
    public readonly code: HermesProfileRuntimeMutationBridgeErrorCode,
    message: string,
    public readonly profileId?: string,
    public readonly correlationId?: string,
  ) {
    super(message)
    this.name = 'HermesProfileRuntimeMutationBridgeError'
  }
}

export type HermesProfileRuntimeMutationBridgeOptions = {
  readonly fetch: HermesProfileRuntimeMutationFetch
  readonly now?: () => number
  readonly maximumResponseBytes?: number
}

type PreviewBinding = {
  readonly profileId: string
  readonly expectedRevision: number
  readonly mutation: string
  readonly expiresAt: number
  consumed: boolean
}

type JsonRecord = Record<string, unknown>

export class HermesProfileRuntimeMutationBridge implements HermesProfileRuntimeAdapter {
  private readonly fetchTransport: HermesProfileRuntimeMutationFetch
  private readonly now: () => number
  private readonly maximumResponseBytes: number
  private readonly correlations = new Set<string>()
  private readonly previews = new Map<string, PreviewBinding>()
  private readonly loadGenerations = new Map<string, number>()

  public constructor(options: HermesProfileRuntimeMutationBridgeOptions) {
    if (!options || typeof options.fetch !== 'function') throw new Error('An injected same-origin fetch implementation is required.')
    this.fetchTransport = options.fetch
    this.now = options.now ?? Date.now
    this.maximumResponseBytes = options.maximumResponseBytes ?? MAX_RESPONSE_BYTES
    if (!Number.isSafeInteger(this.maximumResponseBytes) || this.maximumResponseBytes < 1 || this.maximumResponseBytes > MAX_RESPONSE_BYTES) {
      throw new Error(`maximumResponseBytes must be between 1 and ${MAX_RESPONSE_BYTES}.`)
    }
  }

  public async load(context: RequestContext, signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    validateContext(context, false)
    this.claimCorrelation(context)
    const generation = (this.loadGenerations.get(context.profileId) ?? 0) + 1
    this.loadGenerations.set(context.profileId, generation)
    const query = new URLSearchParams({
      contract: context.contract,
      profileId: context.profileId,
      correlationId: context.correlationId,
    })
    if (context.expectedRevision !== undefined) query.set('expectedRevision', String(context.expectedRevision))
    const result = await this.send(
      `${HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.snapshot}?${query.toString()}`,
      'GET',
      context,
      signal,
      undefined,
      normalizeSnapshot,
    )
    if (this.loadGenerations.get(context.profileId) !== generation) {
      throw bridgeError('stale-response', 'A newer profile-runtime snapshot superseded this result.', context)
    }
    return result
  }

  public async selectActiveProfile(
    request: SelectActiveProfileRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileSelectionResult>> {
    validateContext(request, true)
    assertSafeProfileId(request.targetProfileId, 'Target profile identity')
    return this.post(
      HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.activeProfile,
      request,
      signal,
      { targetProfileId: request.targetProfileId },
      (value, context) => {
        const result = normalizeSelection(value, context)
        if (result.activeProfileId !== request.targetProfileId) {
          throw bridgeError('malformed-response', 'The active-profile result does not match the selected target.', context)
        }
        return result
      },
    )
  }

  public async previewProfileMutation(
    request: PreviewProfileMutationRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<OperationPreview>> {
    validateContext(request, true)
    const mutation = normalizeMutation(request.mutation)
    return this.post(
      HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.mutationPreview,
      request,
      signal,
      { mutation },
      (value, context) => {
        const preview = normalizePreview(value, context, mutation.kind, this.now())
        if (this.previews.size >= MAX_PREVIEWS) throw bridgeError('malformed-response', 'The bounded preview registry is full.', context)
        this.previews.set(preview.previewId, {
          profileId: context.profileId,
          expectedRevision: requireExpectedRevision(context),
          mutation: mutationFingerprint(mutation),
          expiresAt: Date.parse(preview.expiresAt),
          consumed: false,
        })
        return preview
      },
    )
  }

  public async applyProfileMutation(
    request: ApplyProfileMutationRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileMutationResult>> {
    validateContext(request, true)
    const mutation = normalizeMutation(request.mutation)
    const previewId = safeIdentifier(request.previewId, 'Preview identity', 256)
    const binding = this.previews.get(previewId)
    if (!binding) throw bridgeError('preview-mismatch', 'The preview is not known to this bridge instance.', request)
    if (binding.consumed) throw bridgeError('preview-replay', 'The preview was already consumed.', request)
    if (binding.expiresAt <= this.now()) {
      binding.consumed = true
      throw bridgeError('preview-expired', 'The preview has expired.', request)
    }
    if (
      binding.profileId !== request.profileId
      || binding.expectedRevision !== request.expectedRevision
      || binding.mutation !== mutationFingerprint(mutation)
    ) {
      throw bridgeError('preview-mismatch', 'The preview does not bind this exact profile, revision, and mutation.', request)
    }
    const confirmation = request.destructiveConfirmation?.phrase
    if (confirmation !== undefined) safeText(confirmation, 'Confirmation phrase', 256, false)
    binding.consumed = true
    return this.post(
      HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.mutationCommit,
      request,
      signal,
      {
        mutation,
        previewId,
        ...(confirmation === undefined ? {} : { destructiveConfirmation: { phrase: confirmation } }),
      },
      normalizeMutationResult,
    )
  }

  public async saveDocument(
    request: SaveDocumentRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    validateContext(request, true)
    if (request.document !== 'soul') return rejectUnsupported(request, 'Only an atomic SOUL document save is supported.')
    const text = safeText(request.text, 'SOUL document', PROFILE_RUNTIME_LIMITS.documentCharacters, false)
    return this.post(HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.soul, request, signal, { document: 'soul', text }, normalizeSnapshot)
  }

  public async saveIntent(
    request: SaveIntentRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    validateContext(request, true)
    if (request.intent.projectPath !== null || request.intent.worktreePath !== null || request.intent.note !== '') {
      return rejectUnsupported(request, 'Only a model assignment can be changed; project paths, worktrees, and notes are unavailable.')
    }
    if (request.intent.modelId === null) return rejectUnsupported(request, 'Clearing a model assignment is unavailable.')
    const modelId = safeIdentifier(request.intent.modelId, 'Model assignment', 256)
    const separator = modelId.indexOf('::')
    if (separator < 1 || separator === modelId.length - 2 || modelId.indexOf('::', separator + 2) !== -1) {
      throw bridgeError('unsupported-operation', 'Model assignments must use the provider::model identity format.', request)
    }
    const provider = safeIdentifier(modelId.slice(0, separator), 'Provider identity', 128)
    const model = safeIdentifier(modelId.slice(separator + 2), 'Model identity', 256)
    return this.post(HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.model, request, signal, { provider, model }, normalizeSnapshot)
  }

  public async selectTerminalBackend(
    request: SelectTerminalBackendRequest,
    signal: AbortSignal,
  ): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    validateContext(request, true)
    const backendId = safeIdentifier(request.backendId, 'Terminal backend identity', 128)
    return this.post(HERMES_PROFILE_RUNTIME_MUTATION_ROUTES.terminal, request, signal, { backendId }, normalizeSnapshot)
  }

  public async previewPermissionGrant(request: PreviewPermissionGrantRequest, _signal: AbortSignal): Promise<AdapterResponse<OperationPreview>> {
    return rejectUnsupported(request, 'Permission grants are unavailable until a dedicated host authority exists.')
  }

  public async confirmPermissionGrant(request: ConfirmPermissionGrantRequest, _signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectUnsupported(request, 'Permission grants are unavailable until a dedicated host authority exists.')
  }

  public async saveConfiguration(request: SaveConfigurationRequest, _signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectUnsupported(request, 'Configuration writes are unavailable until a dedicated backend authority exists.')
  }

  public async previewImport(request: PreviewImportRequest, _signal: AbortSignal): Promise<AdapterResponse<OperationPreview>> {
    return rejectUnsupported(request, 'Profile import is unavailable until a dedicated backend authority exists.')
  }

  public async applyImport(request: ApplyImportRequest, _signal: AbortSignal): Promise<AdapterResponse<ProfileRuntimeSnapshot>> {
    return rejectUnsupported(request, 'Profile import is unavailable until a dedicated backend authority exists.')
  }

  public async exportProfile(request: ExportProfileRequest, _signal: AbortSignal): Promise<AdapterResponse<ProfileExport>> {
    return rejectUnsupported(request, 'Profile export is unavailable until a dedicated backend authority exists.')
  }

  private post<T>(
    route: string,
    context: RequestContext,
    signal: AbortSignal,
    payload: JsonRecord,
    normalizeValue: (value: unknown, context: RequestContext) => T,
  ): Promise<AdapterResponse<T>> {
    this.claimCorrelation(context)
    const body = {
      contract: context.contract,
      profileId: context.profileId,
      correlationId: context.correlationId,
      expectedRevision: requireExpectedRevision(context),
      ...payload,
    }
    return this.send(route, 'POST', context, signal, body, normalizeValue)
  }

  private claimCorrelation(context: RequestContext): void {
    if (this.correlations.has(context.correlationId)) {
      throw bridgeError('correlation-replay', 'The correlation identity was already used by this bridge instance.', context)
    }
    if (this.correlations.size >= MAX_CORRELATIONS) {
      throw bridgeError('correlation-replay', 'The bounded correlation registry is full.', context)
    }
    this.correlations.add(context.correlationId)
  }

  private async send<T>(
    route: string,
    method: 'GET' | 'POST',
    context: RequestContext,
    signal: AbortSignal,
    body: JsonRecord | undefined,
    normalizeValue: (value: unknown, context: RequestContext) => T,
  ): Promise<AdapterResponse<T>> {
    if (signal.aborted) throw abortError(context)
    let response: Response
    try {
      response = await this.fetchTransport(route, {
        method,
        credentials: 'include',
        signal,
        headers: method === 'POST'
          ? { Accept: 'application/json', 'Content-Type': 'application/json' }
          : { Accept: 'application/json' },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      })
    } catch {
      if (signal.aborted) throw abortError(context)
      throw bridgeError('transport-error', 'The profile-runtime request failed.', context)
    }
    if (signal.aborted) throw abortError(context)
    const raw = await readBoundedJson(response, this.maximumResponseBytes, context)
    if (!response.ok) throw normalizeErrorEnvelope(raw, context)
    const envelope = strictRecord(raw, 'Response envelope', ['contract', 'profileId', 'correlationId', 'expectedRevision', 'revision', 'value'])
    assertEnvelopeIdentity(envelope, context)
    const revision = safeRevision(envelope.revision, 'Response revision')
    let value: T
    try {
      value = normalizeValue(envelope.value, context)
    } catch (error) {
      if (error instanceof HermesProfileRuntimeMutationBridgeError) throw error
      throw bridgeError('malformed-response', 'The profile-runtime response payload failed renderer-safe validation.', context)
    }
    if (isRecord(value) && 'revision' in value && value.revision !== revision) {
      throw bridgeError('malformed-response', 'The snapshot revision does not match its response envelope.', context)
    }
    return { ...context, revision, value }
  }
}

function validateContext(context: RequestContext, requireRevision: boolean): void {
  if (!context || context.contract !== HERMES_PROFILE_RUNTIME_CONTRACT) throw new Error('The request contract is missing or incompatible.')
  assertSafeProfileId(context.profileId)
  if (!CORRELATION_ID.test(context.correlationId)) throw new Error('Correlation identity is malformed.')
  if (context.expectedRevision !== undefined) safeRevision(context.expectedRevision, 'Expected revision')
  if (requireRevision) requireExpectedRevision(context)
}

function requireExpectedRevision(context: RequestContext): number {
  if (context.expectedRevision === undefined) throw bridgeError('stale-response', 'A revision-bound write requires expectedRevision.', context)
  return safeRevision(context.expectedRevision, 'Expected revision')
}

function normalizeMutation(value: ProfileMutation): ProfileMutation {
  if (!isRecord(value)) throw new Error('Profile mutation must be an object.')
  if (value.kind === 'create') return { kind: 'create', name: safeText(value.name, 'Profile name', 128) }
  if (value.kind === 'clone') {
    assertSafeProfileId(value.sourceProfileId, 'Source profile identity')
    return { kind: 'clone', sourceProfileId: value.sourceProfileId, name: safeText(value.name, 'Profile name', 128) }
  }
  if (value.kind === 'rename') return { kind: 'rename', name: safeText(value.name, 'Profile name', 128) }
  if (value.kind === 'delete') return { kind: 'delete' }
  throw new Error('Profile mutation kind is unsupported.')
}

function mutationFingerprint(mutation: ProfileMutation): string {
  return JSON.stringify(mutation)
}

function normalizePreview(value: unknown, context: RequestContext, operation: ProfileMutation['kind'], now: number): OperationPreview {
  const source = strictRecord(value, 'Operation preview', [
    'previewId', 'operation', 'profileId', 'title', 'summary', 'consequences', 'destructive', 'confirmationPhrase', 'expiresAt',
  ])
  if (source.operation !== operation) throw bridgeError('malformed-response', 'The preview operation does not match the requested mutation.', context)
  if (source.profileId !== context.profileId) throw bridgeError('malformed-response', 'The preview is bound to a different profile.', context)
  if (typeof source.destructive !== 'boolean') throw bridgeError('malformed-response', 'The preview destructive flag is malformed.', context)
  const expiresAt = safeText(source.expiresAt, 'Preview expiry', 64)
  const expiry = Date.parse(expiresAt)
  if (!Number.isFinite(expiry) || expiry <= now) throw bridgeError('preview-expired', 'The preview is already expired.', context)
  if (expiry - now > MAX_PREVIEW_TTL_MS) throw bridgeError('malformed-response', 'The preview expiry exceeds the bounded lifetime.', context)
  const consequences = safeTextArray(source.consequences, 'Preview consequence', 32, 1_024)
  const confirmationPhrase = source.confirmationPhrase === null
    ? null
    : safeText(source.confirmationPhrase, 'Confirmation phrase', 256, false)
  return {
    previewId: safeIdentifier(source.previewId, 'Preview identity', 256),
    operation,
    profileId: context.profileId,
    title: safeDisplayText(source.title, 'Preview title', 256),
    summary: safeDisplayText(source.summary, 'Preview summary', 2_048),
    consequences: consequences.map((item) => safeDisplayText(item, 'Preview consequence', 1_024)),
    destructive: source.destructive,
    confirmationPhrase,
    expiresAt,
  }
}

function normalizeSelection(value: unknown, context: RequestContext): ProfileSelectionResult {
  const source = strictRecord(value, 'Profile selection result', ['activeProfileId'])
  assertSafeProfileId(source.activeProfileId, 'Active profile identity')
  return { activeProfileId: source.activeProfileId }
}

function normalizeMutationResult(value: unknown): ProfileMutationResult {
  const source = strictRecord(value, 'Profile mutation result', ['affectedProfileId', 'activeProfileId'])
  assertSafeProfileId(source.affectedProfileId, 'Affected profile identity')
  assertSafeProfileId(source.activeProfileId, 'Active profile identity')
  return { affectedProfileId: source.affectedProfileId, activeProfileId: source.activeProfileId }
}

function normalizeSnapshot(value: unknown, context: RequestContext): ProfileRuntimeSnapshot {
  const source = strictRecord(value, 'Profile runtime snapshot', [
    'contract', 'profileId', 'revision', 'availability', 'detail', 'profiles', 'documents', 'intent', 'terminalBackends',
    'computerUse', 'providers', 'configurationSchema', 'configuration',
  ])
  if (source.contract !== HERMES_PROFILE_RUNTIME_CONTRACT || source.profileId !== context.profileId) {
    throw bridgeError('malformed-response', 'The snapshot identity is incompatible with the request.', context)
  }
  const revision = safeRevision(source.revision, 'Snapshot revision')
  const availability = safeEnum(source.availability, ['ready', 'partial', 'error', 'unavailable'] as const, 'Snapshot availability')
  const profiles = safeArray(source.profiles, 'Profiles', PROFILE_RUNTIME_LIMITS.profiles).map((item) => {
    const profile = strictRecord(item, 'Profile', ['id', 'name', 'description', 'isActive', 'isDeleteProtected'])
    assertSafeProfileId(profile.id)
    if (typeof profile.isActive !== 'boolean' || typeof profile.isDeleteProtected !== 'boolean') throw new Error('Profile flags are malformed.')
    return {
      id: profile.id,
      name: safeDisplayText(profile.name, 'Profile name', 128),
      description: safeDisplayText(profile.description, 'Profile description', 2_048, false),
      isActive: profile.isActive,
      isDeleteProtected: profile.isDeleteProtected,
    }
  })
  if (!profiles.some((profile) => profile.id === context.profileId)) throw new Error('The requested profile is missing from the snapshot.')
  const documents = safeArray(source.documents, 'Documents', 1).map((item) => {
    const document = strictRecord(item, 'Document', ['kind', 'text', 'maximumCharacters'])
    if (document.kind !== 'soul') throw new Error('Only the SOUL document is exposed by this bridge.')
    const maximumCharacters = safeRevision(document.maximumCharacters, 'Document character limit')
    if (maximumCharacters > PROFILE_RUNTIME_LIMITS.documentCharacters) throw new Error('Document character limit is too large.')
    return { kind: 'soul' as const, text: safeText(document.text, 'SOUL document', maximumCharacters, false), maximumCharacters }
  })
  const intentSource = strictRecord(source.intent, 'Intent', ['modelId', 'projectPath', 'worktreePath', 'note'])
  if (intentSource.projectPath !== null || intentSource.worktreePath !== null || intentSource.note !== '') {
    throw new Error('The renderer-safe intent may not expose paths or notes.')
  }
  const modelId = intentSource.modelId === null ? null : safeIdentifier(intentSource.modelId, 'Model assignment', 256)
  const terminals = safeArray(source.terminalBackends, 'Terminal backends', PROFILE_RUNTIME_LIMITS.terminalBackends).map((item) => {
    const terminal = strictRecord(item, 'Terminal backend', ['id', 'label', 'description', 'selected', 'availability', 'detail'])
    if (typeof terminal.selected !== 'boolean') throw new Error('Terminal selected state is malformed.')
    return {
      id: safeIdentifier(terminal.id, 'Terminal backend identity', 128),
      label: safeDisplayText(terminal.label, 'Terminal label', 128),
      description: safeDisplayText(terminal.description, 'Terminal description', 1_024, false),
      selected: terminal.selected,
      availability: safeEnum(terminal.availability, ['ready', 'partial', 'error', 'unavailable'] as const, 'Terminal availability'),
      detail: safeDisplayText(terminal.detail, 'Terminal detail', 2_048, false),
    }
  })
  const computerSource = strictRecord(source.computerUse, 'Computer use', ['availability', 'detail', 'permissions'])
  if (safeArray(computerSource.permissions, 'Computer-use permissions', 0).length !== 0) throw new Error('Permission projection is unavailable.')
  const computerUse = {
    availability: safeEnum(computerSource.availability, ['ready', 'partial', 'error', 'unavailable'] as const, 'Computer-use availability'),
    detail: safeDisplayText(computerSource.detail, 'Computer-use detail', 2_048, false),
    permissions: [],
  }
  const providers = safeArray(source.providers, 'Providers', PROFILE_RUNTIME_LIMITS.providers).map(normalizeProvider)
  assertNoSecretRoundTrip(providers)
  if (safeArray(source.configurationSchema, 'Configuration schema', 0).length !== 0) throw new Error('Configuration schema projection is unavailable.')
  strictRecord(source.configuration, 'Configuration', [])
  return {
    contract: HERMES_PROFILE_RUNTIME_CONTRACT,
    profileId: context.profileId,
    revision,
    availability,
    detail: safeDisplayText(source.detail, 'Snapshot detail', 4_096, false),
    profiles,
    documents,
    intent: { modelId, projectPath: null, worktreePath: null, note: '' },
    terminalBackends: terminals,
    computerUse,
    providers,
    configurationSchema: [],
    configuration: {},
  }
}

function normalizeProvider(value: unknown) {
  const source = strictRecord(value, 'Provider', [
    'id', 'label', 'oauth', 'credentialPool', 'configuredCredentialSlots', 'customEndpoint', 'disposition', 'detail',
  ])
  const endpoint = strictRecord(source.customEndpoint, 'Provider endpoint', ['state', 'origin'])
  const origin = endpoint.origin === null ? null : safeHttpOrigin(endpoint.origin)
  return {
    id: safeIdentifier(source.id, 'Provider identity', 128),
    label: safeDisplayText(source.label, 'Provider label', 128),
    oauth: safeEnum(source.oauth, ['connected', 'disconnected', 'expired', 'unsupported', 'unavailable'] as const, 'Provider OAuth status'),
    credentialPool: safeEnum(source.credentialPool, ['configured', 'empty', 'delegated', 'unsupported', 'unavailable'] as const, 'Provider credential status'),
    configuredCredentialSlots: boundedInteger(source.configuredCredentialSlots, 'Configured credential slots', 0, 1_000),
    customEndpoint: {
      state: safeEnum(endpoint.state, ['configured', 'not_configured', 'delegated', 'unsupported', 'unavailable'] as const, 'Provider endpoint status'),
      origin,
    },
    disposition: safeEnum(source.disposition, ['manageable', 'delegated', 'unsupported'] as const, 'Provider disposition'),
    detail: safeDisplayText(source.detail, 'Provider detail', 2_048, false),
  }
}

async function readBoundedJson(response: Response, maximumBytes: number, context: RequestContext): Promise<unknown> {
  const contentType = response.headers.get('content-type')?.split(';')[0]?.trim().toLowerCase()
  if (contentType !== 'application/json') throw bridgeError('malformed-response', 'The profile-runtime response is not JSON.', context)
  const declared = response.headers.get('content-length')
  if (declared !== null) {
    const declaredBytes = Number(declared)
    if (!Number.isSafeInteger(declaredBytes) || declaredBytes < 0) {
      throw bridgeError('malformed-response', 'The profile-runtime response content length is malformed.', context)
    }
    if (declaredBytes > maximumBytes) {
      throw bridgeError('response-too-large', 'The profile-runtime response exceeds the renderer limit.', context)
    }
  }
  const reader = response.body?.getReader()
  let text = ''
  let bytes = 0
  if (reader) {
    const decoder = new TextDecoder('utf-8', { fatal: true })
    try {
      while (true) {
        const next = await reader.read()
        if (next.done) break
        bytes += next.value.byteLength
        if (bytes > maximumBytes) {
          await reader.cancel()
          throw bridgeError('response-too-large', 'The profile-runtime response exceeds the renderer limit.', context)
        }
        text += decoder.decode(next.value, { stream: true })
      }
      text += decoder.decode()
    } catch (error) {
      if (error instanceof HermesProfileRuntimeMutationBridgeError) throw error
      throw bridgeError('malformed-response', 'The profile-runtime response body is malformed.', context)
    }
  } else {
    text = await response.text()
    if (new TextEncoder().encode(text).byteLength > maximumBytes) {
      throw bridgeError('response-too-large', 'The profile-runtime response exceeds the renderer limit.', context)
    }
  }
  try { return JSON.parse(text) }
  catch { throw bridgeError('malformed-response', 'The profile-runtime response contains malformed JSON.', context) }
}

function assertEnvelopeIdentity(source: JsonRecord, context: RequestContext): void {
  if (source.contract !== context.contract) throw bridgeError('malformed-response', 'The response contract does not match the request.', context)
  if (source.profileId !== context.profileId) throw bridgeError('malformed-response', 'The response is bound to a different profile.', context)
  if (source.correlationId !== context.correlationId) throw bridgeError('malformed-response', 'The response correlation does not match the request.', context)
  if (source.expectedRevision !== context.expectedRevision) throw bridgeError('stale-response', 'The response revision precondition does not match the request.', context)
}

function normalizeErrorEnvelope(value: unknown, context: RequestContext): HermesProfileRuntimeMutationBridgeError {
  try {
    const source = strictRecord(value, 'Error envelope', ['contract', 'profileId', 'correlationId', 'expectedRevision', 'code', 'message'])
    assertEnvelopeIdentity(source, context)
    const code = safeIdentifier(source.code, 'Error code', 128)
    const message = safeDisplayText(source.message, 'Error message', 1_024)
    const mapped: HermesProfileRuntimeMutationBridgeErrorCode = code === 'stale-revision'
      ? 'stale-response'
      : code === 'preview-expired'
        ? 'preview-expired'
        : code === 'preview-replay'
          ? 'preview-replay'
          : 'error-envelope'
    return bridgeError(mapped, message, context)
  } catch (error) {
    if (error instanceof HermesProfileRuntimeMutationBridgeError) return error
    return bridgeError('malformed-response', 'The profile-runtime error envelope is malformed.', context)
  }
}

function strictRecord(value: unknown, label: string, allowedKeys: readonly string[]): JsonRecord {
  if (!isRecord(value)) throw new Error(`${label} must be an object.`)
  const allowed = new Set(allowedKeys)
  const unexpected = Object.keys(value).find((key) => !allowed.has(key))
  if (unexpected) throw new Error(`${label} contains forbidden field ${unexpected}.`)
  return value
}

function isRecord(value: unknown): value is JsonRecord {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function safeArray(value: unknown, label: string, maximum: number): unknown[] {
  if (!Array.isArray(value) || value.length > maximum) throw new Error(`${label} exceed the supported count.`)
  return value
}

function safeTextArray(value: unknown, label: string, maximumItems: number, maximumCharacters: number): string[] {
  return safeArray(value, label, maximumItems).map((item) => safeText(item, label, maximumCharacters, false))
}

function safeText(value: unknown, label: string, maximum: number, required = true): string {
  if (typeof value !== 'string' || CONTROL_CHARACTERS.test(value) || value.length > maximum || (required && !value.trim())) {
    throw new Error(`${label} is malformed or exceeds its limit.`)
  }
  return value
}

function safeDisplayText(value: unknown, label: string, maximum: number, required = true): string {
  const text = safeText(value, label, maximum, required)
  if (NATIVE_PATH.test(text)) throw new Error(`${label} contains a forbidden native path.`)
  return text
}

function safeIdentifier(value: unknown, label: string, maximum: number): string {
  const text = safeText(value, label, maximum)
  if (!SAFE_IDENTIFIER.test(text)) throw new Error(`${label} is malformed.`)
  return text
}

function safeRevision(value: unknown, label: string): number {
  return boundedInteger(value, label, 0, 2_147_483_647)
}

function boundedInteger(value: unknown, label: string, minimum: number, maximum: number): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) throw new Error(`${label} is malformed.`)
  return value as number
}

function safeEnum<T extends string>(value: unknown, options: readonly T[], label: string): T {
  if (typeof value !== 'string' || !options.includes(value as T)) throw new Error(`${label} is malformed.`)
  return value as T
}

function safeHttpOrigin(value: unknown): string {
  const text = safeText(value, 'Provider origin', 2_048)
  const parsed = new URL(text)
  if ((parsed.protocol !== 'http:' && parsed.protocol !== 'https:') || parsed.origin !== text || parsed.username || parsed.password) {
    throw new Error('Provider origin is malformed.')
  }
  return parsed.origin
}

function rejectUnsupported<T>(context: RequestContext, message: string): Promise<T> {
  return Promise.reject(bridgeError('unsupported-operation', message, context))
}

function abortError(context: RequestContext): HermesProfileRuntimeMutationBridgeError {
  return bridgeError('aborted', 'The profile-runtime request was cancelled.', context)
}

function bridgeError(
  code: HermesProfileRuntimeMutationBridgeErrorCode,
  message: string,
  context: RequestContext,
): HermesProfileRuntimeMutationBridgeError {
  return new HermesProfileRuntimeMutationBridgeError(code, message, context.profileId, context.correlationId)
}
