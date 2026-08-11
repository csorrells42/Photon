import {
  HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION,
  extensionLimits,
  type AdvancedModelSettings,
  type AuxiliaryTask,
  type CommitRequest,
  type CommitResult,
  type ExtensionDataState,
  type ExtensionWriteIntent,
  type HermesExtensionSettingsController,
  type HermesExtensionSettingsSnapshot,
  type LoadResult,
  type ModelOption,
  type ProviderValidationIntent,
  type ProviderValidationResult,
  type WriteReview,
} from '../HermesExtensionSettings/contracts'
import {
  HermesExtensionSettingsLiveAdapter,
  type HermesExtensionSettingsLiveFetch,
} from './HermesExtensionSettingsLiveAdapter'
import { liveModelId } from './normalization'

const limits = {
  reviews: 64,
  responseBytes: 64 * 1024,
  defaultWriteTimeoutMs: 10_000,
  defaultReadTimeoutMs: 15_000,
  minimumTimeoutMs: 10,
  maximumWriteTimeoutMs: 30_000,
  maximumReadTimeoutMs: 60_000,
} as const

const supportedAuxiliaryTasks = ['vision', 'compression', 'title-generation'] as const
type SupportedAuxiliaryTask = typeof supportedAuxiliaryTasks[number]
type ReadAdapter = Pick<HermesExtensionSettingsLiveAdapter, 'read'>

export interface HermesExtensionSettingsLiveControllerOptions {
  readAdapter: ReadAdapter
  writeFetch: HermesExtensionSettingsLiveFetch
  profileId?: string
  writeTimeoutMs?: number
  readTimeoutMs?: number
}

interface AssignmentReviewRecord {
  reviewId: string
  scope: 'main' | 'auxiliary'
  task?: SupportedAuxiliaryTask
  beforeModelId: string
  afterModelId: string
  provider: string
  model: string
  requiresExpensiveModelConfirmation: boolean
  committing: boolean
}

interface ModelAssignmentBody {
  scope: 'main' | 'auxiliary'
  provider: string
  model: string
  task?: 'vision' | 'compression' | 'title_generation'
  confirm_expensive_model?: true
}

export type VisionReadinessState = 'native-ready' | 'fallback-ready' | 'configuration-required'

export interface VisionReadinessDiagnostic {
  state: VisionReadinessState
  mainModelId: string
  auxiliaryVisionModelId: string
  message: string
}

class BoundedResponseError extends Error {}

export class HermesExtensionSettingsLiveController implements HermesExtensionSettingsController {
  private readonly readAdapter: ReadAdapter
  private readonly writeFetch: HermesExtensionSettingsLiveFetch
  private readonly profileId: string | undefined
  private readonly writeTimeoutMs: number
  private readonly readTimeoutMs: number
  private readonly reviews = new Map<string, AssignmentReviewRecord>()
  private readonly reviewOrder: string[] = []
  private currentSnapshot: HermesExtensionSettingsSnapshot | null = null
  private readSequence = 0
  private pendingWriteAbort: AbortController | null = null
  private activeWriteReviewId: string | null = null
  private readonly serverConfirmedExpensiveModels = new Set<string>()

  public constructor(options: HermesExtensionSettingsLiveControllerOptions) {
    if (!options || typeof options.writeFetch !== 'function' || !options.readAdapter?.read) {
      throw new Error('Injected live read adapter and write transport are required.')
    }
    this.readAdapter = options.readAdapter
    this.writeFetch = options.writeFetch
    this.profileId = optionalProfileId(options.profileId)
    this.writeTimeoutMs = boundedTimeout(
      options.writeTimeoutMs,
      limits.defaultWriteTimeoutMs,
      limits.maximumWriteTimeoutMs,
    )
    this.readTimeoutMs = boundedTimeout(
      options.readTimeoutMs,
      limits.defaultReadTimeoutMs,
      limits.maximumReadTimeoutMs,
    )
  }

