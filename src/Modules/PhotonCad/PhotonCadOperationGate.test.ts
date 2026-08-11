import { describe, expect, it } from 'vitest'
import { PhotonCadOperationGate } from './PhotonCadOperationGate'
import type { PhotonCadReleaseReviewRequest, PhotonCadReleaseReviewResult } from './PhotonCadContract'

const digest = `sha256:${'a'.repeat(64)}`
const context = { sessionId: 'session:1', projectId: 'project:1', revision: 3 }

function reviewRequest(): PhotonCadReleaseReviewRequest {
  return {
    contractVersion: 1,
    requestId: 'review:1',
    sessionId: context.sessionId,
    projectId: context.projectId,
    revision: context.revision,
    formats: ['step-ap214'],
    destinationHandle: `cad-destination:${'d'.repeat(32)}`,
  }
}

function reviewResult(expiresAtUtc = '2026-08-10T19:00:00Z'): PhotonCadReleaseReviewResult {
  return {
    contractVersion: 1,
    requestId: 'review:1',
    projectId: context.projectId,
    revision: context.revision,
    status: 'ready',
    reason: 'ready',
    reviewHandle: `cad-review:${'r'.repeat(32)}`,
    packageFingerprint: digest,
    expiresAtUtc,
    files: [{ role: 'assembly-step', relativePath: 'assembly/gearbox.step' }],
    issues: [],
  }
}

describe('PhotonCadOperationGate', () => {
  it('implements latest-wins generations independently for description and preview', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const first = gate.beginLatest('preview')!
    const description = gate.beginLatest('describe')!
    const second = gate.beginLatest('preview')!
    expect(gate.isCurrentLatest(first)).toBe(false)
    expect(gate.isCurrentLatest(second)).toBe(true)
    expect(gate.isCurrentLatest(description)).toBe(true)
  })

  it('allows one exclusive operation and settles only its exact token', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const apply = gate.startExclusive('apply', 'apply:1')!
    expect(gate.startExclusive('review', 'review:2')).toBeNull()
    expect(gate.settleExclusive({ ...apply, requestId: 'foreign:1' })).toBe('foreign')
    expect(gate.active).toEqual(apply)
    expect(gate.settleExclusive(apply)).toBe('accepted')
    expect(gate.active).toBeNull()
  })

  it('keeps the exclusive lock while a changed context makes its result stale', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const apply = gate.startExclusive('apply', 'apply:1')!
    gate.transitionContext({ ...context, revision: 4 })
    expect(gate.startExclusive('apply', 'apply:2')).toBeNull()
    expect(gate.settleExclusive(apply)).toBe('stale')
    expect(gate.startExclusive('apply', 'apply:2')).not.toBeNull()
  })

  it('binds an opaque review to the exact context and invalidates it on an edit', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const bound = gate.bindReview(reviewRequest(), reviewResult(), Date.parse('2026-08-10T18:00:00Z'))
    expect(bound).toMatchObject({ projectId: context.projectId, revision: 3, packageFingerprint: digest })
    expect(gate.currentReview(Date.parse('2026-08-10T18:30:00Z'))).toEqual(bound)
    expect(gate.markEdited()).toEqual(bound)
    expect(gate.currentReview(Date.parse('2026-08-10T18:30:00Z'))).toBeNull()
  })

  it.each([
    ['revision', () => ({ ...context, revision: 4 })],
    ['session', () => ({ ...context, sessionId: 'session:2' })],
    ['project', () => ({ ...context, projectId: 'project:2' })],
  ])('invalidates release review on %s change', (_label, next) => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const bound = gate.bindReview(reviewRequest(), reviewResult(), Date.parse('2026-08-10T18:00:00Z'))
    expect(gate.transitionContext(next())).toEqual(bound)
  })

  it('invalidates both review and outstanding latest work on controller replacement', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    const preview = gate.beginLatest('preview')!
    const bound = gate.bindReview(reviewRequest(), reviewResult(), Date.parse('2026-08-10T18:00:00Z'))
    expect(gate.replaceController()).toEqual(bound)
    expect(gate.isCurrentLatest(preview)).toBe(false)
    expect(gate.currentReview(Date.parse('2026-08-10T18:30:00Z'))).toBeNull()
  })

  it('does not expose an expired review for commit', () => {
    const gate = new PhotonCadOperationGate()
    gate.transitionContext(context)
    gate.bindReview(reviewRequest(), reviewResult('2026-08-10T18:01:00Z'), Date.parse('2026-08-10T18:00:00Z'))
    expect(gate.reviewIsExpired(Date.parse('2026-08-10T18:01:00Z'))).toBe(true)
    expect(gate.currentReview(Date.parse('2026-08-10T18:01:00Z'))).toBeNull()
  })
})
