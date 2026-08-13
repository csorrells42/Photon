import {
  AlertTriangle,
  BookOpen,
  Box,
  CheckCircle2,
  CircleAlert,
  DollarSign,
  Eye,
  FileText,
  Gauge,
  Layers3,
  LoaderCircle,
  PackageCheck,
  PanelLeftClose,
  PanelRightClose,
  Play,
  Plus,
  Printer,
  ReceiptText,
  RefreshCw,
  Search,
  ShieldCheck,
  SlidersHorizontal,
  Table2,
  Wrench,
} from 'lucide-react'
import {
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ChangeEvent,
  type KeyboardEvent,
  type PointerEvent,
  type ReactNode,
} from 'react'
import {
  PHOTON_CAD_CONTRACT_VERSION,
  PHOTON_CAD_LIMITS,
  isPhotonCadDigest,
  isPhotonCadIdentifier,
  isPhotonCadSafeText,
  normalizePhotonCadCatalog,
  photonCadDisplayText,
  photonCadReasonText,
  type PhotonCadBomRow,
  type PhotonCadCapability,
  type PhotonCadController,
  type PhotonCadInputValue,
  type PhotonCadIssue,
  type PhotonCadOperationRequest,
  type PhotonCadOperationResult,
  type PhotonCadParameterDefinition,
  type PhotonCadPreviewReceipt,
  type PhotonCadProjectSnapshot,
  type PhotonCadReleaseFormat,
  type PhotonCadReleaseReviewResult,
  type PhotonCadRuntimeDescription,
  type PhotonCadStepExportRequest,
  type PhotonCadStepExportResult,
  type PhotonCadVerificationRequest,
  type PhotonCadVerificationResult,
  type PhotonCadVector3,
} from './PhotonCadContract'
import {
  PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
  PHOTON_CAD_COMMERCIAL_INVARIANT,
  validatePhotonCadCommercialDraft,
  type PhotonCadBomExportFormat,
  type PhotonCadBomExportReviewResult,
  type PhotonCadCommercialApprovalResult,
  type PhotonCadCommercialController,
  type PhotonCadCommercialDraft,
  type PhotonCadCommercialParty,
  type PhotonCadCommercialPreviewPage,
  type PhotonCadCommercialReviewResult,
  type PhotonCadCommercialUnit,
} from './PhotonCadCommercialContract'
import './PhotonCadWorkspace.css'

export type PhotonCadWorkspaceStage = 'library' | 'design' | 'assemble' | 'verify' | 'release'

export type PhotonCadPreviewContext = {
  project: PhotonCadProjectSnapshot | null
  receipt: PhotonCadPreviewReceipt | null
  selectedEntityIds: readonly string[]
  stage: PhotonCadWorkspaceStage
  acceptHydratedReceipt: (receipt: PhotonCadPreviewReceipt) => boolean
  /**
   * The native preview uses this narrow callback for mouse-created rectangle
   * and circle profiles.  It keeps the same catalog authorization, project
   * revision, persistence, and preview-evidence checks as the inspector.
   */
  runManualSketch?: (capabilityId: string, inputs: Record<string, PhotonCadInputValue>, targetEntityIds?: readonly string[]) => Promise<boolean>
  manualSketchReady?: boolean
  operationBusy?: boolean
}

export type PhotonCadCommercialPreviewContext = {
  page: PhotonCadCommercialPreviewPage
  documentKind: PhotonCadCommercialDraft['documentKind']
  pageCount: number
}

export interface PhotonCadWorkspaceProps {
  controller?: PhotonCadController
  runtime?: PhotonCadRuntimeDescription
  project?: PhotonCadProjectSnapshot | null
  bom?: readonly PhotonCadBomRow[]
  verification?: PhotonCadVerificationResult | null
  previewSurface?: ReactNode | ((context: PhotonCadPreviewContext) => ReactNode)
  commercialController?: PhotonCadCommercialController
  bomDigest?: string
  initialCommercialDraft?: PhotonCadCommercialDraft
  commercialPreviewSurface?: (context: PhotonCadCommercialPreviewContext) => ReactNode
  destination?: { handle: string; label: string } | null
  onChooseDestination?: () => void
  printer?: { handle: string; label: string } | null
  onChoosePrinter?: () => void
  initialStage?: PhotonCadWorkspaceStage
  initialReleaseOutput?: PhotonCadReleaseOutput
  evidenceMode?: 'runtime' | 'fixture'
  persistedScratchAuthorized?: boolean
  projectContentDigest?: string
  className?: string
}

const stages = [
  { id: 'library', label: 'Library', detail: 'Find a proven operation', icon: BookOpen },
  { id: 'design', label: 'Design', detail: 'Shape parts by intent', icon: Box },
  { id: 'assemble', label: 'Assemble', detail: 'Structure and bill of materials', icon: Layers3 },
  { id: 'verify', label: 'Verify', detail: 'Run explicit checks', icon: ShieldCheck },
  { id: 'release', label: 'Release', detail: 'Export canonical STEP', icon: PackageCheck },
] as const

const verificationChecks: Array<{ id: PhotonCadVerificationRequest['checks'][number]; label: string; detail: string; available: boolean }> = [
  { id: 'valid-solids', label: 'Valid solids', detail: 'Closed, usable boundary representations', available: true },
  { id: 'interference', label: 'Interference', detail: 'Not mounted yet; overlapping-occurrence analysis remains unavailable', available: false },
  { id: 'dimensions', label: 'Dimensions', detail: 'Finite values and declared units', available: true },
  { id: 'assembly-structure', label: 'Assembly structure', detail: 'Resolvable parts, hierarchy, and transforms', available: true },
  { id: 'export-readiness', label: 'Export readiness', detail: 'Generic STEP prerequisites and supported project structure', available: true },
]

const releaseFormats: Array<{ id: PhotonCadReleaseFormat; label: string; detail: string }> = [
  { id: 'step-ap242', label: 'STEP AP242', detail: 'Preferred exact-BREP exchange package' },
  { id: 'step-ap214', label: 'STEP AP214', detail: 'Broad mechanical CAD interchange' },
  { id: 'stl', label: 'STL', detail: 'Manufacturing mesh derivative' },
  { id: 'dxf', label: 'DXF', detail: 'Planar drawing derivative' },
  { id: 'svg', label: 'SVG', detail: 'Portable drawing preview' },
]

export const PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID = 'assembly.occurrence.place.v1'
export const PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID = 'assembly.occurrence.transform.v1'
export const PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID = 'assembly.occurrence.remove.v1'
export const PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID = 'manual.solid.extrude.add.v1'
export const PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID = 'manual.solid.extrude.cut.v1'
export const PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID = 'manual.solid.mouse-sketch.add.v1'
export const PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID = 'manual.solid.mouse-sketch.cut.v1'
export const PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID = 'manual.solid.hole.cut.v1'
export const PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID = 'manual.feature.pattern.linear.v1'
export const PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID = 'manual.feature.pattern.circular.v1'

export type PhotonCadCatalogSection = {
  id: string
  label: string
  library: 'build123d' | 'bd-warehouse' | 'assembly' | 'other'
  capabilities: PhotonCadCapability[]
}

export type PhotonCadProjectPart = {
  id: string
  name: string
  kind: 'body' | 'part'
  occurrenceCount: number
}

export type PhotonCadPaneLayout = {
  contextWidth: number
  inspectorWidth: number
  contextCollapsed: boolean
  inspectorCollapsed: boolean
}

const PHOTON_CAD_PANE_LAYOUT_STORAGE_KEY = 'hermes-workbench.photon-cad-pane-layout.v1'
const PHOTON_CAD_PANE_BOUNDS = {
  context: { minimum: 160, maximum: 460, initial: 260 },
  inspector: { minimum: 190, maximum: 520, initial: 300 },
} as const
const DEFAULT_PHOTON_CAD_PANE_LAYOUT: PhotonCadPaneLayout = {
  contextWidth: PHOTON_CAD_PANE_BOUNDS.context.initial,
  inspectorWidth: PHOTON_CAD_PANE_BOUNDS.inspector.initial,
  contextCollapsed: false,
  inspectorCollapsed: false,
}
const PHOTON_CAD_CATALOG_PAGE_SIZE = 100

function normalizedCatalogSection(value: string) {
  const normalized = value.trim().toLocaleLowerCase()
    .replace(/^industrial\s+/, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
  return normalized || 'other-parts'
}

function catalogSectionId(capability: PhotonCadCapability): PhotonCadCatalogSection['id'] {
  if (capability.backend === 'geometry' || capability.source.package.toLocaleLowerCase().includes('build123d')) return 'build123d-design'
  if (capability.operation !== 'create') return 'assembly-tools'
  if (capability.source.package.toLocaleLowerCase().includes('bd-warehouse')) return normalizedCatalogSection(capability.category)
  return normalizedCatalogSection(capability.category || 'other-parts')
}

function catalogSectionLabel(id: string, capabilities: readonly PhotonCadCapability[]) {
  if (id === 'build123d-design') return 'Build123d design'
  if (id === 'assembly-tools') return 'Assembly tools'
  const category = capabilities[0]?.category.trim().replace(/^industrial\s+/i, '')
  if (category) return category.replace(/\b\w/g, (value) => value.toLocaleUpperCase())
  return id.split('-').filter(Boolean).map((value) => value[0]?.toLocaleUpperCase() + value.slice(1)).join(' ')
}

export function photonCadCatalogSections(capabilities: readonly PhotonCadCapability[]): PhotonCadCatalogSection[] {
  const buckets = new Map<string, PhotonCadCapability[]>()
  for (const capability of capabilities) {
    const id = catalogSectionId(capability)
    const bucket = buckets.get(id) ?? []
    bucket.push(capability)
    buckets.set(id, bucket)
  }
  const preferred = ['build123d-design', 'bearings', 'gears', 'fasteners', 'flanges', 'openbuilds', 'pipes', 'sprockets', 'threads', 'other-parts', 'assembly-tools']
  return [...buckets.entries()]
    .map(([id, items]) => ({
      id,
      label: catalogSectionLabel(id, items),
      library: id === 'build123d-design' ? 'build123d' as const
        : id === 'assembly-tools' ? 'assembly' as const
          : items.every((item) => item.source.package.toLocaleLowerCase().includes('bd-warehouse')) ? 'bd-warehouse' as const : 'other' as const,
      capabilities: items,
    }))
    .sort((left, right) => {
      const leftIndex = preferred.indexOf(left.id)
      const rightIndex = preferred.indexOf(right.id)
      if (leftIndex >= 0 || rightIndex >= 0) return (leftIndex < 0 ? preferred.length : leftIndex) - (rightIndex < 0 ? preferred.length : rightIndex)
      return left.label.localeCompare(right.label)
    })
}

export function photonCadManualDesignCapabilityId(
  capabilities: readonly PhotonCadCapability[],
  selectedCapabilityId: string,
) {
  const selected = capabilities.find((capability) => capability.id === selectedCapabilityId)
  if (selected && photonCadManualDesignCapability(selected)) {
    return selected.id
  }
  return capabilities.find(photonCadManualDesignCapability)?.id ?? ''
}

function photonCadManualDesignCapability(capability: PhotonCadCapability) {
  return catalogSectionId(capability) === 'build123d-design'
    && (capability.operation === 'create' || capability.operation === 'modify')
}

function photonCadManualModifyCapabilityId(capabilityId: string) {
  return capabilityId === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
    || capabilityId === PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID
    || capabilityId === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID
    || capabilityId === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
    || capabilityId === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID
}

export function photonCadProjectParts(project: PhotonCadProjectSnapshot | null, query = ''): PhotonCadProjectPart[] {
  if (!project) return []
  const needle = query.trim().toLocaleLowerCase()
  const occurrences = project.occurrences ?? []
  return project.entities
    .filter((entity): entity is typeof entity & { kind: 'body' | 'part' } => entity.kind === 'body' || entity.kind === 'part')
    .filter((entity) => !needle || [entity.id, entity.name, entity.kind].some((value) => value.toLocaleLowerCase().includes(needle)))
    .map((entity) => ({
      id: entity.id,
      name: entity.name,
      kind: entity.kind,
      occurrenceCount: occurrences.filter((occurrence) => occurrence.sourceEntityId === entity.id).length,
    }))
}

function clampPhotonCadPaneWidth(pane: 'context' | 'inspector', value: number) {
  const bounds = PHOTON_CAD_PANE_BOUNDS[pane]
  if (!Number.isFinite(value)) return bounds.initial
  return Math.min(bounds.maximum, Math.max(bounds.minimum, Math.round(value)))
}

export function photonCadKeyboardPaneWidth(pane: 'context' | 'inspector', current: number, key: string, largeStep = false) {
  const bounds = PHOTON_CAD_PANE_BOUNDS[pane]
  const step = largeStep ? 48 : 16
  if (key === 'Home') return bounds.minimum
  if (key === 'End') return bounds.maximum
  if (pane === 'context') {
    if (key === 'ArrowLeft') return clampPhotonCadPaneWidth(pane, current - step)
    if (key === 'ArrowRight') return clampPhotonCadPaneWidth(pane, current + step)
  } else {
    if (key === 'ArrowLeft') return clampPhotonCadPaneWidth(pane, current + step)
    if (key === 'ArrowRight') return clampPhotonCadPaneWidth(pane, current - step)
  }
  return null
}

export function loadPhotonCadPaneLayout(storage: Pick<Storage, 'getItem'> | null = typeof window === 'undefined' ? null : window.localStorage): PhotonCadPaneLayout {
  if (!storage) return DEFAULT_PHOTON_CAD_PANE_LAYOUT
  try {
    const parsed = JSON.parse(storage.getItem(PHOTON_CAD_PANE_LAYOUT_STORAGE_KEY) ?? 'null') as unknown
    if (!parsed || typeof parsed !== 'object') return DEFAULT_PHOTON_CAD_PANE_LAYOUT
    const candidate = parsed as Partial<PhotonCadPaneLayout> & { version?: unknown }
    if (candidate.version !== 1 || typeof candidate.contextWidth !== 'number' || typeof candidate.inspectorWidth !== 'number'
      || typeof candidate.contextCollapsed !== 'boolean' || typeof candidate.inspectorCollapsed !== 'boolean') return DEFAULT_PHOTON_CAD_PANE_LAYOUT
    return {
      contextWidth: clampPhotonCadPaneWidth('context', candidate.contextWidth),
      inspectorWidth: clampPhotonCadPaneWidth('inspector', candidate.inspectorWidth),
      contextCollapsed: candidate.contextCollapsed,
      inspectorCollapsed: candidate.inspectorCollapsed,
    }
  } catch {
    return DEFAULT_PHOTON_CAD_PANE_LAYOUT
  }
}

export function savePhotonCadPaneLayout(layout: PhotonCadPaneLayout, storage: Pick<Storage, 'setItem'> | null = typeof window === 'undefined' ? null : window.localStorage) {
  if (!storage) return false
  try {
    storage.setItem(PHOTON_CAD_PANE_LAYOUT_STORAGE_KEY, JSON.stringify({ version: 1, ...layout }))
    return true
  } catch {
    return false
  }
}

export type PhotonCadReleaseOutput = 'cad-package' | 'bom-export' | 'commercial-document'
type PhotonCadCommercialAction = 'export-pdf' | 'print'

function emptyCommercialParty(): PhotonCadCommercialParty {
  return {
    organization: '',
    contactName: '',
    addressLines: [],
    city: '',
    region: '',
    postalCode: '',
    countryCode: '',
    email: '',
    phone: '',
  }
}

function createCommercialDraft(
  project: PhotonCadProjectSnapshot | null,
  bom: readonly PhotonCadBomRow[],
  bomDigest: string,
): PhotonCadCommercialDraft {
  return {
    contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
    documentKind: 'quote',
    documentNumber: '',
    projectId: project?.projectId ?? '',
    projectRevision: project?.revision ?? 0,
    bomDigest,
    issueDate: '',
    currency: 'USD',
    currencyScale: 2,
    seller: emptyCommercialParty(),
    customer: emptyCommercialParty(),
    lines: bom.map((row, index) => ({
      lineId: `bom-line-${index + 1}`,
      sourceBomRowId: row.sourceEntityId,
      partNumber: row.partNumber,
      description: row.description,
      quantity: row.quantity,
      unit: row.unit,
      unitPriceMinorUnits: 0,
      taxable: true,
    })),
    markupBasisPoints: 0,
    discountMinorUnits: 0,
    freightMinorUnits: 0,
    taxBasisPoints: 0,
    terms: '',
    notes: '',
  }
}

function moneyInputValue(minorUnits: number, scale: number) {
  return minorUnits / (10 ** scale)
}

function moneyMinorUnits(value: string, scale: number) {
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed >= 0 ? Math.round(parsed * (10 ** scale)) : 0
}

function rescaleCommercialDraft(draft: PhotonCadCommercialDraft, currencyScale: number): PhotonCadCommercialDraft {
  const factor = 10 ** (currencyScale - draft.currencyScale)
  const rescale = (value: number) => Math.round(value * factor)
  return {
    ...draft,
    currencyScale,
    lines: draft.lines.map((line) => ({ ...line, unitPriceMinorUnits: rescale(line.unitPriceMinorUnits) })),
    discountMinorUnits: rescale(draft.discountMinorUnits),
    freightMinorUnits: rescale(draft.freightMinorUnits),
  }
}

function formatMoney(minorUnits: number, currency: string, scale: number) {
  const amount = moneyInputValue(minorUnits, scale)
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, minimumFractionDigits: scale, maximumFractionDigits: scale }).format(amount)
  } catch {
    return `${currency || '---'} ${amount.toFixed(scale)}`
  }
}

function commercialIssueText(issue: string) {
  const labels: Record<string, string> = {
    'invalid-document-number': 'Enter a document number.',
    'invalid-project': 'Load a valid project revision.',
    'invalid-bom-digest': 'A verified BOM digest is required.',
    'invalid-date': 'Enter a valid issue date and optional due date.',
    'due-date-before-issue-date': 'The due date cannot precede the issue date.',
    'invalid-currency': 'Use a three-letter currency code.',
    'invalid-seller-organization': 'Enter your company name.',
    'invalid-seller-country': 'Enter your two-letter country code.',
    'invalid-customer-organization': 'Enter the customer company name.',
    'invalid-customer-country': 'Enter the customer two-letter country code.',
    'invalid-lines': 'At least one valid BOM or labor line is required.',
    'invalid-line': 'One or more pricing lines need correction.',
    'duplicate-line': 'Pricing line identifiers must be unique.',
    'invalid-rate': 'Markup and tax rates must be valid nonnegative percentages.',
    'invalid-adjustment': 'Discount and freight must be valid nonnegative amounts.',
  }
  return labels[issue] ?? issue.replace(/^invalid-/u, '').replaceAll('-', ' ')
}

