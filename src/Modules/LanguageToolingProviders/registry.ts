import { LANGUAGE_TOOLING_PROVIDER_CATALOG } from './catalog'
import {
  LANGUAGE_TOOLING_CONTRACT,
  type HostCapabilityEvidence,
  type LanguageToolingCapabilityKind,
  type LanguageToolingHostIntent,
  type LanguageToolingHostRequest,
  type LanguageToolingProviderId,
  type LanguageToolingProviderReport,
  type TrustedHostProviderEvidence,
} from './contracts'

const safeCode = (value: string) => value.replace(/[^a-z0-9._-]/gi, '').slice(0, 96) || 'unavailable'
const safeDetail = (value: string) => value.replace(/[\r\n\t\u202a-\u202e\u2066-\u2069]/g, ' ').slice(0, 512) || 'No safe host detail was provided.'
const safeIdentifierPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const boardFqbnPattern = /^[A-Za-z0-9._-]+(?::[A-Za-z0-9._-]+){2,5}$/
const testSelectionPattern = /^[A-Za-z0-9_./:\\[\](),-]{1,512}$/
const opaqueHostIdentifierPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const isoTimestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/
const maximumPathLength = 2_048

export class LanguageToolingRequestError extends Error {}

function normalizeEvidence(value: HostCapabilityEvidence): HostCapabilityEvidence {
  return {
    capabilityId: value.capabilityId.slice(0, 128),
    availability: value.availability === 'available' || value.availability === 'error' ? value.availability : 'unavailable',
    code: safeCode(value.code),
    detail: safeDetail(value.detail),
    version: value.version?.slice(0, 64),
  }
}

export function buildLanguageToolingReports(
  evidence: readonly TrustedHostProviderEvidence[] = [],
): readonly LanguageToolingProviderReport[] {
  const trusted = new Map<LanguageToolingProviderId, TrustedHostProviderEvidence>()
  const duplicateProviders = new Set<LanguageToolingProviderId>()
  for (const item of evidence) {
    if (!isValidEvidenceEnvelope(item)) continue
    if (trusted.has(item.providerId)) {
      duplicateProviders.add(item.providerId)
      continue
    }
    trusted.set(item.providerId, item)
  }
  duplicateProviders.forEach((providerId) => trusted.delete(providerId))

  return LANGUAGE_TOOLING_PROVIDER_CATALOG.map((provider) => {
    const providerEvidence = trusted.get(provider.id)
    const declaredCapabilities = new Set<string>(provider.capabilities.map((capability) => capability.id))
    const capabilityEvidence = new Map<string, HostCapabilityEvidence>()
    const duplicates = new Set<string>()
    for (const candidate of providerEvidence?.capabilities ?? []) {
      const item = normalizeEvidence(candidate)
      if (!declaredCapabilities.has(item.capabilityId)) continue
      if (capabilityEvidence.has(item.capabilityId)) duplicates.add(item.capabilityId)
      else capabilityEvidence.set(item.capabilityId, item)
    }
    duplicates.forEach((capabilityId) => capabilityEvidence.delete(capabilityId))
    return {
      ...provider,
      capabilities: provider.capabilities.map((capability) => {
        const item = capabilityEvidence.get(capability.id)
        return {
          ...capability,
          availability: item?.availability ?? 'unknown',
          code: item?.code ?? 'trusted-host-evidence-required',
          detail: item?.detail ?? 'Availability has not been verified by the desktop host.',
          version: item?.version,
          checkedAt: item ? providerEvidence?.checkedAt : undefined,
          evidenceId: item ? providerEvidence?.evidenceId : undefined,
        }
      }),
    }
  })
}

