import { describe, expect, it, vi } from 'vitest'
import { HermesComposerController } from './HermesComposerController'
import { HERMES_COMPOSER_LIMITS, HermesComposerInputError } from './types'

function ids() {
  let value = 0
  return () => `id-${++value}`
}

describe('HermesComposerController', () => {
  it('edits, removes, and reorders a bounded FIFO queue without a draft field', () => {
    const controller = new HermesComposerController({ submit: async () => ({ accepted: true }), createId: ids(), now: () => 10 })
    const one = controller.enqueuePrompt('one')
    const two = controller.enqueuePrompt('two')
    const three = controller.enqueuePrompt('three')

    controller.editQueueItem(two.id, 'two revised')
    controller.moveQueueItem(three.id, 0)
    controller.removeQueueItem(one.id)

    expect(controller.getSnapshot().queue.map((item) => item.intent)).toEqual([
      { kind: 'prompt', text: 'three' },
      { kind: 'prompt', text: 'two revised' },
    ])
    expect(controller.getSnapshot()).not.toHaveProperty('draft')
  })

  it('submits a queue item at most once and rejects a concurrent duplicate', async () => {
    let resolve!: (value: { accepted: boolean }) => void
    const callback = vi.fn(() => new Promise<{ accepted: boolean }>((done) => { resolve = done }))
    const controller = new HermesComposerController({ submit: callback, createId: ids(), now: () => 10 })
    controller.enqueuePrompt('only once')

    const first = controller.submitNext()
    expect(await controller.submitNext()).toEqual({ status: 'busy' })
    expect(callback).toHaveBeenCalledTimes(1)
    expect(controller.getSnapshot().queue).toHaveLength(0)
    resolve({ accepted: true })
    expect((await first).status).toBe('submitted')
    expect(await controller.submitNext()).toEqual({ status: 'empty' })
    expect(callback).toHaveBeenCalledTimes(1)
  })

  it('submits in the explicit reordered queue order', async () => {
    const submitted: string[] = []
    const controller = new HermesComposerController({
      submit: async (intent) => { if (intent.kind === 'prompt') submitted.push(intent.text); return { accepted: true } },
      createId: ids(),
    })
    controller.enqueuePrompt('first')
    const second = controller.enqueuePrompt('second')
    controller.enqueuePrompt('third')
    controller.moveQueueItem(second.id, 0)

    await controller.submitNext()
    await controller.submitNext()
    await controller.submitNext()
    expect(submitted).toEqual(['second', 'first', 'third'])
  })

  it('cancels without requeueing or replaying', async () => {
    const callback = vi.fn((_intent, context: { signal: AbortSignal }) => new Promise<{ accepted: boolean }>((_resolve, reject) => {
      context.signal.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true })
    }))
    const controller = new HermesComposerController({ submit: callback, createId: ids(), now: () => 11 })
    controller.enqueuePrompt('cancel me')

    const submission = controller.submitNext()
    expect(controller.cancelSubmission()).toBe(true)
    expect((await submission).status).toBe('cancelled')
    expect(controller.getSnapshot().queue).toHaveLength(0)
    expect(controller.getSnapshot().history[0].outcome).toBe('cancelled')
    expect(callback).toHaveBeenCalledTimes(1)
  })

  it('never auto-replays across reconnecting, offline, blocked, or ready transitions', async () => {
    const callback = vi.fn(async () => ({ accepted: true }))
    const controller = new HermesComposerController({ submit: callback, createId: ids() })
    controller.enqueuePrompt('parked')

    controller.setConnection('reconnecting')
    expect(await controller.submitNext()).toEqual({ status: 'unavailable' })
    controller.setConnection('offline')
    controller.setConnection('blocked')
    controller.setConnection('ready')
    expect(callback).not.toHaveBeenCalled()
    expect(controller.getSnapshot().queue).toHaveLength(1)

    expect((await controller.submitNext()).status).toBe('submitted')
    expect(callback).toHaveBeenCalledTimes(1)
  })

  it('recovers a failed intent only after an explicit recovery call', async () => {
    let accepted = false
    const callback = vi.fn(async () => ({ accepted, code: accepted ? undefined : 'offline' }))
    const controller = new HermesComposerController({ submit: callback, createId: ids(), now: () => 12 })
    controller.enqueueEditResend('message-7', 'revised text')

    const failed = await controller.submitNext()
    expect(failed.status).toBe('failed')
    expect(controller.getSnapshot().queue).toHaveLength(0)
    if (failed.status !== 'failed') throw new Error('Expected a failed result.')
    const recovered = controller.recover(failed.history.id)
    expect(recovered.intent).toEqual({ kind: 'edit-resend', messageId: 'message-7', text: 'revised text' })
    expect(recovered.id).not.toBe(failed.history.queueItemId)

    accepted = true
    const succeeded = await controller.submitNext()
    expect(succeeded.status).toBe('submitted')
    if (succeeded.status !== 'submitted') throw new Error('Expected a submitted result.')
    expect(() => controller.recover(succeeded.history.id)).toThrow('Successful submissions')
  })

  it('creates retry and regenerate intents that retain supplied message IDs', () => {
    const controller = new HermesComposerController({ submit: async () => ({ accepted: true }), createId: ids() })
    controller.enqueueRetry('message-retry')
    controller.enqueueRegenerate('message-regenerate')
    expect(controller.getSnapshot().queue.map((item) => item.intent)).toEqual([
      { kind: 'retry', messageId: 'message-retry' },
      { kind: 'regenerate', messageId: 'message-regenerate' },
    ])
    expect(() => controller.editQueueItem(controller.getSnapshot().queue[0].id, 'not allowed')).toThrow('do not contain editable')
  })

  it('enforces prompt, queue, ID, and history bounds', async () => {
    const controller = new HermesComposerController({
      submit: async () => ({ accepted: true }),
      queueLimit: 2,
      historyLimit: 2,
      createId: ids(),
    })
    expect(() => controller.enqueuePrompt('x'.repeat(HERMES_COMPOSER_LIMITS.promptCharacters + 1))).toThrow('cannot exceed')
    controller.enqueuePrompt('one')
    controller.enqueuePrompt('two')
    expect(() => controller.enqueuePrompt('overflow')).toThrow('up to 2')
    await controller.submitNext()
    await controller.submitNext()
    controller.enqueuePrompt('three')
    await controller.submitNext()
    expect(controller.getSnapshot().history).toHaveLength(2)
    expect(() => controller.enqueueRetry('x'.repeat(HERMES_COMPOSER_LIMITS.messageIdCharacters + 1))).toThrow('Message ID')
  })

  it('prevents duplicate factory IDs from entering queue state', () => {
    const controller = new HermesComposerController({ submit: async () => ({ accepted: true }), createId: () => 'duplicate' })
    controller.enqueuePrompt('one')
    expect(() => controller.enqueuePrompt('two')).toThrow('repeatedly returned a duplicate')
  })

  it('supports all callback-only voice states with bounded error text', () => {
    const controller = new HermesComposerController({ submit: async () => ({ accepted: true }), createId: ids() })
    for (const state of [{ kind: 'listening' }, { kind: 'processing' }, { kind: 'idle' }] as const) {
      controller.setVoiceState(state)
      expect(controller.getSnapshot().voice).toEqual(state)
    }
    controller.setVoiceState({ kind: 'error', message: 'Microphone permission was denied.' })
    expect(controller.getSnapshot().voice.kind).toBe('error')
    expect(() => controller.setVoiceState({ kind: 'error', message: 'x'.repeat(HERMES_COMPOSER_LIMITS.descriptionCharacters + 1) })).toThrow(HermesComposerInputError)
  })

  it('sanitizes callback failures into fixed history codes', async () => {
    const controller = new HermesComposerController({
      submit: async () => { throw new Error('<secret callback detail>') },
      createId: ids(),
    })
    controller.enqueuePrompt('safe')
    await controller.submitNext()
    expect(controller.getSnapshot().history[0].code).toBe('submission-failed')
    expect(JSON.stringify(controller.getSnapshot())).not.toContain('secret callback detail')
  })
})