function commercialPageKey(page: PhotonCadCommercialPreviewPage) {
  return `${page.pageNumber}:${page.contentDigest.toLowerCase()}`
}

let requestSequence = 0

function requestId(prefix: string) {
  requestSequence = (requestSequence + 1) % Number.MAX_SAFE_INTEGER
  return `${prefix}-${Date.now().toString(36)}-${requestSequence.toString(36)}`
}

function defaultRuntime(controller?: PhotonCadController): PhotonCadRuntimeDescription {
  return {
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    status: controller ? 'checking' : 'unavailable',
    reason: 'unavailable',
  }
}

function safeText(value: unknown, maximum = 512) {
  return photonCadDisplayText(value, maximum)
}

function unitAbbreviation(project: PhotonCadProjectSnapshot | null) {
  return project?.units === 'inch' ? 'in' : 'mm'
}

function parameterUnit(parameter: PhotonCadParameterDefinition, project: PhotonCadProjectSnapshot | null) {
  if (parameter.unit === 'length') return unitAbbreviation(project)
  if (parameter.unit === 'angle') return 'deg'
  if (parameter.unit === 'ratio') return 'ratio'
  if (parameter.unit === 'count') return 'count'
  return ''
}

function isVector(value: PhotonCadInputValue | undefined): value is PhotonCadVector3 {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value))
}

export function photonCadInitialCapabilityInputs(capability: PhotonCadCapability | null) {
  if (!capability) return {}
  return Object.fromEntries(capability.parameters.map((parameter) => [parameter.id, parameter.defaultValue ?? null]))
}

export function photonCadCapabilityAuthorizationFingerprint(catalogRevision: string, capability: PhotonCadCapability) {
  return JSON.stringify({
    contractVersion: PHOTON_CAD_CONTRACT_VERSION,
    catalogRevision,
    capability: {
      id: capability.id,
      backend: capability.backend,
      category: capability.category,
      title: capability.title,
      description: capability.description,
      operation: capability.operation,
      previewSupported: capability.previewSupported,
      experimental: capability.experimental,
      source: capability.source,
      parameters: capability.parameters.map((parameter) => ({
        id: parameter.id,
        label: parameter.label,
        description: parameter.description,
        kind: parameter.kind,
        required: parameter.required,
        unit: parameter.unit ?? null,
        minimum: parameter.minimum ?? null,
        maximum: parameter.maximum ?? null,
        step: parameter.step ?? null,
        defaultValue: parameter.defaultValue ?? null,
        choices: parameter.choices ?? [],
      })),
    },
  })
}

function validCapabilityInput(
  parameter: PhotonCadParameterDefinition,
  value: PhotonCadInputValue,
  project: PhotonCadProjectSnapshot,
) {
  if (value === null) return !parameter.required
  if (parameter.kind === 'number' || parameter.kind === 'integer') {
    return typeof value === 'number'
      && Number.isFinite(value)
      && Math.abs(value) <= PHOTON_CAD_LIMITS.absoluteMagnitude
      && (parameter.kind !== 'integer' || Number.isSafeInteger(value))
      && (parameter.minimum === undefined || value >= parameter.minimum)
      && (parameter.maximum === undefined || value <= parameter.maximum)
  }
  if (parameter.kind === 'boolean') return typeof value === 'boolean'
  if (parameter.kind === 'text') return typeof value === 'string' && isPhotonCadSafeText(value, PHOTON_CAD_LIMITS.text, parameter.required)
  if (parameter.kind === 'choice') {
    return typeof value === 'string' && Boolean(parameter.choices?.some((choice) => choice.value === value))
  }
  if (parameter.kind === 'vector3') {
    return isVector(value)
      && Object.keys(value).sort().join(',') === 'x,y,z'
      && [value.x, value.y, value.z].every((item) => Number.isFinite(item) && Math.abs(item) <= PHOTON_CAD_LIMITS.absoluteMagnitude)
  }
  const entityIds = new Set(project.entities.map((entity) => entity.id))
  if (parameter.kind === 'entity') return typeof value === 'string' && isPhotonCadIdentifier(value) && entityIds.has(value)
  if (parameter.kind === 'entity-list') {
    return Array.isArray(value)
      && value.length <= 1_000
      && new Set(value).size === value.length
      && value.every((item) => isPhotonCadIdentifier(item) && entityIds.has(item))
  }
  return false
}

export function photonCadCapabilityInputsMatch(
  capability: PhotonCadCapability,
  inputs: Record<string, PhotonCadInputValue>,
  project: PhotonCadProjectSnapshot,
) {
  const expected = capability.parameters.map((parameter) => parameter.id).sort()
  const supplied = Object.keys(inputs).sort()
  return expected.length === supplied.length
    && expected.every((id, index) => id === supplied[index])
    && capability.parameters.every((parameter) => validCapabilityInput(parameter, inputs[parameter.id], project))
    && photonCadManualInputCombinationMatches(capability, inputs)
}

function photonCadAssemblyPlaceSchemaMatches(capability: PhotonCadCapability) {
  const parameters = new Map(capability.parameters.map((parameter) => [parameter.id, parameter]))
  const source = parameters.get('sourceEntityId')
  const parent = parameters.get('parentOccurrenceId')
  const translation = parameters.get('translation')
  const rotation = parameters.get('rotationDegrees')
  const zeroVector = (value: PhotonCadInputValue | undefined) => isVector(value)
    && value.x === 0 && value.y === 0 && value.z === 0
  return capability.parameters.length === 4
    && source?.kind === 'entity' && source.required && source.unit === undefined && source.defaultValue === null
    && parent?.kind === 'text' && !parent.required && parent.unit === undefined && parent.defaultValue === null
    && translation?.kind === 'vector3' && translation.required && translation.unit === 'length'
    && translation.minimum === -1_000_000 && translation.maximum === 1_000_000 && zeroVector(translation.defaultValue)
    && rotation?.kind === 'vector3' && rotation.required && rotation.unit === 'angle'
    && rotation.minimum === -360 && rotation.maximum === 360 && zeroVector(rotation.defaultValue)
}

function photonCadAssemblyRemoveSchemaMatches(capability: PhotonCadCapability) {
  return capability.parameters.length === 0
}

function photonCadAssemblyTransformSchemaMatches(capability: PhotonCadCapability) {
  const parameters = new Map(capability.parameters.map((parameter) => [parameter.id, parameter]))
  const translation = parameters.get('translation')
  const rotation = parameters.get('rotationDegrees')
  const zeroVector = (value: PhotonCadInputValue | undefined) => isVector(value)
    && value.x === 0 && value.y === 0 && value.z === 0
  return capability.parameters.length === 2
    && translation?.kind === 'vector3' && translation.required && translation.unit === 'length'
    && translation.minimum === -1_000_000 && translation.maximum === 1_000_000 && zeroVector(translation.defaultValue)
    && rotation?.kind === 'vector3' && rotation.required && rotation.unit === 'angle'
    && rotation.minimum === -360 && rotation.maximum === 360 && zeroVector(rotation.defaultValue)
}

function photonCadManualNumberParameterMatches(
  parameter: PhotonCadParameterDefinition | undefined,
  required: boolean,
  signed = false,
) {
  return parameter?.kind === 'number'
    && parameter.required === required
    && parameter.unit === 'length'
    && parameter.minimum === (signed ? -1_000_000 : 0.000001)
    && parameter.maximum === 1_000_000
}

function photonCadManualChoiceParameterMatches(
  parameter: PhotonCadParameterDefinition | undefined,
  values: readonly string[],
) {
  return parameter?.kind === 'choice' && parameter.required
    && parameter.choices?.length === values.length
    && parameter.choices.every((choice, index) => choice.value === values[index])
}

function photonCadManualSchemaMatches(capability: PhotonCadCapability) {
  const parameters = new Map(capability.parameters.map((parameter) => [parameter.id, parameter]))
  if (capability.id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID) {
    return capability.operation === 'create' && capability.parameters.length === 6
      && photonCadManualChoiceParameterMatches(parameters.get('profileKind'), ['rectangle', 'circle'])
      && photonCadManualChoiceParameterMatches(parameters.get('sketchPlane'), ['xy'])
      && photonCadManualNumberParameterMatches(parameters.get('profileWidthMm'), false)
      && photonCadManualNumberParameterMatches(parameters.get('profileHeightMm'), false)
      && photonCadManualNumberParameterMatches(parameters.get('profileRadiusMm'), false)
      && photonCadManualNumberParameterMatches(parameters.get('extrusionDepthMm'), true)
  }
  if (capability.id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID) {
    return capability.operation === 'modify' && capability.parameters.length === 4
      && photonCadManualChoiceParameterMatches(parameters.get('sketchPlane'), ['xy'])
      && photonCadManualNumberParameterMatches(parameters.get('profileWidthMm'), true)
      && photonCadManualNumberParameterMatches(parameters.get('profileHeightMm'), true)
      && photonCadManualNumberParameterMatches(parameters.get('cutDepthMm'), true)
  }
  if (capability.id === PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID) {
    const sketch = parameters.get('sketch')
    return capability.operation === 'create' && capability.parameters.length === 2
      && sketch?.kind === 'text' && sketch.required
      && photonCadManualNumberParameterMatches(parameters.get('extrusionDepthMm'), true)
  }
  if (capability.id === PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID) {
    const sketch = parameters.get('sketch')
    return capability.operation === 'modify' && capability.parameters.length === 2
      && sketch?.kind === 'text' && sketch.required
      && photonCadManualNumberParameterMatches(parameters.get('cutDepthMm'), true)
  }
  if (capability.id === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID) {
    return capability.operation === 'modify' && capability.parameters.length === 5
      && photonCadManualNumberParameterMatches(parameters.get('diameterMm'), true)
      && photonCadManualNumberParameterMatches(parameters.get('depthMm'), true)
      && photonCadManualNumberParameterMatches(parameters.get('xMm'), true, true)
      && photonCadManualNumberParameterMatches(parameters.get('yMm'), true, true)
      && photonCadManualNumberParameterMatches(parameters.get('zMm'), true, true)
  }
  const patternSeed = parameters.get('seedFeatureId')
  const patternCount = parameters.get('count')
  const patternCountMatches = patternCount?.kind === 'integer' && patternCount.required
    && patternCount.unit === 'count' && patternCount.minimum === 2 && patternCount.maximum === 256
    && patternCount.defaultValue === 2
  if (capability.id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID) {
    return capability.operation === 'modify' && capability.parameters.length === 3
      && patternSeed?.kind === 'entity' && patternSeed.required && patternSeed.defaultValue === null
      && patternCountMatches && photonCadManualNumberParameterMatches(parameters.get('spacingMm'), true)
  }
  if (capability.id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID) {
    const angle = parameters.get('angleDegrees')
    return capability.operation === 'modify' && capability.parameters.length === 3
      && patternSeed?.kind === 'entity' && patternSeed.required && patternSeed.defaultValue === null
      && patternCountMatches && angle?.kind === 'number' && angle.required && angle.unit === 'angle'
      && angle.minimum === 0.000001 && angle.maximum === 360 && angle.defaultValue === 360
  }
  return false
}

function photonCadManualInputCombinationMatches(
  capability: PhotonCadCapability,
  inputs: Record<string, PhotonCadInputValue>,
) {
  if (capability.id !== PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID) return true
  return inputs.profileKind === 'rectangle'
    ? typeof inputs.profileWidthMm === 'number' && typeof inputs.profileHeightMm === 'number' && inputs.profileRadiusMm === null
    : inputs.profileKind === 'circle'
      && inputs.profileWidthMm === null && inputs.profileHeightMm === null && typeof inputs.profileRadiusMm === 'number'
}

export function photonCadCapabilityRunnable(capability: PhotonCadCapability) {
  if (capability.experimental || !capability.previewSupported) return false
  if (capability.id === PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID) {
    return capability.operation === 'assemble' && photonCadAssemblyPlaceSchemaMatches(capability)
  }
  if (capability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID) {
    return capability.operation === 'assemble' && photonCadAssemblyTransformSchemaMatches(capability)
  }
  if (capability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID) {
    return capability.operation === 'assemble' && photonCadAssemblyRemoveSchemaMatches(capability)
  }
  if (capability.id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_MOUSE_CUT_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
    || capability.id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID) {
    return photonCadManualSchemaMatches(capability)
  }
  return capability.operation === 'create'
}

function photonCadRemovedOccurrenceIds(
  occurrences: NonNullable<PhotonCadProjectSnapshot['occurrences']>,
  targetOccurrenceId: string,
) {
  if (!occurrences.some((occurrence) => occurrence.occurrenceId === targetOccurrenceId)) return null
  const removed = new Set([targetOccurrenceId])
  let changed = true
  while (changed) {
    changed = false
    for (const occurrence of occurrences) {
      if (occurrence.parentOccurrenceId && removed.has(occurrence.parentOccurrenceId) && !removed.has(occurrence.occurrenceId)) {
        removed.add(occurrence.occurrenceId)
        changed = true
      }
    }
  }
  return removed
}

export function photonCadStepExportEntity(project: PhotonCadProjectSnapshot | null, selectedEntityIds: readonly string[]) {
  if (!project || selectedEntityIds.length !== 1) return null
  return project.entities.find((entity) => entity.id === selectedEntityIds[0] && (entity.kind === 'body' || entity.kind === 'part')) ?? null
}

export function photonCadStepExportResultMatches(request: PhotonCadStepExportRequest, result: PhotonCadStepExportResult) {
  if (result.requestId !== request.requestId || result.projectId !== request.projectId || result.revision !== request.revision) return false
  if (result.status !== 'committed') {
    return result.entityId === undefined && result.contentDigest === undefined && result.byteLength === undefined && result.destinationLabel === undefined
  }
  return result.entityId === request.entityId && isPhotonCadDigest(result.contentDigest)
    && typeof result.byteLength === 'number' && Number.isSafeInteger(result.byteLength)
    && result.byteLength > 0 && result.byteLength <= PHOTON_CAD_LIMITS.absoluteMagnitude
    && isPhotonCadSafeText(result.destinationLabel, 256, true) && !/[\\/:]/u.test(result.destinationLabel)
    && result.destinationLabel !== '.' && result.destinationLabel !== '..'
}

export function photonCadAuthorizePersistedScratchRequest(
  runtime: PhotonCadRuntimeDescription,
  project: PhotonCadProjectSnapshot,
  request: PhotonCadOperationRequest,
) {
  const catalog = runtime.status === 'available' && runtime.catalog ? normalizePhotonCadCatalog(runtime.catalog) : null
  const capability = catalog?.capabilities.find((candidate) => candidate.id === request.capabilityId)
  if (!catalog || !capability
    || !photonCadCapabilityRunnable(capability)
    || project.mode !== 'canonical' || project.units !== 'millimeter' || project.dirty
    || request.contractVersion !== PHOTON_CAD_CONTRACT_VERSION || request.mode !== 'scratch'
    || request.sessionId !== project.sessionId || request.projectId !== project.projectId || request.baseRevision !== project.revision
    || !photonCadCapabilityInputsMatch(capability, request.inputs, project)) return null
  if (capability.id === PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID) {
    const sourceEntityId = request.inputs.sourceEntityId
    const parentOccurrenceId = request.inputs.parentOccurrenceId
    const occurrences = project.occurrences ?? []
    const source = typeof sourceEntityId === 'string'
      ? project.entities.find((entity) => entity.id === sourceEntityId && entity.kind !== 'occurrence')
      : undefined
    const defaultParent = typeof sourceEntityId === 'string' ? `${sourceEntityId}.occ` : ''
    if (!source
      || !occurrences.some((occurrence) => occurrence.sourceEntityId === source.id)
      || parentOccurrenceId !== null && (typeof parentOccurrenceId !== 'string'
        || !occurrences.some((occurrence) => occurrence.occurrenceId === parentOccurrenceId))
      || parentOccurrenceId === null && !occurrences.some((occurrence) => occurrence.occurrenceId === defaultParent)) return null
    if (request.targetEntityIds.length !== 0) return null
  } else if (capability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID) {
    const occurrences = project.occurrences ?? []
    const target = request.targetEntityIds[0]
    if (request.targetEntityIds.length !== 1
      || !occurrences.some((occurrence) => occurrence.occurrenceId === target)
      || !project.entities.some((entity) => entity.id === target && entity.kind === 'occurrence')) return null
  } else if (capability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID) {
    const occurrences = project.occurrences ?? []
    const target = request.targetEntityIds[0]
    const removed = request.targetEntityIds.length === 1 ? photonCadRemovedOccurrenceIds(occurrences, target) : null
    if (!removed || removed.size >= occurrences.length
      || !project.entities.some((entity) => entity.id === target && entity.kind === 'occurrence')) return null
  } else if (capability.id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID || capability.id === PHOTON_CAD_MANUAL_MOUSE_ADD_CAPABILITY_ID) {
    if (request.targetEntityIds.length !== 0) return null
  } else if (photonCadManualModifyCapabilityId(capability.id)) {
    const target = request.targetEntityIds[0]
    if (request.targetEntityIds.length !== 1
        || !project.entities.some((entity) => entity.id === target && (entity.kind === 'body' || entity.kind === 'part'))) return null
    if (capability.id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
      || capability.id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID) {
      const seedFeatureId = request.inputs.seedFeatureId
      if (typeof seedFeatureId !== 'string'
        || !project.entities.some((entity) => entity.id === seedFeatureId && entity.kind === 'datum'
          && entity.parentId === target
          && (entity.sourceCapabilityId === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
            || entity.sourceCapabilityId === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID))) return null
    }
  } else if (request.targetEntityIds.length !== 0) {
    return null
  }
  return photonCadCapabilityAuthorizationFingerprint(catalog.catalogRevision, capability)
}

export function photonCadAcceptedPersistedScratchSnapshot(
  request: PhotonCadOperationRequest,
  result: PhotonCadOperationResult,
  authorizationCurrent: boolean,
): PhotonCadProjectSnapshot | null {
  const snapshot = result.snapshot
  const targetsMatch = photonCadManualModifyCapabilityId(request.capabilityId)
    ? request.targetEntityIds.length === 1 && Boolean(snapshot?.entities.some((entity) =>
      entity.id === request.targetEntityIds[0] && (entity.kind === 'body' || entity.kind === 'part')))
    : request.targetEntityIds.length === 0
  if (!authorizationCurrent || request.mode !== 'scratch' || !targetsMatch
    || result.status !== 'accepted' || result.stale || !snapshot
    || result.requestId !== request.requestId || result.projectId !== request.projectId
    || result.baseRevision !== request.baseRevision || result.resultingRevision !== request.baseRevision + 2
    || snapshot.sessionId !== request.sessionId || snapshot.projectId !== request.projectId
    || snapshot.revision !== result.resultingRevision || snapshot.units !== 'millimeter'
    || snapshot.mode !== 'canonical' || snapshot.dirty) return null
  return snapshot
}

