import { describe, expect, it } from 'vitest'
import { createDeterministicFakeSessionAdminAdapter } from './FakeHermesSessionAdminAdapter'
import { sessionAdminBounds, type SessionAdminResult } from './contracts'
import { SessionAdminValidationError, validateResultProfile } from './validation'

let nextCorrelation = 0
function scope(profileId = 'default') {
  nextCorrelation += 1
  return { profileId, correlationId: `test:${nextCorrelation}` }
}

describe('DeterministicFakeHermesSessionAdminAdapter', () => {
  it('lists only the requested profile and returns honest statistics', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const listed = await adapter.listSessions({ ...scope(), limit: 500 })
    const statistics = await adapter.statistics(scope())

    expect(listed.profileId).toBe('default')
    expect(listed.data?.sessions.map((session) => session.sessionId)).toEqual(['child-default', 'root-default'])
    expect(listed.data?.sessions.every((session) => session.profileId === 'default')).toBe(true)
    expect(statistics.data?.total).toMatchObject({ value: 2, quality: 'reported' })
    expect(statistics.data?.storageBytes).toMatchObject({ value: null, quality: 'unavailable' })
  })

  it('creates branch and fork children and returns a bounded descendant tree', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const fork = await adapter.createBranch({ ...scope(), sourceSessionId: 'root-default', newSessionId: 'fork-one', title: 'Fork one', mode: 'fork' })
    const branch = await adapter.createBranch({ ...scope(), sourceSessionId: 'fork-one', newSessionId: 'branch-two', title: 'Branch two', mode: 'branch' })
    const tree = await adapter.descendants({ ...scope(), rootSessionId: 'root-default', maxDepth: 100, maxNodes: 500 })

    expect(fork.data?.created).toMatchObject({ profileId: 'default', parentSessionId: 'root-default' })
    expect(branch.data?.created.parentSessionId).toBe('fork-one')
    expect(tree.data).toMatchObject({ maxDepth: sessionAdminBounds.maxDescendantDepth, returned: 3 })
    expect(tree.data?.nodes.every((node) => node.profileId === 'default')).toBe(true)
  })

  it('requires delete preview and separate destructive confirmation', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const preview = await adapter.previewDelete({ ...scope(), sessionIds: ['root-default'], includeDescendants: true })
    expect(preview.data).toMatchObject({ requestedIds: ['root-default'], descendantCount: 1 })

    await expect(adapter.commitDelete({ ...scope(), previewToken: preview.data!.previewToken, confirmation: 'missing' as 'delete-reviewed' }))
      .rejects.toMatchObject({ code: 'confirmation-required' })
    const committed = await adapter.commitDelete({ ...scope(), previewToken: preview.data!.previewToken, confirmation: 'delete-reviewed' })
    expect(committed.data?.deletedIds).toEqual(['child-default', 'root-default'])
  })

  it('previews and commits a bounded prune while excluding active sessions', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const preview = await adapter.previewPrune({ ...scope(), criteria: { inactiveBefore: 1_000, includeArchived: false, maxDelete: 500 } })
    expect(preview.data).toMatchObject({ candidateIds: ['root-default'], activeExcluded: 1, truncated: false })
    expect(preview.data?.criteria.maxDelete).toBe(sessionAdminBounds.maxDelete)

    const committed = await adapter.commitPrune({ ...scope(), previewToken: preview.data!.previewToken, confirmation: 'prune-reviewed' })
    expect(committed.data?.deletedIds).toEqual(['root-default'])
  })

  it('validates untrusted import text, commits it, and exports it as bounded JSON text', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const unsafeText = '<img src=x onerror=alert(1)><script>bad()</script>'
    const payload = JSON.stringify({ profileId: 'default', sessions: [{
      sessionId: 'imported-one', title: '<b>Unsafe title</b>',
      messages: [{ role: 'user', text: unsafeText }],
    }] })
    const validation = await adapter.validateImport({ ...scope(), untrustedJsonText: payload })
    expect(validation.data).toMatchObject({ valid: true, truncated: false })
    expect(validation.data?.sessions[0]?.messages[0]?.text).toBe(unsafeText)

    const imported = await adapter.commitImport({ ...scope(), validationToken: validation.data!.validationToken! })
    expect(imported.data?.importedIds).toEqual(['imported-one'])
    const exported = await adapter.exportSessions({ ...scope(), sessionIds: ['imported-one'] })
    expect(exported.data).toMatchObject({ mediaType: 'application/json', fileName: 'hermes-sessions-default.json' })
    expect(JSON.parse(exported.data!.contentText)).toMatchObject({ profileId: 'default' })
    expect(exported.data?.sessions[0]?.profileId).toBe('default')
  })

  it('rejects oversized, duplicate, and cross-profile import data', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const oversized = await adapter.validateImport({ ...scope(), untrustedJsonText: 'x'.repeat(sessionAdminBounds.maxImportBytes + 1) })
    expect(oversized.data).toMatchObject({ valid: false, truncated: true })

    const duplicate = JSON.stringify({ sessions: [
      { sessionId: 'same', title: 'One', messages: [] },
      { sessionId: 'same', title: 'Two', messages: [] },
    ] })
    const duplicateResult = await adapter.validateImport({ ...scope(), untrustedJsonText: duplicate })
    expect(duplicateResult.data?.valid).toBe(false)
    expect(duplicateResult.data?.errors.join(' ')).toContain('Duplicate imported session identity')

    const wrongProfile = await adapter.validateImport({ ...scope(), untrustedJsonText: JSON.stringify({ profileId: 'scarlett', sessions: [{ sessionId: 'new', title: 'New', messages: [] }] }) })
    expect(wrongProfile.data?.valid).toBe(false)
    expect(wrongProfile.data?.errors).toContain('Import profile does not match the active profile.')
  })

  it('sets, replacement-confirms, and clears a per-session model lock', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    await expect(adapter.setModelLock({ ...scope(), sessionId: 'child-default', model: 'provider/model-b', expectedCurrentModel: 'provider/model-a', confirmReplace: false }))
      .rejects.toMatchObject({ code: 'replacement-confirmation-required' })
    const replaced = await adapter.setModelLock({ ...scope(), sessionId: 'child-default', model: 'provider/model-b', expectedCurrentModel: 'provider/model-a', confirmReplace: true })
    expect(replaced.data).toMatchObject({ previousModel: 'provider/model-a', model: 'provider/model-b', replaced: true })
    const cleared = await adapter.clearModelLock({ ...scope(), sessionId: 'child-default', expectedCurrentModel: 'provider/model-b', confirmation: 'clear-model-lock' })
    expect(cleared.data).toMatchObject({ previousModel: 'provider/model-b', model: null })
  })

  it('rejects cross-profile session access and preview-token reuse', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    await expect(adapter.previewDelete({ ...scope('scarlett'), sessionIds: ['root-default'], includeDescendants: false }))
      .rejects.toMatchObject({ code: 'cross-profile' })
    const preview = await adapter.previewDelete({ ...scope(), sessionIds: ['root-default'], includeDescendants: false })
    await expect(adapter.commitDelete({ ...scope('scarlett'), previewToken: preview.data!.previewToken, confirmation: 'delete-reviewed' }))
      .rejects.toMatchObject({ code: 'cross-profile' })
  })

  it('enforces bounded selections and duplicate IDs', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter()
    const tooMany = Array.from({ length: sessionAdminBounds.maxExport + 1 }, (_, index) => `session-${index}`)
    await expect(adapter.exportSessions({ ...scope(), sessionIds: tooMany })).rejects.toMatchObject({ code: 'selection-too-large' })
    await expect(adapter.previewDelete({ ...scope(), sessionIds: ['root-default', 'root-default'], includeDescendants: false }))
      .rejects.toMatchObject({ code: 'duplicate-session' })
  })

  it('returns correlated cancellation and prevents duplicate correlations', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter({ latencyMs: 15 })
    const request = { profileId: 'default', correlationId: 'cancel:one', limit: 5 }
    const controller = new AbortController()
    const promise = adapter.listSessions(request, controller.signal)
    controller.abort()
    await expect(promise).resolves.toMatchObject({ profileId: 'default', correlationId: 'cancel:one', status: 'cancelled' })

    const duplicateAdapter = createDeterministicFakeSessionAdminAdapter()
    await duplicateAdapter.listSessions({ ...request, correlationId: 'duplicate:one' })
    const duplicate = await duplicateAdapter.listSessions({ ...request, correlationId: 'duplicate:one' })
    expect(duplicate).toMatchObject({ status: 'error', data: null })
    expect(duplicate.notices.join(' ')).toContain('Duplicate correlation')
  })

  it('surfaces partial delete failures and configured unavailable/error states honestly', async () => {
    const adapter = createDeterministicFakeSessionAdminAdapter({
      partialDeleteIds: ['root-default'], unavailableOperations: ['statistics'], errorOperations: ['export'],
    })
    const preview = await adapter.previewDelete({ ...scope(), sessionIds: ['root-default', 'child-default'], includeDescendants: false })
    const committed = await adapter.commitDelete({ ...scope(), previewToken: preview.data!.previewToken, confirmation: 'delete-reviewed' })
    expect(committed).toMatchObject({ status: 'partial' })
    expect(committed.data).toMatchObject({ deletedIds: ['child-default'], failed: [{ sessionId: 'root-default' }] })
    await expect(adapter.statistics(scope())).resolves.toMatchObject({ status: 'unavailable', data: null })
    await expect(adapter.exportSessions({ ...scope(), sessionIds: ['root-default'] })).resolves.toMatchObject({ status: 'error', data: null })
  })

  it('rejects mismatched result profile and correlation envelopes', () => {
    const request = { profileId: 'default', correlationId: 'guard:1' }
    const result = {
      contractVersion: 'hermes-session-admin/v1', profileId: 'scarlett', correlationId: 'guard:1',
      status: 'success', data: {}, notices: [],
    } as SessionAdminResult<Record<string, never>>
    expect(() => validateResultProfile(request, result)).toThrow(SessionAdminValidationError)
  })
})
