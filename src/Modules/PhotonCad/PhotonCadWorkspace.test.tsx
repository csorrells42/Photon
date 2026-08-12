import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  PHOTON_CAD_CONTRACT_VERSION,
  type PhotonCadCapability,
  type PhotonCadController,
  type PhotonCadInputValue,
  type PhotonCadOperationRequest,
  type PhotonCadProjectSnapshot,
  type PhotonCadRuntimeDescription,
} from './PhotonCadContract'
import {
  PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID,
  PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID,
  PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID,
  PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID,
  PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID,
  PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID,
  PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID,
  PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID,
  PhotonCadWorkspace,
  photonCadAcceptedPersistedScratchCommit,
  photonCadAcceptedAssemblySnapshot,
  photonCadAcceptedPersistedScratchSnapshot,
  photonCadAssemblyRigidTransform,
  photonCadAuthorizePersistedScratchRequest,
  photonCadCapabilityAuthorizationFingerprint,
  photonCadCapabilityInputsMatch,
  photonCadCatalogSections,
  photonCadInitialCapabilityInputs,
  photonCadKeyboardPaneWidth,
  photonCadManualDesignCapabilityId,
  photonCadProjectParts,
  photonCadOperationContinuationAuthorizationBinding,
  loadPhotonCadPaneLayout,
  savePhotonCadPaneLayout,
} from './PhotonCadWorkspace'
import {
  PhotonCadWorkspaceFixture,
  photonCadWorkspaceFixtureBom,
  photonCadWorkspaceFixtureProject,
  photonCadWorkspaceFixtureRuntime,
} from './PhotonCadWorkspaceFixture'

const industrialDigest = 'e'.repeat(64)

function persistedProject(): PhotonCadProjectSnapshot {
  return {
    ...photonCadWorkspaceFixtureProject,
    sessionId: 'session:industrial',
    projectId: 'project:industrial',
    revision: 0,
    mode: 'canonical',
    units: 'millimeter',
    dirty: false,
    entities: [],
    occurrences: [],
    operations: [],
    issues: [],
  }
}

function spurGearCapability(overrides: Partial<PhotonCadCapability> = {}): PhotonCadCapability {
  return {
    id: 'industrial.gear.spur.v1',
    backend: 'assembly',
    category: 'Gears',
    title: 'Spur Gear',
    description: 'Create a discovered spur gear.',
    operation: 'create',
    parameters: [
      { id: 'module', label: 'Module', description: '', kind: 'number', required: true, unit: 'length', minimum: 0.1, maximum: 100 },
      { id: 'pressure_angle', label: 'Pressure angle', description: '', kind: 'number', required: true, unit: 'angle', minimum: 1, maximum: 89 },
      { id: 'thickness', label: 'Thickness', description: '', kind: 'number', required: true, unit: 'length', minimum: 0.1, maximum: 1_000 },
      { id: 'tooth_count', label: 'Tooth count', description: '', kind: 'integer', required: true, unit: 'count', minimum: 3, maximum: 1_000 },
      { id: 'addendum', label: 'Addendum', description: '', kind: 'number', required: false, unit: 'length', defaultValue: null },
      { id: 'dedendum', label: 'Dedendum', description: '', kind: 'number', required: false, unit: 'length', defaultValue: null },
      { id: 'root_fillet', label: 'Root fillet', description: '', kind: 'number', required: false, unit: 'length', defaultValue: null },
    ],
    source: { package: 'bd-warehouse', version: '0.2.0', digest: industrialDigest, license: 'redistribution-blocked' },
    previewSupported: true,
    experimental: false,
    ...overrides,
  }
}

function industrialRuntime(capability: PhotonCadCapability, catalogRevision = 'catalog:industrial:1'): PhotonCadRuntimeDescription {
  return {
    contractVersion: 1,
    status: 'available',
    reason: 'ready',
    geometryBundleId: 'geometry:industrial',
    catalog: {
      contractVersion: 1,
      catalogRevision,
      generatedAtUtc: '2026-08-11T10:00:00Z',
      capabilities: [capability],
      coverage: { discovered: 1, available: 1, unavailable: 0, unavailableReasons: [] },
    },
  }
}

function manualCapability(
  id: typeof PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID | typeof PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID | typeof PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID
    | typeof PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID | typeof PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID,
): PhotonCadCapability {
  const base = {
    id,
    backend: 'geometry' as const,
    category: 'Build123d design',
    title: id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID ? 'Sketch + extrude'
      : id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID ? 'Sketch cut'
        : id === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID ? 'Hole'
          : id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID ? 'Linear pattern' : 'Circular pattern',
    description: 'Persist an exact manual solid operation.',
    operation: id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID ? 'create' as const : 'modify' as const,
    source: { package: 'build123d', version: '0.3.80', digest: industrialDigest, license: 'redistribution-blocked' },
    previewSupported: true,
    experimental: false,
  }
  const positive = (parameterId: string, required = true, defaultValue: PhotonCadInputValue = required ? 10 : null) => ({
    id: parameterId, label: parameterId, description: '', kind: 'number' as const, required, unit: 'length' as const,
    minimum: 0.000001, maximum: 1_000_000, defaultValue,
  })
  if (id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID) return {
    ...base,
    parameters: [
      { id: 'profileKind', label: 'Profile', description: '', kind: 'choice', required: true, defaultValue: 'rectangle', choices: [{ value: 'rectangle', label: 'Rectangle' }, { value: 'circle', label: 'Circle' }] },
      { id: 'sketchPlane', label: 'Sketch plane', description: '', kind: 'choice', required: true, defaultValue: 'xy', choices: [{ value: 'xy', label: 'XY' }] },
      positive('profileWidthMm', false, 10), positive('profileHeightMm', false, 10), positive('profileRadiusMm', false), positive('extrusionDepthMm'),
    ],
  }
  if (id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID) return {
    ...base,
    parameters: [
      { id: 'sketchPlane', label: 'Sketch plane', description: '', kind: 'choice', required: true, defaultValue: 'xy', choices: [{ value: 'xy', label: 'XY' }] },
      positive('profileWidthMm'), positive('profileHeightMm'), positive('cutDepthMm'),
    ],
  }
  if (id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID || id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID) return {
    ...base,
    parameters: [
      { id: 'seedFeatureId', label: 'Seed feature', description: '', kind: 'entity', required: true, defaultValue: null },
      { id: 'count', label: 'Pattern count', description: '', kind: 'integer', required: true, unit: 'count', minimum: 2, maximum: 256, step: 1, defaultValue: 2 },
      id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
        ? positive('spacingMm')
        : { id: 'angleDegrees', label: 'Sweep angle', description: '', kind: 'number' as const, required: true, unit: 'angle' as const, minimum: 0.000001, maximum: 360, defaultValue: 360 },
    ],
  }
  return {
    ...base,
    parameters: [
      positive('diameterMm'), positive('depthMm'),
      ...['xMm', 'yMm', 'zMm'].map((parameterId) => ({
        id: parameterId, label: parameterId, description: '', kind: 'number' as const, required: true, unit: 'length' as const,
        minimum: -1_000_000, maximum: 1_000_000, defaultValue: 0,
      })),
    ],
  }
}