export function photonCadOperationContinuationAuthorizationBinding(
  capabilityAuthorization: string,
  project: PhotonCadProjectSnapshot | null,
  stillAuthorized: boolean,
) {
  if (!stillAuthorized || !capabilityAuthorization || !project
    || project.mode !== 'canonical' || project.units !== 'millimeter' || project.dirty) return ''
  return `${capabilityAuthorization}\n${project.sessionId}\n${project.projectId}`
}

export function photonCadAcceptedPersistedScratchCommit(
  request: PhotonCadOperationRequest,
  result: PhotonCadOperationResult,
  authorizationCurrent: boolean,
) {
  const snapshot = photonCadAcceptedPersistedScratchSnapshot(request, result, authorizationCurrent)
  const preview = result.preview
  if (!snapshot || !preview
    || !isPhotonCadIdentifier(preview.previewId)
    || preview.projectId !== request.projectId
    || preview.revision !== result.resultingRevision
    || preview.units !== 'millimeter'
    || !isPhotonCadDigest(preview.contentDigest)
    || !Number.isSafeInteger(preview.entityCount) || preview.entityCount < 1) return null
  return { snapshot, preview }
}

function sameOccurrence(
  left: NonNullable<PhotonCadProjectSnapshot['occurrences']>[number],
  right: NonNullable<PhotonCadProjectSnapshot['occurrences']>[number],
) {
  return left.occurrenceId === right.occurrenceId
    && left.parentOccurrenceId === right.parentOccurrenceId
    && left.partNumber === right.partNumber
    && left.sourceEntityId === right.sourceEntityId
    && left.transform.every((value, index) => Math.abs(value - right.transform[index]) <= 1e-9)
}

export function photonCadAssemblyRigidTransform(translation: PhotonCadVector3, rotationDegrees: PhotonCadVector3) {
  const x = rotationDegrees.x * Math.PI / 180
  const y = rotationDegrees.y * Math.PI / 180
  const z = rotationDegrees.z * Math.PI / 180
  const sx = Math.sin(x); const cx = Math.cos(x)
  const sy = Math.sin(y); const cy = Math.cos(y)
  const sz = Math.sin(z); const cz = Math.cos(z)
  return [
    cz * cy, (cz * sy * sx) - (sz * cx), (cz * sy * cx) + (sz * sx), translation.x,
    sz * cy, (sz * sy * sx) + (cz * cx), (sz * sy * cx) - (cz * sx), translation.y,
    -sy, cy * sx, cy * cx, translation.z,
    0, 0, 0, 1,
  ] as NonNullable<PhotonCadProjectSnapshot['occurrences']>[number]['transform']
}

export function photonCadAcceptedAssemblySnapshot(
  request: PhotonCadOperationRequest,
  result: PhotonCadOperationResult,
  previous: PhotonCadProjectSnapshot,
  previousPreview?: PhotonCadPreviewReceipt | null,
) {
  const snapshot = result.snapshot
  const preview = result.preview
  const before = previous.occurrences
  const after = snapshot?.occurrences
  const beforeEntities = previous.entities
  const afterEntities = snapshot?.entities
  if (request.capabilityId !== PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID
    && request.capabilityId !== PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
    && request.capabilityId !== PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
    || request.mode !== 'scratch'
    || result.status !== 'accepted' || result.stale || !snapshot || !preview || !before || !after || !afterEntities
    || result.requestId !== request.requestId || result.projectId !== request.projectId
    || result.baseRevision !== request.baseRevision || result.resultingRevision !== request.baseRevision + 2
    || snapshot.sessionId !== request.sessionId || snapshot.projectId !== request.projectId
    || snapshot.revision !== result.resultingRevision || snapshot.units !== 'millimeter'
    || snapshot.mode !== 'canonical' || snapshot.dirty
    || preview.projectId !== request.projectId || preview.revision !== result.resultingRevision
    || preview.units !== 'millimeter' || !isPhotonCadIdentifier(preview.previewId) || !isPhotonCadDigest(preview.contentDigest)
    || preview.entityCount !== after.length
    || previousPreview && previousPreview.contentDigest.toLowerCase() === preview.contentDigest.toLowerCase()) return null
  if (request.capabilityId === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID) {
    if (request.targetEntityIds.length !== 1 || Object.keys(request.inputs).length !== 0) return null
    const removed = photonCadRemovedOccurrenceIds(before, request.targetEntityIds[0])
    if (!removed || removed.size >= before.length || after.length !== before.length - removed.size
      || after.some((occurrence) => removed.has(occurrence.occurrenceId))) return null
    const expected = before.filter((occurrence) => !removed.has(occurrence.occurrenceId))
    if (expected.some((occurrence) => {
      const candidate = after.find((value) => value.occurrenceId === occurrence.occurrenceId)
      return !candidate || !sameOccurrence(occurrence, candidate)
    })) return null
    const canonicalBefore = beforeEntities.filter((entity) => entity.kind !== 'occurrence')
    const canonicalAfter = afterEntities.filter((entity) => entity.kind !== 'occurrence')
    if (canonicalAfter.length !== canonicalBefore.length || canonicalBefore.some((entity) => {
      const candidate = canonicalAfter.find((value) => value.id === entity.id)
      return !candidate || JSON.stringify(candidate) !== JSON.stringify(entity)
    })) return null
    const occurrenceEntities = afterEntities.filter((entity) => entity.kind === 'occurrence')
    if (occurrenceEntities.length !== after.length
      || occurrenceEntities.some((entity) => !after.some((occurrence) => occurrence.occurrenceId === entity.id))) return null
    return snapshot
  }
  if (request.capabilityId === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID) {
    if (request.targetEntityIds.length !== 1 || after.length !== before.length
      || afterEntities.length !== beforeEntities.length) return null
    const targetId = request.targetEntityIds[0]
    const translation = request.inputs.translation
    const rotationDegrees = request.inputs.rotationDegrees
    if (Object.keys(request.inputs).length !== 2 || !isVector(translation) || !isVector(rotationDegrees)) return null
    const expectedTransform = photonCadAssemblyRigidTransform(translation, rotationDegrees)
    if (before.some((occurrence) => {
      const candidate = after.find((value) => value.occurrenceId === occurrence.occurrenceId)
      if (!candidate) return true
      if (occurrence.occurrenceId !== targetId) return !sameOccurrence(occurrence, candidate)
      return candidate.parentOccurrenceId !== occurrence.parentOccurrenceId
        || candidate.partNumber !== occurrence.partNumber
        || candidate.sourceEntityId !== occurrence.sourceEntityId
        || candidate.transform.some((value, index) => Math.abs(value - expectedTransform[index]) > 1e-9)
    })) return null
    if (beforeEntities.some((entity) => {
      const candidate = afterEntities.find((value) => value.id === entity.id)
      return !candidate || JSON.stringify(candidate) !== JSON.stringify(entity)
    })) return null
    return snapshot
  }
  if (request.targetEntityIds.length !== 0
    || after.length !== before.length + 1 || afterEntities.length !== beforeEntities.length + 1) return null
  const prior = new Map(before.map((occurrence) => [occurrence.occurrenceId, occurrence]))
  if (before.some((occurrence) => {
    const candidate = after.find((value) => value.occurrenceId === occurrence.occurrenceId)
    return !candidate || !sameOccurrence(occurrence, candidate)
  })) return null
  const added = after.filter((occurrence) => !prior.has(occurrence.occurrenceId))
  const priorEntities = new Map(beforeEntities.map((entity) => [entity.id, entity]))
  if (beforeEntities.some((entity) => {
    const candidate = afterEntities.find((value) => value.id === entity.id)
    return !candidate || JSON.stringify(candidate) !== JSON.stringify(entity)
  })) return null
  const addedEntities = afterEntities.filter((entity) => !priorEntities.has(entity.id))
  const sourceEntityId = request.inputs.sourceEntityId
  const parentInput = request.inputs.parentOccurrenceId
  const translation = request.inputs.translation
  const rotationDegrees = request.inputs.rotationDegrees
  if (added.length !== 1 || addedEntities.length !== 1 || typeof sourceEntityId !== 'string'
    || parentInput !== null && typeof parentInput !== 'string'
    || !isVector(translation) || !isVector(rotationDegrees)) return null
  const sourceOccurrence = before.find((occurrence) => occurrence.sourceEntityId === sourceEntityId)
  const sourceEntity = beforeEntities.find((entity) => entity.id === sourceEntityId && entity.kind !== 'occurrence')
  const expectedParent = parentInput ?? `${sourceEntityId}.occ`
  const expectedTransform = photonCadAssemblyRigidTransform(translation, rotationDegrees)
  return sourceOccurrence && sourceEntity
    && added[0].sourceEntityId === sourceEntityId
    && added[0].parentOccurrenceId === expectedParent
    && added[0].partNumber === sourceOccurrence.partNumber
    && added[0].transform.every((value, index) => Math.abs(value - expectedTransform[index]) <= 1e-9)
    && addedEntities[0].id === added[0].occurrenceId
    && addedEntities[0].parentId === expectedParent
    && addedEntities[0].kind === 'occurrence'
    && addedEntities[0].name === added[0].partNumber
    && addedEntities[0].visible && !addedEntities[0].suppressed
    && addedEntities[0].sourceCapabilityId === sourceEntity.sourceCapabilityId
    ? snapshot
    : null
}

function issueTone(issue: PhotonCadIssue) {
  return issue.severity === 'error' ? 'error' : issue.severity === 'warning' ? 'warning' : 'info'
}

function formatTimestamp(value: string) {
  const parsed = Date.parse(value)
  if (!Number.isFinite(parsed)) return 'Time unavailable'
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed)
}

function entityDepth(entityId: string, project: PhotonCadProjectSnapshot) {
  const parents = new Map(project.entities.map((entity) => [entity.id, entity.parentId]))
  const visited = new Set<string>()
  let current = parents.get(entityId) ?? null
  let depth = 0
  while (current && depth < 12 && !visited.has(current)) {
    visited.add(current)
    depth += 1
    current = parents.get(current) ?? null
  }
  return depth
}

function runtimeCopy(
  runtime: PhotonCadRuntimeDescription,
  catalogReady: boolean,
  operationReady: boolean,
  evidenceMode: 'runtime' | 'fixture',
) {
  if (evidenceMode === 'fixture') return 'Fixture catalog only. No live CAD runtime action is implied.'
  if (runtime.status === 'checking') return 'Checking the installed CAD runtime…'
  if (catalogReady && !operationReady) return 'Catalog connected · editing locked. Inspect tools and saved data; project editing and output actions remain locked.'
  if (operationReady) return photonCadReasonText(runtime.reason || 'ready')
  if (runtime.status === 'available') return 'The CAD runtime did not return a usable catalog. Design data was not changed.'
  if (runtime.status === 'error') return 'The CAD runtime check failed. Design data was not changed.'
  return photonCadReasonText(runtime.reason || 'unavailable')
}

