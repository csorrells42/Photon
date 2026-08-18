export const HERMES_COMPATIBILITY_ADAPTER_VERSION = 1
export const TESTED_HERMES_RUNTIME_VERSIONS = ['0.20.0'] as const

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

export type HermesRuntimeIdentity = {
  protocolVersion: 1
  observedAtUtc: string | null
  containerName: 'photon'
  imageReference: string | null
  imageId: string
  repoDigest: string | null
  revision: string | null
}

export type HermesCompatibilitySnapshot = {
  adapterVersion: number
  state: 'compatible' | 'review' | 'unverified'
  runtimeVersion: string
  imageId?: string
  repoDigest?: string
  revision?: string
  observedAtUtc?: string
  title: string
  detail: string
}

function object(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? value as Record<string, unknown> : {}
}

function cleanString(value: unknown, pattern: RegExp, maximumLength: number) {
  return typeof value === 'string' && value.length <= maximumLength && pattern.test(value) ? value : null
}

export function normalizeRuntimeIdentity(value: unknown): HermesRuntimeIdentity | null {
  const raw = object(value)
  if (raw.protocolVersion !== 1 || raw.containerName !== 'photon') return null
  const imageId = cleanString(raw.imageId, /^sha256:[a-f0-9]{64}$/i, 71)
  if (!imageId) return null
  return {
    protocolVersion: 1,
    observedAtUtc: cleanString(raw.observedAtUtc, /^\d{4}-\d{2}-\d{2}T[^\s]{1,40}$/i, 64),
    containerName: 'photon',
    imageReference: cleanString(raw.imageReference, /^[a-z0-9./:_-]+$/i, 256),
    imageId,
    repoDigest: cleanString(raw.repoDigest, /^nousresearch\/hermes-agent@sha256:[a-f0-9]{64}$/i, 110),
    revision: cleanString(raw.revision, /^[a-f0-9]{7,64}$/i, 64),
  }
}

export function evaluateHermesCompatibility(
  runtimeVersion: string | undefined,
  identity: HermesRuntimeIdentity | null,
): HermesCompatibilitySnapshot {
  const version = runtimeVersion?.trim() || 'unknown'
  if (!runtimeVersion || !identity) {
    return {
      adapterVersion: HERMES_COMPATIBILITY_ADAPTER_VERSION,
      state: 'unverified',
      runtimeVersion: version,
      title: 'Identity not recorded',
      detail: 'Restart with the Hermes launcher to record the immutable container image identity.',
    }
  }

  const common = {
    adapterVersion: HERMES_COMPATIBILITY_ADAPTER_VERSION,
    runtimeVersion: version,
    imageId: identity.imageId,
    ...(identity.repoDigest ? { repoDigest: identity.repoDigest } : {}),
    ...(identity.revision ? { revision: identity.revision } : {}),
    ...(identity.observedAtUtc ? { observedAtUtc: identity.observedAtUtc } : {}),
  }
  if ((TESTED_HERMES_RUNTIME_VERSIONS as readonly string[]).includes(version)) {
    return {
      ...common,
      state: 'compatible',
      title: 'Recognized contract',
      detail: `Adapter v${HERMES_COMPATIBILITY_ADAPTER_VERSION} targets Hermes ${version}; immutable image identity recorded.`,
    }
  }
  return {
    ...common,
    state: 'review',
    title: 'Compatibility review needed',
    detail: `Hermes ${version} is not in this Workbench build's tested runtime list. Keep the upstream dashboard available until adapter checks pass.`,
  }
}

export class HermesCompatibilityAdapter {
  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  async identity(): Promise<HermesRuntimeIdentity | null> {
    try {
      const response = await this.fetcher('/workbench-api/runtime-identity', { credentials: 'same-origin' })
      if (!response.ok) return null
      return normalizeRuntimeIdentity(await response.json())
    } catch {
      return null
    }
  }
}

export const hermesCompatibilityAdapter = new HermesCompatibilityAdapter()
