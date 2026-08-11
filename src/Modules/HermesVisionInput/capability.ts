export type HermesVisionCapability = 'supported' | 'unsupported' | 'unknown'

export type HermesVisionModelIdentity = {
  model: string
  provider: string
}

export type HermesVisionCapabilityEvidence = HermesVisionModelIdentity & {
  source: 'hermes-model-info/v1'
  supportsVision: boolean
}

function clean(value: string | undefined) {
  return value?.trim() ?? ''
}

export function resolveSelectedHermesVisionCapability(
  selection: HermesVisionModelIdentity | null | undefined,
  evidence?: HermesVisionCapabilityEvidence | null,
): HermesVisionCapability {
  if (!selection || !evidence || evidence.source !== 'hermes-model-info/v1') return 'unknown'
  if (typeof evidence.supportsVision !== 'boolean') return 'unknown'
  if (!clean(selection.model) || !clean(selection.provider)) return 'unknown'
  if (clean(selection.model) !== clean(evidence.model) || clean(selection.provider) !== clean(evidence.provider)) {
    return 'unknown'
  }
  return evidence.supportsVision ? 'supported' : 'unsupported'
}
