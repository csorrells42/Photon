import { hermesGateway } from '../HermesGateway/HermesGatewayClient'
import {
  HermesVisionInputError,
  type HermesVisionByteCarrier,
  type HermesVisionInputProvider,
  type HermesVisionPathCarrier,
  type HermesVisionStageRequest,
} from './contracts'
import { HermesVisionInputController } from './HermesVisionInputController'

export type HermesVisionGatewayRequest = (
  method: string,
  params: Record<string, unknown>,
  timeoutMs?: number,
) => Promise<unknown>

type HermesGatewayImageAttachResponse = {
  attached?: unknown
  bytes?: unknown
  path?: unknown
}

export type HermesTrustedSameHostPathReceipt = {
  consumedPath: string
}

export interface HermesTrustedSameHostVisionPathAttacher {
  attach(
    sessionId: string,
    carrier: HermesVisionPathCarrier,
    signal?: AbortSignal,
  ): Promise<HermesTrustedSameHostPathReceipt>
}

export type HermesGatewayVisionProviderOptions = {
  request?: HermesVisionGatewayRequest
  trustedSameHostPathAttacher?: HermesTrustedSameHostVisionPathAttacher
  encodeBase64?: (bytes: Uint8Array) => string
  createAttachmentId?: () => string
}

const VISION_ATTACH_TIMEOUT_MS = 120_000
const BINARY_CHUNK_SIZE = 0x8000

function attachFailed() {
  return new HermesVisionInputError('provider-failed', 'Hermes could not attach the image. Try again.')
}

function assertNotAborted(signal?: AbortSignal) {
  if (signal?.aborted) throw attachFailed()
}

function defaultEncodeBase64(bytes: Uint8Array) {
  let binary = ''
  for (let offset = 0; offset < bytes.byteLength; offset += BINARY_CHUNK_SIZE) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + BINARY_CHUNK_SIZE))
  }
  return window.btoa(binary)
}

function defaultAttachmentId() {
  return crypto.randomUUID()
}

function validAttachmentId(value: string) {
  const id = value.trim()
  return id.length > 0 && id.length <= 256 && !/[\u0000-\u001f\u007f]/.test(id) ? id : null
}

function byteCarrier(request: HermesVisionStageRequest) {
  return request.input.carriers.find((carrier): carrier is HermesVisionByteCarrier => carrier.kind === 'bytes')
}

function pathCarrier(request: HermesVisionStageRequest) {
  return request.input.carriers.find((carrier): carrier is HermesVisionPathCarrier => carrier.kind === 'path')
}

export function createHermesTrustedSameHostVisionPathAttacher(
  request: HermesVisionGatewayRequest = hermesGateway.request.bind(hermesGateway),
): HermesTrustedSameHostVisionPathAttacher {
  return {
    async attach(sessionId, carrier, signal) {
      assertNotAborted(signal)
      let response: HermesGatewayImageAttachResponse
      try {
        response = await request('image.attach', {
          session_id: sessionId,
          path: carrier.path,
        }, VISION_ATTACH_TIMEOUT_MS) as HermesGatewayImageAttachResponse
      } catch {
        throw attachFailed()
      }
      assertNotAborted(signal)
      if (response?.attached !== true || response.path !== carrier.path) throw attachFailed()
      return { consumedPath: carrier.path }
    },
  }
}

export async function readHermesVisionFileBytes(
  file: Pick<File, 'arrayBuffer'>,
): Promise<Uint8Array> {
  try {
    return new Uint8Array(await file.arrayBuffer())
  } catch {
    throw new HermesVisionInputError('provider-failed', 'Hermes could not read the selected image. Try again.')
  }
}

export class HermesGatewayVisionProvider implements HermesVisionInputProvider {
  private readonly request: HermesVisionGatewayRequest
  private readonly trustedSameHostPathAttacher?: HermesTrustedSameHostVisionPathAttacher
  private readonly encodeBase64: (bytes: Uint8Array) => string
  private readonly createAttachmentId: () => string

  constructor(options: HermesGatewayVisionProviderOptions = {}) {
    this.request = options.request ?? hermesGateway.request.bind(hermesGateway)
    this.trustedSameHostPathAttacher = options.trustedSameHostPathAttacher
    this.encodeBase64 = options.encodeBase64 ?? defaultEncodeBase64
    this.createAttachmentId = options.createAttachmentId ?? defaultAttachmentId
  }

  async stage(request: HermesVisionStageRequest) {
    assertNotAborted(request.signal)
    const bytes = byteCarrier(request)
    if (bytes) return this.stageBytes(request, bytes)

    const path = pathCarrier(request)
    if (!path || !this.trustedSameHostPathAttacher) throw attachFailed()

    let receipt: HermesTrustedSameHostPathReceipt
    try {
      receipt = await this.trustedSameHostPathAttacher.attach(request.sessionId, path, request.signal)
    } catch {
      throw attachFailed()
    }
    assertNotAborted(request.signal)
    if (receipt?.consumedPath !== path.path) throw attachFailed()
    return {
      attachmentId: this.attachmentId(),
      consumed: { kind: 'path' as const, path: path.path },
    }
  }

  private async stageBytes(request: HermesVisionStageRequest, carrier: HermesVisionByteCarrier) {
    let contentBase64: string
    try {
      contentBase64 = `data:${carrier.mediaType};base64,${this.encodeBase64(carrier.bytes)}`
    } catch {
      throw attachFailed()
    }

    assertNotAborted(request.signal)
    let response: HermesGatewayImageAttachResponse
    try {
      response = await this.request('image.attach_bytes', {
        session_id: request.sessionId,
        content_base64: contentBase64,
        filename: carrier.filename,
      }, VISION_ATTACH_TIMEOUT_MS) as HermesGatewayImageAttachResponse
    } catch {
      throw attachFailed()
    } finally {
      contentBase64 = ''
    }

    assertNotAborted(request.signal)
    if (response?.attached !== true || response.bytes !== carrier.byteLength) throw attachFailed()
    return {
      attachmentId: this.attachmentId(),
      consumed: { kind: 'bytes' as const, byteLength: carrier.byteLength },
    }
  }

  private attachmentId() {
    const attachmentId = validAttachmentId(this.createAttachmentId())
    if (!attachmentId) throw attachFailed()
    return attachmentId
  }
}

export const hermesGatewayVisionProvider = new HermesGatewayVisionProvider()
export const hermesGatewayVisionController = new HermesVisionInputController(hermesGatewayVisionProvider)