export function PhotonCadWorkspace({
  controller,
  runtime,
  project,
  bom = [],
  verification,
  previewSurface,
  commercialController,
  bomDigest = '',
  initialCommercialDraft,
  commercialPreviewSurface,
  destination = null,
  onChooseDestination,
  printer = null,
  onChoosePrinter,
  initialStage = 'library',
  initialReleaseOutput = 'cad-package',
  evidenceMode = 'runtime',
  persistedScratchAuthorized = false,
  projectContentDigest = '',
  className = '',
}: PhotonCadWorkspaceProps) {
  const workspaceId = useId()
  const [activeStage, setActiveStage] = useState<PhotonCadWorkspaceStage>(initialStage)
  const [runtimeView, setRuntimeView] = useState<PhotonCadRuntimeDescription>(() => runtime ?? defaultRuntime(controller))
  const effectiveRuntime = runtime ?? runtimeView
  const [currentProject, setCurrentProject] = useState<PhotonCadProjectSnapshot | null>(project ?? null)
  const [query, setQuery] = useState('')
  const [selectedCapabilityId, setSelectedCapabilityId] = useState('')
  const [selectedEntityIds, setSelectedEntityIds] = useState<string[]>([])
  const [inputs, setInputs] = useState<Record<string, PhotonCadInputValue>>({})
  const [previewReceipt, setPreviewReceipt] = useState<PhotonCadPreviewReceipt | null>(null)
  const [operationIssues, setOperationIssues] = useState<PhotonCadIssue[]>([])
  const [verificationView, setVerificationView] = useState<PhotonCadVerificationResult | null>(verification ?? null)
  const [enabledChecks, setEnabledChecks] = useState(() => new Set(verificationChecks.filter((check) => check.available).map((check) => check.id)))
  const [selectedFormats, setSelectedFormats] = useState<Set<PhotonCadReleaseFormat>>(() => new Set(['step-ap242']))
  const [releaseReview, setReleaseReview] = useState<PhotonCadReleaseReviewResult | null>(null)
  const [releaseMessage, setReleaseMessage] = useState('')
  const [stepExportResult, setStepExportResult] = useState<PhotonCadStepExportResult | null>(null)
  const [stepExportMessage, setStepExportMessage] = useState('')
  const [releaseOutput, setReleaseOutput] = useState<PhotonCadReleaseOutput>(initialReleaseOutput)
  const [bomExportFormat, setBomExportFormat] = useState<PhotonCadBomExportFormat>('xlsx')
  const [bomExportReview, setBomExportReview] = useState<PhotonCadBomExportReviewResult | null>(null)
  const [bomExportMessage, setBomExportMessage] = useState('')
  const [commercialDraft, setCommercialDraft] = useState(() => initialCommercialDraft ?? createCommercialDraft(project ?? null, bom, bomDigest))
  const [commercialAction, setCommercialAction] = useState<PhotonCadCommercialAction>('export-pdf')
  const [commercialReview, setCommercialReview] = useState<PhotonCadCommercialReviewResult | null>(null)
  const [commercialApproval, setCommercialApproval] = useState<PhotonCadCommercialApprovalResult | null>(null)
  const [viewedPageKeys, setViewedPageKeys] = useState<Set<string>>(() => new Set())
  const [commercialMessage, setCommercialMessage] = useState('')
  const [outputBusy, setOutputBusy] = useState<'bom-review' | 'bom-commit' | 'commercial-review' | 'commercial-approve' | 'commercial-commit' | null>(null)
  const [busy, setBusy] = useState<'describe' | 'operation' | 'verify' | 'review' | 'commit' | 'step-export' | null>(null)
  const [notice, setNotice] = useState<{ kind: 'status' | 'error'; text: string } | null>(null)
  const [paneLayout, setPaneLayout] = useState(loadPhotonCadPaneLayout)
  const stageRefs = useRef<Array<HTMLButtonElement | null>>([])
  const paneResizeRef = useRef<{ pane: 'context' | 'inspector'; pointerId: number; startX: number; startWidth: number; scale: number } | null>(null)
  const requestGeneration = useRef({ operation: 0, verify: 0, release: 0, stepExport: 0 })
  const ownedReviewHandle = useRef<string | null>(null)
  const ownedBomReviewHandle = useRef<string | null>(null)
  const ownedCommercialHandle = useRef<string | null>(null)

  useEffect(() => {
    savePhotonCadPaneLayout(paneLayout)
  }, [paneLayout])
  const outputGeneration = useRef({ bom: 0, commercial: 0 })
  const releaseBinding = useRef('')
  const commercialSourceBinding = useRef('')
  const commercialOutputBinding = useRef('')
  const mounted = useRef(true)

  useEffect(() => { if (runtime) setRuntimeView(runtime) }, [runtime])
  useEffect(() => { setCurrentProject(project ?? null) }, [project])
  useEffect(() => { setVerificationView(verification ?? null) }, [verification])

  const catalog = useMemo(
    () => effectiveRuntime.catalog ? normalizePhotonCadCatalog(effectiveRuntime.catalog) : null,
    [effectiveRuntime.catalog],
  )
  const capabilities = catalog?.capabilities ?? []
  const selectedCapability = capabilities.find((capability) => capability.id === selectedCapabilityId) ?? capabilities[0] ?? null
  const selectedCapabilityAuthorization = selectedCapability && catalog
    ? photonCadCapabilityAuthorizationFingerprint(catalog.catalogRevision, selectedCapability)
    : ''
  useEffect(() => {
    if (!selectedCapability) {
      setSelectedCapabilityId('')
      setInputs({})
      return
    }
    if (selectedCapability.id !== selectedCapabilityId) setSelectedCapabilityId(selectedCapability.id)
    setInputs(photonCadInitialCapabilityInputs(selectedCapability))
  }, [selectedCapabilityAuthorization])

  useEffect(() => {
    if (activeStage !== 'design') return
    const manualCapabilityId = photonCadManualDesignCapabilityId(capabilities, selectedCapabilityId)
    if (manualCapabilityId && manualCapabilityId !== selectedCapabilityId) setSelectedCapabilityId(manualCapabilityId)
  }, [activeStage, capabilities, selectedCapabilityId])

  useEffect(() => {
    if (runtime || !controller) return
    let active = true
    const generation = ++requestGeneration.current.operation
    setBusy('describe')
    setRuntimeView(defaultRuntime(controller))
    void controller.describe().then((description) => {
      if (active && generation === requestGeneration.current.operation) setRuntimeView(description)
    }).catch(() => {
      if (active && generation === requestGeneration.current.operation) {
        setRuntimeView({ contractVersion: PHOTON_CAD_CONTRACT_VERSION, status: 'error', reason: 'unavailable' })
      }
    }).finally(() => {
      if (active && generation === requestGeneration.current.operation) setBusy(null)
    })
    return () => { active = false }
  }, [controller, runtime])

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      requestGeneration.current.operation += 1
      requestGeneration.current.verify += 1
      requestGeneration.current.release += 1
      requestGeneration.current.stepExport += 1
      outputGeneration.current.bom += 1
      outputGeneration.current.commercial += 1
      if (ownedReviewHandle.current && controller) void controller.discardRelease(ownedReviewHandle.current)
      if (ownedBomReviewHandle.current && commercialController) void commercialController.discardBomExport(ownedBomReviewHandle.current)
      if (ownedCommercialHandle.current && commercialController) void commercialController.discardCommercialDocument(ownedCommercialHandle.current)
      ownedReviewHandle.current = null
      ownedBomReviewHandle.current = null
      ownedCommercialHandle.current = null
    }
  }, [controller, commercialController])

  useEffect(() => {
    setReleaseReview(null)
    setReleaseMessage('')
  }, [controller])

  useEffect(() => {
    setBomExportReview(null)
    setBomExportMessage('')
    setCommercialReview(null)
    setCommercialApproval(null)
    setViewedPageKeys(new Set())
    setCommercialMessage('')
  }, [commercialController])

  const currentReleaseBinding = `${currentProject?.projectId ?? ''}:${currentProject?.revision ?? ''}:${destination?.handle ?? ''}`
  useEffect(() => {
    if (releaseBinding.current && releaseBinding.current !== currentReleaseBinding) discardOwnedReview()
    releaseBinding.current = currentReleaseBinding
  }, [currentReleaseBinding])

  const bomSourceSignature = JSON.stringify(bom)
  const currentCommercialSourceBinding = `${currentProject?.projectId ?? ''}:${currentProject?.revision ?? ''}:${bomDigest}:${bomSourceSignature}`
  useEffect(() => {
    if (commercialSourceBinding.current && commercialSourceBinding.current !== currentCommercialSourceBinding) {
      discardOwnedBomReview()
      discardOwnedCommercialReview()
      setCommercialDraft(createCommercialDraft(currentProject, bom, bomDigest))
    }
    commercialSourceBinding.current = currentCommercialSourceBinding
  }, [currentCommercialSourceBinding])

  const currentCommercialOutputBinding = `${destination?.handle ?? ''}:${printer?.handle ?? ''}`
  useEffect(() => {
    if (commercialOutputBinding.current && commercialOutputBinding.current !== currentCommercialOutputBinding) {
      discardOwnedBomReview()
      discardOwnedCommercialReview()
    }
    commercialOutputBinding.current = currentCommercialOutputBinding
  }, [currentCommercialOutputBinding])

  const filteredCapabilities = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase()
    if (!needle) return capabilities
    return capabilities.filter((capability) => [
      capability.title,
      capability.description,
      capability.category,
      capability.operation,
      capability.source.package,
    ].some((value) => value.toLocaleLowerCase().includes(needle)))
  }, [capabilities, query])

  const documentTitle = safeText(currentProject?.title, 180) || 'Untitled CAD workspace'
  const selectedTargetsReady = selectedCapability?.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
    || selectedCapability?.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
    ? selectedEntityIds.length === 1 && Boolean(currentProject?.entities.some((entity) =>
        entity.id === selectedEntityIds[0] && entity.kind === 'occurrence'))
    : selectedCapability && photonCadManualModifyCapabilityId(selectedCapability.id)
      ? selectedEntityIds.length === 1 && Boolean(currentProject?.entities.some((entity) =>
          entity.id === selectedEntityIds[0] && (entity.kind === 'body' || entity.kind === 'part')))
      : true
  const requiredInputsReady = Boolean(selectedCapability && currentProject
    && photonCadCapabilityInputsMatch(selectedCapability, inputs, currentProject) && selectedTargetsReady)
  const catalogReady = effectiveRuntime.status === 'available' && Boolean(catalog)
  const selectedCapabilityRunnable = Boolean(selectedCapability && photonCadCapabilityRunnable(selectedCapability))
  const operationReady = catalogReady && Boolean(controller) && persistedScratchAuthorized
    && selectedCapabilityRunnable && currentProject?.mode === 'canonical' && currentProject.units === 'millimeter' && !currentProject.dirty
  const operationContinuationAuthorizationRef = useRef('')
  operationContinuationAuthorizationRef.current = photonCadOperationContinuationAuthorizationBinding(
    selectedCapabilityAuthorization,
    currentProject,
    operationReady,
  )
  const browseOnly = evidenceMode === 'runtime' && catalogReady && !operationReady
  const selectedEntitySet = new Set(selectedEntityIds)
  const stepExportEntity = photonCadStepExportEntity(currentProject, selectedEntityIds)
  const stepExportAvailable = Boolean(controller?.exportStep && persistedScratchAuthorized && currentProject
    && currentProject.mode === 'canonical' && currentProject.units === 'millimeter' && !currentProject.dirty
    && isPhotonCadDigest(projectContentDigest) && stepExportEntity)
  const stepExportBinding = stepExportAvailable && currentProject && stepExportEntity
    ? `${currentProject.sessionId}\n${currentProject.projectId}\n${currentProject.revision}\n${projectContentDigest.toLowerCase()}\n${stepExportEntity.id}`
    : ''
  const stepExportBindingRef = useRef('')
  stepExportBindingRef.current = stepExportBinding

  useEffect(() => {
    requestGeneration.current.stepExport += 1
    setStepExportResult(null)
    setStepExportMessage('')
  }, [stepExportBinding])
  const commercialDraftIssues = useMemo(() => validatePhotonCadCommercialDraft(commercialDraft), [commercialDraft])

  function selectStage(stage: PhotonCadWorkspaceStage, focus = false) {
    const index = stages.findIndex((item) => item.id === stage)
    if (index < 0) return
    setActiveStage(stage)
    if (stage === 'design') {
      const manualCapabilityId = photonCadManualDesignCapabilityId(capabilities, selectedCapabilityId)
      if (manualCapabilityId && manualCapabilityId !== selectedCapabilityId) setSelectedCapabilityId(manualCapabilityId)
    }
    if (focus) queueMicrotask(() => stageRefs.current[index]?.focus())
  }

  function handleStageKey(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    let next = index
    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % stages.length
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index - 1 + stages.length) % stages.length
    else if (event.key === 'Home') next = 0
    else if (event.key === 'End') next = stages.length - 1
    else return
    event.preventDefault()
    selectStage(stages[next].id, true)
  }

  function updateInput(id: string, value: PhotonCadInputValue) {
    setInputs((current) => {
      if (selectedCapability?.id === PHOTON_CAD_MANUAL_ADD_CAPABILITY_ID && id === 'profileKind') {
        if (value === 'rectangle') return {
          ...current,
          profileKind: value,
          profileWidthMm: 10,
          profileHeightMm: 10,
          profileRadiusMm: null,
        }
        if (value === 'circle') return {
          ...current,
          profileKind: value,
          profileWidthMm: null,
          profileHeightMm: null,
          profileRadiusMm: 5,
        }
      }
      return { ...current, [id]: value }
    })
    setNotice(null)
  }

  function toggleEntity(id: string) {
    setSelectedEntityIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id])
  }

  function beginPaneResize(pane: 'context' | 'inspector', event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    const scale = Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--ui-scale')) || 1
    paneResizeRef.current = {
      pane,
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: pane === 'context' ? paneLayout.contextWidth : paneLayout.inspectorWidth,
      scale,
    }
  }

  function movePaneResize(event: PointerEvent<HTMLDivElement>) {
    const active = paneResizeRef.current
    if (!active || active.pointerId !== event.pointerId) return
    const direction = active.pane === 'context' ? 1 : -1
    const next = active.startWidth + ((event.clientX - active.startX) / active.scale) * direction
    setPaneLayout((current) => active.pane === 'context'
      ? { ...current, contextWidth: clampPhotonCadPaneWidth('context', next), contextCollapsed: false }
      : { ...current, inspectorWidth: clampPhotonCadPaneWidth('inspector', next), inspectorCollapsed: false })
  }

  function endPaneResize(event: PointerEvent<HTMLDivElement>) {
    const active = paneResizeRef.current
    if (!active || active.pointerId !== event.pointerId) return
    paneResizeRef.current = null
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
  }

  function resizePaneWithKeyboard(pane: 'context' | 'inspector', event: KeyboardEvent<HTMLDivElement>) {
    const currentWidth = pane === 'context' ? paneLayout.contextWidth : paneLayout.inspectorWidth
    const next = photonCadKeyboardPaneWidth(pane, currentWidth, event.key, event.shiftKey)
    if (next === null) return
    event.preventDefault()
    setPaneLayout((current) => pane === 'context'
      ? { ...current, contextWidth: next, contextCollapsed: false }
      : { ...current, inspectorWidth: next, inspectorCollapsed: false })
  }

  function toggleViewedPage(page: PhotonCadCommercialPreviewPage) {
    const key = commercialPageKey(page)
    setViewedPageKeys((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  async function runOperation(override?: {
    capabilityId: string
    inputs: Record<string, PhotonCadInputValue>
    targetEntityIds?: readonly string[]
  }): Promise<boolean> {
    const operationCapability = override
      ? capabilities.find((capability) => capability.id === override.capabilityId) ?? null
      : selectedCapability
    const operationInputs = override?.inputs ?? inputs
    const operationTargetEntityIds = override?.targetEntityIds
      ? [...override.targetEntityIds]
      : operationCapability?.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
        || operationCapability?.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
        || (operationCapability && photonCadManualModifyCapabilityId(operationCapability.id))
        ? [...selectedEntityIds]
        : []
    const overrideInputsReady = Boolean(operationCapability && currentProject
      && photonCadCapabilityInputsMatch(operationCapability, operationInputs, currentProject)
      && (operationCapability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
        || operationCapability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
        || photonCadManualModifyCapabilityId(operationCapability.id)
        ? operationTargetEntityIds.length === 1 && Boolean(currentProject.entities.some((entity) =>
          entity.id === operationTargetEntityIds[0] && (entity.kind === 'body' || entity.kind === 'part' || entity.kind === 'occurrence')))
        : operationTargetEntityIds.length === 0))
    const runnable = Boolean(operationCapability && photonCadCapabilityRunnable(operationCapability))
    if (!catalogReady || !controller || !currentProject || !operationCapability || !overrideInputsReady || !runnable
      || !persistedScratchAuthorized || currentProject.mode !== 'canonical' || currentProject.units !== 'millimeter' || currentProject.dirty || busy) return false
    const request: PhotonCadOperationRequest = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: requestId('cad-operation'),
      sessionId: currentProject.sessionId,
      projectId: currentProject.projectId,
      baseRevision: currentProject.revision,
      mode: 'scratch',
      capabilityId: operationCapability.id,
      inputs: operationInputs,
      targetEntityIds: operationTargetEntityIds,
    }
    const authorization = photonCadAuthorizePersistedScratchRequest(effectiveRuntime, currentProject, request)
    if (!authorization) {
      setNotice({ kind: 'error', text: 'The host-described catalog or parameter schema is no longer authorized. Design data was not changed.' })
      return false
    }
    const generation = ++requestGeneration.current.operation
    const continuationAuthorizationBinding = photonCadOperationContinuationAuthorizationBinding(
      authorization,
      currentProject,
      true,
    )
    setBusy('operation')
    setNotice(null)
    setOperationIssues([])
    try {
      const result = await controller.execute(request)
      if (!mounted.current || generation !== requestGeneration.current.operation) return false
      if (!override && operationContinuationAuthorizationRef.current !== continuationAuthorizationBinding) {
        setNotice({ kind: 'error', text: 'The host-described catalog changed while the operation was running. Refresh the project before continuing.' })
        return false
      }
      setOperationIssues(result.issues)
      if (result.stale) {
        setNotice({ kind: 'error', text: photonCadReasonText('stale-result') })
      } else if (result.status === 'accepted') {
        const commit = operationCapability.id === PHOTON_CAD_ASSEMBLY_PLACE_CAPABILITY_ID
          || operationCapability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
          || operationCapability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
          ? (() => {
              const snapshot = photonCadAcceptedAssemblySnapshot(request, result, currentProject, previewReceipt)
              return snapshot && result.preview ? { snapshot, preview: result.preview } : null
            })()
          : photonCadAcceptedPersistedScratchCommit(request, result, true)
        if (!commit) {
          setNotice({ kind: 'error', text: 'The CAD operation did not return the exact persisted revision, occurrence, and preview evidence. Design data was not changed.' })
          return false
        }
        setCurrentProject(commit.snapshot)
        setPreviewReceipt(commit.preview)
        setSelectedEntityIds((current) => current.filter((id) => commit.snapshot.entities.some((entity) => entity.id === id)))
        setNotice({
          kind: 'status',
          text: 'The host-authorized scratch operation was persisted. Verification and release remain separate human actions.',
        })
        return true
      } else {
        setNotice({ kind: 'error', text: photonCadReasonText(result.reason) })
      }
    } catch {
      if (mounted.current && generation === requestGeneration.current.operation) {
        setNotice({ kind: 'error', text: 'The CAD operation did not return a usable result. Design data was not changed.' })
      }
    } finally {
      if (mounted.current && generation === requestGeneration.current.operation) setBusy(null)
    }
    return false
  }

  async function runVerification() {
    if (!operationReady || !controller || !currentProject || enabledChecks.size === 0 || busy) return
    const generation = ++requestGeneration.current.verify
    setBusy('verify')
    setNotice(null)
    try {
      const result = await controller.verify({
        contractVersion: PHOTON_CAD_CONTRACT_VERSION,
        requestId: requestId('cad-verify'),
        sessionId: currentProject.sessionId,
        projectId: currentProject.projectId,
        revision: currentProject.revision,
        checks: verificationChecks.map((check) => check.id).filter((id) => enabledChecks.has(id)),
      })
      if (!mounted.current || generation !== requestGeneration.current.verify) return
      if (result.stale) setNotice({ kind: 'error', text: photonCadReasonText('stale-result') })
      else {
        setVerificationView(result)
        setNotice({
          kind: result.status === 'passed' ? 'status' : 'error',
          text: result.status === 'passed'
            ? 'The selected checks passed for this exact revision.'
            : result.status === 'failed'
              ? photonCadReasonText('validation-failed')
              : 'Verification is unavailable. No passing status was recorded.',
        })
      }
    } catch {
      if (mounted.current && generation === requestGeneration.current.verify) {
        setNotice({ kind: 'error', text: 'Verification did not return a usable result. No passing status was recorded.' })
      }
    } finally {
      if (mounted.current && generation === requestGeneration.current.verify) setBusy(null)
    }
  }

  function discardOwnedReview() {
    requestGeneration.current.release += 1
    const handle = ownedReviewHandle.current
    ownedReviewHandle.current = null
    if (handle && controller) void controller.discardRelease(handle)
    setReleaseReview(null)
    setReleaseMessage('')
  }

  function discardOwnedBomReview() {
    outputGeneration.current.bom += 1
    const handle = ownedBomReviewHandle.current
    ownedBomReviewHandle.current = null
    if (handle && commercialController) void commercialController.discardBomExport(handle)
    setBomExportReview(null)
    setBomExportMessage('')
  }

  function discardOwnedCommercialReview() {
    outputGeneration.current.commercial += 1
    const handle = ownedCommercialHandle.current
    ownedCommercialHandle.current = null
    if (handle && commercialController) void commercialController.discardCommercialDocument(handle)
    setCommercialReview(null)
    setCommercialApproval(null)
    setViewedPageKeys(new Set())
    setCommercialMessage('')
  }

  function updateCommercialDraft(update: (current: PhotonCadCommercialDraft) => PhotonCadCommercialDraft) {
    discardOwnedCommercialReview()
    setCommercialDraft(update)
  }

  function chooseBomExportFormat(format: PhotonCadBomExportFormat) {
    discardOwnedBomReview()
    setBomExportFormat(format)
  }

  function chooseCommercialAction(action: PhotonCadCommercialAction) {
    discardOwnedCommercialReview()
    setCommercialAction(action)
  }

  async function prepareBomExport() {
    if (!operationReady || !commercialController || !currentProject || !destination || !isPhotonCadDigest(bomDigest) || outputBusy) return
    discardOwnedBomReview()
    const generation = ++outputGeneration.current.bom
    setOutputBusy('bom-review')
    setNotice(null)
    try {
      const result = await commercialController.reviewBomExport({
        contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
        requestId: requestId('cad-bom-review'),
        projectId: currentProject.projectId,
        projectRevision: currentProject.revision,
        bomDigest,
        format: bomExportFormat,
        destinationHandle: destination.handle,
      })
      if (!mounted.current || generation !== outputGeneration.current.bom) {
        if (result.reviewHandle) void commercialController.discardBomExport(result.reviewHandle)
        return
      }
      if (result.status === 'ready' && result.reviewHandle && result.exportFingerprint) {
        ownedBomReviewHandle.current = result.reviewHandle
        setBomExportMessage('The BOM export plan is ready. No file has been written yet.')
      } else {
        setBomExportMessage(photonCadReasonText(result.reason))
      }
      setBomExportReview(result)
    } catch {
      if (mounted.current && generation === outputGeneration.current.bom) {
        setBomExportMessage('BOM export review failed. No file was written and no success was recorded.')
      }
    } finally {
      if (mounted.current) setOutputBusy((current) => current === 'bom-review' ? null : current)
    }
  }

  async function commitBomExport() {
    if (!operationReady || !commercialController || !bomExportReview?.reviewHandle || !bomExportReview.exportFingerprint || outputBusy) return
    const generation = ++outputGeneration.current.bom
    const handle = bomExportReview.reviewHandle
    ownedBomReviewHandle.current = null
    setOutputBusy('bom-commit')
    setNotice(null)
    try {
      const result = await commercialController.commitBomExport({
        contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
        requestId: requestId('cad-bom-commit'),
        reviewHandle: handle,
        exportFingerprint: bomExportReview.exportFingerprint,
      })
      if (!mounted.current || generation !== outputGeneration.current.bom) return
      setBomExportReview(null)
      setBomExportMessage(photonCadReasonText(result.reason))
      setNotice({ kind: result.status === 'exported' ? 'status' : 'error', text: photonCadReasonText(result.reason) })
    } catch {
      if (mounted.current && generation === outputGeneration.current.bom) {
        setBomExportReview(null)
        setBomExportMessage('BOM export failed. No success was recorded.')
        setNotice({ kind: 'error', text: 'BOM export failed. No success was recorded.' })
      }
    } finally {
      if (mounted.current) setOutputBusy((current) => current === 'bom-commit' ? null : current)
    }
  }

  async function prepareCommercialDocument() {
    const actionReady = commercialAction === 'export-pdf' ? Boolean(destination) : Boolean(printer)
    if (!operationReady || !commercialController || !actionReady || outputBusy || validatePhotonCadCommercialDraft(commercialDraft).length) return
    discardOwnedCommercialReview()
    const generation = ++outputGeneration.current.commercial
    setOutputBusy('commercial-review')
    setNotice(null)
    try {
      const result = await commercialController.reviewCommercialDocument({
        contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
        requestId: requestId('cad-document-review'),
        draft: commercialDraft,
        action: commercialAction === 'export-pdf'
          ? { kind: 'export-pdf', destinationHandle: destination!.handle }
          : { kind: 'print', printerHandle: printer!.handle, copies: 1 },
      })
      if (!mounted.current || generation !== outputGeneration.current.commercial) {
        if (result.reviewHandle) void commercialController.discardCommercialDocument(result.reviewHandle)
        return
      }
      if (result.status === 'ready' && result.reviewHandle && result.documentFingerprint && result.actionFingerprint) {
        ownedCommercialHandle.current = result.reviewHandle
        setCommercialMessage(`Rendered ${result.pages.length} page${result.pages.length === 1 ? '' : 's'} for review. No output has been created.`)
      } else {
        setCommercialMessage(photonCadReasonText(result.reason))
      }
      setCommercialReview(result)
      setCommercialApproval(null)
      setViewedPageKeys(new Set())
    } catch {
      if (mounted.current && generation === outputGeneration.current.commercial) {
        setCommercialMessage('Document review failed. Nothing was exported, printed, sent, posted, charged, or marked paid.')
      }
    } finally {
      if (mounted.current) setOutputBusy((current) => current === 'commercial-review' ? null : current)
    }
  }

  async function approveCommercialDocument() {
    if (!operationReady || !commercialController || !commercialReview?.reviewHandle || !commercialReview.documentFingerprint
      || !commercialReview.actionFingerprint || !commercialPreviewSurface || outputBusy
      || commercialReview.pages.some((page) => !viewedPageKeys.has(commercialPageKey(page)))) return
    const generation = ++outputGeneration.current.commercial
    setOutputBusy('commercial-approve')
    setNotice(null)
    try {
      const result = await commercialController.approveCommercialDocument({
        contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
        requestId: requestId('cad-document-approve'),
        reviewHandle: commercialReview.reviewHandle,
        documentFingerprint: commercialReview.documentFingerprint,
        actionFingerprint: commercialReview.actionFingerprint,
        viewedPageDigests: commercialReview.pages.map((page) => page.contentDigest),
      })
      if (!mounted.current || generation !== outputGeneration.current.commercial) {
        if (result.approvalHandle) void commercialController.discardCommercialDocument(result.approvalHandle)
        return
      }
      if (result.status === 'approved' && result.approvalHandle) {
        ownedCommercialHandle.current = result.approvalHandle
        setCommercialMessage('The exact rendered document and output action are approved. Export or print has not happened yet.')
      } else {
        setCommercialMessage(photonCadReasonText(result.reason))
      }
      setCommercialApproval(result)
    } catch {
      if (mounted.current && generation === outputGeneration.current.commercial) {
        setCommercialMessage('Document approval failed. Nothing was exported or printed.')
      }
    } finally {
      if (mounted.current) setOutputBusy((current) => current === 'commercial-approve' ? null : current)
    }
  }

  async function commitCommercialDocument() {
    if (!operationReady || !commercialController || !commercialApproval?.approvalHandle || !commercialApproval.documentFingerprint
      || !commercialApproval.actionFingerprint || outputBusy) return
    const generation = ++outputGeneration.current.commercial
    const handle = commercialApproval.approvalHandle
    ownedCommercialHandle.current = null
    setOutputBusy('commercial-commit')
    setNotice(null)
    try {
      const result = await commercialController.commitCommercialDocument({
        contractVersion: PHOTON_CAD_COMMERCIAL_CONTRACT_VERSION,
        requestId: requestId('cad-document-commit'),
        approvalHandle: handle,
        documentFingerprint: commercialApproval.documentFingerprint,
        actionFingerprint: commercialApproval.actionFingerprint,
      })
      if (!mounted.current || generation !== outputGeneration.current.commercial) return
      setCommercialReview(null)
      setCommercialApproval(null)
      setViewedPageKeys(new Set())
      setCommercialMessage(photonCadReasonText(result.reason))
      setNotice({ kind: result.status === 'exported' || result.status === 'printed' ? 'status' : 'error', text: photonCadReasonText(result.reason) })
    } catch {
      if (mounted.current && generation === outputGeneration.current.commercial) {
        setCommercialReview(null)
        setCommercialApproval(null)
        setCommercialMessage('Document output failed. Nothing was sent, posted, charged, or marked paid.')
        setNotice({ kind: 'error', text: 'Document output failed. No success was recorded.' })
      }
    } finally {
      if (mounted.current) setOutputBusy((current) => current === 'commercial-commit' ? null : current)
    }
  }

  function toggleFormat(format: PhotonCadReleaseFormat) {
    discardOwnedReview()
    setSelectedFormats((current) => {
      const next = new Set(current)
      if (next.has(format)) next.delete(format)
      else next.add(format)
      return next
    })
  }

  async function prepareRelease() {
    if (!operationReady || !controller || !currentProject || !destination || selectedFormats.size === 0 || busy) return
    discardOwnedReview()
    const generation = ++requestGeneration.current.release
    setBusy('review')
    setNotice(null)
    try {
      const result = await controller.reviewRelease({
        contractVersion: PHOTON_CAD_CONTRACT_VERSION,
        requestId: requestId('cad-release-review'),
        sessionId: currentProject.sessionId,
        projectId: currentProject.projectId,
        revision: currentProject.revision,
        formats: releaseFormats.map((format) => format.id).filter((id) => selectedFormats.has(id)),
        destinationHandle: destination.handle,
      })
      if (!mounted.current || generation !== requestGeneration.current.release) {
        if (result.reviewHandle) void controller.discardRelease(result.reviewHandle)
        return
      }
      if (result.status === 'ready' && result.reviewHandle && result.packageFingerprint) {
        ownedReviewHandle.current = result.reviewHandle
        setReleaseReview(result)
        setReleaseMessage('The package plan is ready for explicit creation. No files have been written yet.')
      } else {
        setReleaseReview(result)
        setReleaseMessage(photonCadReasonText(result.reason))
      }
    } catch {
      if (mounted.current && generation === requestGeneration.current.release) {
        setReleaseMessage('Release review failed. No package was written and no success was recorded.')
      }
    } finally {
      if (mounted.current) setBusy((current) => current === 'review' ? null : current)
    }
  }

  async function commitRelease() {
    if (!operationReady || !controller || !releaseReview?.reviewHandle || !releaseReview.packageFingerprint || busy) return
    const generation = ++requestGeneration.current.release
    setBusy('commit')
    setNotice(null)
    const handle = releaseReview.reviewHandle
    ownedReviewHandle.current = null
    try {
      const result = await controller.commitRelease({
        contractVersion: PHOTON_CAD_CONTRACT_VERSION,
        requestId: requestId('cad-release-commit'),
        reviewHandle: handle,
        packageFingerprint: releaseReview.packageFingerprint,
      })
      if (!mounted.current || generation !== requestGeneration.current.release) return
      setReleaseReview(null)
      setReleaseMessage(photonCadReasonText(result.reason))
      setNotice({ kind: result.status === 'committed' ? 'status' : 'error', text: photonCadReasonText(result.reason) })
    } catch {
      if (mounted.current && generation === requestGeneration.current.release) {
        setReleaseReview(null)
        setReleaseMessage(photonCadReasonText('commit-failed'))
        setNotice({ kind: 'error', text: photonCadReasonText('commit-failed') })
      }
    } finally {
      if (mounted.current) setBusy((current) => current === 'commit' ? null : current)
    }
  }

  async function exportSelectedStep() {
    if (!controller?.exportStep || !currentProject || !stepExportEntity || !stepExportAvailable || busy) return
    const request: PhotonCadStepExportRequest = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      requestId: requestId('cad-step-export'),
      sessionId: currentProject.sessionId,
      projectId: currentProject.projectId,
      revision: currentProject.revision,
      contentDigest: projectContentDigest,
      entityId: stepExportEntity.id,
    }
    const binding = stepExportBinding
    const generation = ++requestGeneration.current.stepExport
    setBusy('step-export')
    setStepExportResult(null)
    setStepExportMessage('')
    try {
      const result = await controller.exportStep(request)
      if (!mounted.current || generation !== requestGeneration.current.stepExport || binding !== stepExportBindingRef.current) return
      if (!photonCadStepExportResultMatches(request, result)) {
        setStepExportMessage('The native STEP export result did not match the selected entity and project revision. No success was accepted.')
        return
      }
      setStepExportResult(result)
      setStepExportMessage(result.status === 'committed'
        ? `STEP export committed as ${result.destinationLabel}.`
        : result.status === 'cancelled'
          ? 'STEP export was cancelled. No file success was recorded.'
          : photonCadReasonText(result.reason))
    } catch {
      if (mounted.current && generation === requestGeneration.current.stepExport) {
        setStepExportMessage('The native STEP export did not return a usable result. No file success was recorded.')
      }
    } finally {
      if (mounted.current && generation === requestGeneration.current.stepExport) setBusy(null)
    }
  }

  const previewContext: PhotonCadPreviewContext = {
    project: currentProject,
    receipt: previewReceipt,
    selectedEntityIds,
    stage: activeStage,
    acceptHydratedReceipt: (receipt) => {
      if (!currentProject || receipt.projectId !== currentProject.projectId || receipt.revision !== currentProject.revision) return false
      setPreviewReceipt(receipt)
      return true
    },
    runManualSketch: (capabilityId, manualInputs, targetEntityIds = []) => runOperation({
      capabilityId,
      inputs: manualInputs,
      targetEntityIds,
    }),
    manualSketchReady: activeStage === 'design' && operationReady,
    operationBusy: busy === 'operation',
  }

  return (
    <main
      className={`photon-cad-workspace ${className}`.trim()}
      aria-label={`${documentTitle} CAD workspace`}
      data-stage={activeStage}
      data-runtime={effectiveRuntime.status}
      data-operation-access={operationReady ? 'enabled' : 'locked'}
    >
      {evidenceMode === 'fixture' ? (
        <div className="pcad-fixture-banner" role="note">
          <Gauge size={13} /><strong>Demonstration fixture</strong><span>No CAD runtime action, geometry render, verification pass, or release success is implied.</span>
        </div>
      ) : null}

      {browseOnly ? (
        <div className="pcad-fixture-banner" role="note">
          <BookOpen size={13} /><strong>Browse-only catalog</strong><span>Inspect tools, provenance, saved hierarchy, and BOM data. No model, verification, release, BOM export, or commercial action can run in this state.</span>
        </div>
      ) : null}

      <nav className="pcad-workflow-tabs" role="tablist" aria-label="CAD workflow">
        {stages.map(({ id, label, detail, icon: Icon }, index) => (
          <button
            type="button"
            id={`${workspaceId}-pcad-tab-${id}`}
            role="tab"
            aria-selected={activeStage === id}
            aria-controls={activeStage === id ? `${workspaceId}-pcad-panel-${id}` : undefined}
            tabIndex={activeStage === id ? 0 : -1}
            className={activeStage === id ? 'active' : ''}
            key={id}
            ref={(element) => { stageRefs.current[index] = element }}
            onClick={() => selectStage(id)}
            onKeyDown={(event) => handleStageKey(event, index)}
          >
            <Icon size={14} aria-hidden="true" />
            <span><strong>{label}</strong><small>{detail}</small></span>
          </button>
        ))}
      </nav>

      <section
        id={`${workspaceId}-pcad-panel-${activeStage}`}
        role="tabpanel"
        aria-labelledby={`${workspaceId}-pcad-tab-${activeStage}`}
        className={`pcad-stage ${paneLayout.contextCollapsed ? 'context-collapsed' : ''} ${paneLayout.inspectorCollapsed ? 'inspector-collapsed' : ''}`}
        style={{
          '--pcad-context-width': paneLayout.contextCollapsed ? '0px' : `${paneLayout.contextWidth}px`,
          '--pcad-inspector-width': paneLayout.inspectorCollapsed ? '0px' : `${paneLayout.inspectorWidth}px`,
        } as CSSProperties}
      >
        <div className="pcad-pane-layout-controls" role="group" aria-label="CAD work area visibility">
          <button
            type="button"
            aria-pressed={!paneLayout.contextCollapsed}
            title={paneLayout.contextCollapsed ? 'Restore parts and context pane' : 'Collapse parts and context pane'}
            onClick={() => setPaneLayout((current) => ({ ...current, contextCollapsed: !current.contextCollapsed }))}
          ><PanelLeftClose size={12} /><span>Parts</span></button>
          <button
            type="button"
            aria-pressed={!paneLayout.inspectorCollapsed}
            title={paneLayout.inspectorCollapsed ? 'Restore parameter inspector' : 'Collapse parameter inspector'}
            onClick={() => setPaneLayout((current) => ({ ...current, inspectorCollapsed: !current.inspectorCollapsed }))}
          ><PanelRightClose size={12} /><span>Inspector</span></button>
        </div>
        <aside className="pcad-context-pane" aria-label={`${stages.find((item) => item.id === activeStage)?.label} context`}>
          {activeStage === 'library' ? (
            <CatalogPane
              catalog={catalog}
              capabilities={filteredCapabilities}
              project={currentProject}
              query={query}
              selectedCapabilityId={selectedCapability?.id ?? ''}
              onQueryChange={setQuery}
              onSelectCapability={setSelectedCapabilityId}
            />
          ) : activeStage === 'design' ? (
            <>
              <ManualDesignTools
                capabilities={capabilities}
                selectedCapabilityId={selectedCapability?.id ?? ''}
                onSelectCapability={setSelectedCapabilityId}
              />
              <ModelTreePane
                project={currentProject}
                selectedEntityIds={selectedEntitySet}
                assemblyOnly={false}
                onToggle={toggleEntity}
              />
            </>
          ) : activeStage === 'assemble' ? (
            <ModelTreePane
              project={currentProject}
              selectedEntityIds={selectedEntitySet}
              assemblyOnly
              onToggle={toggleEntity}
            />
          ) : activeStage === 'verify' ? (
            <VerificationChecklist checks={enabledChecks} disabled={!operationReady} onChange={setEnabledChecks} />
          ) : (
            <>
              <ReleaseOutputPicker
                value={releaseOutput}
                disabled={!operationReady || Boolean(busy === 'review' || busy === 'commit' || outputBusy)}
                onChange={setReleaseOutput}
              />
              {releaseOutput === 'cad-package' ? (
                <ReleasePlan
                  project={currentProject}
                  selectedEntityIds={selectedEntitySet}
                  onToggleEntity={toggleEntity}
                />
              ) : releaseOutput === 'bom-export' ? (
                <BomExportPlan
                  format={bomExportFormat}
                  destination={destination}
                  disabled={!operationReady || !commercialController || Boolean(outputBusy)}
                  bomReady={bom.length > 0 && isPhotonCadDigest(bomDigest)}
                  onFormat={chooseBomExportFormat}
                  onChooseDestination={onChooseDestination}
                />
              ) : (
                <CommercialDraftEditor
                  draft={commercialDraft}
                  action={commercialAction}
                  destination={destination}
                  printer={printer}
                  disabled={!operationReady || !commercialController || Boolean(outputBusy)}
                  onChange={updateCommercialDraft}
                  onAction={chooseCommercialAction}
                  onChooseDestination={onChooseDestination}
                  onChoosePrinter={onChoosePrinter}
                />
              )}
            </>
          )}
        </aside>

        {!paneLayout.contextCollapsed ? (
          <div
            className="pcad-pane-resizer pcad-pane-resizer--context"
            role="separator"
            aria-label="Resize parts and context pane"
            aria-orientation="vertical"
            aria-valuemin={PHOTON_CAD_PANE_BOUNDS.context.minimum}
            aria-valuemax={PHOTON_CAD_PANE_BOUNDS.context.maximum}
            aria-valuenow={paneLayout.contextWidth}
            tabIndex={0}
            onPointerDown={(event) => beginPaneResize('context', event)}
            onPointerMove={movePaneResize}
            onPointerUp={endPaneResize}
            onPointerCancel={endPaneResize}
            onKeyDown={(event) => resizePaneWithKeyboard('context', event)}
          />
        ) : null}

        {activeStage === 'release' && releaseOutput === 'commercial-document' ? (
          <CommercialPreviewPane
            review={commercialReview}
            draft={commercialDraft}
            previewSurface={commercialPreviewSurface}
            viewedPageKeys={viewedPageKeys}
            locked={Boolean(commercialApproval || outputBusy)}
            onToggleViewed={toggleViewedPage}
          />
        ) : <PreviewPane context={previewContext} previewSurface={previewSurface} />}

        {!paneLayout.inspectorCollapsed ? (
          <div
            className="pcad-pane-resizer pcad-pane-resizer--inspector"
            role="separator"
            aria-label="Resize parameter inspector"
            aria-orientation="vertical"
            aria-valuemin={PHOTON_CAD_PANE_BOUNDS.inspector.minimum}
            aria-valuemax={PHOTON_CAD_PANE_BOUNDS.inspector.maximum}
            aria-valuenow={paneLayout.inspectorWidth}
            tabIndex={0}
            onPointerDown={(event) => beginPaneResize('inspector', event)}
            onPointerMove={movePaneResize}
            onPointerUp={endPaneResize}
            onPointerCancel={endPaneResize}
            onKeyDown={(event) => resizePaneWithKeyboard('inspector', event)}
          />
        ) : null}

        <aside className="pcad-inspector-pane" aria-label={`${stages.find((item) => item.id === activeStage)?.label} details`}>
          {activeStage === 'library' || activeStage === 'design' ? (
            <CapabilityInspector
              capability={selectedCapability}
              project={currentProject}
              inputs={inputs}
              selectedEntityIds={selectedEntityIds}
              issues={[...(currentProject?.issues ?? []), ...operationIssues]}
              operationReady={operationReady}
              busy={busy === 'operation'}
              requiredInputsReady={requiredInputsReady}
              manualDesign={activeStage === 'design'
                && selectedCapability !== null
                && photonCadManualDesignCapability(selectedCapability)}
              onInput={updateInput}
              onRun={() => void runOperation()}
            />
          ) : activeStage === 'assemble' ? (
            <BomPane bom={bom} project={currentProject} />
          ) : activeStage === 'verify' ? (
            <VerificationPane
              result={verificationView}
              project={currentProject}
              projectIssues={currentProject?.issues ?? []}
              enabledCheckCount={enabledChecks.size}
              available={Boolean(operationReady && currentProject)}
              busy={busy === 'verify'}
              onRun={() => void runVerification()}
            />
          ) : releaseOutput === 'cad-package' ? (
            <ReleasePane
              stepExportEntity={stepExportEntity}
              stepExportResult={stepExportResult}
              stepExportMessage={stepExportMessage}
              stepExportAvailable={stepExportAvailable}
              stepExportBusy={busy === 'step-export'}
              onExportStep={() => void exportSelectedStep()}
            />
          ) : releaseOutput === 'bom-export' ? (
            <BomExportPane
              review={bomExportReview}
              message={bomExportMessage}
              available={Boolean(operationReady && commercialController && currentProject && bom.length && isPhotonCadDigest(bomDigest))}
              destinationReady={Boolean(destination)}
              busy={outputBusy === 'bom-review' || outputBusy === 'bom-commit'}
              onPrepare={() => void prepareBomExport()}
              onCommit={() => void commitBomExport()}
            />
          ) : (
            <CommercialReviewPane
              review={commercialReview}
              approval={commercialApproval}
              message={commercialMessage}
              draftIssues={commercialDraftIssues}
              currency={commercialDraft.currency}
              currencyScale={commercialDraft.currencyScale}
              actionReady={commercialAction === 'export-pdf' ? Boolean(destination) : Boolean(printer)}
              controllerReady={Boolean(operationReady && commercialController)}
              previewReady={Boolean(commercialPreviewSurface)}
              allPagesViewed={Boolean(commercialReview?.pages.length && commercialReview.pages.every((page) => viewedPageKeys.has(commercialPageKey(page))))}
              busy={outputBusy}
              onPrepare={() => void prepareCommercialDocument()}
              onApprove={() => void approveCommercialDocument()}
              onCommit={() => void commitCommercialDocument()}
            />
          )}
        </aside>
      </section>

      <footer className="pcad-statusbar">
        <span className={`pcad-status-dot ${effectiveRuntime.status}`} aria-hidden="true" />
        <span>{runtimeCopy(effectiveRuntime, catalogReady, operationReady, evidenceMode)}</span>
        {notice ? <strong className={notice.kind} role={notice.kind === 'error' ? 'alert' : 'status'} aria-live="polite">{notice.text}</strong> : null}
        <span className="pcad-status-spacer" />
        {currentProject?.dirty ? <span className="pcad-unsaved-status">Unsaved changes</span> : null}
        <span>{currentProject ? `Revision ${currentProject.revision}` : 'No project snapshot'}</span>
        <span>{currentProject ? unitAbbreviation(currentProject) : 'Units unavailable'}</span>
        <span>{currentProject?.mode === 'scratch' ? 'Scratch workspace' : 'Canonical workspace'}</span>
        <span>{catalog ? `${catalog.capabilities.length} catalog operations` : 'No catalog evidence'}</span>
        <span>{currentProject ? `${currentProject.entities.length} entities` : 'No geometry snapshot'}</span>
      </footer>
    </main>
  )
}

