export const HERMES_VISION_INPUT_CONTRACT_VERSION = 1 as const
export const HERMES_VISION_INPUT_MAX_BYTES = 25 * 1024 * 1024

export type HermesVisionInputOrigin =
  | 'clipboard'
  | 'drag-drop'
  | 'file-picker'
  | 'host-path'
  | 'screenshot'

export type HermesVisionMediaType =
  | 'image/bmp'
  | 'image/gif'
  | 'image/jpeg'
  | 'image/png'
  | 'image/svg+xml'
  | 'image/tiff'
  | 'image/vnd.microsoft.icon'
  | 'image/webp'

export type HermesVisionRawInput = {
  id: string
  origin: HermesVisionInputOrigin
  displayName?: string
  declaredMediaType?: string
  bytes?: ArrayBuffer | Uint8Array
  path?: string
}

export type HermesVisionByteCarrier = {
  kind: 'bytes'
  bytes: Uint8Array
  byteLength: number
  filename: string
  mediaType: HermesVisionMediaType
}

export type HermesVisionPathCarrier = {
  kind: 'path'
  path: string
  mediaType: HermesVisionMediaType
}

export type HermesVisionCarrier = HermesVisionByteCarrier | HermesVisionPathCarrier
export type HermesVisionCarrierKind = HermesVisionCarrier['kind']

export type NormalizedHermesVisionInput = {
  contractVersion: typeof HERMES_VISION_INPUT_CONTRACT_VERSION
  id: string
  origin: HermesVisionInputOrigin
  displayName: string
  carriers: readonly HermesVisionCarrier[]
}

export type HermesVisionStageRequest = {
  contractVersion: typeof HERMES_VISION_INPUT_CONTRACT_VERSION
  sessionId: string
  input: NormalizedHermesVisionInput
  signal?: AbortSignal
}

export type HermesVisionConsumptionEvidence =
  | { kind: 'bytes'; byteLength: number }
  | { kind: 'path'; path: string }

export type HermesVisionProviderStageResult = {
  attachmentId: string
  consumed: HermesVisionConsumptionEvidence
  gatewayPath?: string
}

export interface HermesVisionInputProvider {
  stage(request: HermesVisionStageRequest): Promise<HermesVisionProviderStageResult>
}

export type HermesVisionStageReceipt = {
  attachmentId: string
  displayName: string
  consumed: HermesVisionConsumptionEvidence
  availableCarrierKinds: readonly HermesVisionCarrierKind[]
  unconsumedCarrierKinds: readonly HermesVisionCarrierKind[]
  gatewayPath?: string
}

export type HermesVisionInputErrorCode =
  | 'empty-bytes'
  | 'invalid-input'
  | 'invalid-session'
  | 'provider-failed'
  | 'source-not-confirmed'
  | 'too-large'
  | 'unsupported-format'

export class HermesVisionInputError extends Error {
  readonly displaySafe = true

  constructor(
    readonly code: HermesVisionInputErrorCode,
    message: string,
  ) {
    super(message)
    this.name = 'HermesVisionInputError'
  }
}

export type HermesVisionDisplayError = {
  code: HermesVisionInputErrorCode
  message: string
}