  public async load(): Promise<LoadResult> {
    const result = await this.readLiveSnapshot()
    if (!result.snapshot) {
      this.currentSnapshot = null
      return { state: result.state, message: result.message }
    }
    this.currentSnapshot = cloneSnapshot(result.snapshot)
    return { state: result.state, snapshot: cloneSnapshot(result.snapshot) }
  }

  public async preview(intent: ExtensionWriteIntent): Promise<WriteReview> {
    if (intent.kind !== 'models') {
      throw new Error('This live controller version supports only one advanced-model assignment per review.')
    }
    const snapshot = this.currentSnapshot ?? (await this.requireLiveSnapshot())
    const assignment = deriveSingleAssignment(snapshot, intent.settings)
    const selected = requireAvailableModel(snapshot, assignment.afterModelId)
    const decoded = decodeLiveModelId(selected.id)
    const reviewId = deterministicReviewId(
      this.profileId,
      assignment.scope,
      assignment.task,
      assignment.beforeModelId,
      selected.id,
    )
    const existing = this.reviews.get(reviewId)
    if (existing) {
      if (
        existing.scope === assignment.scope
        && existing.task === assignment.task
        && existing.beforeModelId === assignment.beforeModelId
        && existing.afterModelId === selected.id
      ) return publicReview(existing)
      throw new Error('The deterministic review identity collided; no review was stored.')
    }

    const record: AssignmentReviewRecord = {
      reviewId,
      scope: assignment.scope,
      ...(assignment.task ? { task: assignment.task } : {}),
      beforeModelId: assignment.beforeModelId,
      afterModelId: selected.id,
      provider: decoded.provider,
      model: decoded.model,
      requiresExpensiveModelConfirmation: selected.costTier === 'expensive'
        || this.serverConfirmedExpensiveModels.has(selected.id),
      committing: false,
    }
    this.rememberReview(record)
    return publicReview(record)
  }

  public async commit(request: CommitRequest): Promise<CommitResult> {
    const reviewId = safeReviewId(request?.reviewId)
    const review = this.reviews.get(reviewId)
    if (!review || review.committing) {
      return { status: 'error', message: 'This review is missing, expired, already submitted, or currently committing.' }
    }
    if (request?.confirmed !== true) {
      return { status: 'error', message: 'Explicit confirmation is required.' }
    }
    if (review.requiresExpensiveModelConfirmation && request.expensiveModelConfirmed !== true) {
      return { status: 'error', message: 'Explicit expensive-model confirmation is required before the single write.' }
    }
    if (this.activeWriteReviewId !== null) {
      return { status: 'error', message: 'Another model assignment is already committing; wait for it to finish and refresh.' }
    }

    review.committing = true
    this.activeWriteReviewId = review.reviewId
    try {
      const fresh = await this.readLiveSnapshot()
      if (!fresh.snapshot) return { status: 'error', message: 'Fresh live model state is unavailable; nothing was written.' }
      const currentBefore = currentAssignment(fresh.snapshot, review.scope, review.task)
      if (currentBefore !== review.beforeModelId) {
        return { status: 'error', message: 'The reviewed model assignment is stale; refresh and preview again.' }
      }
      try {
        requireAvailableModel(fresh.snapshot, review.afterModelId)
      } catch {
        return { status: 'error', message: 'The reviewed model is no longer advertised as available; nothing was written.' }
      }

      const body: ModelAssignmentBody = {
        scope: review.scope,
        provider: review.provider,
        model: review.model,
        ...(review.task ? { task: upstreamTask(review.task) } : {}),
        ...(review.requiresExpensiveModelConfirmation && request.expensiveModelConfirmed === true
          ? { confirm_expensive_model: true as const }
          : {}),
      }

      // Delete immediately before dispatch. One review can cause at most one POST,
      // including timeout, cancellation, malformed acknowledgement, or confirm_required.
      this.consumeReview(review.reviewId)
      const mutation = await this.postAssignment(body, review)
      if (mutation.status !== 'success') return mutation

      this.currentSnapshot = null
      const reloaded = await this.readLiveSnapshot()
      if (!reloaded.snapshot) {
        return {
          status: 'success',
          message: 'Model assignment was saved, but the post-write live refresh is unavailable.',
        }
      }
      if (currentAssignment(reloaded.snapshot, review.scope, review.task) !== review.afterModelId) {
        return {
          status: 'success',
          message: 'Model assignment was acknowledged, but the immediate live refresh did not yet reflect it.',
        }
      }
      this.currentSnapshot = cloneSnapshot(reloaded.snapshot)
      return {
        status: 'success',
        message: review.scope === 'main'
          ? 'Default model assignment saved for new sessions.'
          : `${review.task} auxiliary model assignment saved for new sessions.`,
        snapshot: cloneSnapshot(reloaded.snapshot),
      }
    } finally {
      const retained = this.reviews.get(review.reviewId)
      if (retained) retained.committing = false
      if (this.activeWriteReviewId === review.reviewId) this.activeWriteReviewId = null
    }
  }