export function createLanguageToolingHostRequest(
  intent: LanguageToolingHostIntent,
  evidence: readonly TrustedHostProviderEvidence[] = [],
): LanguageToolingHostRequest {
  validateIdentifier(intent.requestId, 'request identifier')
  validateIdentifier(intent.workspaceId, 'workspace identifier')
  const descriptor = LANGUAGE_TOOLING_PROVIDER_CATALOG.find((provider) => provider.id === intent.providerId)
  if (!descriptor) throw new LanguageToolingRequestError(`Unknown language-tooling provider: ${intent.providerId}`)
  const envelope = {
    contract: LANGUAGE_TOOLING_CONTRACT,
    requestId: intent.requestId,
    workspaceId: intent.workspaceId,
    providerId: intent.providerId,
  } as const
  if (intent.operation === 'inspect-provider') return { ...envelope, operation: intent.operation }

  const capabilityKind = capabilityKindFor(intent.operation)
  const capability = descriptor.capabilities.find((candidate) => candidate.kind === capabilityKind)
  if (!capability) throw new LanguageToolingRequestError(`${descriptor.label} does not declare ${capabilityKind}.`)
  const report = buildLanguageToolingReports(evidence).find((provider) => provider.id === intent.providerId)
  const runtime = report?.capabilities.find((candidate) => candidate.id === capability.id)
  if (runtime?.availability !== 'available') {
    throw new LanguageToolingRequestError(runtime?.detail ?? 'The trusted host has not verified this capability.')
  }

  switch (intent.operation) {
    case 'start-language-session':
      return { ...envelope, operation: intent.operation, documentPath: normalizeWorkspacePath(intent.documentPath) }
    case 'stop-language-session':
      validateIdentifier(intent.sessionId, 'language session identifier')
      return { ...envelope, operation: intent.operation, sessionId: intent.sessionId }
    case 'inspect-project':
      return { ...envelope, operation: intent.operation, projectPath: normalizeWorkspacePath(intent.projectPath) }
    case 'compile':
      if (intent.mode !== undefined && intent.mode !== 'debug' && intent.mode !== 'release' && intent.mode !== 'check') {
        throw new LanguageToolingRequestError('The compile mode is invalid.')
      }
      if (intent.boardFqbn !== undefined && (intent.providerId !== 'arduino' || !boardFqbnPattern.test(intent.boardFqbn))) {
        throw new LanguageToolingRequestError('The Arduino board identifier is invalid.')
      }
      return {
        ...envelope,
        operation: intent.operation,
        targetPath: normalizeWorkspacePath(intent.targetPath),
        mode: intent.mode ?? 'check',
        ...(intent.boardFqbn ? { boardFqbn: intent.boardFqbn } : {}),
      }
    case 'run-tests':
      if (intent.selection !== undefined && !testSelectionPattern.test(intent.selection)) {
        throw new LanguageToolingRequestError('The test selection is invalid.')
      }
      return {
        ...envelope,
        operation: intent.operation,
        targetPath: normalizeWorkspacePath(intent.targetPath),
        ...(intent.selection ? { selection: intent.selection } : {}),
      }
    case 'start-debug':
      return {
        ...envelope,
        operation: intent.operation,
        programPath: normalizeWorkspacePath(intent.programPath),
        stopAtEntry: intent.stopAtEntry === true,
      }
    case 'inspect-remote-target':
      validateOpaqueHostIdentifier(intent.targetId, 'remote target identifier')
      return { ...envelope, operation: intent.operation, targetId: intent.targetId }
    case 'deploy-file':
      validateOpaqueHostIdentifier(intent.targetId, 'remote target identifier')
      validateOpaqueHostIdentifier(intent.destinationId, 'remote destination identifier')
      return {
        ...envelope,
        operation: intent.operation,
        targetId: intent.targetId,
        sourcePath: normalizeWorkspacePath(intent.sourcePath),
        destinationId: intent.destinationId,
      }
  }
}

function isValidEvidenceEnvelope(item: TrustedHostProviderEvidence) {
  return item.contract === LANGUAGE_TOOLING_CONTRACT
    && item.source === 'trusted-host'
    && LANGUAGE_TOOLING_PROVIDER_CATALOG.some((provider) => provider.id === item.providerId)
    && safeIdentifierPattern.test(item.evidenceId)
    && isoTimestampPattern.test(item.checkedAt)
    && Number.isFinite(Date.parse(item.checkedAt))
    && Array.isArray(item.capabilities)
}

function capabilityKindFor(operation: Exclude<LanguageToolingHostIntent['operation'], 'inspect-provider'>): LanguageToolingCapabilityKind {
  switch (operation) {
    case 'start-language-session':
    case 'stop-language-session': return 'language-server'
    case 'inspect-project': return 'project-inspection'
    case 'compile': return 'compiler'
    case 'run-tests': return 'test-runner'
    case 'start-debug': return 'debug-adapter'
    case 'inspect-remote-target': return 'remote-host'
    case 'deploy-file': return 'deployment'
  }
}

function validateIdentifier(value: string, label: string) {
  if (!safeIdentifierPattern.test(value)) throw new LanguageToolingRequestError(`The ${label} is invalid.`)
}

function validateOpaqueHostIdentifier(value: string, label: string) {
  if (!opaqueHostIdentifierPattern.test(value)) throw new LanguageToolingRequestError(`The ${label} is invalid.`)
}

function normalizeWorkspacePath(value: string) {
  const path = value.trim().replaceAll('\\', '/')
  const segments = path.split('/')
  if (
    path.length === 0
    || path.length > maximumPathLength
    || path.startsWith('/')
    || /^[A-Za-z]:/.test(path)
    || segments.some((segment) => !segment || segment === '.' || segment === '..')
    || /[\u0000-\u001f]/.test(path)
  ) {
    throw new LanguageToolingRequestError('Tooling paths must stay workspace-relative.')
  }
  return path
}