function operationRequest(capabilityId: string, inputs: Record<string, PhotonCadInputValue>): PhotonCadOperationRequest {
  const project = persistedProject()
  return {
    contractVersion: 1,
    requestId: 'operation:industrial',
    sessionId: project.sessionId,
    projectId: project.projectId,
    baseRevision: project.revision,
    mode: 'scratch',
    capabilityId,
    inputs,
    targetEntityIds: [],
  }
}

const exactSpurGearInputs: Record<string, PhotonCadInputValue> = {
  module: 2,
  pressure_angle: 20,
  thickness: 10,
  tooth_count: 24,
  addendum: null,
  dedendum: null,
  root_fillet: null,
}

const identityTransform = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] as const

function assemblyCapability(overrides: Partial<PhotonCadCapability> = {}): PhotonCadCapability {
  return {
    id: PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID,
    backend: 'assembly',
    category: 'Assembly',
    title: 'Place occurrence',
    description: 'Place one existing part occurrence.',
    operation: 'assemble',
    parameters: [
      { id: 'sourceEntityId', label: 'Source part', description: '', kind: 'entity', required: true, defaultValue: null },
      { id: 'parentOccurrenceId', label: 'Parent occurrence', description: '', kind: 'text', required: false, defaultValue: null },
      { id: 'translation', label: 'Translation', description: '', kind: 'vector3', required: true, unit: 'length', minimum: -1_000_000, maximum: 1_000_000, defaultValue: { x: 0, y: 0, z: 0 } },
      { id: 'rotationDegrees', label: 'Rotation', description: '', kind: 'vector3', required: true, unit: 'angle', minimum: -360, maximum: 360, defaultValue: { x: 0, y: 0, z: 0 } },
    ],
    source: { package: 'photon-industrial', version: '1.0.0', digest: industrialDigest, license: 'local-engineering-only' },
    previewSupported: true,
    experimental: false,
    ...overrides,
  }
}

function assemblyRemoveCapability(overrides: Partial<PhotonCadCapability> = {}): PhotonCadCapability {
  return {
    id: PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID,
    backend: 'assembly',
    category: 'Assembly',
    title: 'Remove occurrence',
    description: 'Remove the selected occurrence and descendants while retaining source parts.',
    operation: 'assemble',
    parameters: [],
    source: { package: 'photon-industrial', version: '1.0.0', digest: industrialDigest, license: 'local-engineering-only' },
    previewSupported: true,
    experimental: false,
    ...overrides,
  }
}

function assemblyTransformCapability(overrides: Partial<PhotonCadCapability> = {}): PhotonCadCapability {
  return {
    id: PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID,
    backend: 'assembly',
    category: 'Assembly',
    title: 'Move occurrence',
    description: 'Replace the selected occurrence rigid transform.',
    operation: 'assemble',
    parameters: [
      { id: 'translation', label: 'Translation', description: '', kind: 'vector3', required: true, unit: 'length', minimum: -1_000_000, maximum: 1_000_000, defaultValue: { x: 0, y: 0, z: 0 } },
      { id: 'rotationDegrees', label: 'Rotation', description: '', kind: 'vector3', required: true, unit: 'angle', minimum: -360, maximum: 360, defaultValue: { x: 0, y: 0, z: 0 } },
    ],
    source: { package: 'photon-industrial', version: '1.0.0', digest: industrialDigest, license: 'local-engineering-only' },
    previewSupported: true,
    experimental: false,
    ...overrides,
  }
}

function twoPartProject(): PhotonCadProjectSnapshot {
  return {
    ...persistedProject(),
    revision: 4,
    entities: [
      { id: 'part:bearing', parentId: null, kind: 'part', name: 'Bearing', visible: true, suppressed: false },
      { id: 'part:gear', parentId: null, kind: 'part', name: 'Spur Gear', visible: true, suppressed: false },
      { id: 'part:bearing.occ', parentId: null, kind: 'occurrence', name: 'BRG-008', visible: true, suppressed: false },
      { id: 'part:gear.occ', parentId: 'part:bearing.occ', kind: 'occurrence', name: 'GEAR-024', visible: true, suppressed: false },
    ],
    occurrences: [
      { occurrenceId: 'part:bearing.occ', parentOccurrenceId: null, sourceEntityId: 'part:bearing', partNumber: 'BRG-008', transform: identityTransform },
      { occurrenceId: 'part:gear.occ', parentOccurrenceId: 'part:bearing.occ', sourceEntityId: 'part:gear', partNumber: 'GEAR-024', transform: identityTransform },
    ],
  }
}

function assemblyRequest(project = twoPartProject()): PhotonCadOperationRequest {
  return {
    contractVersion: 1,
    requestId: 'operation:assembly:nested',
    sessionId: project.sessionId,
    projectId: project.projectId,
    baseRevision: project.revision,
    mode: 'scratch',
    capabilityId: PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID,
    inputs: {
      sourceEntityId: 'part:gear',
      parentOccurrenceId: 'part:gear.occ',
      translation: { x: 10, y: 20, z: 30 },
      rotationDegrees: { x: 0, y: 0, z: 90 },
    },
    targetEntityIds: [],
  }
}