  public async validateProvider(intent: ProviderValidationIntent): Promise<ProviderValidationResult> {
    // Read only the non-secret identity. Never enumerate, copy, stringify,
    // retain, log, compare, or send intent.secrets or their values.
    const providerId = safeProviderId(intent?.providerId)
    await Promise.resolve()
    return {
      providerId,
      state: 'unavailable',
      message: 'Live provider credential validation is unavailable in this first model controller.',
    }
  }

  public cancelPendingWrite(): boolean {
    if (!this.pendingWriteAbort) return false
    this.pendingWriteAbort.abort()
    return true
  }

  private async requireLiveSnapshot(): Promise<HermesExtensionSettingsSnapshot> {
    const live = await this.readLiveSnapshot()
    if (!live.snapshot) throw new Error(live.message ?? 'Live model settings are unavailable.')
    this.currentSnapshot = cloneSnapshot(live.snapshot)
    return this.currentSnapshot
  }

  private async readLiveSnapshot(): Promise<{
    state: ExtensionDataState
    snapshot?: HermesExtensionSettingsSnapshot
    message?: string
  }> {
    const controller = new AbortController()
    const timeout = setTimeout(() => controller.abort(), this.readTimeoutMs)
    try {
      const result = await withAbort(this.readAdapter.read({
        ...(this.profileId ? { profileId: this.profileId } : {}),
        correlationId: this.nextReadCorrelation(),
        signal: controller.signal,
      }), controller.signal)
      if (result.state !== 'ready' && result.state !== 'partial') {
        return { state: mapLiveState(result.state), message: 'Live extension model state is unavailable.' }
      }
      if (!isUsableLiveSnapshot(result.snapshot)) {
        return { state: 'error', message: 'The live model snapshot is malformed or empty.' }
      }
      return { state: result.snapshot.state, snapshot: cloneSnapshot(result.snapshot) }
    } catch (reason) {
      return {
        state: 'error',
        message: isAbort(reason) || controller.signal.aborted
          ? 'Live model state read timed out or was cancelled.'
          : 'Live model state could not be read.',
      }
    } finally {
      clearTimeout(timeout)
    }
  }

