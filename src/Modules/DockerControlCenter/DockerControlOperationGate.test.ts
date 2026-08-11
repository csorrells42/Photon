import { describe, expect, it } from 'vitest'
import { DOCKER_CONTROL_PROTOCOL_VERSION, type DockerMutationReviewResult } from './contracts'
import { DockerControlOperationGate } from './DockerControlOperationGate'

const readyReview: Extract<DockerMutationReviewResult, { status: 'ready' }> = {
  protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
  requestId: 'docker-control:review-1',
  status: 'ready',
  snapshotRevision: 4,
  reviewToken: 'R'.repeat(48),
  fingerprint: `sha256:${'b'.repeat(64)}`,
  expiresAtUtc: '2030-01-01T00:00:00Z',
  affectedServices: ['hermes', 'serena'],
  summary: 'Start stack',
  warnings: [],
}

describe('DockerControlOperationGate', () => {
  it('blocks duplicate work, binds a review to its snapshot, and consumes it before commit', () => {
    const gate = new DockerControlOperationGate()
    gate.setSnapshotRevision(4)
    const operation = gate.beginReview(readyReview.requestId, { kind: 'start-stack' })!
    expect(gate.beginReview('docker-control:review-2', { kind: 'stop-stack' })).toBeNull()
    expect(gate.finishReview(operation, readyReview, Date.parse('2029-01-01T00:00:00Z'))).toBe('accepted')
    expect(gate.review?.fingerprint).toBe(readyReview.fingerprint)

    const commit = gate.beginCommit('docker-control:commit-1', Date.parse('2029-01-01T00:00:00Z'))!
    expect(commit.review.reviewToken).toBe(readyReview.reviewToken)
    expect(gate.review).toBeNull()
    expect(gate.beginCommit('docker-control:commit-2', Date.parse('2029-01-01T00:00:00Z'))).toBeNull()
    expect(gate.finishCommit(commit.operation)).toBe('accepted')
  })

  it('rejects expired and stale review completion', () => {
    const expiredGate = new DockerControlOperationGate()
    expiredGate.setSnapshotRevision(4)
    const expiredOperation = expiredGate.beginReview(readyReview.requestId, { kind: 'start-stack' })!
    expect(expiredGate.finishReview(expiredOperation, readyReview, Date.parse('2031-01-01T00:00:00Z'))).toBe('stale')
    expect(expiredGate.review).toBeNull()

    const staleGate = new DockerControlOperationGate()
    staleGate.setSnapshotRevision(4)
    const staleOperation = staleGate.beginReview(readyReview.requestId, { kind: 'start-stack' })!
    staleGate.setSnapshotRevision(5)
    expect(staleGate.finishReview(staleOperation, readyReview, Date.parse('2029-01-01T00:00:00Z'))).toBe('stale')
    expect(staleGate.review).toBeNull()
  })
})
