import { describe, expect, it } from 'vitest'
import { HERMES_PROFILE_RUNTIME_CONTRACT, type PermissionGrantIntent, type ProfileMutation } from './contracts'
import { DeterministicHermesProfileRuntimeAdapter } from './DeterministicHermesProfileRuntimeAdapter'
import { createRequestContext, inspectProfileImport } from './runtimeSafety'

function context(profileId = 'profile-main', correlationId = 'test:1', expectedRevision?: number) {
  return createRequestContext(profileId, correlationId, expectedRevision)
}

describe('deterministic profile-runtime adapter', () => {
  it('isolates document saves and enforces optimistic revisions', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const controller = new AbortController()
    const main = await adapter.load(context('profile-main', 'load:main'), controller.signal)
    const lab = await adapter.load(context('profile-lab', 'load:lab'), controller.signal)
    const saved = await adapter.saveDocument({ ...context('profile-main', 'save:main', main.revision), document: 'persona', text: 'Main changed' }, controller.signal)
    const labAfter = await adapter.load(context('profile-lab', 'load:lab:2'), controller.signal)
    expect(saved.value.documents.find((item) => item.kind === 'persona')?.text).toBe('Main changed')
    expect(labAfter.value.documents).toEqual(lab.value.documents)
    await expect(adapter.saveDocument({ ...context('profile-main', 'save:stale', main.revision), document: 'soul', text: 'stale' }, controller.signal)).rejects.toThrow(/revision conflict/i)
  })

  it('requires a matching one-time destructive confirmation for deletion', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const signal = new AbortController().signal
    const create: ProfileMutation = { kind: 'create', name: 'Disposable' }
    const createPreview = await adapter.previewProfileMutation({ ...context('profile-main', 'create:preview'), mutation: create }, signal)
    const created = await adapter.applyProfileMutation({ ...context('profile-main', 'create:apply'), mutation: create, previewId: createPreview.value.previewId }, signal)
    const disposable = created.value.affectedProfileId
    const deletion: ProfileMutation = { kind: 'delete' }
    const deletePreview = await adapter.previewProfileMutation({ ...context(disposable, 'delete:preview'), mutation: deletion }, signal)
    await expect(adapter.applyProfileMutation({ ...context(disposable, 'delete:bad'), mutation: deletion, previewId: deletePreview.value.previewId, destructiveConfirmation: { phrase: 'yes' } }, signal)).rejects.toThrow(/confirmation/i)
    await expect(adapter.applyProfileMutation({ ...context('profile-lab', 'delete:cross'), mutation: deletion, previewId: deletePreview.value.previewId, destructiveConfirmation: { phrase: deletePreview.value.confirmationPhrase! } }, signal)).rejects.toThrow(/cross-profile|preview/i)
    const deleted = await adapter.applyProfileMutation({ ...context(disposable, 'delete:ok'), mutation: deletion, previewId: deletePreview.value.previewId, destructiveConfirmation: { phrase: deletePreview.value.confirmationPhrase! } }, signal)
    expect(deleted.value.affectedProfileId).toBe(disposable)
    await expect(adapter.load(context(disposable, 'load:deleted'), signal)).rejects.toThrow(/unavailable/i)
  })

  it('separates permission preview from the explicit typed grant intent', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const signal = new AbortController().signal
    const intent: PermissionGrantIntent = { permissionId: 'screen-read', duration: 'session', rationale: 'test' }
    const before = await adapter.load(context('profile-main', 'permission:before'), signal)
    expect(before.value.computerUse.permissions[0].state).toBe('not_requested')
    await expect(adapter.confirmPermissionGrant({ ...context('profile-main', 'permission:direct'), intent, previewId: 'missing', confirmationPhrase: 'anything' }, signal)).rejects.toThrow(/preview/i)
    const preview = await adapter.previewPermissionGrant({ ...context('profile-main', 'permission:preview'), intent }, signal)
    const stillNotGranted = await adapter.load(context('profile-main', 'permission:middle'), signal)
    expect(stillNotGranted.value.computerUse.permissions[0].state).toBe('not_requested')
    const granted = await adapter.confirmPermissionGrant({ ...context('profile-main', 'permission:confirm'), intent, previewId: preview.value.previewId, confirmationPhrase: preview.value.confirmationPhrase! }, signal)
    expect(granted.value.computerUse.permissions[0].state).toBe('granted')
  })

  it('exports only bounded profile settings and never round-trips provider secrets or status', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const signal = new AbortController().signal
    const result = await adapter.exportProfile(context('profile-main', 'export:1'), signal)
    const parsed = JSON.parse(result.value.text) as Record<string, unknown>
    expect(parsed.format).toBe(HERMES_PROFILE_RUNTIME_CONTRACT)
    expect(parsed).not.toHaveProperty('providers')
    expect(parsed).not.toHaveProperty('computerUse')
    expect(result.value.text).not.toMatch(/oauth|credential|token|secret|permission/i)
    expect(result.value.byteCount).toBeLessThan(256 * 1024)
  })

  it('imports bounded non-secret values without importing permissions or providers', async () => {
    const adapter = new DeterministicHermesProfileRuntimeAdapter()
    const signal = new AbortController().signal
    const inspection = inspectProfileImport(JSON.stringify({
      format: HERMES_PROFILE_RUNTIME_CONTRACT,
      name: 'Imported',
      documents: { persona: 'Imported persona' },
      intent: { modelId: 'imported-model', projectPath: null, worktreePath: null, note: '' },
      configuration: { 'runtime.mode': 'careful' },
    }))
    const preview = await adapter.previewImport({ ...context('profile-main', 'import:preview'), inspection }, signal)
    const result = await adapter.applyImport({ ...context('profile-main', 'import:apply'), inspection, previewId: preview.value.previewId, confirmationPhrase: preview.value.confirmationPhrase! }, signal)
    expect(result.value.documents.find((item) => item.kind === 'persona')?.text).toBe('Imported persona')
    expect(result.value.configuration['runtime.mode']).toBe('careful')
    expect(result.value.computerUse.permissions[0].state).toBe('not_requested')
    expect(result.value.providers[0].configuredCredentialSlots).toBe(1)
  })
})