  private async postAssignment(body: ModelAssignmentBody, review: AssignmentReviewRecord): Promise<CommitResult> {
    const controller = new AbortController()
    this.pendingWriteAbort = controller
    let timedOut = false
    const timeout = setTimeout(() => {
      timedOut = true
      controller.abort()
    }, this.writeTimeoutMs)
    try {
      const profileQuery = this.profileId ? `?profile=${encodeURIComponent(this.profileId)}` : ''
      const response = await withAbort(this.writeFetch(`/api/model/set${profileQuery}`, {
        method: 'POST',
        credentials: 'include',
        signal: controller.signal,
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          'X-Hermes-Correlation-Id': review.reviewId,
        },
        body: JSON.stringify(body),
      }), controller.signal)
      if (!response.ok) return { status: 'error', message: `Model assignment returned HTTP ${response.status}.` }
      const raw = parseObject(await boundedResponseText(response, controller.signal))
      if (raw.confirm_required === true) {
        if (!review.requiresExpensiveModelConfirmation) this.serverConfirmedExpensiveModels.add(review.afterModelId)
        return {
          status: 'unavailable',
          message: review.requiresExpensiveModelConfirmation
            ? 'Upstream still requires confirmation; the controller will not retry or resend.'
            : 'Upstream requires an expensive-model review that this preview did not advertise. Refresh and preview again.',
        }
      }
      if (raw.ok !== true || !acknowledges(raw, body)) {
        return { status: 'error', message: 'Hermes returned a malformed or mismatched model-assignment acknowledgement.' }
      }
      return { status: 'success', message: 'Model assignment acknowledged.' }
    } catch (reason) {
      if (isAbort(reason) || controller.signal.aborted) {
        return { status: 'error', message: timedOut ? 'Model assignment timed out.' : 'Model assignment was cancelled.' }
      }
      if (reason instanceof BoundedResponseError) {
        return { status: 'error', message: 'Model assignment response exceeded 64 KiB.' }
      }
      return { status: 'error', message: 'Model assignment transport or response was malformed.' }
    } finally {
      clearTimeout(timeout)
      if (this.pendingWriteAbort === controller) this.pendingWriteAbort = null
    }
  }

  private nextReadCorrelation(): string {
    this.readSequence = (this.readSequence + 1) % Number.MAX_SAFE_INTEGER
    return `live-model-read-${this.readSequence.toString(36)}`
  }

  private rememberReview(record: AssignmentReviewRecord): void {
    this.reviews.set(record.reviewId, record)
    this.reviewOrder.push(record.reviewId)
    while (this.reviewOrder.length > limits.reviews) {
      const oldest = this.reviewOrder.shift()
      if (oldest) this.reviews.delete(oldest)
    }
  }

  private consumeReview(reviewId: string): void {
    this.reviews.delete(reviewId)
    const index = this.reviewOrder.indexOf(reviewId)
    if (index >= 0) this.reviewOrder.splice(index, 1)
  }
}

export function diagnoseVisionReadiness(snapshot: HermesExtensionSettingsSnapshot): VisionReadinessDiagnostic {
  if (!isUsableLiveSnapshot(snapshot)) {
    return {
      state: 'configuration-required',
      mainModelId: '',
      auxiliaryVisionModelId: '',
      message: 'Live model state is unavailable or malformed.',
    }
  }
  const mainModelId = snapshot.modelSettings.defaultModelId
  const main = snapshot.models.find((model) => model.id === mainModelId && model.available)
  if (main?.specialties.includes('vision')) {
    return {
      state: 'native-ready',
      mainModelId,
      auxiliaryVisionModelId: '',
      message: 'The active main model advertises native vision support.',
    }
  }
  const auxiliaryVisionModelId =
    snapshot.modelSettings.auxiliaryModels.find((item) => item.task === 'vision')?.modelId ?? ''
  const auxiliary = snapshot.models.find((model) => model.id === auxiliaryVisionModelId && model.available)
  if (auxiliary) {
    return {
      state: 'fallback-ready',
      mainModelId,
      auxiliaryVisionModelId,
      message: 'An advertised auxiliary vision model is assigned for fallback analysis.',
    }
  }
  return {
    state: 'configuration-required',
    mainModelId,
    auxiliaryVisionModelId: '',
    message: 'Neither native main-model vision nor an available auxiliary vision assignment is ready.',
  }
}

