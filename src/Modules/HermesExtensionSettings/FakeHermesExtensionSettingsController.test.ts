import { describe, expect, it } from 'vitest'
import {
  FakeHermesExtensionSettingsController,
  createDeterministicExtensionSettingsSnapshot,
} from './FakeHermesExtensionSettingsController'

describe('FakeHermesExtensionSettingsController', () => {
  it('requires explicit clone-to-user intent and preserves upstream provenance', async () => {
    const controller = new FakeHermesExtensionSettingsController()

    await expect(controller.preview({
      kind: 'edit',
      skillId: 'nous-research',
      name: 'Changed upstream',
      description: 'Should fail.',
      content: '# Changed',
    })).rejects.toThrow(/Clone upstream skills instead/)

    const review = await controller.preview({
      kind: 'clone-to-user',
      sourceSkillId: 'nous-research',
      name: 'My research',
      description: 'User clone.',
      content: '# My research',
    })

    expect(review.before.join('\n')).toContain('Provenance: nous-approved')
    expect(review.after.join('\n')).toContain('Provenance: user-created')
    const result = await controller.commit({ reviewId: review.reviewId, confirmed: true })
    expect(result.status).toBe('success')

    const snapshot = result.snapshot!
    expect(snapshot.skills.find((skill) => skill.id === 'nous-research')?.provenance).toBe('nous-approved')
    expect(snapshot.skills.find((skill) => skill.name === 'My research')).toMatchObject({
      provenance: 'user-created',
      editable: true,
    })
  })

  it('uses preview then single-use commit for safe MCP update, test, and enable intents', async () => {
    const controller = new FakeHermesExtensionSettingsController()
    const seed = createDeterministicExtensionSettingsSnapshot()
    const original = seed.mcpServers.find((server) => server.id === 'external-docs')!

    const updateReview = await controller.preview({
      kind: 'mcp-update',
      serverId: original.id,
      configuration: { ...original, name: 'External documentation', endpoint: 'https://docs.example.test/mcp' },
    })
    expect(updateReview.before.join('\n')).toContain('External docs')
    expect(updateReview.after.join('\n')).toContain('External documentation')

    const update = await controller.commit({ reviewId: updateReview.reviewId, confirmed: true })
    expect(update.status).toBe('success')
    expect(update.snapshot?.mcpServers.find((server) => server.id === original.id)).toMatchObject({
      name: 'External documentation',
      provenance: 'external-unreviewed',
    })

    const duplicate = await controller.commit({ reviewId: updateReview.reviewId, confirmed: true })
    expect(duplicate.status).toBe('error')
    expect(duplicate.message).toMatch(/already submitted/)

    const testReview = await controller.preview({ kind: 'mcp-test', serverId: original.id })
    expect(testReview.warnings.join('\n')).toMatch(/may contact/)
    expect((await controller.commit({ reviewId: testReview.reviewId, confirmed: true })).message).toMatch(/no live service/i)

    const enableReview = await controller.preview({ kind: 'mcp-enable', serverId: original.id, enabled: true })
    const enabled = await controller.commit({ reviewId: enableReview.reviewId, confirmed: true })
    expect(enabled.snapshot?.mcpServers.find((server) => server.id === original.id)?.enabled).toBe(true)
  })

  it('never round-trips or retains write-only provider secrets', async () => {
    const controller = new FakeHermesExtensionSettingsController()
    const secret = 'do-not-round-trip-91f7d4'
    const result = await controller.validateProvider({
      providerId: 'custom',
      endpoint: { baseUrl: 'https://models.example.test/', apiPath: '/v1', authHeaderName: 'Authorization' },
      secrets: [{ fieldName: 'provider-secret', value: secret }],
    })

    expect(result.state).toBe('valid')
    expect(JSON.stringify(result)).not.toContain(secret)
    expect(JSON.stringify(controller.debugSafeState())).not.toContain(secret)
    expect(JSON.stringify(controller.debugSafeState())).not.toMatch(/secret.*value/i)
  })

  it('requires a separate expensive-model confirmation and prevents duplicate submission', async () => {
    const controller = new FakeHermesExtensionSettingsController()
    const settings = createDeterministicExtensionSettingsSnapshot().modelSettings
    const review = await controller.preview({
      kind: 'models',
      settings: { ...settings, defaultModelId: 'aggregate-pro' },
    })

    expect(review.requiresExpensiveModelConfirmation).toBe(true)
    expect(review.warnings.join('\n')).toMatch(/expensive/i)

    const denied = await controller.commit({ reviewId: review.reviewId, confirmed: true })
    expect(denied.status).toBe('error')
    expect(denied.message).toMatch(/expensive-model confirmation/)

    const committed = await controller.commit({
      reviewId: review.reviewId,
      confirmed: true,
      expensiveModelConfirmed: true,
    })
    expect(committed.status).toBe('success')
    expect(committed.snapshot?.modelSettings.defaultModelId).toBe('aggregate-pro')

    const duplicate = await controller.commit({
      reviewId: review.reviewId,
      confirmed: true,
      expensiveModelConfirmed: true,
    })
    expect(duplicate.status).toBe('error')
  })

  it('rejects an unadvertised Extract backend without changing Search', async () => {
    const controller = new FakeHermesExtensionSettingsController()
    const before = (await controller.load()).snapshot!

    await expect(controller.preview({
      kind: 'toolsets',
      search: { providerId: 'exa', backendId: 'exa-search', specialtyModelId: 'search-fast' },
      extract: { providerId: 'exa', backendId: 'hidden-extract', specialtyModelId: 'extract-precise' },
    })).rejects.toThrow(/Extract selection is not an advertised/)

    expect((await controller.load()).snapshot?.toolsets).toEqual(before.toolsets)
  })
})