function CatalogPane({
  catalog,
  capabilities,
  project,
  query,
  selectedCapabilityId,
  onQueryChange,
  onSelectCapability,
}: {
  catalog: ReturnType<typeof normalizePhotonCadCatalog>
  capabilities: PhotonCadCapability[]
  project: PhotonCadProjectSnapshot | null
  query: string
  selectedCapabilityId: string
  onQueryChange: (value: string) => void
  onSelectCapability: (id: string) => void
}) {
  const [visibleLimit, setVisibleLimit] = useState(PHOTON_CAD_CATALOG_PAGE_SIZE)
  const sections = photonCadCatalogSections(capabilities)
  const projectParts = photonCadProjectParts(project, query)
  const selectedSectionId = sections.find((section) => section.capabilities.some((capability) => capability.id === selectedCapabilityId))?.id
  const [expandedSections, setExpandedSections] = useState<Set<string>>(() => new Set([
    ...(projectParts.length ? ['project-parts'] : []),
    ...(selectedSectionId ? [selectedSectionId] : []),
  ]))
  const shownCapabilities = capabilities.slice(0, visibleLimit)
  const hiddenCapabilityCount = Math.max(0, capabilities.length - shownCapabilities.length)
  const shownCapabilityIds = new Set(shownCapabilities.map((capability) => capability.id))
  const shownSections = sections
    .map((section) => ({ ...section, capabilities: section.capabilities.filter((capability) => shownCapabilityIds.has(capability.id)) }))
    .filter((section) => section.capabilities.length > 0)
  const queryActive = Boolean(query.trim())

  useEffect(() => {
    setVisibleLimit(PHOTON_CAD_CATALOG_PAGE_SIZE)
  }, [query, capabilities.length])

  useEffect(() => {
    if (!selectedSectionId) return
    setExpandedSections((current) => current.has(selectedSectionId) ? current : new Set([...current, selectedSectionId]))
  }, [selectedSectionId])

  const setSectionExpanded = (sectionId: string, expanded: boolean) => {
    if (queryActive) return
    setExpandedSections((current) => {
      const next = new Set(current)
      if (expanded) next.add(sectionId)
      else next.delete(sectionId)
      return next
    })
  }

  const libraryLabel = (library: PhotonCadCatalogSection['library']) => {
    if (library === 'build123d') return 'Build123d generator'
    if (library === 'bd-warehouse') return 'BD Warehouse'
    if (library === 'assembly') return 'Assembly tools'
    return 'Verified catalog'
  }
  return (
    <>
      <header className="pcad-pane-header">
        <span><BookOpen size={14} /><strong>Parts library</strong></span>
        <small>{catalog ? `${catalog.coverage.available} available${catalog.coverage.unavailable ? ` · ${catalog.coverage.unavailable} unavailable` : ''}` : 'Unavailable'}</small>
      </header>
      <label className="pcad-search">
        <Search size={13} aria-hidden="true" />
        <span className="pcad-visually-hidden">Search CAD parts and tools</span>
        <input value={query} placeholder="Search parts, tools, operations…" onChange={(event) => onQueryChange(event.target.value)} />
      </label>
      <div className="pcad-catalog-tree" aria-label="Part library categories">
        {projectParts.length ? (
          <details
            className="pcad-catalog-group project"
            open={queryActive || expandedSections.has('project-parts')}
            onToggle={(event) => setSectionExpanded('project-parts', event.currentTarget.open)}
          >
            <summary><span><PackageCheck size={13} /><strong>Project parts</strong><small>Current project</small></span><b>{projectParts.length}</b></summary>
            <div className="pcad-catalog-group-body pcad-project-parts">
              <p>Reusable definitions already sealed in this project. Place another occurrence from Assemble.</p>
              {projectParts.map((part) => (
                <article className="pcad-project-part" key={part.id}>
                  <span className="pcad-capability-icon"><PackageCheck size={14} /></span>
                  <span><strong>{safeText(part.name, 180)}</strong><small>{part.kind} · reusable source</small></span>
                  <i>{part.occurrenceCount} placed</i>
                </article>
              ))}
            </div>
          </details>
        ) : null}
        {shownCapabilities.length ? shownSections.map((section) => (
          <details
            className="pcad-catalog-group"
            data-library={section.library}
            key={section.id}
            open={queryActive || expandedSections.has(section.id)}
            onToggle={(event) => setSectionExpanded(section.id, event.currentTarget.open)}
          >
            <summary>
              <span>{section.library === 'assembly' ? <Layers3 size={13} /> : <Box size={13} />}<strong>{section.label}</strong><small>{libraryLabel(section.library)}</small></span>
              <b>{section.capabilities.length}</b>
            </summary>
            <div className="pcad-catalog-group-body">
              {section.capabilities.map((capability) => (
              <button
                type="button"
                className={selectedCapabilityId === capability.id ? 'selected' : ''}
                aria-pressed={selectedCapabilityId === capability.id}
                key={capability.id}
                onClick={() => onSelectCapability(capability.id)}
              >
                <span className="pcad-capability-icon">{capability.backend === 'assembly' ? <Layers3 size={14} /> : <Box size={14} />}</span>
                <span>
                  <strong>{safeText(capability.title, 180)}</strong>
                  <small>{safeText(capability.category, 90)} · {safeText(capability.operation, 40)}</small>
                </span>
                {capability.experimental ? <i>Experimental</i> : null}
              </button>
              ))}
            </div>
          </details>
        )) : !projectParts.length ? (
          <div className="pcad-empty compact"><Search size={20} /><strong>No matching part or tool</strong><p>Change the search or choose another library category.</p></div>
        ) : null}
        {hiddenCapabilityCount ? (
          <button className="pcad-catalog-more" type="button" onClick={() => setVisibleLimit((current) => current + PHOTON_CAD_CATALOG_PAGE_SIZE)}>
            Show {Math.min(PHOTON_CAD_CATALOG_PAGE_SIZE, hiddenCapabilityCount)} more <span>{hiddenCapabilityCount} remaining</span>
          </button>
        ) : null}
      </div>
    </>
  )
}

