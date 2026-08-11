import type { ReactNode } from 'react'
import {
  AGENT_PANEL_RENDER_CONTRACT,
  type AgentPanelDescriptor,
  type AgentPanelDockRegistration,
  type AgentPanelFactory,
  type AgentPanelRegistrySnapshot,
} from './contracts'
import { validateAgentPanelRegistrySnapshot } from './registryValidation'

export type AgentPanelFactoryResolution =
  | { kind: 'ready'; panel: AgentPanelDescriptor; factory: AgentPanelFactory }
  | { kind: 'unavailable'; panel: AgentPanelDescriptor; reason: 'panel_unavailable' | 'factory_unregistered' }

const FACTORY_KEY = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/

function validateFactoryKey(value: string) {
  if (!FACTORY_KEY.test(value)) throw new Error('Agent panel factory key is malformed.')
  return value
}

export class AgentPanelFactoryCatalog {
  private readonly factories = new Map<string, AgentPanelFactory>()

  get size() { return this.factories.size }

  register(factoryKey: string, factory: AgentPanelFactory): void {
    const key = validateFactoryKey(factoryKey)
    if (typeof factory !== 'function') throw new Error('Agent panel factory must be a render function.')
    if (this.factories.has(key)) throw new Error(`Agent panel factory ${key} is already registered.`)
    this.factories.set(key, factory)
  }

  unregister(factoryKey: string): boolean {
    return this.factories.delete(validateFactoryKey(factoryKey))
  }

  has(factoryKey: string) { return this.factories.has(validateFactoryKey(factoryKey)) }

  resolve(panel: AgentPanelDescriptor): AgentPanelFactoryResolution {
    if (panel.render.contract !== AGENT_PANEL_RENDER_CONTRACT) throw new Error(`Panel ${panel.id} has an incompatible render contract.`)
    if (panel.lifecycle === 'unavailable' || panel.lifecycle === 'retired') return { kind: 'unavailable', panel: structuredClone(panel), reason: 'panel_unavailable' }
    const factory = this.factories.get(panel.render.factoryKey)
    return factory
      ? { kind: 'ready', panel: structuredClone(panel), factory }
      : { kind: 'unavailable', panel: structuredClone(panel), reason: 'factory_unregistered' }
  }
}

export function createAgentPanelDockRegistrations(
  snapshotValue: AgentPanelRegistrySnapshot,
  catalog: AgentPanelFactoryCatalog,
  renderUnavailable: (resolution: Extract<AgentPanelFactoryResolution, { kind: 'unavailable' }>, dockControls: ReactNode) => ReactNode = () => null,
): readonly AgentPanelDockRegistration[] {
  const snapshot = validateAgentPanelRegistrySnapshot(snapshotValue)
  const panels = new Map(snapshot.panels.map((panel) => [panel.id, panel]))
  return snapshot.order.flatMap((id) => {
    const panel = panels.get(id)
    if (!panel || !panel.enabled) return []
    return [{
      id: panel.id,
      label: panel.display.label,
      render: (dockControls: ReactNode) => {
        const resolution = catalog.resolve(panel)
        return resolution.kind === 'ready'
          ? resolution.factory({ panel: resolution.panel, dockControls })
          : renderUnavailable(resolution, dockControls)
      },
    } satisfies AgentPanelDockRegistration]
  })
}