function deriveSingleAssignment(
  snapshot: HermesExtensionSettingsSnapshot,
  next: AdvancedModelSettings,
): {
  scope: 'main' | 'auxiliary'
  task?: SupportedAuxiliaryTask
  beforeModelId: string
  afterModelId: string
} {
  if (
    !next
    || !Array.isArray(next.auxiliaryModels)
    || !Array.isArray(next.taskOverrides)
    || !Array.isArray(next.providers)
  ) throw new Error('Model settings are malformed.')
  if (!sameMoa(snapshot.modelSettings.moa, next.moa)) {
    throw new Error('MoA edits are unavailable in this controller version.')
  }
  if (!sameProviders(snapshot.modelSettings.providers, next.providers)) {
    throw new Error('Provider edits are unavailable in this controller version.')
  }
  if (!sameTaskOverrides(snapshot.modelSettings.taskOverrides, next.taskOverrides)) {
    throw new Error('Task override edits are unavailable in this controller version.')
  }

  const beforeAuxiliary = auxiliaryMap(snapshot.modelSettings.auxiliaryModels)
  const afterAuxiliary = auxiliaryMap(next.auxiliaryModels)
  if (!beforeAuxiliary || !afterAuxiliary) throw new Error('Auxiliary assignments are malformed or duplicated.')

  const changes: Array<{
    scope: 'main' | 'auxiliary'
    task?: SupportedAuxiliaryTask
    beforeModelId: string
    afterModelId: string
  }> = []
  if (snapshot.modelSettings.defaultModelId !== next.defaultModelId) {
    changes.push({
      scope: 'main',
      beforeModelId: snapshot.modelSettings.defaultModelId,
      afterModelId: next.defaultModelId,
    })
  }
  const tasks = new Set<AuxiliaryTask>([...beforeAuxiliary.keys(), ...afterAuxiliary.keys()])
  for (const task of tasks) {
    const beforeModelId = beforeAuxiliary.get(task) ?? ''
    const afterModelId = afterAuxiliary.get(task) ?? ''
    if (beforeModelId === afterModelId) continue
    if (!isSupportedAuxiliaryTask(task)) {
      throw new Error(`Auxiliary task ${task} is unavailable in this controller version.`)
    }
    changes.push({ scope: 'auxiliary', task, beforeModelId, afterModelId })
  }
  if (changes.length !== 1) {
    throw new Error(
      changes.length === 0
        ? 'No supported model assignment changed.'
        : 'Exactly one model assignment may change per review.',
    )
  }
  if (!changes[0].afterModelId) throw new Error('Clearing a model assignment is unavailable in this controller version.')
  return changes[0]
}

function currentAssignment(
  snapshot: HermesExtensionSettingsSnapshot,
  scope: 'main' | 'auxiliary',
  task?: SupportedAuxiliaryTask,
): string {
  return scope === 'main'
    ? snapshot.modelSettings.defaultModelId
    : snapshot.modelSettings.auxiliaryModels.find((item) => item.task === task)?.modelId ?? ''
}

function requireAvailableModel(snapshot: HermesExtensionSettingsSnapshot, modelId: string): ModelOption {
  const model = snapshot.models.find((item) => item.id === modelId && item.available === true)
  if (!model) throw new Error('The selected model is unavailable or is not advertised by the current live snapshot.')
  const decoded = decodeLiveModelId(model.id)
  if (decoded.provider !== model.providerId) throw new Error('The selected model identity is malformed.')
  return model
}

export function decodeLiveModelId(id: string): { provider: string; model: string } {
  if (typeof id !== 'string' || id.length > extensionLimits.label * 2) {
    throw new Error('The model identity is malformed.')
  }
  const delimiter = id.indexOf('::')
  if (delimiter <= 0 || delimiter >= id.length - 2) throw new Error('The model identity is malformed.')
  const provider = id.slice(0, delimiter)
  const model = id.slice(delimiter + 2)
  if (liveModelId(provider, model) !== id) throw new Error('The model identity is malformed.')
  return { provider, model }
}

function publicReview(record: AssignmentReviewRecord): WriteReview {
  const slot = record.scope === 'main' ? 'Default model' : `Auxiliary ${record.task}`
  return {
    reviewId: record.reviewId,
    kind: 'models',
    title: `Change ${slot}`,
    before: [`${slot}: ${record.beforeModelId || '(unassigned)'}`],
    after: [`${slot}: ${record.afterModelId}`],
    warnings: record.requiresExpensiveModelConfirmation
      ? ['The advertised model cost tier requires separate explicit confirmation.']
      : [],
    requiresExpensiveModelConfirmation: record.requiresExpensiveModelConfirmation,
  }
}

function deterministicReviewId(
  profileId: string | undefined,
  scope: string,
  task: string | undefined,
  before: string,
  after: string,
): string {
  const value = [profileId ?? '(default)', scope, task ?? '', before, after].join('\u001f')
  let hash = 0x811c9dc5
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193)
  }
  return `live-model-review-${(hash >>> 0).toString(16).padStart(8, '0')}`
}