function ManualDesignTools({
  capabilities,
  selectedCapabilityId,
  onSelectCapability,
}: {
  capabilities: readonly PhotonCadCapability[]
  selectedCapabilityId: string
  onSelectCapability: (id: string) => void
}) {
  const tools = capabilities.filter(photonCadManualDesignCapability)
  return (
    <section className="pcad-manual-design" aria-label="Manual solid tools">
      <header className="pcad-pane-header">
        <span><Wrench size={14} /><strong>Manual solid tools</strong></span>
        <small>{tools.length} available</small>
      </header>
      <p>Create or modify an exact parametric B-rep solid, then inspect the persisted body in the model tree.</p>
      <div role="group" aria-label="Build123d solid constructors">
        {tools.map((capability) => (
          <button
            type="button"
            aria-pressed={selectedCapabilityId === capability.id}
            className={selectedCapabilityId === capability.id ? 'selected' : ''}
            key={capability.id}
            onClick={() => onSelectCapability(capability.id)}
          >{capability.operation === 'create' ? <Plus size={12} /> : <Wrench size={12} />}<span><strong>{safeText(capability.title, 80)}</strong><small>{capability.parameters.length} dimensions</small></span></button>
        ))}
      </div>
      {!tools.length ? <small>No verified manual solid constructor is mounted.</small> : null}
    </section>
  )
}

function ModelTreePane({
  project,
  selectedEntityIds,
  assemblyOnly,
  onToggle,
}: {
  project: PhotonCadProjectSnapshot | null
  selectedEntityIds: Set<string>
  assemblyOnly: boolean
  onToggle: (id: string) => void
}) {
  const entities = project?.entities.filter((entity) => !assemblyOnly || ['assembly', 'occurrence', 'part'].includes(entity.kind)) ?? []
  const [focusIndex, setFocusIndex] = useState(0)
  const entityRefs = useRef<Array<HTMLButtonElement | null>>([])

  useEffect(() => {
    if (focusIndex >= entities.length) setFocusIndex(Math.max(0, entities.length - 1))
  }, [entities.length, focusIndex])

  function handleEntityKey(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (!entities.length) return
    let next = index
    if (event.key === 'ArrowDown') next = Math.min(index + 1, entities.length - 1)
    else if (event.key === 'ArrowUp') next = Math.max(index - 1, 0)
    else if (event.key === 'Home') next = 0
    else if (event.key === 'End') next = entities.length - 1
    else return
    event.preventDefault()
    setFocusIndex(next)
    entityRefs.current[next]?.focus()
  }

  return (
    <>
      <header className="pcad-pane-header">
        <span>{assemblyOnly ? <Layers3 size={14} /> : <Box size={14} />}<strong>{assemblyOnly ? 'Assembly context' : 'Model tree'}</strong></span>
        <small>{entities.length} entities</small>
      </header>
      {project && entities.length ? (
        <div className="pcad-model-tree" role="tree" aria-label={assemblyOnly ? 'Assembly entities' : 'Design entities'} aria-multiselectable="true">
          {entities.map((entity, index) => {
            const selected = selectedEntityIds.has(entity.id)
            return (
              <button
                type="button"
                role="treeitem"
                aria-selected={selected}
                aria-level={entityDepth(entity.id, project) + 1}
                tabIndex={focusIndex === index ? 0 : -1}
                className={selected ? 'selected' : ''}
                style={{ '--pcad-depth': entityDepth(entity.id, project) } as CSSProperties}
                key={entity.id}
                ref={(element) => { entityRefs.current[index] = element }}
                onClick={() => onToggle(entity.id)}
                onFocus={() => setFocusIndex(index)}
                onKeyDown={(event) => handleEntityKey(event, index)}
              >
                <span>{entity.kind === 'assembly' || entity.kind === 'occurrence' ? <Layers3 size={13} /> : <Box size={13} />}</span>
                <span><strong>{safeText(entity.name, 180)}</strong><small>{safeText(entity.kind, 40)}</small></span>
                {!entity.visible ? <i>Hidden</i> : entity.suppressed ? <i>Suppressed</i> : null}
              </button>
            )
          })}
        </div>
      ) : (
        <div className="pcad-empty"><Box size={24} /><strong>No {assemblyOnly ? 'assembly' : 'model'} snapshot</strong><p>Open or create a project through the CAD runtime before editing structure.</p></div>
      )}
      <p className="pcad-pane-note">Selection identifies operation targets. It does not change visibility, suppression, or model ownership.</p>
    </>
  )
}

function PreviewPane({ context, previewSurface }: { context: PhotonCadPreviewContext; previewSurface?: PhotonCadWorkspaceProps['previewSurface'] }) {
  const evidenceId = useId()
  const rendered = typeof previewSurface === 'function' ? previewSurface(context) : previewSurface
  return (
    <section className="pcad-preview-pane" aria-label="3D preview" aria-describedby={evidenceId}>
      <header>
        <span><Box size={14} /><strong>Model preview</strong></span>
        <div>
          <span>{context.project ? unitAbbreviation(context.project) : 'Units unavailable'}</span>
          <span>{context.receipt ? `${context.receipt.entityCount} preview entities` : 'No preview receipt'}</span>
        </div>
      </header>
      <div className="pcad-preview-boundary" data-preview={context.receipt ? 'receipt' : 'empty'}>
        {rendered ?? (
          <div className="pcad-preview-empty">
            <span className="pcad-preview-orbit"><Box size={42} /></span>
            <strong>No geometry renderer connected</strong>
            <p>This bounded surface is reserved for a native or injected viewer. It does not execute scripts or infer geometry from labels.</p>
          </div>
        )}
      </div>
      <footer id={evidenceId}>
        {context.receipt ? (
          <><CheckCircle2 size={12} /><span>Preview receipt matches project <strong>{safeText(context.receipt.projectId, 80)}</strong>, revision {context.receipt.revision}. Preview is not verification.</span></>
        ) : (
          <><CircleAlert size={12} /><span>No renderer evidence is available. Preview visibility is never treated as a passing check.</span></>
        )}
      </footer>
    </section>
  )
}

function CapabilityInspector({
  capability,
  project,
  inputs,
  selectedEntityIds,
  issues,
  operationReady,
  busy,
  requiredInputsReady,
  manualDesign = false,
  onInput,
  onRun,
}: {
  capability: PhotonCadCapability | null
  project: PhotonCadProjectSnapshot | null
  inputs: Record<string, PhotonCadInputValue>
  selectedEntityIds: string[]
  issues: PhotonCadIssue[]
  operationReady: boolean
  busy: boolean
  requiredInputsReady: boolean
  manualDesign?: boolean
  onInput: (id: string, value: PhotonCadInputValue) => void
  onRun: () => void
}) {
  if (!capability) return <div className="pcad-empty"><SlidersHorizontal size={24} /><strong>No operation selected</strong><p>Select an available catalog operation to inspect its declared parameters and provenance.</p></div>
  const mode = project?.mode ?? 'canonical'
  return (
    <>
      <header className="pcad-pane-header">
        <span><SlidersHorizontal size={14} /><strong>Parameter inspector</strong></span>
        <small>{capability.parameters.length} fields</small>
      </header>
      <div className="pcad-inspector-scroll">
        <section className="pcad-capability-summary">
          <div><strong>{safeText(capability.title, 180)}</strong>{capability.experimental ? <span>Experimental</span> : null}</div>
          <p>{safeText(capability.description) || 'No operation description was supplied.'}</p>
          <dl>
            <div><dt>Backend</dt><dd>{safeText(capability.backend, 40)}</dd></div>
            <div><dt>Operation</dt><dd>{safeText(capability.operation, 40)}</dd></div>
            <div><dt>Targets</dt><dd>{capability.operation === 'create'
              ? 'New part'
              : photonCadManualModifyCapabilityId(capability.id)
                ? selectedEntityIds.length === 1 ? '1 solid selected' : 'Select one solid'
              : capability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
                || capability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
                ? selectedEntityIds.length === 1 ? '1 occurrence selected' : 'Select one occurrence'
                : selectedEntityIds.length || 'None selected'}</dd></div>
          </dl>
        </section>
        <div className="pcad-parameter-list">
          {capability.parameters.length ? capability.parameters.map((parameter) => (
            <ParameterField
              key={parameter.id}
              parameter={parameter}
              project={project}
              value={inputs[parameter.id]}
              disabled={!operationReady}
              onChange={(value) => onInput(parameter.id, value)}
            />
          )) : <p className="pcad-no-parameters">This operation declares no parameters.</p>}
        </div>
        <section className="pcad-provenance" aria-label="Operation provenance">
          <header><ShieldCheck size={13} /><strong>Declared provenance</strong></header>
          <dl>
            <div><dt>Package</dt><dd>{safeText(capability.source.package, 128)}</dd></div>
            <div><dt>Version</dt><dd>{safeText(capability.source.version, 64)}</dd></div>
            <div><dt>License</dt><dd>{safeText(capability.source.license, 64)}</dd></div>
            <div><dt>Digest</dt><dd title={safeText(capability.source.digest, 80)}>{safeText(capability.source.digest, 18)}…</dd></div>
          </dl>
          <p>Identity is reported by the supplied catalog boundary. This view does not independently attest the installed package.</p>
        </section>
        {issues.length ? <IssueList issues={issues} label="Operation issues" /> : null}
      </div>
      <footer className="pcad-pane-actions">
        <p>{operationReady
          ? capability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
            ? 'Select one occurrence in Assemble. Its descendants leave the scene and BOM; the reusable source part and STEP remain in the project.'
            : capability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
              ? 'Select one occurrence in Assemble. Translation and rotation replace only that occurrence transform and reseal the complete preview.'
            : manualDesign
              ? capability.id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
                ? 'Select one body or part in Design. The exact rectangular cut replaces its canonical STEP and reseals the complete preview.'
                : capability.id === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID
                  ? 'Select one body or part in Design. The exact cylindrical hole replaces its canonical STEP and reseals the complete preview.'
                  : capability.id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
                    ? 'Select one body or part and a durable cut or hole feature. The linear pattern replaces canonical STEP and reseals the complete preview.'
                    : capability.id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID
                      ? 'Select one body or part and a durable cut or hole feature. The circular pattern replaces canonical STEP and reseals the complete preview.'
                  : 'Add one exact parametric solid to this project. Its canonical STEP and preview are persisted before the operation is accepted.'
            : 'This exact host-described scratch operation will be persisted to the attached project. Verification and release remain separate human actions.'
          : mode === 'scratch'
            ? 'Catalog parameters are read-only until model operations can be committed to the authoritative project file. Autonomy stays inside scratch drafting.'
            : 'Catalog parameters are read-only until model operations can be committed to the authoritative project file. Canonical designs accept suggestions only; no write is available.'}</p>
        <button type="button" disabled={!operationReady || !project || !requiredInputsReady || busy} onClick={onRun}>
          {busy ? <LoaderCircle className="pcad-spin" size={13} /> : <Play size={13} />}
          {operationReady
            ? capability.id === PHOTON_CAD_ASSEMBLY_REMOVE_CAPABILITY_ID
              ? 'Remove selected occurrence'
              : capability.id === PHOTON_CAD_ASSEMBLY_TRANSFORM_CAPABILITY_ID
                ? 'Move selected occurrence'
              : capability.id === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
                ? 'Cut selected solid'
                : capability.id === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID
                  ? 'Cut hole in selected solid'
                  : capability.id === PHOTON_CAD_MANUAL_LINEAR_PATTERN_CAPABILITY_ID
                    ? 'Create linear feature pattern'
                    : capability.id === PHOTON_CAD_MANUAL_CIRCULAR_PATTERN_CAPABILITY_ID
                      ? 'Create circular feature pattern'
                  : manualDesign ? 'Add solid to project' : 'Run persisted scratch operation'
            : mode === 'scratch' ? 'Run scratch draft' : 'Prepare suggestion'}
        </button>
      </footer>
    </>
  )
}