describe('PhotonCadWorkspace', () => {
  it('authorizes exact host-described Spur Gear and real-shaped bearing inputs only', () => {
    const project = persistedProject()
    const gear = spurGearCapability()
    const gearRuntime = industrialRuntime(gear)
    const gearRequest = operationRequest(gear.id, exactSpurGearInputs)
    expect(photonCadCapabilityInputsMatch(gear, exactSpurGearInputs, project)).toBe(true)
    expect(photonCadAuthorizePersistedScratchRequest(gearRuntime, project, gearRequest)).toBe(
      photonCadCapabilityAuthorizationFingerprint(gearRuntime.catalog!.catalogRevision, gear),
    )

    const bearing: PhotonCadCapability = {
      ...spurGearCapability(),
      id: 'industrial.bearing.single-row-deep-groove.v1',
      category: 'Industrial bearings',
      title: 'Single Row Deep Groove Ball Bearing',
      parameters: [{
        id: 'size', label: 'Bearing size', description: '', kind: 'choice', required: true,
        choices: [{ value: 'M8-22-7', label: '8 x 22 x 7 mm' }, { value: 'M10-26-8', label: '10 x 26 x 8 mm' }],
      }],
    }
    const bearingInputs = { size: 'M8-22-7' }
    expect(photonCadCapabilityInputsMatch(bearing, bearingInputs, project)).toBe(true)
    expect(photonCadAuthorizePersistedScratchRequest(
      industrialRuntime(bearing), project, operationRequest(bearing.id, bearingInputs),
    )).not.toBeNull()
    expect(photonCadCapabilityInputsMatch(bearing, { size: 'not-in-host-catalog' }, project)).toBe(false)
  })

  it('authorizes exact manual add, cut, and hole transactions with body-bound modification targets', () => {
    const empty = persistedProject()
    const add = manualCapability(PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID)
    const rectangleInputs = photonCadInitialCapabilityInputs(add)
    const addRequest = operationRequest(add.id, rectangleInputs)
    expect(rectangleInputs).toMatchObject({
      profileKind: 'rectangle', profileWidthMm: 10, profileHeightMm: 10,
      profileRadiusMm: null, extrusionDepthMm: 10,
    })
    expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(add), empty, addRequest)).not.toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(add), empty, {
      ...addRequest,
      inputs: { ...rectangleInputs, profileKind: 'circle', profileWidthMm: null, profileHeightMm: null, profileRadiusMm: 5 },
    })).not.toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(add), empty, {
      ...addRequest,
      inputs: { ...rectangleInputs, profileRadiusMm: 5 },
    })).toBeNull()

    const body = { id: 'body:manual', parentId: null, kind: 'body' as const, name: 'Manual body', visible: true, suppressed: false }
    const project = { ...empty, revision: 2, entities: [body] }
    for (const capability of [manualCapability(PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID), manualCapability(PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID)]) {
      const request: PhotonCadOperationRequest = {
        ...operationRequest(capability.id, photonCadInitialCapabilityInputs(capability)),
        baseRevision: project.revision,
        targetEntityIds: [body.id],
      }
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, request)).not.toBeNull()
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, { ...request, targetEntityIds: [] })).toBeNull()
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, { ...request, targetEntityIds: ['body:foreign'] })).toBeNull()
      const accepted = {
        contractVersion: 1 as const,
        requestId: request.requestId,
        projectId: request.projectId,
        baseRevision: request.baseRevision,
        resultingRevision: request.baseRevision + 2,
        status: 'accepted' as const,
        stale: false,
        reason: 'accepted-and-saved',
        snapshot: { ...project, revision: request.baseRevision + 2 },
        preview: {
          previewId: 'preview:manual', projectId: request.projectId, revision: request.baseRevision + 2,
          contentDigest: 'b'.repeat(64), units: 'millimeter' as const,
          bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 10, y: 10, z: 10 } }, entityCount: 1,
        },
        issues: [],
      }
      expect(photonCadAcceptedPersistedScratchCommit(request, accepted, true)?.snapshot.revision).toBe(request.baseRevision + 2)
      expect(photonCadAcceptedPersistedScratchCommit({ ...request, targetEntityIds: [] }, accepted, true)).toBeNull()
    }
  })

  it('authorizes exact manual patterns only from a durable cut or hole seed on the selected solid', () => {
    const body = { id: 'body:manual', parentId: null, kind: 'body' as const, name: 'Manual body', visible: true, suppressed: false }
    const cut = { id: 'feature:cut', parentId: body.id, kind: 'datum' as const, name: 'Sketch cut feature', visible: true, suppressed: false, sourceCapabilityId: PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID }
    const hole = { id: 'feature:hole', parentId: body.id, kind: 'datum' as const, name: 'Hole feature', visible: true, suppressed: false, sourceCapabilityId: PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID }
    const project = { ...persistedProject(), revision: 6, entities: [body, cut, hole] }
    for (const [capabilityId, seedFeatureId] of [
      [PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID, cut.id],
      [PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID, hole.id],
    ] as const) {
      const capability = manualCapability(capabilityId)
      const request: PhotonCadOperationRequest = {
        ...operationRequest(capability.id, { ...photonCadInitialCapabilityInputs(capability), seedFeatureId }),
        baseRevision: project.revision,
        targetEntityIds: [body.id],
      }
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, request)).not.toBeNull()
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, {
        ...request, inputs: { ...request.inputs, seedFeatureId: body.id },
      })).toBeNull()
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, {
        ...request, inputs: { ...request.inputs, seedFeatureId: 'feature:foreign' },
      })).toBeNull()
      expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(capability), project, {
        ...request, targetEntityIds: [],
      })).toBeNull()
    }
  })

  it('rejects arbitrary capabilities, unsafe schemas, hostile values, and non-persisted contexts', () => {
    const project = persistedProject()
    const gear = spurGearCapability()
    const runtime = industrialRuntime(gear)
    const authorize = (inputs: Record<string, PhotonCadInputValue>, request: Partial<PhotonCadOperationRequest> = {}, current = project) =>
      photonCadAuthorizePersistedScratchRequest(runtime, current, { ...operationRequest(gear.id, inputs), ...request })

    expect(authorize({ ...exactSpurGearInputs, unexpected: 1 })).toBeNull()
    expect(authorize({ ...exactSpurGearInputs, module: '2' })).toBeNull()
    expect(authorize({ ...exactSpurGearInputs, module: Number.NaN })).toBeNull()
    expect(authorize({ ...exactSpurGearInputs, module: 0.01 })).toBeNull()
    expect(authorize({ ...exactSpurGearInputs, tooth_count: 24.5 })).toBeNull()
    expect(authorize(exactSpurGearInputs, { capabilityId: 'industrial.arbitrary.v1' })).toBeNull()
    expect(authorize(exactSpurGearInputs, { mode: 'suggest' })).toBeNull()
    expect(authorize(exactSpurGearInputs, { targetEntityIds: ['part:foreign'] })).toBeNull()
    expect(authorize(exactSpurGearInputs, {}, { ...project, dirty: true })).toBeNull()
    expect(authorize(exactSpurGearInputs, {}, { ...project, units: 'inch' })).toBeNull()
    for (const rejected of [
      spurGearCapability({ experimental: true }),
      spurGearCapability({ previewSupported: false }),
      spurGearCapability({ operation: 'modify' }),
    ]) {
      expect(photonCadAuthorizePersistedScratchRequest(
        industrialRuntime(rejected), project, operationRequest(rejected.id, exactSpurGearInputs),
      )).toBeNull()
    }
  })

  it('binds authorization to catalog revision and schema and accepts only exact +2 results', () => {
    const project = persistedProject()
    const gear = spurGearCapability()
    const request = operationRequest(gear.id, exactSpurGearInputs)
    const first = photonCadCapabilityAuthorizationFingerprint('catalog:1', gear)
    const nextRevision = photonCadCapabilityAuthorizationFingerprint('catalog:2', gear)
    const nextSchema = photonCadCapabilityAuthorizationFingerprint('catalog:1', spurGearCapability({
      parameters: gear.parameters.map((parameter) => parameter.id === 'module' ? { ...parameter, maximum: 50 } : parameter),
    }))
    const nextSource = photonCadCapabilityAuthorizationFingerprint('catalog:1', spurGearCapability({
      source: { ...gear.source, digest: 'f'.repeat(64) },
    }))
    expect(nextRevision).not.toBe(first)
    expect(nextSchema).not.toBe(first)
    expect(nextSource).not.toBe(first)
    expect(photonCadInitialCapabilityInputs(gear)).toEqual({
      module: null,
      pressure_angle: null,
      thickness: null,
      tooth_count: null,
      addendum: null,
      dedendum: null,
      root_fillet: null,
    })

    const accepted = {
      contractVersion: 1 as const,
      requestId: request.requestId,
      projectId: request.projectId,
      baseRevision: 0,
      resultingRevision: 2,
      status: 'accepted' as const,
      stale: false,
      reason: 'accepted-and-saved',
      snapshot: { ...project, revision: 2 },
      preview: {
        previewId: 'preview:accepted',
        projectId: request.projectId,
        revision: 2,
        contentDigest: 'b'.repeat(64),
        units: 'millimeter' as const,
        bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 10, y: 10, z: 10 } },
        entityCount: 1,
      },
      issues: [],
    }
    expect(photonCadAcceptedPersistedScratchSnapshot(request, accepted, true)?.revision).toBe(2)
    expect(photonCadAcceptedPersistedScratchSnapshot(request, { ...accepted, resultingRevision: 1, snapshot: { ...project, revision: 1 } }, true)).toBeNull()
    expect(photonCadAcceptedPersistedScratchSnapshot(request, accepted, false)).toBeNull()
    const dispatchContinuation = photonCadOperationContinuationAuthorizationBinding(first, project, true)
    const parentAdvancedToAccepted = { ...project, revision: 2 }
    expect(photonCadOperationContinuationAuthorizationBinding(first, parentAdvancedToAccepted, true)).toBe(dispatchContinuation)
    expect(photonCadAcceptedPersistedScratchCommit(request, accepted, true)).toEqual({
      snapshot: accepted.snapshot,
      preview: accepted.preview,
    })
    expect(photonCadOperationContinuationAuthorizationBinding(nextRevision, parentAdvancedToAccepted, true)).not.toBe(dispatchContinuation)
    expect(photonCadOperationContinuationAuthorizationBinding(nextSchema, parentAdvancedToAccepted, true)).not.toBe(dispatchContinuation)
    expect(photonCadOperationContinuationAuthorizationBinding(nextSource, parentAdvancedToAccepted, true)).not.toBe(dispatchContinuation)
    expect(photonCadOperationContinuationAuthorizationBinding(first, {
      ...parentAdvancedToAccepted,
      sessionId: 'session:foreign',
      projectId: 'project:foreign',
    }, true)).not.toBe(dispatchContinuation)
    expect(photonCadOperationContinuationAuthorizationBinding(first, parentAdvancedToAccepted, false)).toBe('')
    expect(photonCadAcceptedPersistedScratchCommit(request, { ...accepted, preview: { ...accepted.preview, projectId: 'project:foreign' } }, true)).toBeNull()
  })

  it('authorizes only the exact assembly placement schema and accepts one nested rotated occurrence at +2', () => {
    const project = twoPartProject()
    const capability = assemblyCapability()
    const runtime = industrialRuntime(capability, 'catalog:assembly:1')
    const request = assemblyRequest(project)
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, request)).toBe(
      photonCadCapabilityAuthorizationFingerprint('catalog:assembly:1', capability),
    )
    const transform = photonCadAssemblyRigidTransform(
      request.inputs.translation as { x: number; y: number; z: number },
      request.inputs.rotationDegrees as { x: number; y: number; z: number },
    )
    const nested = {
      occurrenceId: 'occurrence:gear:nested',
      parentOccurrenceId: 'part:gear.occ',
      sourceEntityId: 'part:gear',
      partNumber: 'GEAR-024',
      transform,
    }
    const nestedEntity = { id: nested.occurrenceId, parentId: nested.parentOccurrenceId, kind: 'occurrence' as const, name: nested.partNumber, visible: true, suppressed: false }
    const snapshot = { ...project, revision: 6, entities: [...project.entities, nestedEntity], occurrences: [...project.occurrences!, nested] }
    const accepted = {
      contractVersion: 1 as const,
      requestId: request.requestId,
      projectId: request.projectId,
      baseRevision: 4,
      resultingRevision: 6,
      status: 'accepted' as const,
      stale: false,
      reason: 'accepted-and-saved',
      snapshot,
      preview: {
        previewId: 'preview:6', projectId: request.projectId, revision: 6, contentDigest: 'b'.repeat(64), units: 'millimeter' as const,
        bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 100, y: 100, z: 100 } }, entityCount: 3,
      },
      issues: [],
    }
    const priorPreview = { ...accepted.preview, previewId: 'preview:4', revision: 4, contentDigest: 'a'.repeat(64), entityCount: 2 }
    expect(photonCadAcceptedAssemblySnapshot(request, accepted, project, priorPreview)?.occurrences?.[2]).toEqual(nested)
    expect(transform[0]).toBeCloseTo(0, 12)
    expect(transform[1]).toBeCloseTo(-1, 12)
    expect(transform[3]).toBe(10)
    expect(transform[4]).toBeCloseTo(1, 12)
    expect(transform[5]).toBeCloseTo(0, 12)
    expect(transform[7]).toBe(20)
    expect(transform[11]).toBe(30)

    for (const hostile of [
      { ...accepted, stale: true },
      { ...accepted, resultingRevision: 5, snapshot: { ...snapshot, revision: 5 }, preview: { ...accepted.preview, revision: 5 } },
      { ...accepted, preview: { ...accepted.preview, contentDigest: priorPreview.contentDigest } },
      { ...accepted, snapshot: { ...snapshot, occurrences: [...project.occurrences!, { ...nested, sourceEntityId: 'part:foreign' }] } },
      { ...accepted, snapshot: { ...snapshot, occurrences: [...project.occurrences!, { ...nested, parentOccurrenceId: 'part:foreign.occ' }] } },
    ]) expect(photonCadAcceptedAssemblySnapshot(request, hostile, project, priorPreview)).toBeNull()

    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, capabilityId: 'assembly.arbitrary.v1' })).toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, inputs: { ...request.inputs, sourceEntityId: 'part:foreign' } })).toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, inputs: { ...request.inputs, parentOccurrenceId: 'part:foreign.occ' } })).toBeNull()
    for (const rejected of [
      assemblyCapability({ experimental: true }),
      assemblyCapability({ previewSupported: false }),
      assemblyCapability({ operation: 'create' }),
      assemblyCapability({ parameters: capability.parameters.map((parameter) => parameter.id === 'rotationDegrees' ? { ...parameter, maximum: 180 } : parameter) }),
    ]) expect(photonCadAuthorizePersistedScratchRequest(industrialRuntime(rejected), project, request)).toBeNull()
  })

  it('renders exact host-declared assembly controls without offering pseudo-occurrences as source parts', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspace
        controller={{} as PhotonCadController}
        runtime={industrialRuntime(assemblyCapability())}
        project={twoPartProject()}
        initialStage="design"
        persistedScratchAuthorized
      />,
    )
    expect(markup).toContain('data-operation-access="enabled"')
    expect(markup).toContain('Run persisted scratch operation')
    expect(markup).toContain('Source part')
    expect(markup).toContain('Parent occurrence')
    expect(markup).toContain('Translation X')
    expect(markup).toContain('Rotation Z')
    expect(markup).toContain('value="part:gear"')
    expect(markup).not.toContain('value="part:gear.occ"')
  })

  it('authorizes one selected occurrence and accepts only its exact +2 rigid-transform replacement', () => {
    const project = twoPartProject()
    const capability = assemblyTransformCapability()
    const runtime = industrialRuntime(capability, 'catalog:assembly:transform:1')
    const request: PhotonCadOperationRequest = {
      contractVersion: 1,
      requestId: 'operation:assembly:transform',
      sessionId: project.sessionId,
      projectId: project.projectId,
      baseRevision: project.revision,
      mode: 'scratch',
      capabilityId: capability.id,
      inputs: { translation: { x: 2, y: 3, z: 4 }, rotationDegrees: { x: 0, y: 180, z: 0 } },
      targetEntityIds: ['part:gear.occ'],
    }
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, request)).toBe(
      photonCadCapabilityAuthorizationFingerprint('catalog:assembly:transform:1', capability),
    )
    const expectedTransform = photonCadAssemblyRigidTransform(
      request.inputs.translation as { x: number; y: number; z: number },
      request.inputs.rotationDegrees as { x: number; y: number; z: number },
    )
    const snapshot: PhotonCadProjectSnapshot = {
      ...project,
      revision: 6,
      occurrences: project.occurrences!.map((occurrence) => occurrence.occurrenceId === 'part:gear.occ'
        ? { ...occurrence, transform: expectedTransform }
        : occurrence),
    }
    const previousPreview = {
      previewId: 'preview:4', projectId: project.projectId, revision: 4, contentDigest: 'a'.repeat(64), units: 'millimeter' as const,
      bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 20, y: 20, z: 20 } }, entityCount: 2,
    }
    const accepted = {
      contractVersion: 1 as const,
      requestId: request.requestId,
      projectId: request.projectId,
      baseRevision: request.baseRevision,
      resultingRevision: 6,
      status: 'accepted' as const,
      stale: false,
      reason: 'accepted-and-saved',
      snapshot,
      preview: { ...previousPreview, previewId: 'preview:6', revision: 6, contentDigest: 'b'.repeat(64) },
      issues: [],
    }
    expect(photonCadAcceptedAssemblySnapshot(request, accepted, project, previousPreview)).toEqual(snapshot)
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, targetEntityIds: [] })).toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, targetEntityIds: ['part:gear'] })).toBeNull()
    expect(photonCadAcceptedAssemblySnapshot(request, {
      ...accepted,
      snapshot: { ...snapshot, occurrences: snapshot.occurrences!.map((occurrence) => occurrence.occurrenceId === 'part:bearing.occ'
        ? { ...occurrence, transform: expectedTransform }
        : occurrence) },
    }, project, previousPreview)).toBeNull()
  })

  it('authorizes one selected occurrence and accepts exact subtree removal at +2', () => {
    const project = twoPartProject()
    const capability = assemblyRemoveCapability()
    const runtime = industrialRuntime(capability, 'catalog:assembly:remove:1')
    const request: PhotonCadOperationRequest = {
      contractVersion: 1,
      requestId: 'operation:assembly:remove',
      sessionId: project.sessionId,
      projectId: project.projectId,
      baseRevision: project.revision,
      mode: 'scratch',
      capabilityId: capability.id,
      inputs: {},
      targetEntityIds: ['part:gear.occ'],
    }
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, request)).toBe(
      photonCadCapabilityAuthorizationFingerprint('catalog:assembly:remove:1', capability),
    )
    const snapshot: PhotonCadProjectSnapshot = {
      ...project,
      revision: 6,
      entities: project.entities.filter((entity) => entity.id !== 'part:gear.occ'),
      occurrences: project.occurrences!.filter((occurrence) => occurrence.occurrenceId !== 'part:gear.occ'),
    }
    const previousPreview = {
      previewId: 'preview:4', projectId: project.projectId, revision: 4, contentDigest: 'a'.repeat(64), units: 'millimeter' as const,
      bounds: { minimum: { x: 0, y: 0, z: 0 }, maximum: { x: 20, y: 20, z: 20 } }, entityCount: 2,
    }
    const accepted = {
      contractVersion: 1 as const,
      requestId: request.requestId,
      projectId: request.projectId,
      baseRevision: request.baseRevision,
      resultingRevision: 6,
      status: 'accepted' as const,
      stale: false,
      reason: 'accepted-and-saved',
      snapshot,
      preview: { ...previousPreview, previewId: 'preview:6', revision: 6, contentDigest: 'b'.repeat(64), entityCount: 1 },
      issues: [],
    }
    expect(photonCadAcceptedAssemblySnapshot(request, accepted, project, previousPreview)).toEqual(snapshot)
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, targetEntityIds: [] })).toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, targetEntityIds: ['part:gear'] })).toBeNull()
    expect(photonCadAuthorizePersistedScratchRequest(runtime, project, { ...request, targetEntityIds: ['part:bearing.occ'] })).toBeNull()
    expect(photonCadAcceptedAssemblySnapshot(request, { ...accepted, preview: { ...accepted.preview, contentDigest: previousPreview.contentDigest } }, project, previousPreview)).toBeNull()
    expect(photonCadAcceptedAssemblySnapshot(request, {
      ...accepted,
      snapshot: { ...snapshot, entities: [...snapshot.entities, project.entities.find((entity) => entity.id === 'part:gear.occ')!] },
    }, project, previousPreview)).toBeNull()
  })

  it('labels the explicit persisted-scratch boundary without enabling an unattached workspace', () => {
    const runtime = industrialRuntime(spurGearCapability())
    const project = persistedProject()
    const controller = {} as PhotonCadController
    const locked = renderToStaticMarkup(
      <PhotonCadWorkspace controller={controller} runtime={runtime} project={project} initialStage="design" />,
    )
    const authorized = renderToStaticMarkup(
      <PhotonCadWorkspace
        controller={controller}
        runtime={runtime}
        project={project}
        initialStage="design"
        persistedScratchAuthorized
      />,
    )
    expect(locked).toContain('data-operation-access="locked"')
    expect(locked).toContain('Prepare suggestion')
    expect(authorized).toContain('data-operation-access="enabled"')
    expect(authorized).toContain('Run persisted scratch operation')
    expect(authorized).toContain('exact host-described scratch operation will be persisted')
    expect(authorized).toContain('<dt>Targets</dt><dd>New part</dd>')
    for (const rejected of [
      spurGearCapability({ experimental: true }),
      spurGearCapability({ previewSupported: false }),
      spurGearCapability({ operation: 'modify' }),
    ]) {
      const rejectedMarkup = renderToStaticMarkup(
        <PhotonCadWorkspace
          controller={controller}
          runtime={industrialRuntime(rejected)}
          project={project}
          initialStage="design"
          persistedScratchAuthorized
        />,
      )
      expect(rejectedMarkup).toContain('data-operation-access="locked"')
      expect(rejectedMarkup).not.toContain('Run persisted scratch operation')
    }
  })

  it('uses the shell project tab as the only document identity row while retaining workflow tabs', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture />)

    expect(markup).not.toContain('aria-label="CAD documents"')
    expect(markup).not.toContain('aria-label="CAD document location"')
    expect(markup).toContain('aria-label="Conveyor Drive Gearbox CAD workspace"')
    expect(markup).toContain('aria-label="CAD workflow"')
    expect(markup).toContain('role="tabpanel"')
    expect(markup).toContain('aria-selected="true"')
    expect(markup).toContain('Conveyor Drive Gearbox')
    expect(markup).toContain('Library')
    expect(markup).toContain('Design')
    expect(markup).toContain('Assemble')
    expect(markup).toContain('Verify')
    expect(markup).toContain('Release')
    expect(markup).not.toContain('Activity rail')
    expect(markup).not.toContain('Photon agent')
    expect(markup).toContain('aria-label="Resize parts and context pane"')
    expect(markup).toContain('aria-label="Resize parameter inspector"')
    expect(markup).toContain('aria-label="CAD work area visibility"')
  })

  it('renders compact expandable categories, human parameters, units, and declared provenance honestly', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="library" />)

    expect(markup).toContain('Parts library')
    expect(markup).toContain('Part library categories')
    expect(markup).toContain('Primitives')
    expect(markup).toContain('<details')
    expect(markup).toContain('Build123d generator')
    expect(markup).not.toContain('Catalog coverage')
    expect(markup).not.toContain('Catalog scope')
    expect(markup).toContain('Precision box')
    expect(markup).toContain('Length')
    expect(markup).toContain('Overall X dimension.')
    expect(markup).toContain('mm')
    expect(markup).toContain('Declared provenance')
    expect(markup).toContain('build123d')
    expect(markup).toContain('Apache-2.0')
    expect(markup).toContain('does not independently attest')
  })

  it('groups source-backed parts separately from assembly tools and exposes reusable project definitions', () => {
    const gear = spurGearCapability()
    const primitive: PhotonCadCapability = { ...gear, id: 'geometry.box.create.v1', backend: 'geometry', category: 'Primitive solids', title: 'Box' }
    const bearing: PhotonCadCapability = { ...gear, id: 'industrial.bearing.deep-groove.v1', category: 'Industrial bearings', title: 'Deep Groove Ball Bearing' }
    const fastener: PhotonCadCapability = { ...gear, id: 'industrial.fastener.socket-head.v1', category: 'Industrial fasteners', title: 'Socket Head Cap Screw' }
    const assembly = assemblyCapability()

    expect(photonCadCatalogSections([assembly, bearing, primitive, gear, fastener]).map((section) => [section.id, section.label, section.library, section.capabilities.length])).toEqual([
      ['build123d-design', 'Build123d design', 'build123d', 1],
      ['bearings', 'Bearings', 'bd-warehouse', 1],
      ['gears', 'Gears', 'bd-warehouse', 1],
      ['fasteners', 'Fasteners', 'bd-warehouse', 1],
      ['assembly-tools', 'Assembly tools', 'assembly', 1],
    ])
    expect(photonCadProjectParts(twoPartProject())).toEqual([
      { id: 'part:bearing', name: 'Bearing', kind: 'part', occurrenceCount: 1 },
      { id: 'part:gear', name: 'Spur Gear', kind: 'part', occurrenceCount: 1 },
    ])
    expect(photonCadProjectParts(twoPartProject(), 'gear')).toEqual([
      { id: 'part:gear', name: 'Spur Gear', kind: 'part', occurrenceCount: 1 },
    ])

    const markup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={industrialRuntime(gear)} project={twoPartProject()} initialStage="library" />,
    )
    expect(markup).toContain('Project parts')
    expect(markup).toContain('Reusable definitions already sealed in this project')
    expect(markup).toContain('Bearing')
    expect(markup).toContain('1 placed')
    expect(markup).toContain('BD Warehouse')
    expect(markup).toContain('Gears')
  })

  it('presents verified Build123d constructors directly in the Design drawer', () => {
    const add = manualCapability(PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID)
    const cut = manualCapability(PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID)
    const hole = manualCapability(PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID)
    const runtime: PhotonCadRuntimeDescription = {
      ...industrialRuntime(add),
      catalog: {
        ...industrialRuntime(add).catalog!,
        capabilities: [add, cut, hole],
        coverage: { discovered: 3, available: 3, unavailable: 0, unavailableReasons: [] },
      },
    }
    const markup = renderToStaticMarkup(<PhotonCadWorkspace runtime={runtime} project={persistedProject()} initialStage="design" />)

    expect(markup).toContain('Manual solid tools')
    expect(markup).toContain('Build123d solid constructors')
    expect(markup).toContain('Create or modify an exact parametric B-rep solid')
    expect(markup).toContain('Sketch + extrude')
    expect(markup).toContain('Sketch cut')
    expect(markup).toContain('Hole')
  })

  it('enters Design on a verified manual constructor instead of retaining a library part', () => {
    const gear = spurGearCapability()
    const box: PhotonCadCapability = {
      ...gear,
      id: 'geometry.box.create.v1',
      backend: 'geometry',
      category: 'Primitive solids',
      title: 'Box',
      source: { ...gear.source, package: 'build123d' },
    }
    const cylinder: PhotonCadCapability = { ...box, id: 'geometry.cylinder.create.v1', title: 'Cylinder' }

    expect(photonCadManualDesignCapabilityId([gear, box, cylinder], gear.id)).toBe(box.id)
    expect(photonCadManualDesignCapabilityId([gear, box, cylinder], cylinder.id)).toBe(cylinder.id)
    const cut = manualCapability(PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID)
    expect(photonCadManualDesignCapabilityId([gear, box, cut], cut.id)).toBe(cut.id)
    expect(photonCadManualDesignCapabilityId([gear], gear.id)).toBe('')
  })

  it('bounds large library rendering and offers the next catalog page', () => {
    const capabilities = Array.from({ length: 105 }, (_, index) => spurGearCapability({
      id: `bdw_${String(index).padStart(48, '0')}`,
      title: `Verified gear ${index + 1}`,
    }))
    const runtime: PhotonCadRuntimeDescription = {
      ...industrialRuntime(capabilities[0]),
      catalog: {
        contractVersion: 1,
        catalogRevision: 'catalog:large:1',
        generatedAtUtc: '2026-08-11T10:00:00Z',
        capabilities,
        coverage: { discovered: capabilities.length, available: capabilities.length, unavailable: 0, unavailableReasons: [] },
      },
    }
    const markup = renderToStaticMarkup(<PhotonCadWorkspace runtime={runtime} project={persistedProject()} initialStage="library" />)

    expect(markup).toContain('Show 5 more')
    expect(markup).toContain('5 remaining')
    expect(markup).toContain('Verified gear 100')
    expect(markup).not.toContain('Verified gear 101')
  })

  it('persists bounded CAD work-area widths and supports keyboard separators', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value) },
    }
    const layout = { contextWidth: 344, inspectorWidth: 416, contextCollapsed: true, inspectorCollapsed: false }

    expect(savePhotonCadPaneLayout(layout, storage)).toBe(true)
    expect(loadPhotonCadPaneLayout(storage)).toEqual(layout)
    expect(photonCadKeyboardPaneWidth('context', 260, 'ArrowRight')).toBe(276)
    expect(photonCadKeyboardPaneWidth('inspector', 300, 'ArrowLeft', true)).toBe(348)
    expect(photonCadKeyboardPaneWidth('inspector', 300, 'Enter')).toBeNull()
  })

  it('keeps the viewer boundary injectable and makes missing evidence unmistakable', () => {
    const emptyMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="design" />)
    const injectedMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        previewSurface={<div aria-label="Injected bounded preview">Injected renderer boundary</div>}
        evidenceMode="fixture"
      />,
    )

    expect(emptyMarkup).toContain('aria-label="3D preview"')
    expect(emptyMarkup).toContain('No geometry renderer connected')
    expect(emptyMarkup).toContain('Preview visibility is never treated as a passing check')
    expect(injectedMarkup).toContain('Injected renderer boundary')
    expect(injectedMarkup).not.toContain('No geometry renderer connected')
    expect(injectedMarkup).toContain('No preview receipt')
  })

  it('runs only mounted verification checks by default and marks interference unavailable', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="verify" />)

    expect(markup).toContain('4 selected')
    expect(markup).toContain('Interference — unavailable')
    expect(markup).toContain('Not mounted yet; overlapping-occurrence analysis remains unavailable')
  })

  it('makes the real selected-part generic STEP exporter reachable from Release', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="release" />)

    expect(markup).toContain('Portable STEP source')
    expect(markup).toContain('Generic Part 21')
    expect(markup).toContain('Select exactly one saved Body or Part')
    expect(markup).toContain('Model tree')
    expect(markup).toContain('Export selected STEP')
    expect(markup).toContain('BOM files and quote/invoice output are not mounted yet')
    expect(markup).not.toContain('>Quote / invoice<')
    expect(markup).not.toContain('STEP AP242')
    expect(markup).not.toContain('STEP AP214')
  })

  it('limits autonomous language to scratch drafting and never advertises a canonical write', () => {
    const scratchMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="design" />)
    const canonicalMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={{ ...photonCadWorkspaceFixtureProject, mode: 'canonical', dirty: false }}
        initialStage="design"
      />,
    )

    expect(scratchMarkup).toContain('Scratch workspace')
    expect(scratchMarkup).toContain('Autonomy stays inside scratch drafting')
    expect(scratchMarkup).toContain('Run scratch draft')
    expect(canonicalMarkup).toContain('Canonical workspace')
    expect(canonicalMarkup).toContain('Canonical designs accept suggestions only')
    expect(canonicalMarkup).toContain('Prepare suggestion')
    expect(canonicalMarkup).not.toContain('Apply automatically')
  })

  it('renders assembly context and a supplied BOM without inventing missing rows', () => {
    const populated = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="assemble" />)
    const empty = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={[]}
        initialStage="assemble"
      />,
    )
    const industrialPartNumber = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={[{ partNumber: 'AUGER / FLIGHT 12 GA', description: 'Formed flight section', quantity: 1, unit: 'each', sourceEntityId: 'part-auger-flight' }]}
        initialStage="assemble"
      />,
    )

    expect(populated).toContain('Assembly context')
    expect(populated).toContain('Bill of materials')
    expect(populated).toContain('Part number')
    expect(populated).toContain('GBX-110')
    expect(populated).toContain('6205-2RS')
    expect(populated).toContain('5 discrete parts')
    expect(empty).toContain('No BOM evidence')
    expect(empty).toContain('does not invent missing rows')
    expect(industrialPartNumber).toContain('AUGER / FLIGHT 12 GA')
  })

  it('separates preview, verification, and release evidence', () => {
    const verifyMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="verify" />)
    const releaseMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="release" />)

    expect(verifyMarkup).toContain('Verification plan')
    expect(verifyMarkup).toContain('Valid solids')
    expect(verifyMarkup).toContain('Assembly structure')
    expect(verifyMarkup).toContain('No verification result')
    expect(verifyMarkup).toContain('Passing status is never inferred')
    expect(verifyMarkup).toContain('Preview answers')
    expect(releaseMarkup).toContain('Canonical source export')
    expect(releaseMarkup).toContain('Select exactly one saved Body or Part')
    expect(releaseMarkup).toContain('STEP is the portable source of truth.')
    expect(releaseMarkup).toContain('Autodesk Inventor is an optional later bridge')
    expect(releaseMarkup).toContain('Export selected STEP')
    expect(releaseMarkup).not.toContain('Prepare review')
    expect(releaseMarkup).not.toContain('Create package')
    expect(releaseMarkup).not.toContain('Package plan ready')
  })

  it('offers reviewed BOM exports in XLSX, CSV, and PDF without claiming a file exists', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspaceFixture initialStage="release" initialReleaseOutput="bom-export" />,
    )

    expect(markup).toContain('BOM export')
    expect(markup).toContain('Excel workbook')
    expect(markup).toContain('CSV data')
    expect(markup).toContain('PDF report')
    expect(markup).toContain('authoritative revision-bound digest')
    expect(markup).toContain('No BOM export review')
    expect(markup).toContain('Prepare export')
    expect(markup).toContain('Export reviewed BOM')
    expect(markup).not.toContain('BOM export plan ready')
  })

  it('makes quote and invoice output a preview, page review, approval, then output workflow', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspaceFixture initialStage="release" initialReleaseOutput="commercial-document" />,
    )

    expect(markup).toContain('Draft quote')
    expect(markup).toContain('Draft invoice')
    expect(markup).toContain('Your company')
    expect(markup).toContain('Customer')
    expect(markup).toContain('Line pricing')
    expect(markup).toContain('Add labor')
    expect(markup).toContain('Markup (%)')
    expect(markup).toContain('Discount (USD)')
    expect(markup).toContain('Tax (%)')
    expect(markup).toContain('Freight (USD)')
    expect(markup).toContain('Terms')
    expect(markup).toContain('Notes')
    expect(markup).toContain('Rendered all-page preview')
    expect(markup).toContain('No document preview')
    expect(markup).toContain('Approve exact preview')
    expect(markup).toContain('Create approved output')
    expect(markup).toContain('No email, accounting post, payment, charge, paid status, or delivery side effect')
    expect(markup).toContain('only after every rendered page is shown and a human explicitly approves')
    expect(markup.match(/disabled=""/gu)?.length ?? 0).toBeGreaterThanOrEqual(3)
  })

  it('marks fixture data and unavailable runtime states without claiming live success', () => {
    const fixtureMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture />)
    const unavailableRuntime: PhotonCadRuntimeDescription = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      status: 'unavailable',
      reason: 'unavailable',
    }
    const unavailableMarkup = renderToStaticMarkup(<PhotonCadWorkspace runtime={unavailableRuntime} project={null} />)

    expect(fixtureMarkup).toContain('Demonstration fixture')
    expect(fixtureMarkup).toContain('Fixture catalog')
    expect(fixtureMarkup).not.toContain('Runtime ready')
    expect(fixtureMarkup).toContain('No CAD runtime action, geometry render, verification pass, or release success is implied.')
    expect(unavailableMarkup).toContain('data-runtime="unavailable"')
    expect(unavailableMarkup).toContain('The verified CAD runtime is not installed')
    expect(unavailableMarkup).toContain('No catalog evidence')
    expect(unavailableMarkup).toContain('No geometry snapshot')
  })

  it('shows a live catalog in browse-only mode without exposing any persistence-implying action', () => {
    const liveRuntime: PhotonCadRuntimeDescription = { ...photonCadWorkspaceFixtureRuntime, status: 'available', reason: 'ready' }
    const libraryMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} />,
    )
    const verifyMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} initialStage="verify" />,
    )
    const releaseMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} initialStage="release" />,
    )
    const bomMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={liveRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        bomDigest={`sha256:${'d'.repeat(64)}`}
        initialStage="release"
        initialReleaseOutput="bom-export"
      />,
    )
    const commercialMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={liveRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        bomDigest={`sha256:${'d'.repeat(64)}`}
        initialStage="release"
        initialReleaseOutput="commercial-document"
      />,
    )

    expect(libraryMarkup).toContain('Catalog connected · editing locked')
    expect(libraryMarkup).toContain('Browse-only catalog')
    expect(libraryMarkup).toContain('data-operation-access="locked"')
    expect(libraryMarkup).not.toContain('Runtime ready')
    expect(libraryMarkup).toMatch(/<input[^>]*disabled=""[^>]*type="number"/u)
    expect(libraryMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Run scratch draft<\/button>/u)
    expect(verifyMarkup).toMatch(/<input[^>]*type="checkbox"[^>]*disabled=""/u)
    expect(verifyMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Run selected checks<\/button>/u)
    expect(releaseMarkup).toMatch(/<fieldset[^>]*class="pcad-release-output"[^>]*disabled=""/u)
    expect(releaseMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Export selected STEP<\/button>/u)
    expect(bomMarkup).toMatch(/<fieldset[^>]*class="pcad-format-list"[^>]*disabled=""/u)
    expect(commercialMarkup).toMatch(/<span>Document number<\/span><input disabled=""/u)
    expect(commercialMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Prepare preview<\/button>/u)
  })

  it('renders fixture exports from dynamic contract types', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        initialStage="assemble"
      />,
    )

    expect(photonCadWorkspaceFixtureRuntime.contractVersion).toBe(PHOTON_CAD_CONTRACT_VERSION)
    expect(photonCadWorkspaceFixtureProject.contractVersion).toBe(PHOTON_CAD_CONTRACT_VERSION)
    expect(markup).toContain('GBX-100 Gearbox')
  })

  it('keeps generated ARIA relationships unique and exposes one keyboard entry into the model tree', () => {
    const markup = renderToStaticMarkup(
      <>
        <PhotonCadWorkspaceFixture initialStage="design" />
        <PhotonCadWorkspaceFixture initialStage="verify" />
      </>,
    )
    const ids = [...markup.matchAll(/id="([^"]*pcad-(?:tab|panel)-[^"]+)"/gu)].map((match) => match[1])
    const controls = [...markup.matchAll(/aria-controls="([^"]+)"/gu)].map((match) => match[1])

    expect(ids.length).toBeGreaterThan(0)
    expect(new Set(ids).size).toBe(ids.length)
    expect(controls.every((id) => ids.includes(id))).toBe(true)
    expect(markup).toContain('role="tree"')
    expect(markup).toContain('aria-multiselectable="true"')
    expect(markup).toContain('role="treeitem"')
    expect(markup).toContain('tabindex="0"')
    expect(markup).toContain('tabindex="-1"')
  })
})
