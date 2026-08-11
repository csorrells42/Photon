import { describe, expect, it } from 'vitest'
import {
  PHOTON_CAD_CONTRACT_VERSION,
  canonicalPhotonCadReleaseBinding,
  isOpaquePhotonCadReviewHandle,
  normalizePhotonCadCatalog,
  normalizePhotonCadRelativePath,
  validatePhotonCadInterchangeManifest,
  validatePhotonCadOperationRequest,
  type PhotonCadInterchangeManifest,
  type PhotonCadOperationRequest,
} from './PhotonCadContract'

const digest = `sha256:${'a'.repeat(64)}`

describe('PhotonCadContract', () => {
  it('normalizes a discovered capability catalog without executable instructions', () => {
    const catalog = normalizePhotonCadCatalog({
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      catalogRevision: 'catalog:1',
      generatedAtUtc: '2026-08-10T18:00:00Z',
      capabilities: [{
        id: 'warehouse.fastener.hex-bolt',
        backend: 'geometry',
        category: 'Fasteners',
        title: 'Hex bolt',
        description: 'Create a standards-based hex bolt.',
        operation: 'create',
        parameters: [{ id: 'length', label: 'Length', description: '', kind: 'number', required: true, unit: 'length', choices: [] }],
        source: { package: 'bd_warehouse', version: '0.3.0', digest, license: 'Apache-2.0' },
        previewSupported: true,
        experimental: false,
      }],
      coverage: { discovered: 1, available: 1, unavailable: 0, unavailableReasons: [] },
    })
    expect(catalog?.capabilities[0]?.title).toBe('Hex bolt')
    expect(catalog?.capabilities[0]?.source.digest).toBe(digest)
    expect(catalog?.capabilities[0]?.parameters[0]?.choices).toEqual([])
  })

  it('rejects inconsistent catalog coverage', () => {
    expect(normalizePhotonCadCatalog({
      contractVersion: 1,
      catalogRevision: 'catalog:1',
      generatedAtUtc: '2026-08-10T18:00:00Z',
      capabilities: [],
      coverage: { discovered: 2, available: 1, unavailable: 0, unavailableReasons: [] },
    })).toBeNull()
  })

  it('fails closed instead of truncating or dropping malformed catalog entries', () => {
    const base = {
      contractVersion: 1,
      catalogRevision: 'catalog:1',
      generatedAtUtc: '2026-08-10T18:00:00Z',
      coverage: { discovered: 1, available: 1, unavailable: 0, unavailableReasons: [] },
    }
    expect(normalizePhotonCadCatalog({
      ...base,
      capabilities: [{
        id: 'bad-capability', backend: 'geometry', category: 'Parts', title: 'Bad', description: '', operation: 'create',
        parameters: [{ id: 'length', label: 'Length', description: '', kind: 'number', required: 'yes' }],
        source: { package: 'build123d', version: '0.11.1', digest, license: 'Apache-2.0' },
        previewSupported: true, experimental: false,
      }],
    })).toBeNull()
    expect(normalizePhotonCadCatalog({ ...base, generatedAtUtc: '2026-02-31T18:00:00Z', capabilities: [] })).toBeNull()
  })

  it.each([
    ['parts/shaft.step', 'parts/shaft.step'],
    ['../secret.step', null],
    ['C:\\secret.step', null],
    ['//server/share.step', null],
    ['parts/file.step:stream', null],
    ['CON.step', null],
    ['parts/shaft.step.', null],
    ['parts/shaft.step ', null],
    ['parts/sha*ft.step', null],
    ['parts//shaft.step', null],
  ])('normalizes package path %s', (value, expected) => {
    expect(normalizePhotonCadRelativePath(value)).toBe(expected)
  })

  it('validates typed operations and rejects script-shaped identifiers and unbounded numbers', () => {
    const request: PhotonCadOperationRequest = {
      contractVersion: 1,
      requestId: 'operation:1',
      sessionId: 'session:1',
      projectId: 'gearbox:1',
      baseRevision: 3,
      mode: 'scratch',
      capabilityId: 'build123d.extrude',
      inputs: { length: 25.4, direction: { x: 0, y: 0, z: 1 } },
      targetEntityIds: ['sketch:1'],
    }
    expect(validatePhotonCadOperationRequest(request)).toEqual([])
    expect(validatePhotonCadOperationRequest({
      ...request,
      capabilityId: 'python -c evil',
      inputs: { length: Number.POSITIVE_INFINITY },
    })).toEqual(expect.arrayContaining(['invalid-capability', 'invalid-input']))
  })

  it('canonicalizes release formats independently of selection order', () => {
    const base = {
      contractVersion: 1 as const,
      requestId: 'review:1',
      sessionId: 'session:1',
      projectId: 'gearbox:1',
      revision: 3,
      destinationHandle: 'destination:1',
    }
    expect(canonicalPhotonCadReleaseBinding({ ...base, formats: ['stl', 'step-ap214'] }))
      .toBe(canonicalPhotonCadReleaseBinding({ ...base, formats: ['step-ap214', 'stl', 'stl'] }))
  })

  it('accepts only opaque release handles', () => {
    expect(isOpaquePhotonCadReviewHandle(`cad-review:${'a'.repeat(32)}`)).toBe(true)
    expect(isOpaquePhotonCadReviewHandle('C:\\exports\\gearbox.step')).toBe(false)
  })

  it('validates a portable, self-describing interchange manifest', () => {
    const manifest: PhotonCadInterchangeManifest = {
      schemaVersion: 1,
      packageId: 'package:1',
      packageFingerprint: digest,
      project: { projectId: 'gearbox:1', title: 'Gearbox', revision: 4, units: 'millimeter' },
      profile: {
        id: 'step-portable-v1',
        stepApplicationProtocol: 'AP214',
        geometry: 'exact-brep',
        coordinateSystem: {
          handedness: 'right',
          upAxis: 'z',
          matrixOrder: 'row-major',
          vectorConvention: 'column',
          transformMeaning: 'local-to-parent',
        },
      },
      createdAtUtc: '2026-08-10T18:00:00Z',
      provenance: {
        photonVersion: '0.1.0',
        geometryRuntime: { package: 'build123d', version: '0.11.1', digest, license: 'Apache-2.0' },
        operationDigest: digest,
      },
      files: [{ role: 'part-step', relativePath: 'parts/shaft.step', sha256: digest, byteLength: 1_024, mediaType: 'model/step' }],
      bom: [{ partNumber: 'SHAFT-001', description: 'Input shaft', quantity: 1, unit: 'each', sourceEntityId: 'part:shaft' }],
      occurrences: [{
        occurrenceId: 'occurrence:shaft',
        parentOccurrenceId: null,
        partNumber: 'SHAFT-001',
        sourceEntityId: 'part:shaft',
        transform: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
      }],
      validation: { status: 'passed', checks: ['valid-solids'], issues: [] },
      acceptance: [{ system: 'Photon', profile: 'fresh-reimport', status: 'passed', observedAtUtc: '2026-08-10T18:00:00Z', reportDigest: digest }],
    }
    expect(validatePhotonCadInterchangeManifest(manifest)).toEqual([])
    expect(validatePhotonCadInterchangeManifest({
      ...manifest,
      files: [...manifest.files, { ...manifest.files[0], relativePath: 'PARTS/SHAFT.STEP' }],
    })).toContain('duplicate-file')
    expect(validatePhotonCadInterchangeManifest({
      ...manifest,
      profile: { ...manifest.profile, coordinateSystem: { ...manifest.profile.coordinateSystem, upAxis: 'y' as 'z' } },
    })).toContain('invalid-coordinate-system')
  })
})
