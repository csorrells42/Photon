import { describe, expect, it } from 'vitest'
import { AgentPanelRegistry } from './AgentPanelRegistry'
import { AGENT_PANEL_REGISTRY_FORMAT } from './contracts'
import { createDeterministicAgentPanelFixtures } from './deterministicFixtures'
import { createAgentPanelRegistryFromSerialized, parseAgentPanelRegistry, serializeAgentPanelRegistry } from './registryPersistence'
import { AGENT_PANEL_REGISTRY_LIMITS, validateAgentPanelRegistrySnapshot } from './registryValidation'

describe('current-format agent registry persistence', () => {
  it('round-trips layout-safe metadata without factories or provider clients', () => {
    const registry = new AgentPanelRegistry(createDeterministicAgentPanelFixtures())
    const text = serializeAgentPanelRegistry(registry)
    const parsed = JSON.parse(text) as Record<string, unknown>
    expect(parsed.format).toBe(AGENT_PANEL_REGISTRY_FORMAT)
    expect(text).toContain('fixture.hermes')
    expect(text).not.toMatch(/localStorage|providerClient|rawClient|nativeHandle/i)
    expect(createAgentPanelRegistryFromSerialized(text).snapshot).toEqual(registry.snapshot)
  })

  it('keeps display markup as inert serialized text', () => {
    const value = createDeterministicAgentPanelFixtures()
    value.panels[0].display.description = '<script>alert(1)</script>'
    const parsed = parseAgentPanelRegistry(serializeAgentPanelRegistry(value))
    expect(parsed.panels[0].display.description).toBe('<script>alert(1)</script>')
  })

  it.each(['password', 'accessToken', 'api_key', 'browserCookie', 'nativeProcessHandle', 'rawProviderClient'])(
    'rejects forbidden field %s before normalization',
    (key) => {
      const value = createDeterministicAgentPanelFixtures() as unknown as Record<string, unknown>
      value[key] = 'forbidden'
      expect(() => validateAgentPanelRegistrySnapshot(value)).toThrow(/forbidden/i)
    },
  )

  it('rejects malformed JSON, non-current formats, unknown fields, bad order, and invalid enum data', () => {
    expect(() => parseAgentPanelRegistry('{bad')).toThrow(/valid JSON/i)
    expect(() => parseAgentPanelRegistry(JSON.stringify({ ...createDeterministicAgentPanelFixtures(), format: 'agent-panel-registry/v0' }))).toThrow(/migration is not attempted/i)
    expect(() => parseAgentPanelRegistry(JSON.stringify({ ...createDeterministicAgentPanelFixtures(), legacy: true }))).toThrow(/unsupported field/i)
    expect(() => parseAgentPanelRegistry(JSON.stringify({ ...createDeterministicAgentPanelFixtures(), order: ['hermes'] }))).toThrow(/every panel/i)
    const invalid = createDeterministicAgentPanelFixtures()
    invalid.panels[0].connectionState = 'online' as never
    expect(() => serializeAgentPanelRegistry(invalid)).toThrow(/connection state is invalid/i)
  })

  it('rejects oversized serialized and metadata inputs', () => {
    expect(() => parseAgentPanelRegistry(' '.repeat(AGENT_PANEL_REGISTRY_LIMITS.serializedBytes + 1))).toThrow(/size limit/i)
    const invalid = createDeterministicAgentPanelFixtures()
    invalid.panels[0].display.description = 'x'.repeat(AGENT_PANEL_REGISTRY_LIMITS.description + 1)
    expect(() => serializeAgentPanelRegistry(invalid)).toThrow(/exceeds/i)
  })
})
