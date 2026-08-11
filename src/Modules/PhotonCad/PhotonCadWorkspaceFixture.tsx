import type {
  PhotonCadBomRow,
  PhotonCadProjectSnapshot,
  PhotonCadRuntimeDescription,
} from './PhotonCadContract'
import { PHOTON_CAD_CONTRACT_VERSION } from './PhotonCadContract'
import { PhotonCadWorkspace, type PhotonCadReleaseOutput, type PhotonCadWorkspaceStage } from './PhotonCadWorkspace'

const geometrySource = {
  package: 'build123d',
  version: 'fixture-version',
  digest: `sha256:${'a'.repeat(64)}`,
  license: 'Apache-2.0',
}

const warehouseSource = {
  package: 'bd_warehouse',
  version: 'fixture-version',
  digest: `sha256:${'b'.repeat(64)}`,
  license: 'Apache-2.0',
}

const assemblySource = {
  package: 'PartCAD',
  version: 'fixture-version',
  digest: `sha256:${'c'.repeat(64)}`,
  license: 'Apache-2.0',
}

export const photonCadWorkspaceFixtureRuntime: PhotonCadRuntimeDescription = {
  contractVersion: PHOTON_CAD_CONTRACT_VERSION,
  status: 'unavailable',
  reason: 'unavailable',
  geometryBundleId: 'fixture-geometry-bundle',
  assemblyBundleId: 'fixture-assembly-bundle',
  catalog: {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    catalogRevision: 'fixture-catalog-1',
    generatedAtUtc: '2026-08-10T18:30:00Z',
    coverage: { discovered: 4, available: 4, unavailable: 0, unavailableReasons: [] },
    capabilities: [
      {
        id: 'build123d.box',
        backend: 'geometry',
        category: 'Primitives',
        title: 'Precision box',
        description: 'Create a dimensioned rectangular solid from length, width, and height.',
        operation: 'create',
        parameters: [
          { id: 'length', label: 'Length', description: 'Overall X dimension.', kind: 'number', required: true, unit: 'length', minimum: 0.01, step: 0.1, defaultValue: 240 },
          { id: 'width', label: 'Width', description: 'Overall Y dimension.', kind: 'number', required: true, unit: 'length', minimum: 0.01, step: 0.1, defaultValue: 180 },
          { id: 'height', label: 'Height', description: 'Overall Z dimension.', kind: 'number', required: true, unit: 'length', minimum: 0.01, step: 0.1, defaultValue: 120 },
          { id: 'centered', label: 'Center on origin', description: 'Place the solid symmetrically around the origin.', kind: 'boolean', required: false, defaultValue: true },
        ],
        source: geometrySource,
        previewSupported: true,
        experimental: false,
      },
      {
        id: 'bd_warehouse.bearing',
        backend: 'geometry',
        category: 'Power transmission',
        title: 'Rolling bearing',
        description: 'Select a declared bearing family and size from the component warehouse.',
        operation: 'create',
        parameters: [
          {
            id: 'series',
            label: 'Bearing series',
            description: 'Catalog family; availability depends on the installed warehouse bundle.',
            kind: 'choice',
            required: true,
            defaultValue: '6205',
            choices: [{ value: '6205', label: '6205 deep-groove ball bearing' }, { value: '30205', label: '30205 tapered roller bearing' }],
          },
          { id: 'sealed', label: 'Sealed', description: 'Use the sealed catalog variant when declared.', kind: 'boolean', required: false, defaultValue: true },
        ],
        source: warehouseSource,
        previewSupported: true,
        experimental: false,
      },
      {
        id: 'build123d.auger_flight',
        backend: 'geometry',
        category: 'Industrial machinery',
        title: 'Auger flight',
        description: 'Draft a helical flight from diameter, pitch, thickness, and length.',
        operation: 'create',
        parameters: [
          { id: 'diameter', label: 'Outside diameter', description: 'Finished flight outside diameter.', kind: 'number', required: true, unit: 'length', minimum: 1, defaultValue: 305 },
          { id: 'pitch', label: 'Pitch', description: 'Axial travel per revolution.', kind: 'number', required: true, unit: 'length', minimum: 1, defaultValue: 250 },
          { id: 'turns', label: 'Turns', description: 'Number of complete revolutions.', kind: 'number', required: true, unit: 'count', minimum: 0.25, defaultValue: 8 },
        ],
        source: geometrySource,
        previewSupported: true,
        experimental: true,
      },
      {
        id: 'partcad.place_occurrence',
        backend: 'assembly',
        category: 'Assembly',
        title: 'Place component occurrence',
        description: 'Place a selected part into the scratch assembly using a bounded transform.',
        operation: 'assemble',
        parameters: [
          { id: 'part', label: 'Part', description: 'Existing part entity to place.', kind: 'entity', required: true, defaultValue: 'part-shaft' },
          { id: 'position', label: 'Position', description: 'Occurrence translation in project units.', kind: 'vector3', required: true, unit: 'length', defaultValue: { x: 0, y: 0, z: 0 } },
          { id: 'grounded', label: 'Ground occurrence', description: 'Prevent free placement in the scratch assembly.', kind: 'boolean', required: false, defaultValue: false },
        ],
        source: assemblySource,
        previewSupported: true,
        experimental: false,
      },
    ],
  },
}

