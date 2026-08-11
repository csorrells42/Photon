import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { DockerControlCenter, nextDockerServiceIndex } from './DockerControlCenter'
import { DockerControlController } from './DockerControlController'
import { DOCKER_CONTROL_PROTOCOL_VERSION, type DockerControlAdapter } from './contracts'

const hash = `sha256:${'d'.repeat(64)}`
const snapshot = {
  protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
  revision: 9,
  observedAtUtc: '2026-08-10T18:00:00Z',
  engine: { state: 'running', version: '28.1.0' },
  compose: { state: 'degraded', definitionFingerprint: hash, upstreamRevision: 'rev-123', runtimeProtocol: 'authenticated-v2' },
  services: [
    { id: 'hermes', state: 'running', health: 'healthy', version: '1.0.0', image: { imageId: hash, approvedDigest: hash, ociRevision: 'rev-123', verification: 'verified' }, ports: [{ address: '127.0.0.1', hostPort: 9119, containerPort: 8000, protocol: 'tcp' }] },
    { id: 'serena', state: 'stopped', health: 'unknown', ports: [] },
    { id: 'model-runner', state: 'unavailable', health: 'not-configured', ports: [] },
  ],
  volumes: [{ role: 'data', state: 'mounted', persistent: true }, { role: 'workspace', state: 'mounted', persistent: true }],
  lastWorkflow: { kind: 'rollback', state: 'succeeded', completedAtUtc: '2026-08-10T17:00:00Z', summary: 'Previous verified image restored.' },
}

function adapter(): DockerControlAdapter {
  return {
    availability: { state: 'available' },
    describe: vi.fn(async () => ({
      protocolVersion: DOCKER_CONTROL_PROTOCOL_VERSION,
      availability: { state: 'available' },
      services: ['hermes', 'serena', 'model-runner'],
      operations: { startStack: true, stopStack: true, restartService: true, update: false },
      updateReason: 'derived-runtime-updater-not-integrated',
    } as const)),
    refresh: vi.fn(async () => snapshot),
    readLogs: vi.fn(async (request) => ({ protocolVersion: 1, requestId: request.requestId, service: request.service, entries: [], truncated: false })),
    reviewMutation: vi.fn(async (request) => ({
      protocolVersion: 1, requestId: request.requestId, status: 'ready', snapshotRevision: request.snapshotRevision,
      reviewToken: 'U'.repeat(48), fingerprint: hash, expiresAtUtc: '2030-01-01T00:00:00Z',
      affectedServices: ['hermes', 'serena'], summary: 'Start the approved Photon stack.', warnings: ['Serena is currently stopped.'],
    })),
    commitMutation: vi.fn(),
  }
}

describe('DockerControlCenter', () => {
  it('renders honest unavailable state with all mutations disabled and no demo data', () => {
    const markup = renderToStaticMarkup(<DockerControlCenter controller={new DockerControlController()} />)
    expect(markup).toContain('Docker integration unavailable')
    expect(markup).toContain('never inferred')
    expect(markup).toContain('No generic Docker command surface')
    expect(markup).not.toContain('Running</strong>')
    expect(markup).not.toContain('docker compose')
  })

  it('renders exact status, image identity, loopback ports, volumes, workflow, and keyboard tabs', async () => {
    const controller = new DockerControlController({ adapter: adapter() })
    await controller.refresh()
    const markup = renderToStaticMarkup(<DockerControlCenter controller={controller} />)
    for (const expected of [
      'aria-label="Docker Control Center"', 'aria-label="Docker services"', 'role="tab"', 'role="tabpanel"',
      'Hermes', 'Serena', 'Model Runner', 'Revision 9', 'sha256:', '127.0.0.1:9119',
      'Data:', 'Workspace:', 'Previous verified image restored.', 'Review stack start', 'Update workflow unavailable',
      'Environment values, Docker credentials, mounted secret contents',
    ]) expect(markup).toContain(expected)
    expect(markup).toContain('tabindex="0"')
    expect(markup).toContain('tabindex="-1"')
    expect(markup).toContain('title="The trusted Docker updater is not integrated."')
  })

  it('renders exact affected services, fingerprint, expiry, warnings, and disabled commit in review', async () => {
    const controller = new DockerControlController({ adapter: adapter(), now: () => Date.parse('2026-08-10T18:00:00Z') })
    await controller.refresh()
    await controller.requestMutation({ kind: 'start-stack' })
    const markup = renderToStaticMarkup(<DockerControlCenter controller={controller} />)
    expect(markup).toContain('role="dialog"')
    expect(markup).toContain('Review before mutation')
    expect(markup).toContain('Exact affected services:')
    expect(markup).toContain('Start the approved Photon stack.')
    expect(markup).toContain('Serena is currently stopped.')
    expect(markup).toContain(hash)
    expect(markup).toContain('2030-01-01T00:00:00Z')
    expect(markup).toContain('Apply reviewed operation</button>')
    expect(markup).toContain('disabled=""')
  })

  it('supports wrapping arrow navigation and Home/End without trapping unrelated keys', () => {
    expect(nextDockerServiceIndex(0, 3, 'ArrowLeft')).toBe(2)
    expect(nextDockerServiceIndex(2, 3, 'ArrowRight')).toBe(0)
    expect(nextDockerServiceIndex(1, 3, 'Home')).toBe(0)
    expect(nextDockerServiceIndex(1, 3, 'End')).toBe(2)
    expect(nextDockerServiceIndex(1, 3, 'Enter')).toBeNull()
  })
})
