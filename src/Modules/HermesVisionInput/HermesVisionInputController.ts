import {
  HERMES_VISION_INPUT_CONTRACT_VERSION,
  HermesVisionInputError,
  type HermesVisionCarrier,
  type HermesVisionConsumptionEvidence,
  type HermesVisionInputProvider,
  type HermesVisionProviderStageResult,
  type HermesVisionRawInput,
  type HermesVisionStageReceipt,
} from './contracts'
import { normalizeHermesVisionInput } from './normalization'

function invalidSession() {
  return new HermesVisionInputError('invalid-session', 'Open a Hermes conversation before attaching an image.')
}

function sourceNotConfirmed() {
  return new HermesVisionInputError(
    'source-not-confirmed',
    'Hermes did not confirm that the image source was attached. Try again.',
  )
}

function matchingCarrier(carriers: readonly HermesVisionCarrier[], consumed: HermesVisionConsumptionEvidence) {
  if (consumed.kind === 'bytes') {
    return carriers.find((carrier) => carrier.kind === 'bytes' && carrier.byteLength === consumed.byteLength)
  }
  return carriers.find((carrier) => carrier.kind === 'path' && carrier.path === consumed.path)
}

function validateResult(
  result: HermesVisionProviderStageResult,
  carriers: readonly HermesVisionCarrier[],
) {
  if (!result || typeof result.attachmentId !== 'string' || !result.attachmentId.trim()) throw sourceNotConfirmed()
  if (!result.consumed || !matchingCarrier(carriers, result.consumed)) throw sourceNotConfirmed()
  if (result.gatewayPath !== undefined && (typeof result.gatewayPath !== 'string' || !result.gatewayPath.trim())) {
    throw sourceNotConfirmed()
  }
}

export class HermesVisionInputController {
  constructor(private readonly provider: HermesVisionInputProvider) {}

  async stage(
    sessionId: string,
    raw: HermesVisionRawInput,
    signal?: AbortSignal,
  ): Promise<HermesVisionStageReceipt> {
    if (!sessionId?.trim() || /[\u0000-\u001f\u007f]/.test(sessionId)) throw invalidSession()
    const input = normalizeHermesVisionInput(raw)

    let result: HermesVisionProviderStageResult
    try {
      result = await this.provider.stage({
        contractVersion: HERMES_VISION_INPUT_CONTRACT_VERSION,
        sessionId,
        input,
        ...(signal ? { signal } : {}),
      })
    } catch (reason) {
      throw new HermesVisionInputError('provider-failed', 'Hermes could not attach the image. Try again.')
    }

    validateResult(result, input.carriers)
    const availableCarrierKinds = input.carriers.map((carrier) => carrier.kind)
    const unconsumedCarrierKinds = availableCarrierKinds.filter((kind) => kind !== result.consumed.kind)
    return {
      attachmentId: result.attachmentId,
      displayName: input.displayName,
      consumed: result.consumed,
      availableCarrierKinds,
      unconsumedCarrierKinds,
      ...(result.gatewayPath ? { gatewayPath: result.gatewayPath } : {}),
    }
  }
}