function ParameterField({
  parameter,
  project,
  value,
  disabled,
  onChange,
}: {
  parameter: PhotonCadParameterDefinition
  project: PhotonCadProjectSnapshot | null
  value: PhotonCadInputValue | undefined
  disabled: boolean
  onChange: (value: PhotonCadInputValue) => void
}) {
  const descriptionId = useId()
  const units = parameterUnit(parameter, project)
  const common = { 'aria-describedby': descriptionId, required: parameter.required, disabled }
  let control: ReactNode
  if (parameter.kind === 'boolean') {
    control = <input {...common} type="checkbox" checked={value === true} onChange={(event) => onChange(event.target.checked)} />
  } else if (parameter.kind === 'choice') {
    control = (
      <select {...common} value={typeof value === 'string' ? value : ''} onChange={(event) => onChange(event.target.value || null)}>
        <option value="">Select…</option>
        {(parameter.choices ?? []).map((choice) => <option key={choice.value} value={choice.value}>{safeText(choice.label, 180)}</option>)}
      </select>
    )
  } else if (parameter.kind === 'entity') {
    const entities = (project?.entities ?? []).filter((entity) => {
      if (parameter.id === 'seedFeatureId') {
        return entity.kind === 'datum'
          && (entity.sourceCapabilityId === PHOTON_CAD_MANUAL_CUT_CAPABILITY_ID
            || entity.sourceCapabilityId === PHOTON_CAD_MANUAL_HOLE_CAPABILITY_ID)
      }
      return parameter.id !== 'sourceEntityId' || entity.kind !== 'occurrence'
    })
    control = (
      <select {...common} value={typeof value === 'string' ? value : ''} onChange={(event) => onChange(event.target.value || null)}>
        <option value="">Select an entity…</option>
        {entities.map((entity) => <option key={entity.id} value={entity.id}>{safeText(entity.name, 180)}</option>)}
      </select>
    )
  } else if (parameter.kind === 'entity-list') {
    const selected = Array.isArray(value) ? value : []
    control = (
      <select
        {...common}
        multiple
        value={selected}
        onChange={(event: ChangeEvent<HTMLSelectElement>) => onChange(Array.from(event.target.selectedOptions, (option) => option.value))}
      >
        {(project?.entities ?? []).map((entity) => <option key={entity.id} value={entity.id}>{safeText(entity.name, 180)}</option>)}
      </select>
    )
  } else if (parameter.kind === 'vector3') {
    const vector = isVector(value) ? value : { x: 0, y: 0, z: 0 }
    control = (
      <div className="pcad-vector-input">
        {(['x', 'y', 'z'] as const).map((axis) => (
          <label key={axis}><span>{axis.toUpperCase()}</span><input {...common} aria-label={`${parameter.label} ${axis.toUpperCase()}`} type="number" min={parameter.minimum} max={parameter.maximum} step={parameter.step ?? 'any'} value={vector[axis]} onChange={(event) => onChange({ ...vector, [axis]: Number(event.target.value) })} /></label>
        ))}
      </div>
    )
  } else if (parameter.kind === 'number' || parameter.kind === 'integer') {
    control = (
      <div className="pcad-number-input">
        <input
          {...common}
          type="number"
          inputMode="decimal"
          min={parameter.minimum}
          max={parameter.maximum}
          step={parameter.kind === 'integer' ? 1 : parameter.step ?? 'any'}
          value={typeof value === 'number' ? value : ''}
          onChange={(event) => onChange(event.target.value === '' ? null : Number(event.target.value))}
        />
        {units ? <span>{units}</span> : null}
      </div>
    )
  } else {
    control = <input {...common} type="text" maxLength={4_096} value={typeof value === 'string' ? value : ''} onChange={(event) => onChange(event.target.value || (parameter.required ? '' : null))} />
  }
  return (
    <label className={`pcad-parameter ${parameter.kind === 'boolean' ? 'boolean' : ''}`}>
      <span><strong>{safeText(parameter.label, 180)}</strong>{parameter.required ? <i>Required</i> : <i>Optional</i>}</span>
      {control}
      <small id={descriptionId}>{safeText(parameter.description) || `Declared ${safeText(parameter.kind, 40)} parameter${units ? ` in ${units}` : ''}.`}</small>
    </label>
  )
}