export const photonCadWorkspaceFixtureProject: PhotonCadProjectSnapshot = {
  contractVersion: PHOTON_CAD_CONTRACT_VERSION,
  sessionId: 'fixture-session',
  projectId: 'gearbox-fixture',
  revision: 12,
  title: 'Conveyor Drive Gearbox',
  units: 'millimeter',
  mode: 'scratch',
  dirty: true,
  entities: [
    { id: 'assembly-gearbox', parentId: null, kind: 'assembly', name: 'GBX-100 Gearbox', visible: true, suppressed: false },
    { id: 'part-housing', parentId: 'assembly-gearbox', kind: 'part', name: 'GBX-110 Split housing', visible: true, suppressed: false, sourceCapabilityId: 'build123d.box' },
    { id: 'part-shaft', parentId: 'assembly-gearbox', kind: 'part', name: 'GBX-120 Input shaft', visible: true, suppressed: false },
    { id: 'part-gear', parentId: 'assembly-gearbox', kind: 'part', name: 'GBX-130 Helical gear', visible: true, suppressed: false },
    { id: 'occ-bearing-a', parentId: 'assembly-gearbox', kind: 'occurrence', name: '6205 bearing · input A', visible: true, suppressed: false, sourceCapabilityId: 'bd_warehouse.bearing' },
    { id: 'occ-bearing-b', parentId: 'assembly-gearbox', kind: 'occurrence', name: '6205 bearing · input B', visible: true, suppressed: false, sourceCapabilityId: 'bd_warehouse.bearing' },
    { id: 'datum-output-axis', parentId: 'assembly-gearbox', kind: 'datum', name: 'Output shaft axis', visible: false, suppressed: false },
  ],
  operations: [
    { id: 'fixture-operation-1', capabilityId: 'build123d.box', label: 'Draft split housing envelope', createdAtUtc: '2026-08-10T18:18:00Z', state: 'proposed' },
    { id: 'fixture-operation-2', capabilityId: 'bd_warehouse.bearing', label: 'Place declared 6205 bearing', createdAtUtc: '2026-08-10T18:21:00Z', state: 'proposed' },
  ],
  issues: [
    { code: 'fixture-not-verified', severity: 'info', message: 'This demonstration snapshot has not run geometry or assembly verification.', entityIds: [] },
  ],
}

export const photonCadWorkspaceFixtureBom: readonly PhotonCadBomRow[] = [
  { partNumber: 'GBX-110', description: 'Split housing', quantity: 1, unit: 'each', sourceEntityId: 'part-housing' },
  { partNumber: 'GBX-120', description: 'Input shaft', quantity: 1, unit: 'each', sourceEntityId: 'part-shaft' },
  { partNumber: 'GBX-130', description: 'Helical gear', quantity: 1, unit: 'each', sourceEntityId: 'part-gear' },
  { partNumber: '6205-2RS', description: 'Deep-groove ball bearing', quantity: 2, unit: 'each', sourceEntityId: 'occ-bearing-a' },
]

export function PhotonCadWorkspaceFixture({
  initialStage = 'library',
  initialReleaseOutput = 'cad-package',
}: {
  initialStage?: PhotonCadWorkspaceStage
  initialReleaseOutput?: PhotonCadReleaseOutput
}) {
  return (
    <PhotonCadWorkspace
      runtime={photonCadWorkspaceFixtureRuntime}
      project={photonCadWorkspaceFixtureProject}
      bom={photonCadWorkspaceFixtureBom}
      bomDigest={`sha256:${'d'.repeat(64)}`}
      initialStage={initialStage}
      initialReleaseOutput={initialReleaseOutput}
      evidenceMode="fixture"
    />
  )
}
