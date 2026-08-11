import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type AdapterResponse,
  type ApplyImportRequest,
  type ApplyProfileMutationRequest,
  type ConfirmPermissionGrantRequest,
  type EditableProfileDocument,
  type ExportProfileRequest,
  type HermesProfileRuntimeAdapter,
  type OperationPreview,
  type PreviewImportRequest,
  type PreviewPermissionGrantRequest,
  type PreviewProfileMutationRequest,
  type ProfileId,
  type ProfileImportInspection,
  type ProfileMutation,
  type ProfileRuntimeSnapshot,
  type RequestContext,
  type SaveConfigurationRequest,
  type SaveDocumentRequest,
  type SaveIntentRequest,
  type SelectActiveProfileRequest,
  type SelectTerminalBackendRequest,
} from './contracts'
import {
  assertNoSecretRoundTrip,
  assertSafeProfileId,
  createSecretFreeExport,
  sanitizeProviderOrigin,
  validateConfigurationValues,
} from './runtimeSafety'

type StoredProfile = Omit<ProfileRuntimeSnapshot, 'contract' | 'profileId' | 'profiles'> & { name: string; description: string; isDeleteProtected: boolean }
type StoredPreview = { profileId: ProfileId; operation: OperationPreview['operation']; fingerprint: string; preview: OperationPreview }

function abortError() {
  return new DOMException('The operation was cancelled.', 'AbortError')
}

function clone<T>(value: T): T {
  return structuredClone(value)
}

function fingerprint(value: unknown) {
  return JSON.stringify(value)
}

export class DeterministicHermesProfileRuntimeAdapter implements HermesProfileRuntimeAdapter {
  private readonly profiles = new Map<ProfileId, StoredProfile>()
  private readonly previews = new Map<string, StoredPreview>()
  private activeProfileId: ProfileId = 'profile-main'
  private sequence = 0

  constructor(private readonly latencyMilliseconds = 0) {
    this.profiles.set('profile-main', this.seed('Main', true, 'balanced'))
    this.profiles.set('profile-lab', this.seed('Lab', false, 'fast'))
  }

  private seed(name: string, isDeleteProtected: boolean, mode: string): StoredProfile {
    return {
      name,
      description: `${name} deterministic demonstration profile`,
      isDeleteProtected,
      revision: 1,
      availability: 'ready',
      detail: 'Deterministic local demonstration data; no live Hermes calls are made.',
      documents: [
        { kind: 'persona', text: `You are the ${name} profile.`, maximumCharacters: 65_536 },
        { kind: 'soul', text: 'Be direct, careful, and useful.', maximumCharacters: 65_536 },
        { kind: 'context', text: 'Caller-owned context. Save is always explicit.', maximumCharacters: 65_536 },
      ],
      intent: { modelId: 'hermes-default', projectPath: null, worktreePath: null, note: '' },
      terminalBackends: [
        { id: 'hermes-shell', label: 'Hermes agent shell', description: 'Shell backend used by the Hermes agent runtime.', selected: true, availability: 'ready', detail: 'Synthetic status.' },
        { id: 'hermes-container', label: 'Hermes isolated container', description: 'Delegated isolated backend for Hermes tool execution.', selected: false, availability: 'partial', detail: 'Selection is manageable; provisioning is delegated to the host.' },
      ],
      computerUse: {
        availability: 'partial',
        detail: 'Status is synthetic. Operating-system consent remains delegated to the trusted host.',
        permissions: [
          { id: 'screen-read', label: 'Read screen', description: 'Allow the Hermes computer-use worker to observe the screen.', state: 'not_requested', disposition: 'manageable', detail: 'Requires a separate preview and explicit confirmation.' },
          { id: 'input-control', label: 'Control input', description: 'Allow keyboard and pointer intents.', state: 'unavailable', disposition: 'delegated', detail: 'The trusted host must expose this capability.' },
        ],
      },
      providers: [
        { id: 'openai', label: 'OpenAI', oauth: 'connected', credentialPool: 'configured', configuredCredentialSlots: 1, customEndpoint: { state: 'not_configured', origin: null }, disposition: 'delegated', detail: 'Credential values remain in the trusted host.' },
        { id: 'local', label: 'Local compatible endpoint', oauth: 'unsupported', credentialPool: 'empty', configuredCredentialSlots: 0, customEndpoint: { state: 'configured', origin: sanitizeProviderOrigin('http://127.0.0.1:11434/v1') }, disposition: 'manageable', detail: 'Only the endpoint origin is exposed.' },
      ],
      configurationSchema: [
        { key: 'runtime.mode', label: 'Runtime mode', description: 'Non-secret runtime preference.', kind: 'enum', required: true, options: ['balanced', 'fast', 'careful'], disposition: 'manageable' },
        { key: 'runtime.maxToolSeconds', label: 'Tool timeout', description: 'Maximum requested tool duration in seconds.', kind: 'integer', required: true, minimum: 5, maximum: 600, disposition: 'manageable' },
        { key: 'runtime.hostPolicy', label: 'Host policy', description: 'Reported by the host and not editable here.', kind: 'string', required: true, maximumLength: 128, disposition: 'delegated' },
      ],
      configuration: { 'runtime.mode': mode, 'runtime.maxToolSeconds': 90, 'runtime.hostPolicy': 'host-owned' },
    }
  }

