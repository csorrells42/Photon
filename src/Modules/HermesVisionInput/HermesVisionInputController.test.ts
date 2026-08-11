import { describe, expect, it, vi } from 'vitest'
import {
  HermesVisionInputController,
  HermesVisionInputError,
  normalizeHermesVisionInput,
  toHermesVisionDisplayError,
  type HermesVisionInputProvider,
} from './index'

const PNG_BYTES = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01])

describe('Hermes vision input normalization', () => {
  it('copies bytes and preserves a simultaneously supplied host path', () => {
    const sourceBytes = new Uint8Array(PNG_BYTES)
    const normalized = normalizeHermesVisionInput({
      id: 'image-1',
      origin: 'screenshot',
      displayName: 'capture.png',
      declaredMediaType: 'image/png',
      bytes: sourceBytes,
      path: 'C:\\captures\\capture.png',
    })

    sourceBytes[0] = 0
    expect(normalized.carriers).toHaveLength(2)
    expect(normalized.carriers[0]).toMatchObject({ kind: 'bytes', byteLength: PNG_BYTES.byteLength, mediaType: 'image/png' })
    expect(normalized.carriers[0].kind === 'bytes' ? [...normalized.carriers[0].bytes] : []).toEqual([...PNG_BYTES])
    expect(normalized.carriers[1]).toEqual({ kind: 'path', path: 'C:\\captures\\capture.png', mediaType: 'image/png' })
  })

  it('rejects missing carriers and disguised non-image bytes with display-safe messages', () => {
    expect(() => normalizeHermesVisionInput({ id: 'none', origin: 'file-picker' }))
      .toThrow('Choose an image or capture a screenshot before attaching it.')
    expect(() => normalizeHermesVisionInput({
      id: 'bad',
      origin: 'clipboard',
      displayName: 'secret\nvalue.png',
      bytes: new Uint8Array([1, 2, 3, 4]),
    })).toThrow('The selected image format is not supported.')
  })
})

describe('HermesVisionInputController', () => {
  it('forwards every carrier without loss and reports an explicit byte-consumption choice', async () => {
    const stage = vi.fn<HermesVisionInputProvider['stage']>(async (request) => {
      expect(request.input.carriers[0]).toMatchObject({ kind: 'bytes', byteLength: PNG_BYTES.byteLength })
      expect(request.input.carriers[1]).toEqual({ kind: 'path', path: 'C:\\captures\\screen.png', mediaType: 'image/png' })
      return { attachmentId: 'attached-1', consumed: { kind: 'bytes', byteLength: PNG_BYTES.byteLength }, gatewayPath: '/images/screen.png' }
    })
    const controller = new HermesVisionInputController({ stage })

    const receipt = await controller.stage('session-1', {
      id: 'screen-1',
      origin: 'screenshot',
      bytes: PNG_BYTES,
      path: 'C:\\captures\\screen.png',
    })

    expect(stage).toHaveBeenCalledOnce()
    expect(receipt.availableCarrierKinds).toEqual(['bytes', 'path'])
    expect(receipt.consumed).toEqual({ kind: 'bytes', byteLength: PNG_BYTES.byteLength })
    expect(receipt.unconsumedCarrierKinds).toEqual(['path'])
  })

  it('preserves exact path evidence when a provider consumes the host path', async () => {
    const path = 'C:\\captures\\screen.webp'
    const controller = new HermesVisionInputController({
      stage: async () => ({ attachmentId: 'attached-2', consumed: { kind: 'path', path } }),
    })

    await expect(controller.stage('session-2', { id: 'screen-2', origin: 'host-path', path }))
      .resolves.toMatchObject({ consumed: { kind: 'path', path }, availableCarrierKinds: ['path'] })
  })

  it.each([
    { attachmentId: 'bad-bytes', consumed: { kind: 'bytes' as const, byteLength: PNG_BYTES.byteLength - 1 } },
    { attachmentId: 'bad-path', consumed: { kind: 'path' as const, path: 'C:\\other\\screen.png' } },
  ])('rejects provider success without matching source evidence', async (result) => {
    const controller = new HermesVisionInputController({ stage: async () => result })
    await expect(controller.stage('session-3', {
      id: 'screen-3',
      origin: 'screenshot',
      bytes: PNG_BYTES,
      path: 'C:\\captures\\screen.png',
    })).rejects.toMatchObject({ code: 'source-not-confirmed' })
  })

  it('converts provider failures into a message safe for direct display', async () => {
    const controller = new HermesVisionInputController({
      stage: async () => { throw new Error('C:\\Users\\person\\secret.png token=real-secret\nstack details') },
    })

    const caught = await controller.stage('session-4', {
      id: 'screen-4',
      origin: 'clipboard',
      bytes: PNG_BYTES,
    }).catch((reason: unknown) => reason)

    expect(caught).toBeInstanceOf(HermesVisionInputError)
    expect(toHermesVisionDisplayError(caught)).toEqual({
      code: 'provider-failed',
      message: 'Hermes could not attach the image. Try again.',
    })
    expect(String((caught as Error).message)).not.toMatch(/Users|secret|token|stack/)
  })

  it('never surfaces unknown raw errors through the display helper', () => {
    expect(toHermesVisionDisplayError(new Error('api_key=private-value\nC:\\private\\image.png'))).toEqual({
      code: 'provider-failed',
      message: 'Hermes could not attach the image. Try again.',
    })
  })
})
