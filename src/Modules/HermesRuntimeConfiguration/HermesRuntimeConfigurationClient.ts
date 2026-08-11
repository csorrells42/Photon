import {
  assertRuntimeEndpointDraft,
  normalizeRuntimeEndpointSnapshot,
  normalizeRuntimeEndpointValidation,
  normalizeRuntimeBaseUrl,
  runtimeProfilePayload,
  type HermesRuntimeEndpointDraft,
  type HermesRuntimeProfileDraft,
  type HermesRuntimeEndpointSnapshot,
  type HermesRuntimeEndpointValidation,
} from './contracts'

export interface HermesRuntimeConfigurationClient {
  activate(id: string, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  delete(id: string, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  list(signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  save(draft: HermesRuntimeEndpointDraft, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  validate(draft: HermesRuntimeEndpointDraft, signal?: AbortSignal): Promise<HermesRuntimeEndpointValidation>
  saveProfile(endpointId: string, draft: HermesRuntimeProfileDraft, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  activateProfile(endpointId: string, model: string, profileId: string | null, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
  deleteProfile(endpointId: string, profileId: string, signal?: AbortSignal): Promise<HermesRuntimeEndpointSnapshot>
}

type FetchLike = typeof globalThis.fetch

function safeEndpointId(value: string): string {
  const id = value.trim()
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$/.test(id)) throw new Error('The endpoint identifier is invalid.')
  return id
}

function payload(draft: HermesRuntimeEndpointDraft) {
  const safe = assertRuntimeEndpointDraft(draft)
  return {
    ...(safe.id ? { id: safe.id } : {}), name: safe.name, base_url: safe.baseUrl, model: safe.model,
    discover_models: safe.discoverModels, make_default: safe.makeDefault,
    ...(safe.contextLength ? { context_length: safe.contextLength } : {}),
    ...(safe.models?.length ? { models: safe.models } : {}),
  }
}

function validationPayload(draft: HermesRuntimeEndpointDraft) {
  const baseUrl = normalizeRuntimeBaseUrl(draft.baseUrl)
  if (!baseUrl) throw new Error('Enter a valid HTTP or HTTPS endpoint URL.')
  const name = draft.name.trim().slice(0, 120)
  const model = draft.model.trim().slice(0, 512)
  return {
    name: name || 'Endpoint probe',
    base_url: baseUrl,
    model: model || 'discovery-probe',
    discover_models: true,
    make_default: false,
  }
}

export class SameOriginHermesRuntimeConfigurationClient implements HermesRuntimeConfigurationClient {
  constructor(private readonly fetch: FetchLike = globalThis.fetch.bind(globalThis)) {}

  async list(signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request('/api/providers/custom-endpoints', { signal }))
  }

  async save(draft: HermesRuntimeEndpointDraft, signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request('/api/providers/custom-endpoints', {
      method: 'POST', signal, body: JSON.stringify(payload(draft)),
    }))
  }

  async validate(draft: HermesRuntimeEndpointDraft, signal?: AbortSignal) {
    return normalizeRuntimeEndpointValidation(await this.request('/api/providers/custom-endpoints/validate', {
      method: 'POST', signal, body: JSON.stringify(validationPayload(draft)),
    }))
  }

  async activate(id: string, signal?: AbortSignal) {
    await this.request(`/api/providers/custom-endpoints/${encodeURIComponent(safeEndpointId(id))}/activate`, { method: 'POST', signal })
    return this.list(signal)
  }

  async delete(id: string, signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request(`/api/providers/custom-endpoints/${encodeURIComponent(safeEndpointId(id))}`, {
      method: 'DELETE', signal,
    }))
  }

  async saveProfile(endpointId: string, draft: HermesRuntimeProfileDraft, signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request(`/api/providers/custom-endpoints/${encodeURIComponent(safeEndpointId(endpointId))}/profiles`, {
      method: 'POST', signal, body: JSON.stringify(runtimeProfilePayload(draft)),
    }))
  }

  async activateProfile(endpointId: string, model: string, profileId: string | null, signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request(`/api/providers/custom-endpoints/${encodeURIComponent(safeEndpointId(endpointId))}/profiles/activate`, {
      method: 'POST', signal, body: JSON.stringify({ model, profile_id: profileId }),
    }))
  }

  async deleteProfile(endpointId: string, profileId: string, signal?: AbortSignal) {
    return normalizeRuntimeEndpointSnapshot(await this.request(`/api/providers/custom-endpoints/${encodeURIComponent(safeEndpointId(endpointId))}/profiles/${encodeURIComponent(safeEndpointId(profileId))}`, {
      method: 'DELETE', signal,
    }))
  }

  private async request(path: string, init: RequestInit = {}): Promise<unknown> {
    const response = await this.fetch(path, {
      ...init,
      credentials: 'include',
      headers: { Accept: 'application/json', ...(init.body ? { 'Content-Type': 'application/json' } : {}) },
    })
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) throw new Error('Sign in to Hermes before changing runtime providers.')
      throw new Error(`Hermes runtime configuration returned HTTP ${response.status}.`)
    }
    return response.json()
  }
}

export const hermesRuntimeConfigurationClient = new SameOriginHermesRuntimeConfigurationClient()