  private async pause(signal: AbortSignal) {
    if (signal.aborted) throw abortError()
    if (!this.latencyMilliseconds) return
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(resolve, this.latencyMilliseconds)
      signal.addEventListener('abort', () => { clearTimeout(timer); reject(abortError()) }, { once: true })
    })
    if (signal.aborted) throw abortError()
  }

  private profile(context: RequestContext): StoredProfile {
    if (context.contract !== HERMES_PROFILE_RUNTIME_CONTRACT) throw new Error('Unsupported profile-runtime contract.')
    assertSafeProfileId(context.profileId)
    const profile = this.profiles.get(context.profileId)
    if (!profile) throw new Error(`Profile ${context.profileId} is unavailable.`)
    if (context.expectedRevision !== undefined && context.expectedRevision !== profile.revision) throw new Error('Profile revision conflict; refresh before saving.')
    return profile
  }

  private snapshot(profileId: ProfileId): ProfileRuntimeSnapshot {
    const profile = this.profiles.get(profileId)
    if (!profile) throw new Error(`Profile ${profileId} is unavailable.`)
    assertNoSecretRoundTrip(profile.providers)
    return clone({
      contract: HERMES_PROFILE_RUNTIME_CONTRACT,
      profileId,
      ...profile,
      profiles: [...this.profiles.entries()].map(([id, item]) => ({
        id, name: item.name, description: item.description, isActive: id === this.activeProfileId, isDeleteProtected: item.isDeleteProtected,
      })),
    })
  }

  private response<T>(context: RequestContext, value: T, revision = this.profile(context).revision): AdapterResponse<T> {
    return { ...context, revision, value: clone(value) }
  }

  private update(context: RequestContext, change: (profile: StoredProfile) => void): ProfileRuntimeSnapshot {
    const profile = this.profile(context)
    change(profile)
    profile.revision += 1
    return this.snapshot(context.profileId)
  }

  private makePreview(context: RequestContext, operation: OperationPreview['operation'], title: string, summary: string, consequences: string[], destructive: boolean, fingerprintValue: unknown) {
    this.profile(context)
    const previewId = `preview-${++this.sequence}`
    const preview: OperationPreview = {
      previewId, operation, profileId: context.profileId, title, summary, consequences,
      destructive, confirmationPhrase: destructive ? `CONFIRM ${context.profileId}` : null,
      expiresAt: new Date(Date.UTC(2035, 0, 1, 0, 0, this.sequence)).toISOString(),
    }
    this.previews.set(previewId, { profileId: context.profileId, operation, fingerprint: fingerprint(fingerprintValue), preview })
    return preview
  }

  private consumePreview(context: RequestContext, previewId: string, operation: OperationPreview['operation'], fingerprintValue: unknown, phrase?: string) {
    const stored = this.previews.get(previewId)
    if (!stored || stored.profileId !== context.profileId || stored.operation !== operation || stored.fingerprint !== fingerprint(fingerprintValue)) {
      throw new Error('Preview is missing, stale, cross-profile, or does not match the requested operation.')
    }
    if (stored.preview.destructive && phrase !== stored.preview.confirmationPhrase) throw new Error('Explicit destructive confirmation does not match the preview.')
    this.previews.delete(previewId)
  }

  async load(context: RequestContext, signal: AbortSignal) {
    await this.pause(signal)
    return this.response(context, this.snapshot(context.profileId))
  }

  async selectActiveProfile(request: SelectActiveProfileRequest, signal: AbortSignal) {
    await this.pause(signal); this.profile(request); assertSafeProfileId(request.targetProfileId, 'Target profile identity')
    if (!this.profiles.has(request.targetProfileId)) throw new Error('Target profile is unavailable.')
    this.activeProfileId = request.targetProfileId
    return this.response(request, { activeProfileId: this.activeProfileId })
  }

  async previewProfileMutation(request: PreviewProfileMutationRequest, signal: AbortSignal) {
    await this.pause(signal)
    const mutation = request.mutation
    const destructive = mutation.kind === 'delete'
    if (mutation.kind === 'delete' && this.profile(request).isDeleteProtected) throw new Error('This profile is protected from deletion.')
    if (mutation.kind === 'clone' && mutation.sourceProfileId !== request.profileId) throw new Error('Cross-profile clone sources are rejected by this adapter boundary.')
    const name = mutation.kind === 'delete' ? this.profile(request).name : mutation.name.trim()
    if (!name || name.length > 128) throw new Error('Profile name is invalid.')
    const preview = this.makePreview(request, mutation.kind, `${mutation.kind[0].toUpperCase()}${mutation.kind.slice(1)} profile`, `${mutation.kind} ${name}`, destructive ? ['Deletes this profile from the fake adapter.', 'Selects another profile if this one is active.'] : ['Changes deterministic fake profile state only.'], destructive, mutation)
    return this.response(request, preview)
  }

  async applyProfileMutation(request: ApplyProfileMutationRequest, signal: AbortSignal) {
    await this.pause(signal); this.profile(request)
    this.consumePreview(request, request.previewId, request.mutation.kind, request.mutation, request.destructiveConfirmation?.phrase)
    const mutation = request.mutation
    let affectedProfileId = request.profileId
    if (mutation.kind === 'rename') {
      this.update(request, (profile) => { profile.name = mutation.name.trim() })
    } else if (mutation.kind === 'delete') {
      if (this.profile(request).isDeleteProtected) throw new Error('This profile is protected from deletion.')
      this.profiles.delete(request.profileId)
      if (this.activeProfileId === request.profileId) this.activeProfileId = this.profiles.keys().next().value ?? ''
    } else {
      const id = `profile-${++this.sequence}`
      const source = mutation.kind === 'clone' ? clone(this.profile(request)) : this.seed(mutation.name.trim(), false, 'balanced')
      source.name = mutation.name.trim(); source.isDeleteProtected = false; source.revision = 1
      this.profiles.set(id, source); affectedProfileId = id
    }
    const revision = this.profiles.get(request.profileId)?.revision ?? 0
    return this.response(request, { affectedProfileId, activeProfileId: this.activeProfileId }, revision)
  }

  async saveDocument(request: SaveDocumentRequest, signal: AbortSignal) {
    await this.pause(signal)
    if (request.text.length > 65_536) throw new Error('Profile document exceeds 65,536 characters.')
    const snapshot = this.update(request, (profile) => {
      const document = profile.documents.find((item) => item.kind === request.document)
      if (!document) throw new Error('Requested profile document is unavailable.')
      document.text = request.text
    })
    return this.response(request, snapshot, snapshot.revision)
  }

  async saveIntent(request: SaveIntentRequest, signal: AbortSignal) {
    await this.pause(signal)
    const snapshot = this.update(request, (profile) => { profile.intent = clone(request.intent) })
    return this.response(request, snapshot, snapshot.revision)
  }

  async selectTerminalBackend(request: SelectTerminalBackendRequest, signal: AbortSignal) {
    await this.pause(signal)
    const snapshot = this.update(request, (profile) => {
      if (!profile.terminalBackends.some((item) => item.id === request.backendId && item.availability !== 'unavailable')) throw new Error('Hermes terminal backend is unavailable.')
      profile.terminalBackends.forEach((item) => { item.selected = item.id === request.backendId })
    })
    return this.response(request, snapshot, snapshot.revision)
  }

  async previewPermissionGrant(request: PreviewPermissionGrantRequest, signal: AbortSignal) {
    await this.pause(signal)
    const permission = this.profile(request).computerUse.permissions.find((item) => item.id === request.intent.permissionId)
    if (!permission || permission.disposition !== 'manageable' || permission.state === 'unavailable') throw new Error('Permission is delegated or unavailable.')
    const preview = this.makePreview(request, 'permission_grant', `Grant ${permission.label}`, `Request ${request.intent.duration} computer-use permission.`, ['The row click does not grant anything.', 'Confirmation emits a separate typed grant intent.'], true, request.intent)
    return this.response(request, preview)
  }

  async confirmPermissionGrant(request: ConfirmPermissionGrantRequest, signal: AbortSignal) {
    await this.pause(signal)
    this.consumePreview(request, request.previewId, 'permission_grant', request.intent, request.confirmationPhrase)
    const snapshot = this.update(request, (profile) => {
      const permission = profile.computerUse.permissions.find((item) => item.id === request.intent.permissionId)
      if (!permission || permission.disposition !== 'manageable') throw new Error('Permission cannot be granted here.')
      permission.state = 'granted'; permission.detail = `Synthetic ${request.intent.duration} grant confirmed.`
    })
    return this.response(request, snapshot, snapshot.revision)
  }

  async saveConfiguration(request: SaveConfigurationRequest, signal: AbortSignal) {
    await this.pause(signal)
    const profile = this.profile(request)
    const merged = { ...profile.configuration, ...request.values }
    const manageableSchema = profile.configurationSchema.filter((field) => field.disposition === 'manageable')
    const manageableValues = Object.fromEntries(manageableSchema.map((field) => [field.key, merged[field.key]]))
    const validated = validateConfigurationValues(manageableSchema, manageableValues)
    const snapshot = this.update(request, (item) => { item.configuration = { ...item.configuration, ...validated } })
    return this.response(request, snapshot, snapshot.revision)
  }

  async previewImport(request: PreviewImportRequest, signal: AbortSignal) {
    await this.pause(signal)
    const preview = this.makePreview(request, 'import', 'Import profile settings', `Replace editable settings in ${this.profile(request).name}.`, ['Persona, soul, context, intents, and declared non-secret configuration may change.', 'Provider credentials and permissions are never imported.'], true, request.inspection)
    return this.response(request, preview)
  }

  async applyImport(request: ApplyImportRequest, signal: AbortSignal) {
    await this.pause(signal)
    this.consumePreview(request, request.previewId, 'import', request.inspection, request.confirmationPhrase)
    const inspection: ProfileImportInspection = clone(request.inspection)
    const snapshot = this.update(request, (profile) => {
      for (const document of profile.documents) {
        const imported = inspection.data.documents[document.kind]
        if (imported !== undefined) document.text = imported
      }
      profile.intent = inspection.data.intent
      const manageable = profile.configurationSchema.filter((field) => field.disposition === 'manageable')
      const accepted = Object.fromEntries(Object.entries(inspection.data.configuration).filter(([key]) => manageable.some((field) => field.key === key)))
      const complete = Object.fromEntries(manageable.map((field) => [field.key, keyPresent(accepted, field.key) ? accepted[field.key] : profile.configuration[field.key]]))
      profile.configuration = { ...profile.configuration, ...validateConfigurationValues(manageable, complete) }
    })
    return this.response(request, snapshot, snapshot.revision)
  }

  async exportProfile(request: ExportProfileRequest, signal: AbortSignal) {
    await this.pause(signal)
    const snapshot = this.snapshot(request.profileId)
    const text = createSecretFreeExport(snapshot)
    const safeName = this.profile(request).name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'profile'
    return this.response(request, { fileName: `${safeName}.hermes-profile.json`, text, byteCount: new TextEncoder().encode(text).byteLength })
  }
}

function keyPresent(values: Record<string, unknown>, key: string) {
  return Object.prototype.hasOwnProperty.call(values, key)
}

export const deterministicHermesProfileRuntimeAdapter = new DeterministicHermesProfileRuntimeAdapter()
