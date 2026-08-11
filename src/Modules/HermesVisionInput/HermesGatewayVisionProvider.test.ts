import { describe, expect, it, vi } from 'vitest'
import {
  createHermesTrustedSameHostVisionPathAttacher,
  HermesGatewayVisionProvider,
  HermesVisionInputController,
  type HermesVisionGatewayRequest,
} from './index'

const PNG_BYTES = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01])

function provider(request: HermesVisionGatewayRequest, options: Partial<ConstructorParameters<typeof HermesGatewayVisionProvider>[0]> = {}) {
  return new HermesGatewayVisionProvider({
    request,
    encodeBase64: () => 'encoded-pixels',
    createAttachmentId: () => 'opaque-attachment-1',
    ...options,
  })
}

describe('HermesGatewayVisionProvider', () => {
  it('uploads controller-normalized bytes and requires the exact decoded byte-count receipt', async () => {
    const request = vi.fn<HermesVisionGatewayRequest>(async (method, params) => {
      expect(method).toBe('image.attach_bytes')
      expect(params).toEqual({
        session_id: 'session-1',
        content_base64: 'data:image/png;base64,encoded-pixels',
        filename: 'screen.png',
      })
      return { attached: true, bytes: PNG_BYTES.byteLength, path: 'C:\\private\\gateway\\upload.png' } as never
    })
    const controller = new HermesVisionInputController(provider(request))

    const receipt = await controller.stage('session-1', {
      id: 'screen-1',
      origin: 'clipboard',
      displayName: 'screen.png',
      bytes: PNG_BYTES,
    })

    expect(receipt).toEqual({
      attachmentId: 'opaque-attachment-1',
      displayName: 'screen.png',
      consumed: { kind: 'bytes', byteLength: PNG_BYTES.byteLength },
      availableCarrierKinds: ['bytes'],
      unconsumedCarrierKinds: [],
    })
    expect(JSON.stringify(receipt)).not.toMatch(/encoded-pixels|private|gateway|upload\.png/)
  })

  it('fails closed with a display-safe error when byte attestation is absent or wrong', async () => {
    const rawSecret = 'C:\\Users\\person\\secret.png?token=private'
    const request: HermesVisionGatewayRequest = async () => ({
      attached: true,
      bytes: PNG_BYTES.byteLength - 1,
      path: rawSecret,
    } as never)
    const controller = new HermesVisionInputController(provider(request))

    const reason = await controller.stage('session-2', {
      id: 'screen-2',
      origin: 'screenshot',
      bytes: PNG_BYTES,
    }).catch((error: unknown) => error)

    expect(reason).toMatchObject({ code: 'provider-failed', message: 'Hermes could not attach the image. Try again.' })
    expect(String((reason as Error).message)).not.toContain(rawSecret)
  })

  it('does not attach a path unless the explicit same-host path seam is injected', async () => {
    const request = vi.fn<HermesVisionGatewayRequest>()
    const controller = new HermesVisionInputController(provider(request))

    await expect(controller.stage('session-3', {
      id: 'path-1',
      origin: 'host-path',
      path: 'C:\\captures\\screen.png',
    })).rejects.toMatchObject({ code: 'provider-failed' })
    expect(request).not.toHaveBeenCalled()
  })

  it('uses image.attach only through the same-host path seam and requires an exact path receipt', async () => {
    const request = vi.fn<HermesVisionGatewayRequest>(async (method, params) => {
      expect(method).toBe('image.attach')
      expect(params).toEqual({ session_id: 'session-4', path: 'C:\\captures\\screen.webp' })
      return { attached: true, path: 'C:\\captures\\screen.webp' } as never
    })
    const gatewayProvider = provider(request, {
      trustedSameHostPathAttacher: createHermesTrustedSameHostVisionPathAttacher(request),
    })
    const controller = new HermesVisionInputController(gatewayProvider)

    await expect(controller.stage('session-4', {
      id: 'path-2',
      origin: 'host-path',
      path: 'C:\\captures\\screen.webp',
    })).resolves.toMatchObject({
      attachmentId: 'opaque-attachment-1',
      consumed: { kind: 'path', path: 'C:\\captures\\screen.webp' },
      unconsumedCarrierKinds: [],
    })
  })

  it('prefers bytes when both carriers exist and never invokes the trusted path seam', async () => {
    const request: HermesVisionGatewayRequest = async () => ({ attached: true, bytes: PNG_BYTES.byteLength } as never)
    const attach = vi.fn(async () => ({ consumedPath: 'C:\\captures\\screen.png' }))
    const controller = new HermesVisionInputController(provider(request, {
      trustedSameHostPathAttacher: { attach },
    }))

    const receipt = await controller.stage('session-5', {
      id: 'both-1',
      origin: 'screenshot',
      bytes: PNG_BYTES,
      path: 'C:\\captures\\screen.png',
    })

    expect(receipt.consumed).toEqual({ kind: 'bytes', byteLength: PNG_BYTES.byteLength })
    expect(receipt.unconsumedCarrierKinds).toEqual(['path'])
    expect(attach).not.toHaveBeenCalled()
  })
})
