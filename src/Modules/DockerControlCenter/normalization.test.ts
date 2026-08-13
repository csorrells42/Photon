import { describe, expect, it } from 'vitest'
import { DOCKER_CONTROL_PROTOCOL_VERSION } from './contracts'
import { normalizeDockerLogs, normalizeDockerReview, normalizeDockerSnapshot, redactDockerLogText } from './normalization'

const hash = `sha256:${'a'.repeat(64)}`

function snapshot(revision = 7) {
  return {
    protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
    revision,
    observedAtUtc: '2026-08-10T18:00:00Z',
    engine: { state: 'running', version: '28.1.0', environment: { API_KEY: 'never-project-this' } },
    compose: { state: 'running', definitionFingerprint: hash, upstreamRevision: 'abc123', runtimeProtocol: 'v2', composeFile: 'C:\\secret\\compose.yml' },
    services: [{
      id: 'hermes', state: 'running', health: 'healthy', manageable: true, containerId: 'b'.repeat(64), version: '1.2.3',
      image: { imageId: hash, approvedDigest: hash, ociRevision: 'abc123', verification: 'verified', registryAuth: 'secret' },
      ports: [{ address: '127.0.0.1', hostPort: 9119, containerPort: 8000, protocol: 'tcp' }],
      resources: { cpuPercent: 1.25, memoryUsage: '512MiB', memoryLimit: '8GiB', memoryPercent: 6.25, networkIo: '1MB / 2MB', blockIo: '3MB / 4MB', pids: 12 },
      command: ['docker', 'run'],
    }, { id: 'serena', state: 'running', health: 'healthy', ports: [] }],
    volumes: [{ role: 'data', state: 'mounted', persistent: true, path: 'C:\\Users\\private' }],
    modelRunner: {
      state: 'running', version: 'v1.2.6', endpoint: 'http://127.0.0.1:12434/v1/', kind: 'Docker Engine', diskUsage: '5.64GB',
      loadAvailable: false, unloadAvailable: true, message: 'Docker Model Runner is available.',
      models: [{ reference: 'docker.io/ai/qwen3:4B-UD-Q4_K_XL', modelId: hash, size: '2.37 GiB', format: 'gguf', parameters: '4.02 B', loaded: true, backend: 'llama.cpp', mode: 'completion' }],
    },
    lastWorkflow: { kind: 'update', state: 'succeeded', completedAtUtc: '2026-08-10T17:30:00Z', summary: 'Verified update applied.', rawOutput: 'token=secret' },
    dockerConfig: { auths: { example: 'secret' } },
  }
}