function BomPane({ bom, project }: { bom: readonly PhotonCadBomRow[]; project: PhotonCadProjectSnapshot | null }) {
  const discreteTotal = bom.filter((row) => row.unit === 'each').reduce((sum, row) => sum + row.quantity, 0)
  const measuredRows = bom.filter((row) => row.unit === 'length').length
  return (
    <>
      <header className="pcad-pane-header">
        <span><FileText size={14} /><strong>Bill of materials</strong></span>
        <small>{bom.length} rows</small>
      </header>
      <div className="pcad-bom-summary">
        <strong>{discreteTotal.toLocaleString()} discrete parts{measuredRows ? `; ${measuredRows} measured stock ${measuredRows === 1 ? 'row' : 'rows'}` : ''}</strong>
        <span>{project ? `Revision ${project.revision}` : 'No project revision'}</span>
      </div>
      {bom.length ? (
        <div className="pcad-table-scroll">
          <table className="pcad-bom-table">
            <caption className="pcad-visually-hidden">Assembly bill of materials</caption>
            <thead><tr><th scope="col">Part number</th><th scope="col">Description</th><th scope="col">Quantity</th></tr></thead>
            <tbody>{bom.map((row) => (
              <tr key={`${row.partNumber}:${row.sourceEntityId}`}>
                <th scope="row">{safeText(row.partNumber, 128)}</th>
                <td>{safeText(row.description, 256)}</td>
                <td>{row.quantity.toLocaleString()} {row.unit}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      ) : <div className="pcad-empty"><FileText size={24} /><strong>No BOM evidence</strong><p>A bill of materials is shown only when supplied by the assembly boundary.</p></div>}
      <p className="pcad-pane-note">BOM rows preserve declared part numbers and source entity IDs; the viewer does not invent missing rows.</p>
    </>
  )
}

function VerificationChecklist({
  checks,
  disabled,
  onChange,
}: {
  checks: Set<PhotonCadVerificationRequest['checks'][number]>
  disabled: boolean
  onChange: (checks: Set<PhotonCadVerificationRequest['checks'][number]>) => void
}) {
  return (
    <>
      <header className="pcad-pane-header"><span><ShieldCheck size={14} /><strong>Verification plan</strong></span><small>{checks.size} selected</small></header>
      <div className="pcad-check-list">
        {verificationChecks.map((check) => (
          <label key={check.id} data-available={check.available || undefined}>
            <input type="checkbox" checked={checks.has(check.id)} disabled={disabled || !check.available} onChange={() => {
              if (!check.available) return
              const next = new Set(checks)
              if (next.has(check.id)) next.delete(check.id)
              else next.add(check.id)
              onChange(next)
            }} />
            <span><strong>{check.label}{check.available ? '' : ' — unavailable'}</strong><small>{check.detail}</small></span>
          </label>
        ))}
      </div>
      <p className="pcad-pane-note">Checks run against one exact project revision. A later edit makes that evidence stale.</p>
    </>
  )
}

function VerificationPane({
  result,
  project,
  projectIssues,
  enabledCheckCount,
  available,
  busy,
  onRun,
}: {
  result: PhotonCadVerificationResult | null
  project: PhotonCadProjectSnapshot | null
  projectIssues: readonly PhotonCadIssue[]
  enabledCheckCount: number
  available: boolean
  busy: boolean
  onRun: () => void
}) {
  const matchesRevision = Boolean(result && project && result.projectId === project.projectId && result.revision === project.revision && !result.stale)
  return (
    <>
      <header className="pcad-pane-header"><span><Wrench size={14} /><strong>Check results</strong></span><small>{result?.status ?? 'Not run'}</small></header>
      <div className="pcad-inspector-scroll">
        {result ? (
          <section className={`pcad-verification-result ${matchesRevision ? result.status : 'stale'}`}>
            <span>{matchesRevision && result.status === 'passed' ? <CheckCircle2 size={22} /> : <AlertTriangle size={22} />}</span>
            <div>
              <strong>{!matchesRevision ? 'Evidence does not match this revision' : result.status === 'passed' ? 'Selected checks passed' : result.status === 'failed' ? 'Checks found problems' : 'Checks unavailable'}</strong>
              <p>{matchesRevision ? `Measured ${formatTimestamp(result.measuredAtUtc)} for revision ${result.revision}.` : 'Run verification again before preparing a release.'}</p>
            </div>
          </section>
        ) : (
          <div className="pcad-empty"><ShieldCheck size={24} /><strong>No verification result</strong><p>Passing status is never inferred from model visibility or preview generation.</p></div>
        )}
        {result?.issues.length ? <IssueList issues={result.issues} label="Verification issues" /> : null}
        {!result && projectIssues.length ? <IssueList issues={[...projectIssues]} label="Project issues before verification" /> : null}
        <section className="pcad-trust-note"><ShieldCheck size={14} /><p><strong>Evidence boundary</strong> Preview answers “can it be shown?” Verification answers “did these checks pass for this revision?” STEP export is a third, separate action.</p></section>
      </div>
      <footer className="pcad-pane-actions">
        <p>{enabledCheckCount ? `${enabledCheckCount} checks will run against ${project ? `revision ${project.revision}` : 'no loaded revision'}.` : 'Select at least one explicit check.'}</p>
        <button type="button" disabled={!available || enabledCheckCount === 0 || busy} onClick={onRun}>{busy ? <LoaderCircle className="pcad-spin" size={13} /> : <Play size={13} />}Run selected checks</button>
      </footer>
    </>
  )
}

function ReleaseOutputPicker({
  value,
  disabled,
  onChange,
}: {
  value: PhotonCadReleaseOutput
  disabled: boolean
  onChange: (value: PhotonCadReleaseOutput) => void
}) {
  const groupName = useId()
  const options: Array<{ id: PhotonCadReleaseOutput; label: string; icon: typeof PackageCheck }> = [
    { id: 'cad-package', label: 'STEP', icon: PackageCheck },
  ]
  return (
    <>
      <fieldset className="pcad-release-output" disabled={disabled}>
        <legend className="pcad-visually-hidden">Release output type</legend>
        {options.map(({ id, label, icon: Icon }) => (
          <label className={value === id ? 'selected' : ''} key={id}>
            <input type="radio" name={groupName} checked={value === id} onChange={() => onChange(id)} />
            <Icon size={12} aria-hidden="true" /><span>{label}</span>
          </label>
        ))}
      </fieldset>
      <p className="pcad-pane-note">BOM files and quote/invoice output are not mounted yet, so no inactive controls are shown.</p>
    </>
  )
}

function BomExportPlan({
  format,
  destination,
  disabled,
  bomReady,
  onFormat,
  onChooseDestination,
}: {
  format: PhotonCadBomExportFormat
  destination: PhotonCadWorkspaceProps['destination']
  disabled: boolean
  bomReady: boolean
  onFormat: (format: PhotonCadBomExportFormat) => void
  onChooseDestination?: () => void
}) {
  const groupName = useId()
  const formats: Array<{ id: PhotonCadBomExportFormat; label: string; detail: string }> = [
    { id: 'xlsx', label: 'Excel workbook', detail: 'Structured XLSX rows for estimating and purchasing' },
    { id: 'csv', label: 'CSV data', detail: 'Portable comma-separated BOM rows' },
    { id: 'pdf', label: 'PDF report', detail: 'Human-readable BOM document' },
  ]
  return (
    <>
      <header className="pcad-pane-header"><span><Table2 size={14} /><strong>BOM export</strong></span><small>{format.toUpperCase()}</small></header>
      <fieldset className="pcad-format-list" disabled={disabled}>
        <legend className="pcad-visually-hidden">BOM export format</legend>
        {formats.map((item) => (
          <label key={item.id}>
            <input type="radio" name={groupName} checked={format === item.id} onChange={() => onFormat(item.id)} />
            <span><strong>{item.label}</strong><small>{item.detail}</small></span>
          </label>
        ))}
      </fieldset>
      <section className="pcad-destination">
        <span><strong>Destination</strong><small>{destination ? safeText(destination.label, 256) : 'No destination selected'}</small></span>
        {onChooseDestination ? <button type="button" disabled={disabled} onClick={onChooseDestination}>Choose…</button> : <span className="pcad-unavailable-action">Desktop picker unavailable</span>}
      </section>
      <p className={`pcad-pane-note ${bomReady ? '' : 'warning'}`}>BOM exports require an authoritative revision-bound digest. Export is additive and never overwrites CAD source.</p>
    </>
  )
}

function CommercialDraftEditor({
  draft,
  action,
  destination,
  printer,
  disabled,
  onChange,
  onAction,
  onChooseDestination,
  onChoosePrinter,
}: {
  draft: PhotonCadCommercialDraft
  action: PhotonCadCommercialAction
  destination: PhotonCadWorkspaceProps['destination']
  printer: PhotonCadWorkspaceProps['printer']
  disabled: boolean
  onChange: (update: (current: PhotonCadCommercialDraft) => PhotonCadCommercialDraft) => void
  onAction: (action: PhotonCadCommercialAction) => void
  onChooseDestination?: () => void
  onChoosePrinter?: () => void
}) {
  const documentKindGroup = useId()
  const actionGroup = useId()
  const scale = draft.currencyScale
  function updateParty(key: 'seller' | 'customer', update: (party: PhotonCadCommercialParty) => PhotonCadCommercialParty) {
    onChange((current) => ({ ...current, [key]: update(current[key]) }))
  }
  function updateLine(lineId: string, update: Partial<PhotonCadCommercialDraft['lines'][number]>) {
    onChange((current) => ({ ...current, lines: current.lines.map((line) => line.lineId === lineId ? { ...line, ...update } : line) }))
  }
  function addLaborLine() {
    onChange((current) => ({
      ...current,
      lines: [...current.lines, {
        lineId: requestId('labor-line'),
        partNumber: 'LABOR',
        description: 'Labor',
        quantity: 1,
        unit: 'hour' as PhotonCadCommercialUnit,
        unitPriceMinorUnits: 0,
        taxable: true,
      }],
    }))
  }
  return (
    <>
      <header className="pcad-pane-header"><span><ReceiptText size={14} /><strong>Commercial draft</strong></span><small>Not sent or posted</small></header>
      <div className="pcad-commercial-editor">
        <fieldset className="pcad-inline-choice" disabled={disabled}>
          <legend>Document</legend>
          {(['quote', 'invoice'] as const).map((kind) => <label key={kind}><input type="radio" name={documentKindGroup} checked={draft.documentKind === kind} onChange={() => onChange((current) => ({ ...current, documentKind: kind }))} /><span>Draft {kind}</span></label>)}
        </fieldset>
        <div className="pcad-form-grid compact">
          <label><span>Document number</span><input disabled={disabled} value={draft.documentNumber} maxLength={128} onChange={(event) => onChange((current) => ({ ...current, documentNumber: event.target.value }))} /></label>
          <label><span>Currency</span><input disabled={disabled} value={draft.currency} maxLength={3} onChange={(event) => onChange((current) => ({ ...current, currency: event.target.value.toUpperCase() }))} /></label>
          <label><span>Currency decimals</span><select disabled={disabled} value={draft.currencyScale} onChange={(event) => onChange((current) => rescaleCommercialDraft(current, Number(event.target.value)))}>{[0, 1, 2, 3, 4].map((scaleValue) => <option key={scaleValue} value={scaleValue}>{scaleValue}</option>)}</select></label>
          <label><span>Issue date</span><input disabled={disabled} type="date" value={draft.issueDate} onChange={(event) => onChange((current) => ({ ...current, issueDate: event.target.value }))} /></label>
          <label><span>Due date</span><input disabled={disabled} type="date" value={draft.dueDate ?? ''} onChange={(event) => onChange((current) => ({ ...current, dueDate: event.target.value || undefined }))} /></label>
        </div>
        <CommercialPartyFields title="Your company" party={draft.seller} disabled={disabled} onChange={(update) => updateParty('seller', update)} />
        <CommercialPartyFields title="Customer" party={draft.customer} disabled={disabled} onChange={(update) => updateParty('customer', update)} />
        <section className="pcad-commercial-lines" aria-label="Line pricing">
          <header><strong>Line pricing</strong><button type="button" disabled={disabled} onClick={addLaborLine}><Plus size={11} />Add labor</button></header>
          {draft.lines.map((line) => (
            <article key={line.lineId}>
              <div><strong>{safeText(line.partNumber, 128)}</strong><small>{safeText(line.description, 256)}</small></div>
              <label><span>{line.unit === 'hour' ? 'Hours' : 'Quantity'}</span><input disabled={disabled} type="number" min="0.0001" step="any" value={line.quantity} onChange={(event) => updateLine(line.lineId, { quantity: Number(event.target.value) })} /></label>
              <label><span>Unit price ({draft.currency})</span><input disabled={disabled} type="number" min="0" step={1 / (10 ** scale)} value={moneyInputValue(line.unitPriceMinorUnits, scale)} onChange={(event) => updateLine(line.lineId, { unitPriceMinorUnits: moneyMinorUnits(event.target.value, scale) })} /></label>
              <label className="pcad-check-inline"><input disabled={disabled} type="checkbox" checked={line.taxable} onChange={(event) => updateLine(line.lineId, { taxable: event.target.checked })} /><span>Taxable</span></label>
              {!line.sourceBomRowId ? <button type="button" disabled={disabled} onClick={() => onChange((current) => ({ ...current, lines: current.lines.filter((item) => item.lineId !== line.lineId) }))}>Remove</button> : null}
            </article>
          ))}
        </section>
        <section className="pcad-adjustments" aria-label="Price adjustments">
          <label><span>Markup (%)</span><input disabled={disabled} type="number" min="0" step="0.01" value={draft.markupBasisPoints / 100} onChange={(event) => onChange((current) => ({ ...current, markupBasisPoints: Math.round(Math.max(0, Number(event.target.value)) * 100) }))} /></label>
          <label><span>Discount ({draft.currency})</span><input disabled={disabled} type="number" min="0" step={1 / (10 ** scale)} value={moneyInputValue(draft.discountMinorUnits, scale)} onChange={(event) => onChange((current) => ({ ...current, discountMinorUnits: moneyMinorUnits(event.target.value, scale) }))} /></label>
          <label><span>Tax (%)</span><input disabled={disabled} type="number" min="0" step="0.01" value={draft.taxBasisPoints / 100} onChange={(event) => onChange((current) => ({ ...current, taxBasisPoints: Math.round(Math.max(0, Number(event.target.value)) * 100) }))} /></label>
          <label><span>Freight ({draft.currency})</span><input disabled={disabled} type="number" min="0" step={1 / (10 ** scale)} value={moneyInputValue(draft.freightMinorUnits, scale)} onChange={(event) => onChange((current) => ({ ...current, freightMinorUnits: moneyMinorUnits(event.target.value, scale) }))} /></label>
        </section>
        <label className="pcad-textarea-field"><span>Terms</span><textarea disabled={disabled} maxLength={4_096} value={draft.terms} onChange={(event) => onChange((current) => ({ ...current, terms: event.target.value }))} /></label>
        <label className="pcad-textarea-field"><span>Notes</span><textarea disabled={disabled} maxLength={4_096} value={draft.notes} onChange={(event) => onChange((current) => ({ ...current, notes: event.target.value }))} /></label>
        <fieldset className="pcad-inline-choice" disabled={disabled}>
          <legend>Approved output action</legend>
          <label><input type="radio" name={actionGroup} checked={action === 'export-pdf'} onChange={() => onAction('export-pdf')} /><span>Export PDF</span></label>
          <label><input type="radio" name={actionGroup} checked={action === 'print'} onChange={() => onAction('print')} /><span>Print</span></label>
        </fieldset>
        <section className="pcad-destination">
          <span><strong>{action === 'export-pdf' ? 'PDF destination' : 'Printer'}</strong><small>{action === 'export-pdf' ? safeText(destination?.label, 256) || 'No destination selected' : safeText(printer?.label, 256) || 'No printer selected'}</small></span>
          {action === 'export-pdf'
            ? onChooseDestination ? <button type="button" disabled={disabled} onClick={onChooseDestination}>Choose…</button> : <span className="pcad-unavailable-action">Picker unavailable</span>
            : onChoosePrinter ? <button type="button" disabled={disabled} onClick={onChoosePrinter}>Choose…</button> : <span className="pcad-unavailable-action">Printer picker unavailable</span>}
        </section>
      </div>
    </>
  )
}

function CommercialPartyFields({
  title,
  party,
  disabled,
  onChange,
}: {
  title: string
  party: PhotonCadCommercialParty
  disabled: boolean
  onChange: (update: (current: PhotonCadCommercialParty) => PhotonCadCommercialParty) => void
}) {
  const update = (field: keyof PhotonCadCommercialParty, value: string | string[]) => onChange((current) => ({ ...current, [field]: value }))
  return (
    <details className="pcad-party" open>
      <summary>{title}</summary>
      <div className="pcad-form-grid">
        <label><span>Company</span><input disabled={disabled} maxLength={256} value={party.organization} onChange={(event) => update('organization', event.target.value)} /></label>
        <label><span>Contact</span><input disabled={disabled} maxLength={256} value={party.contactName} onChange={(event) => update('contactName', event.target.value)} /></label>
        <label className="wide"><span>Address</span><textarea disabled={disabled} maxLength={2_048} value={party.addressLines.join('\n')} onChange={(event) => update('addressLines', event.target.value.split(/\r?\n/u).slice(0, 8))} /></label>
        <label><span>City</span><input disabled={disabled} maxLength={256} value={party.city} onChange={(event) => update('city', event.target.value)} /></label>
        <label><span>State / region</span><input disabled={disabled} maxLength={256} value={party.region} onChange={(event) => update('region', event.target.value)} /></label>
        <label><span>Postal code</span><input disabled={disabled} maxLength={32} value={party.postalCode} onChange={(event) => update('postalCode', event.target.value)} /></label>
        <label><span>Country code</span><input disabled={disabled} maxLength={2} value={party.countryCode} onChange={(event) => update('countryCode', event.target.value.toUpperCase())} /></label>
        <label><span>Email</span><input disabled={disabled} type="email" maxLength={320} value={party.email} onChange={(event) => update('email', event.target.value)} /></label>
        <label><span>Phone</span><input disabled={disabled} maxLength={64} value={party.phone} onChange={(event) => update('phone', event.target.value)} /></label>
      </div>
    </details>
  )
}

function CommercialPreviewPane({
  review,
  draft,
  previewSurface,
  viewedPageKeys,
  locked,
  onToggleViewed,
}: {
  review: PhotonCadCommercialReviewResult | null
  draft: PhotonCadCommercialDraft
  previewSurface?: PhotonCadWorkspaceProps['commercialPreviewSurface']
  viewedPageKeys: Set<string>
  locked: boolean
  onToggleViewed: (page: PhotonCadCommercialPreviewPage) => void
}) {
  const ready = review?.status === 'ready' && Boolean(review.reviewHandle && review.documentFingerprint && review.actionFingerprint)
  return (
    <section className="pcad-preview-pane pcad-document-preview" aria-label="Commercial document preview">
      <header><span><Eye size={14} /><strong>Rendered all-page preview</strong></span><div><span>{ready ? `${review!.pages.length} pages` : 'Not rendered'}</span></div></header>
      <div className="pcad-document-pages">
        {ready ? review!.pages.map((page) => {
          const rendered = previewSurface?.({ page, documentKind: draft.documentKind, pageCount: review!.pages.length })
          return (
            <article key={commercialPageKey(page)}>
              <header><strong>Page {page.pageNumber}</strong><span>{safeText(page.contentDigest, 18)}…</span></header>
              <div className="pcad-document-page">{rendered ?? <div className="pcad-empty"><FileText size={24} /><strong>Rendered-page bridge unavailable</strong><p>The opaque preview handle is never interpreted as document content in the renderer.</p></div>}</div>
              <label><input type="checkbox" disabled={!rendered || locked} checked={viewedPageKeys.has(commercialPageKey(page))} onChange={() => onToggleViewed(page)} /><span>I reviewed this rendered page</span></label>
            </article>
          )
        }) : <div className="pcad-empty"><ReceiptText size={24} /><strong>No document preview</strong><p>Prepare a valid draft to render every page before approval.</p></div>}
      </div>
      <footer><ShieldCheck size={12} /><span>{PHOTON_CAD_COMMERCIAL_INVARIANT}</span></footer>
    </section>
  )
}

function BomExportPane({
  review,
  message,
  available,
  destinationReady,
  busy,
  onPrepare,
  onCommit,
}: {
  review: PhotonCadBomExportReviewResult | null
  message: string
  available: boolean
  destinationReady: boolean
  busy: boolean
  onPrepare: () => void
  onCommit: () => void
}) {
  const ready = review?.status === 'ready' && Boolean(review.reviewHandle && review.exportFingerprint)
  return (
    <>
      <header className="pcad-pane-header"><span><Table2 size={14} /><strong>BOM export review</strong></span><small>{review?.status ?? 'Not prepared'}</small></header>
      <div className="pcad-inspector-scroll">
        <section className="pcad-release-trust"><ShieldCheck size={17} /><div><strong>Review before export</strong><p>The reviewed file is bound to the project revision, BOM digest, format, and destination.</p></div></section>
        {review ? <section className={`pcad-review-result ${review.status}`}><header><strong>{ready ? 'BOM export plan ready' : 'BOM export needs attention'}</strong><span>{review.files.length} files</span></header><p>{safeText(message) || photonCadReasonText(review.reason)}</p>{review.files.length ? <ul>{review.files.map((file, index) => <li key={`${file.role}:${file.relativePath}:${index}`}><span>{file.role}</span><code>{safeText(file.relativePath, 512)}</code></li>)}</ul> : null}</section> : <div className="pcad-empty"><Table2 size={24} /><strong>No BOM export review</strong><p>Choose XLSX, CSV, or PDF and an empty destination, then prepare the exact export.</p></div>}
        {message && !review ? <p className="pcad-release-message" role="status">{safeText(message)}</p> : null}
      </div>
      <footer className="pcad-pane-actions split"><button type="button" disabled={!available || !destinationReady || busy} onClick={onPrepare}>{busy && !ready ? <LoaderCircle className="pcad-spin" size={13} /> : <RefreshCw size={13} />}Prepare export</button><button className="primary" type="button" disabled={!ready || busy} onClick={onCommit}>{busy && ready ? <LoaderCircle className="pcad-spin" size={13} /> : <Table2 size={13} />}Export reviewed BOM</button></footer>
    </>
  )
}

function CommercialReviewPane({
  review,
  approval,
  message,
  draftIssues,
  currency,
  currencyScale,
  actionReady,
  controllerReady,
  previewReady,
  allPagesViewed,
  busy,
  onPrepare,
  onApprove,
  onCommit,
}: {
  review: PhotonCadCommercialReviewResult | null
  approval: PhotonCadCommercialApprovalResult | null
  message: string
  draftIssues: string[]
  currency: string
  currencyScale: number
  actionReady: boolean
  controllerReady: boolean
  previewReady: boolean
  allPagesViewed: boolean
  busy: 'bom-review' | 'bom-commit' | 'commercial-review' | 'commercial-approve' | 'commercial-commit' | null
  onPrepare: () => void
  onApprove: () => void
  onCommit: () => void
}) {
  const reviewReady = review?.status === 'ready' && Boolean(review.reviewHandle && review.documentFingerprint && review.actionFingerprint)
  const approved = approval?.status === 'approved' && Boolean(approval.approvalHandle && approval.documentFingerprint && approval.actionFingerprint)
  return (
    <>
      <header className="pcad-pane-header"><span><ReceiptText size={14} /><strong>Quote / invoice review</strong></span><small>{approved ? 'Approved' : review?.status ?? 'Draft'}</small></header>
      <div className="pcad-inspector-scroll">
        <section className="pcad-commercial-guard"><ShieldCheck size={17} /><div><strong>Draft output only</strong><p>No email, accounting post, payment, charge, paid status, or delivery side effect exists in this workflow.</p></div></section>
        {draftIssues.length ? <section className="pcad-draft-issues" aria-label="Draft fields needing attention"><strong>{draftIssues.length} fields need attention</strong><ul>{draftIssues.slice(0, 12).map((issue) => <li key={issue}>{commercialIssueText(issue)}</li>)}</ul></section> : <p className="pcad-ready-note"><CheckCircle2 size={13} />Draft fields are ready for a rendered review.</p>}
        {review?.totals ? <section className="pcad-commercial-totals" aria-label="Reviewed document totals"><div><span>Lines</span><strong>{formatMoney(review.totals.lineSubtotalMinorUnits, currency, currencyScale)}</strong></div><div><span>Markup</span><strong>{formatMoney(review.totals.markupMinorUnits, currency, currencyScale)}</strong></div><div><span>Discount</span><strong>-{formatMoney(review.totals.discountMinorUnits, currency, currencyScale)}</strong></div><div><span>Freight</span><strong>{formatMoney(review.totals.freightMinorUnits, currency, currencyScale)}</strong></div><div><span>Tax</span><strong>{formatMoney(review.totals.taxMinorUnits, currency, currencyScale)}</strong></div><div className="total"><span>Total</span><strong>{formatMoney(review.totals.totalMinorUnits, currency, currencyScale)}</strong></div></section> : null}
        {reviewReady && review?.expiresAtUtc ? <p className="pcad-expiry">Rendered review expires {formatTimestamp(review.expiresAtUtc)}.</p> : null}
        {approved && approval?.expiresAtUtc ? <p className="pcad-expiry">Approval expires {formatTimestamp(approval.expiresAtUtc)}.</p> : null}
        {message ? <p className="pcad-commercial-message" role="status">{safeText(message)}</p> : null}
        {reviewReady && !previewReady ? <p className="pcad-release-message" role="alert">The rendered-page bridge is unavailable, so human page approval remains disabled.</p> : null}
        <section className="pcad-trust-note"><Eye size={14} /><p><strong>Three deliberate steps</strong> Prepare the rendered pages, review every page and approve the exact output action, then export or print.</p></section>
      </div>
      <footer className="pcad-commercial-actions">
        <button type="button" disabled={!controllerReady || !actionReady || draftIssues.length > 0 || Boolean(busy)} onClick={onPrepare}>{busy === 'commercial-review' ? <LoaderCircle className="pcad-spin" size={13} /> : <RefreshCw size={13} />}{reviewReady ? 'Refresh preview' : 'Prepare preview'}</button>
        <button type="button" disabled={!reviewReady || !previewReady || !allPagesViewed || Boolean(busy) || approved} onClick={onApprove}>{busy === 'commercial-approve' ? <LoaderCircle className="pcad-spin" size={13} /> : <Eye size={13} />}Approve exact preview</button>
        <button className="primary" type="button" disabled={!approved || Boolean(busy)} onClick={onCommit}>{busy === 'commercial-commit' ? <LoaderCircle className="pcad-spin" size={13} /> : <Printer size={13} />}Create approved output</button>
      </footer>
    </>
  )
}

function ReleasePlan({
  project,
  selectedEntityIds,
  onToggleEntity,
}: {
  project: PhotonCadProjectSnapshot | null
  selectedEntityIds: Set<string>
  onToggleEntity: (id: string) => void
}) {
  return (
    <>
      <header className="pcad-pane-header"><span><PackageCheck size={14} /><strong>Portable STEP source</strong></span><small>Generic Part 21</small></header>
      <p className="pcad-pane-note">Select exactly one saved Body or Part. The native exporter writes its canonical generic STEP bytes; AP-specific and multi-file package claims remain unavailable.</p>
      <ModelTreePane project={project} selectedEntityIds={selectedEntityIds} assemblyOnly={false} onToggle={onToggleEntity} />
    </>
  )
}

function ReleasePane({
  stepExportEntity,
  stepExportResult,
  stepExportMessage,
  stepExportAvailable,
  stepExportBusy,
  onExportStep,
}: {
  stepExportEntity: PhotonCadProjectSnapshot['entities'][number] | null
  stepExportResult: PhotonCadStepExportResult | null
  stepExportMessage: string
  stepExportAvailable: boolean
  stepExportBusy: boolean
  onExportStep: () => void
}) {
  return (
    <>
      <header className="pcad-pane-header"><span><PackageCheck size={14} /><strong>STEP export</strong></span><small>Selected part</small></header>
      <div className="pcad-inspector-scroll">
        <section className="pcad-release-trust">
          <ShieldCheck size={17} />
          <div><strong>Canonical source export</strong><p>The export is bound to the selected saved entity and exact project revision. The native picker opens only after an explicit export click.</p></div>
        </section>
        <section className="pcad-step-authority" role="note">
          <strong>STEP is the portable source of truth.</strong>
          <p>Autodesk Inventor is an optional later bridge and is never required for Photon CAD design, verification, backup, or release.</p>
        </section>
        <PhotonCadStepExportPane
          entity={stepExportEntity}
          result={stepExportResult}
          message={stepExportMessage}
          available={stepExportAvailable}
          busy={stepExportBusy}
          onExport={onExportStep}
        />
      </div>
    </>
  )
}

export function PhotonCadStepExportPane({
  entity,
  result,
  message,
  available,
  busy,
  onExport,
}: {
  entity: PhotonCadProjectSnapshot['entities'][number] | null
  result: PhotonCadStepExportResult | null
  message: string
  available: boolean
  busy: boolean
  onExport: () => void
}) {
  return (
    <section className="pcad-step-export" aria-label="Selected STEP export">
      <strong>Export selected STEP</strong>
      <p>{entity ? `Selected ${safeText(entity.name, 180)} (${safeText(entity.kind, 24)}).` : 'Select exactly one Body or Part in the model tree.'}</p>
      <button type="button" disabled={!available || busy} onClick={onExport}>
        {busy ? <LoaderCircle className="pcad-spin" size={13} /> : <PackageCheck size={13} />}Export selected STEP
      </button>
      {message ? <p role="status">{safeText(message)}</p> : null}
      {result?.status === 'committed' ? (
        <dl>
          <div><dt>File</dt><dd>{safeText(result.destinationLabel, 256)}</dd></div>
          <div><dt>Bytes</dt><dd>{result.byteLength?.toLocaleString()}</dd></div>
          <div><dt>Digest</dt><dd>{safeText(result.contentDigest, 80)}</dd></div>
        </dl>
      ) : null}
      <small>The native picker opens only after this click. No path enters the renderer, and no overwrite or release-package claim is made.</small>
    </section>
  )
}

function IssueList({ issues, label }: { issues: PhotonCadIssue[]; label: string }) {
  return (
    <section className="pcad-issues" aria-label={label}>
      <header><CircleAlert size={13} /><strong>{label}</strong><span>{issues.length}</span></header>
      {issues.map((issue, index) => (
        <article className={issueTone(issue)} key={`${issue.code}:${index}`}>
          <span>{issue.severity === 'error' ? <CircleAlert size={13} /> : issue.severity === 'warning' ? <AlertTriangle size={13} /> : <FileText size={13} />}</span>
          <div><strong>{safeText(issue.code, 128)}</strong><p>{safeText(issue.message)}</p>{issue.entityIds.length ? <small>{issue.entityIds.length} related entities</small> : null}</div>
        </article>
      ))}
    </section>
  )
}