function upstreamTask(task: SupportedAuxiliaryTask): 'vision' | 'compression' | 'title_generation' {
  return task === 'title-generation' ? 'title_generation' : task
}

function acknowledges(raw: Record<string, unknown>, body: ModelAssignmentBody): boolean {
  if (raw.scope !== body.scope || raw.provider !== body.provider || raw.model !== body.model) return false
  if (body.scope === 'main') return true
  return Array.isArray(raw.tasks) && raw.tasks.length === 1 && raw.tasks[0] === body.task
}

function parseObject(text: string): Record<string, unknown> {
  let value: unknown
  try {
    value = JSON.parse(text) as unknown
  } catch {
    throw new Error('Malformed JSON response.')
  }
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('Malformed response object.')
  }
  return value as Record<string, unknown>
}

async function boundedResponseText(response: Response, signal: AbortSignal): Promise<string> {
  const declaredText = response.headers.get('Content-Length')
  if (declaredText !== null) {
    const declared = Number(declaredText)
    if (!Number.isFinite(declared) || declared < 0) throw new Error('Malformed Content-Length.')
    if (declared > limits.responseBytes) throw new BoundedResponseError()
  }
  if (!response.body) throw new Error('Missing response body.')
  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  try {
    while (true) {
      if (signal.aborted) {
        await reader.cancel().catch(() => undefined)
        throw abortError()
      }
      const next = await withAbort(reader.read(), signal)
      if (next.done) break
      total += next.value.byteLength
      if (total > limits.responseBytes) {
        await reader.cancel().catch(() => undefined)
        throw new BoundedResponseError()
      }
      chunks.push(next.value)
    }
  } finally {
    reader.releaseLock()
  }
  const merged = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    merged.set(chunk, offset)
    offset += chunk.byteLength
  }
  return new TextDecoder('utf-8', { fatal: true }).decode(merged)
}

function isUsableLiveSnapshot(value: unknown): value is HermesExtensionSettingsSnapshot {
  if (value === null || typeof value !== 'object') return false
  const snapshot = value as Partial<HermesExtensionSettingsSnapshot>
  if (
    snapshot.contractVersion !== HERMES_EXTENSION_SETTINGS_CONTRACT_VERSION
    || !Array.isArray(snapshot.models)
    || snapshot.models.length === 0
    || snapshot.models.length > extensionLimits.collection
    || !snapshot.modelSettings
    || typeof snapshot.modelSettings.defaultModelId !== 'string'
    || !Array.isArray(snapshot.modelSettings.auxiliaryModels)
    || !Array.isArray(snapshot.modelSettings.taskOverrides)
    || !Array.isArray(snapshot.modelSettings.providers)
    || !snapshot.modelSettings.moa
    || !Array.isArray(snapshot.modelSettings.moa.referenceModelIds)
  ) return false
  if (
    auxiliaryMap(snapshot.modelSettings.auxiliaryModels) === null
    || snapshot.modelSettings.taskOverrides.some((item) =>
      !item || typeof item.task !== 'string' || typeof item.modelId !== 'string')
    || snapshot.modelSettings.providers.some((item) =>
      !item
      || typeof item.providerId !== 'string'
      || !Number.isFinite(item.timeoutMs)
      || !Number.isInteger(item.maxRetries))
  ) return false
  const seen = new Set<string>()
  for (const model of snapshot.models) {
    if (
      !model
      || typeof model.id !== 'string'
      || typeof model.providerId !== 'string'
      || typeof model.available !== 'boolean'
      || !Array.isArray(model.specialties)
      || (model.costTier !== 'standard' && model.costTier !== 'expensive')
    ) return false
    try {
      const decoded = decodeLiveModelId(model.id)
      if (decoded.provider !== model.providerId || seen.has(model.id)) return false
    } catch {
      return false
    }
    seen.add(model.id)
  }
  return true
}