describe('Docker Control normalization', () => {
  it('projects only bounded product evidence and drops paths, commands, environment, and auth material', () => {
    const normalized = normalizeDockerSnapshot(snapshot())
    expect(normalized).not.toBeNull()
    expect(normalized?.services.map((service) => service.id)).toEqual(['hermes', 'serena'])
    expect(normalized?.services[0].ports[0].address).toBe('127.0.0.1')
    expect(normalized?.services[0].resources?.cpuPercent).toBe(1.25)
    expect(normalized?.modelRunner?.models[0]).toMatchObject({ reference: 'docker.io/ai/qwen3:4B-UD-Q4_K_XL', loaded: true })
    const serialized = JSON.stringify(normalized)
    for (const forbidden of ['never-project-this', 'compose.yml', 'registryAuth', 'dockerConfig', 'C:\\Users', 'docker run']) {
      expect(serialized).not.toContain(forbidden)
    }
  })

  it('accepts native nullable absence without weakening invalid-value rejection', () => {
    const native = snapshot() as unknown as {
      lastWorkflow: unknown
      services: Array<Record<string, unknown>>
      modelRunner: { models: Array<Record<string, unknown>> }
    }
    native.lastWorkflow = null
    Object.assign(native.services[1], {
      version: null,
      containerId: null,
      image: null,
      resources: null,
    })
    Object.assign(native.modelRunner.models[0], {
      size: null,
      parameters: null,
    })

    const normalized = normalizeDockerSnapshot(native)
    expect(normalized).not.toBeNull()
    expect(normalized?.lastWorkflow).toBeUndefined()
    expect(normalized?.services[1]).toMatchObject({ id: 'serena', image: undefined, resources: undefined })
    expect(normalized?.modelRunner?.models[0]).toMatchObject({ size: undefined, parameters: undefined })

    native.services[1].containerId = 'not-a-container-id'
    expect(normalizeDockerSnapshot(native)).toBeNull()
  })

  it('rejects non-loopback ports, duplicate services, invalid identity, and invented states', () => {
    const publicPort = snapshot()
    publicPort.services[0].ports[0].address = '0.0.0.0'
    expect(normalizeDockerSnapshot(publicPort)).toBeNull()

    const duplicate = snapshot()
    duplicate.services[1].id = 'hermes'
    expect(normalizeDockerSnapshot(duplicate)).toBeNull()

    const invalidDigest = snapshot()
    invalidDigest.services[0].image!.approvedDigest = 'latest'
    expect(normalizeDockerSnapshot(invalidDigest)).toBeNull()

    const invented = snapshot()
    invented.services[0].state = 'probably-running'
    expect(normalizeDockerSnapshot(invented)).toBeNull()
  })

  it('bounds and redacts secret-like log content before rendering', () => {
    const requestId = 'docker-control:logs-1'
    const result = normalizeDockerLogs({
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      requestId,
      service: 'hermes',
      truncated: false,
      entries: [
        { stream: 'stdout', text: 'Authorization: Bearer topsecretvalue123456789' },
        { stream: 'stderr', text: 'password=hunter2 cookie=session-value' },
        { stream: 'system', text: 'https://user:password@example.test/path' },
        { stream: 'stdout', text: 'github_pat_abcdefghijklmnopqrstuvwxyz012345' },
      ],
      environment: { TOKEN: 'must-not-project' },
    }, requestId, 'hermes')

    expect(result?.entries).toHaveLength(4)
    const serialized = JSON.stringify(result)
    for (const secret of ['topsecretvalue', 'hunter2', 'session-value', 'user:password', 'github_pat_', 'must-not-project']) {
      expect(serialized).not.toContain(secret)
    }
    expect(serialized).toContain('[REDACTED]')
    expect(redactDockerLogText(`api_key=${'x'.repeat(900)}`).length).toBeLessThanOrEqual(512)
  })

  it('requires opaque revision-bound reviews with the exact intent service set', () => {
    const ready = {
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      requestId: 'docker-control:review-1',
      status: 'ready',
      snapshotRevision: 7,
      reviewToken: 'A'.repeat(48),
      fingerprint: hash,
      expiresAtUtc: '2026-08-10T19:00:00Z',
      affectedServices: ['hermes', 'serena'],
      summary: 'Start the approved stack. password=summary-secret',
      warnings: ['Authorization: Bearer warning-secret-value'],
      command: 'docker compose up',
    }
    const review = normalizeDockerReview(ready, ready.requestId, 7, { kind: 'start-stack' })
    expect(review?.status).toBe('ready')
    expect(JSON.stringify(review)).not.toContain('docker compose')
    expect(JSON.stringify(review)).not.toContain('summary-secret')
    expect(JSON.stringify(review)).not.toContain('warning-secret')
    expect(normalizeDockerReview({ ...ready, affectedServices: ['hermes'] }, ready.requestId, 7, { kind: 'start-stack' })?.status).toBe('ready')
    expect(normalizeDockerReview({ ...ready, affectedServices: ['serena'] }, ready.requestId, 7, { kind: 'restart-service', service: 'hermes' })).toBeNull()
    expect(normalizeDockerReview({ ...ready, affectedServices: ['model-runner'] }, ready.requestId, 7, { kind: 'unload-model', model: 'qwen3:4b' })?.status).toBe('ready')
    expect(normalizeDockerReview({ ...ready, snapshotRevision: 6 }, ready.requestId, 7, { kind: 'start-stack' })).toBeNull()
  })
})
