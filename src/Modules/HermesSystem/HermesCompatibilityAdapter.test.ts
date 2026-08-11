import { describe, expect, it, vi } from 'vitest'
import {
  evaluateHermesCompatibility,
  HermesCompatibilityAdapter,
  normalizeRuntimeIdentity,
} from './HermesCompatibilityAdapter'

const imageId = `sha256:${'a'.repeat(64)}`

describe('HermesCompatibilityAdapter', () => {
  it('normalizes only the safe versioned runtime identity shape', () => {
    expect(normalizeRuntimeIdentity({
      protocolVersion: 1,
      containerName: 'hermes',
      observedAtUtc: '2026-08-09T05:00:00.0000000Z',
      imageReference: 'nousresearch/hermes-agent:latest',
      imageId,
      repoDigest: `nousresearch/hermes-agent@sha256:${'b'.repeat(64)}`,
      revision: '51597c5e078256680ab05f0f2aad625ed8efeb30',
      ignoredSecret: 'must not survive normalization',
    })).toEqual({
      protocolVersion: 1,
      containerName: 'hermes',
      observedAtUtc: '2026-08-09T05:00:00.0000000Z',
      imageReference: 'nousresearch/hermes-agent:latest',
      imageId,
      repoDigest: `nousresearch/hermes-agent@sha256:${'b'.repeat(64)}`,
      revision: '51597c5e078256680ab05f0f2aad625ed8efeb30',
    })
    expect(normalizeRuntimeIdentity({ protocolVersion: 1, containerName: 'hermes', imageId: 'latest' })).toBeNull()
  })

  it('recognizes the tested runtime only when immutable identity is present', () => {
    const identity = normalizeRuntimeIdentity({ protocolVersion: 1, containerName: 'hermes', imageId })
    expect(evaluateHermesCompatibility('0.20.0', identity).state).toBe('compatible')
    expect(evaluateHermesCompatibility('0.21.0', identity).state).toBe('review')
    expect(evaluateHermesCompatibility('0.20.0', null).state).toBe('unverified')
  })

  it('treats a missing runtime-identity endpoint as unverified instead of failing status load', async () => {
    const fetcher = vi.fn(async () => new Response('{"error":"unavailable"}', { status: 503 }))
    await expect(new HermesCompatibilityAdapter(fetcher).identity()).resolves.toBeNull()
  })
})
