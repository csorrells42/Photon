import { describe, expect, it, vi } from 'vitest'
import type { HermesVisionInputController, HermesVisionInputOrigin } from '../HermesVisionInput'
import { stageHermesVisionAttachmentForSend } from './useHermesChat'

const PNG_BYTES = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01])

function attachment(
  arrayBuffer: () => Promise<ArrayBuffer> = async () => PNG_BYTES.slice().buffer,
  origin: HermesVisionInputOrigin = 'clipboard',
) {
  return {
    id: 'image-1',
    file: {
      name: 'screen.png',
      type: 'image/png',
      arrayBuffer,
    } as File,
    kind: 'image' as const,
    label: 'screen.png',
    origin,
  }
}

function controller(
  stage: Pick<HermesVisionInputController, 'stage'>['stage'],
): Pick<HermesVisionInputController, 'stage'> {
  return { stage }
}

describe('useHermesChat vision staging seam', () => {
  it('awaits the controller consumption receipt before resolving the prompt-ready attachment', async () => {
    let release: ((value: Awaited<ReturnType<HermesVisionInputController['stage']>>) => void) | undefined
    const stage = vi.fn<Pick<HermesVisionInputController, 'stage'>['stage']>(() => new Promise((resolve) => { release = resolve }))
    const staged = stageHermesVisionAttachmentForSend(controller(stage), 'session-1', attachment(), () => true)
    let resolved = false
    void staged.then(() => { resolved = true })

    await Promise.resolve()
    await Promise.resolve()
    expect(stage).toHaveBeenCalledWith('session-1', expect.objectContaining({
      id: 'image-1',
      origin: 'clipboard',
      displayName: 'screen.png',
      declaredMediaType: 'image/png',
      bytes: PNG_BYTES,
    }))
    expect(resolved).toBe(false)

    release?.({
      attachmentId: 'opaque-1',
      displayName: 'screen.png',
      consumed: { kind: 'bytes', byteLength: PNG_BYTES.byteLength },
      availableCarrierKinds: ['bytes'],
      unconsumedCarrierKinds: [],
    })

    await expect(staged).resolves.toEqual({ kind: 'image', label: 'screen.png' })
  })

  it('fails closed when the carrier was not consumed or the session changed', async () => {
    const unconsumed = controller(async () => ({
      attachmentId: 'opaque-2',
      displayName: 'screen.png',
      consumed: { kind: 'bytes', byteLength: PNG_BYTES.byteLength },
      availableCarrierKinds: ['bytes', 'path'],
      unconsumedCarrierKinds: ['path'],
    }))
    await expect(stageHermesVisionAttachmentForSend(unconsumed, 'session-2', attachment(), () => true))
      .rejects.toMatchObject({ code: 'provider-failed' })

    let current = true
    const stale = controller(async () => {
      current = false
      return {
        attachmentId: 'opaque-3',
        displayName: 'screen.png',
        consumed: { kind: 'bytes', byteLength: PNG_BYTES.byteLength },
        availableCarrierKinds: ['bytes'],
        unconsumedCarrierKinds: [],
      }
    })
    await expect(stageHermesVisionAttachmentForSend(stale, 'session-3', attachment(), () => current))
      .rejects.toMatchObject({ code: 'provider-failed' })
  })

  it('turns file-read failures into a bounded display-safe error', async () => {
    const secret = 'C:\\Users\\person\\private.png token=secret'
    const stage = vi.fn<Pick<HermesVisionInputController, 'stage'>['stage']>()
    const reason = await stageHermesVisionAttachmentForSend(
      controller(stage),
      'session-4',
      attachment(async () => { throw new Error(secret) }),
      () => true,
    ).catch((error: unknown) => error)

    expect(reason).toMatchObject({ code: 'provider-failed', message: 'Hermes could not read the selected image. Try again.' })
    expect(String((reason as Error).message)).not.toContain(secret)
    expect(stage).not.toHaveBeenCalled()
  })
})
