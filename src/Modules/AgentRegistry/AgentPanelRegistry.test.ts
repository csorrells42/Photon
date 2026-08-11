import { describe, expect, it } from 'vitest'
import { AgentPanelRegistry } from './AgentPanelRegistry'
import { AGENT_PANEL_REGISTRY_FORMAT, AGENT_PANEL_RENDER_CONTRACT, type AgentPanelDescriptor } from './contracts'

function descriptor(index: number, providerKind = `provider-${index}`): AgentPanelDescriptor {
  const id = `panel-${index}`
  return {
    id,
    providerKind,
    display: { label: `Panel ${index}`, shortLabel: `P${index}`, description: `Descriptor ${index}`, iconKey: 'bot', accent: 'neutral' },
    capabilities: [{ id: 'conversation', label: 'Conversation', description: 'Neutral capability', availability: 'available' }],
    lifecycle: 'available',
    lifecycleDetail: 'Ready',
    connectionState: 'disconnected',
    connectionDetail: 'Not connected',
    authenticationDisposition: 'delegated',
    authenticationDetail: 'Owned elsewhere',
    dock: { preferredZone: 'right', preferredMode: 'vertical', minimumWidth: 200, minimumHeight: 120, placementPriority: index },
    render: { contract: AGENT_PANEL_RENDER_CONTRACT, factoryKey: `factory-${index}` },
    enabled: true,
  }
}

function snapshot(count: number) {
  const panels = Array.from({ length: count }, (_, index) => descriptor(index))
  return { format: AGENT_PANEL_REGISTRY_FORMAT, order: panels.map((panel) => panel.id), panels } as const
}

describe('AgentPanelRegistry', () => {
  it.each([0, 1, 12, 257])('supports an arbitrary provider-neutral panel count: %i', (count) => {
    const registry = new AgentPanelRegistry(snapshot(count))
    expect(registry.size).toBe(count)
    expect(registry.snapshot.order).toHaveLength(count)
    expect(registry.list()).toHaveLength(count)
  })

  it('rejects duplicate stable identities on construction and add', () => {
    const duplicate = descriptor(1)
    expect(() => new AgentPanelRegistry({ format: AGENT_PANEL_REGISTRY_FORMAT, order: ['panel-1', 'panel-1'], panels: [duplicate, duplicate] })).toThrow(/duplicate/i)
    const registry = new AgentPanelRegistry(snapshot(2))
    expect(() => registry.add(descriptor(1))).toThrow(/already registered/i)
  })

  it('adds, removes, enables, disables, moves, and reorders without changing identities', () => {
    const registry = new AgentPanelRegistry(snapshot(3))
    registry.add(descriptor(3), 1)
    expect(registry.snapshot.order).toEqual(['panel-0', 'panel-3', 'panel-1', 'panel-2'])
    registry.setEnabled('panel-3', false)
    expect(registry.get('panel-3')?.enabled).toBe(false)
    expect(registry.list({ enabledOnly: true }).map((panel) => panel.id)).not.toContain('panel-3')
    registry.setEnabled('panel-3', true)
    registry.move('panel-3', 3)
    expect(registry.snapshot.order).toEqual(['panel-0', 'panel-1', 'panel-2', 'panel-3'])
    registry.reorder(['panel-3', 'panel-1', 'panel-0', 'panel-2'])
    expect(registry.snapshot.order).toEqual(['panel-3', 'panel-1', 'panel-0', 'panel-2'])
    registry.remove('panel-1')
    expect(registry.snapshot.order).toEqual(['panel-3', 'panel-0', 'panel-2'])
    expect(registry.has('panel-1')).toBe(false)
  })

  it('rejects duplicate, missing, and unknown reorder identities', () => {
    const registry = new AgentPanelRegistry(snapshot(3))
    expect(() => registry.reorder(['panel-0', 'panel-0', 'panel-2'])).toThrow(/duplicate|unknown|missing/i)
    expect(() => registry.reorder(['panel-0', 'panel-1'])).toThrow(/every panel/i)
    expect(() => registry.reorder(['panel-0', 'panel-1', 'other'])).toThrow(/unknown/i)
  })

  it('patches only runtime status and preserves provider-neutral stable metadata', () => {
    const registry = new AgentPanelRegistry(snapshot(1))
    registry.patchRuntime('panel-0', { lifecycle: 'unavailable', connectionState: 'unavailable', authenticationDisposition: 'unavailable' })
    const panel = registry.get('panel-0')!
    expect(panel).toMatchObject({ id: 'panel-0', providerKind: 'provider-0', lifecycle: 'unavailable', connectionState: 'unavailable' })
    expect(panel.render.factoryKey).toBe('factory-0')
    expect(() => registry.patchRuntime('panel-0', { providerKind: 'changed' } as never)).toThrow(/stable field/i)
  })

  it('accepts a completely unknown future provider kind without registry branches', () => {
    const registry = new AgentPanelRegistry()
    registry.add(descriptor(99, 'future-lab-provider'))
    expect(registry.get('panel-99')?.providerKind).toBe('future-lab-provider')
  })
})