function cloneSnapshot(snapshot: HermesExtensionSettingsSnapshot): HermesExtensionSettingsSnapshot {
  return structuredClone(snapshot)
}

function auxiliaryMap(
  values: readonly { task: AuxiliaryTask; modelId: string }[],
): Map<AuxiliaryTask, string> | null {
  if (values.length > extensionLimits.smallCollection) return null
  const result = new Map<AuxiliaryTask, string>()
  for (const value of values) {
    if (
      !value
      || typeof value.task !== 'string'
      || typeof value.modelId !== 'string'
      || result.has(value.task)
    ) return null
    result.set(value.task, value.modelId)
  }
  return result
}

function sameTaskOverrides(
  left: readonly { task: string; modelId: string }[],
  right: readonly { task: string; modelId: string }[],
): boolean {
  if (left.length !== right.length || left.length > extensionLimits.smallCollection) return false
  const rightMap = new Map(right.map((item) => [item.task, item.modelId]))
  return rightMap.size === right.length
    && left.every((item) => rightMap.get(item.task) === item.modelId)
}

function sameMoa(left: AdvancedModelSettings['moa'], right: AdvancedModelSettings['moa']): boolean {
  return Boolean(left && right)
    && left.enabled === right.enabled
    && left.presetId === right.presetId
    && left.aggregatorModelId === right.aggregatorModelId
    && sameStringArray(left.referenceModelIds, right.referenceModelIds)
}

function sameProviders(
  left: AdvancedModelSettings['providers'],
  right: AdvancedModelSettings['providers'],
): boolean {
  if (left.length !== right.length || left.length > extensionLimits.smallCollection) return false
  return left.every((provider, index) => {
    const candidate = right[index]
    if (!candidate) return false
    const leftEndpoint = provider.customEndpoint
    const rightEndpoint = candidate.customEndpoint
    return provider.providerId === candidate.providerId
      && provider.timeoutMs === candidate.timeoutMs
      && provider.maxRetries === candidate.maxRetries
      && (
        (leftEndpoint === null && rightEndpoint === null)
        || (
          leftEndpoint !== null
          && rightEndpoint !== null
          && leftEndpoint.baseUrl === rightEndpoint.baseUrl
          && leftEndpoint.apiPath === rightEndpoint.apiPath
          && leftEndpoint.authHeaderName === rightEndpoint.authHeaderName
        )
      )
  })
}

function sameStringArray(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function isSupportedAuxiliaryTask(task: AuxiliaryTask): task is SupportedAuxiliaryTask {
  return (supportedAuxiliaryTasks as readonly string[]).includes(task)
}

function mapLiveState(state: string): ExtensionDataState {
  return state === 'partial' ? 'partial' : state === 'unavailable' ? 'unavailable' : 'error'
}

function boundedTimeout(value: number | undefined, fallback: number, maximum: number): number {
  const result = value ?? fallback
  if (!Number.isInteger(result) || result < limits.minimumTimeoutMs || result > maximum) {
    throw new Error(`Timeout must be an integer from ${limits.minimumTimeoutMs} through ${maximum} milliseconds.`)
  }
  return result
}

function optionalProfileId(value: string | undefined): string | undefined {
  if (value === undefined) return undefined
  if (!/^[A-Za-z0-9._:-]{1,128}$/.test(value)) throw new Error('The profile identity is invalid.')
  return value
}

function safeReviewId(value: unknown): string {
  return typeof value === 'string' && /^live-model-review-[0-9a-f]{8}$/.test(value) ? value : ''
}

function safeProviderId(value: unknown): string {
  return typeof value === 'string' && /^[A-Za-z0-9._:/-]{1,128}$/.test(value) ? value : ''
}

function withAbort<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  if (signal.aborted) return Promise.reject(abortError())
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(abortError())
    signal.addEventListener('abort', onAbort, { once: true })
    operation.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort))
  })
}
function isAbort(reason: unknown): boolean {
  return reason instanceof Error && reason.name === 'AbortError'
}

function abortError(): Error {
  const error = new Error('Aborted')
  error.name = 'AbortError'
  return error
}



