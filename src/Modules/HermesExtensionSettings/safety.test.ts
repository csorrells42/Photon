import { describe, expect, it } from 'vitest'
import { createDeterministicExtensionSettingsSnapshot } from './FakeHermesExtensionSettingsController'
import {
  backendsForProviderCapability,
  isAdvertisedToolsetSelection,
  nextWorkspaceTab,
  normalizeExtensionSettingsSnapshot,
  providersForCapability,
} from './safety'

describe('Hermes extension settings safety helpers', () => {
  it('keeps Search and Extract providers independent and filters unadvertised combinations', () => {
    const snapshot = createDeterministicExtensionSettingsSnapshot()
    const searchProviders = providersForCapability(snapshot, 'search')
    const extractProviders = providersForCapability(snapshot, 'extract')

    expect(searchProviders.map((provider) => provider.id)).toEqual(['tavily', 'exa'])
    expect(extractProviders.map((provider) => provider.id)).toEqual(['tavily', 'firecrawl'])
    expect(searchProviders.map((provider) => provider.id)).not.toEqual(extractProviders.map((provider) => provider.id))

    const exa = searchProviders.find((provider) => provider.id === 'exa')!
    expect(backendsForProviderCapability(exa, 'search').map((backend) => backend.id)).toEqual(['exa-search'])
    expect(backendsForProviderCapability(exa, 'extract')).toEqual([])
    expect(isAdvertisedToolsetSelection(snapshot, 'search', {
      providerId: 'exa',
      backendId: 'exa-search',
      specialtyModelId: 'search-fast',
    })).toBe(true)
    expect(isAdvertisedToolsetSelection(snapshot, 'extract', {
      providerId: 'exa',
      backendId: 'hidden-extract',
      specialtyModelId: 'extract-precise',
    })).toBe(false)
  })

  it('bounds malformed data, drops secret-shaped fields, redacts credential text, and preserves provenance labels', () => {
    const snapshot = normalizeExtensionSettingsSnapshot({
      state: 'partial',
      skills: [
        {
          id: 'malformed',
          name: 'Unsafe',
          description: 'Authorization: Bearer abcdefghijklmnop',
          content: '<img src=x onerror=alert(1)> api_key=super-secret-value',
          provenance: 'nous-approved',
          editable: true,
          token: 'must-not-survive',
        },
        ...Array.from({ length: 200 }, (_, index) => ({ id: `skill-${index}`, name: `Skill ${index}`, content: 'text', provenance: 'user-created' })),
      ],
      toolsetProviders: 'not-an-array',
      models: [{ id: 'one', label: 'One', providerId: 'provider', specialties: ['general', 'invalid'], available: true, password: 'hidden' }],
      modelSettings: { defaultModelId: 'one', apiKey: 'hidden' },
      mcpServers: [{ id: 'mcp', name: 'MCP', transport: 'stdio', command: 'safe', env: { SECRET: 'hidden' }, provenance: 'external-unreviewed' }],
      notices: [{ id: 'notice', message: 'sk-abcdefghijklmnop' }],
    })

    expect(snapshot.skills).toHaveLength(128)
    expect(snapshot.skills[0].provenance).toBe('nous-approved')
    expect(snapshot.skills[0].editable).toBe(false)
    expect(JSON.stringify(snapshot)).not.toMatch(/must-not-survive|super-secret-value|abcdefghijklmnop|apiKey|password/i)
    expect(snapshot.skills[0].content).toContain('<img src=x onerror=alert(1)>')
    expect(snapshot.skills[0].content).toContain('[redacted]')
    expect(snapshot.models[0].specialties).toEqual(['general'])
    expect(snapshot.mcpServers[0].provenance).toBe('external-unreviewed')
  })

  it('supports roving keyboard tab operation including wrap, Home, and End', () => {
    expect(nextWorkspaceTab(0, 'ArrowRight', 4)).toBe(1)
    expect(nextWorkspaceTab(3, 'ArrowRight', 4)).toBe(0)
    expect(nextWorkspaceTab(0, 'ArrowLeft', 4)).toBe(3)
    expect(nextWorkspaceTab(2, 'Home', 4)).toBe(0)
    expect(nextWorkspaceTab(1, 'End', 4)).toBe(3)
    expect(nextWorkspaceTab(2, 'Enter', 4)).toBe(2)
  })
})
