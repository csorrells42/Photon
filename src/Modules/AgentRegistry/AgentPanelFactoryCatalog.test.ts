import { describe, expect, it } from 'vitest'
import { AgentPanelFactoryCatalog, createAgentPanelDockRegistrations } from './AgentPanelFactoryCatalog'
import { AgentPanelRegistry } from './AgentPanelRegistry'
import { createDeterministicAgentPanelFixtures } from './deterministicFixtures'

describe('provider-neutral agent panel render boundary', () => {
  it('projects enabled panels in registry order and invokes factories without provider layout branches', () => {
    const registry = new AgentPanelRegistry(createDeterministicAgentPanelFixtures())
    const catalog = new AgentPanelFactoryCatalog()
    catalog.register('fixture.hermes', ({ panel, dockControls }) => `${panel.providerKind}:${String(dockControls)}`)
    catalog.register('fixture.codex', ({ panel }) => panel.display.label)
    const registrations = createAgentPanelDockRegistrations(registry.snapshot, catalog, (resolution) => `unavailable:${resolution.panel.id}:${resolution.reason}`)
    expect(registrations.map((item) => item.id)).toEqual(['hermes', 'codex', 'claude', 'chatgpt', 'scarlett', 'ali'])
    expect(registrations[0].render('controls')).toBe('nous-hermes:controls')
    expect(registrations[1].render(null)).toBe('Codex')
    expect(registrations[2].render(null)).toBe('unavailable:claude:panel_unavailable')
  })

  it('routes a missing factory to neutral unavailable rendering', () => {
    const registry = new AgentPanelRegistry(createDeterministicAgentPanelFixtures())
    registry.patchRuntime('codex', { lifecycle: 'available' })
    const registrations = createAgentPanelDockRegistrations(registry.snapshot, new AgentPanelFactoryCatalog(), (resolution) => resolution.reason)
    expect(registrations.find((item) => item.id === 'codex')?.render(null)).toBe('factory_unregistered')
  })

  it('omits disabled panels and rejects duplicate factories', () => {
    const registry = new AgentPanelRegistry(createDeterministicAgentPanelFixtures())
    registry.setEnabled('hermes', false)
    const catalog = new AgentPanelFactoryCatalog()
    catalog.register('fixture.codex', () => null)
    expect(() => catalog.register('fixture.codex', () => null)).toThrow(/already registered/i)
    expect(createAgentPanelDockRegistrations(registry.snapshot, catalog).map((item) => item.id)).not.toContain('hermes')
  })

  it('ships six deterministic descriptors as fixtures, not live integrations', () => {
    const fixtures = createDeterministicAgentPanelFixtures()
    expect(fixtures.panels.map((panel) => panel.display.label)).toEqual(['Hermes', 'Codex', 'Claude', 'ChatGPT', 'Scarlett', 'Ali'])
    expect(fixtures.panels.every((panel) => panel.display.description.includes('not a live integration'))).toBe(true)
    expect(new Set(fixtures.panels.map((panel) => panel.providerKind)).size).toBe(6)
  })
})
