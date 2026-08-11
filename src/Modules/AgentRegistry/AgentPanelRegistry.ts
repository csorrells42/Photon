import {
  AGENT_PANEL_REGISTRY_FORMAT,
  type AgentPanelDescriptor,
  type AgentPanelId,
  type AgentPanelRegistrySnapshot,
  type AgentPanelRuntimePatch,
} from './contracts'
import { AGENT_PANEL_REGISTRY_LIMITS, validateAgentPanelDescriptor, validateAgentPanelRegistrySnapshot } from './registryValidation'

function indexFor(value: number, length: number, label: string) {
  if (!Number.isSafeInteger(value) || value < 0 || value > length) throw new Error(`${label} index is outside the registry range.`)
  return value
}

export class AgentPanelRegistry {
  private readonly panels = new Map<AgentPanelId, AgentPanelDescriptor>()
  private order: AgentPanelId[] = []

  constructor(initial: AgentPanelRegistrySnapshot = { format: AGENT_PANEL_REGISTRY_FORMAT, order: [], panels: [] }) {
    const snapshot = validateAgentPanelRegistrySnapshot(initial)
    for (const panel of snapshot.panels) this.panels.set(panel.id, panel)
    this.order = [...snapshot.order]
  }

  get size() { return this.order.length }

  get snapshot(): AgentPanelRegistrySnapshot {
    return structuredClone({
      format: AGENT_PANEL_REGISTRY_FORMAT,
      order: this.order,
      panels: this.order.map((id) => this.required(id)),
    })
  }

  has(panelId: AgentPanelId) { return this.panels.has(panelId) }

  get(panelId: AgentPanelId): AgentPanelDescriptor | null {
    const panel = this.panels.get(panelId)
    return panel ? structuredClone(panel) : null
  }

  list(options: { enabledOnly?: boolean } = {}): readonly AgentPanelDescriptor[] {
    return this.order.flatMap((id) => {
      const panel = this.required(id)
      return options.enabledOnly && !panel.enabled ? [] : [structuredClone(panel)]
    })
  }

  add(value: AgentPanelDescriptor, requestedIndex = this.order.length): AgentPanelRegistrySnapshot {
    if (this.size >= AGENT_PANEL_REGISTRY_LIMITS.panels) throw new Error('Agent panel registry has reached its supported bound.')
    const panel = validateAgentPanelDescriptor(value)
    if (this.panels.has(panel.id)) throw new Error(`Panel identity ${panel.id} is already registered.`)
    const index = indexFor(requestedIndex, this.order.length, 'Add')
    this.panels.set(panel.id, panel)
    this.order.splice(index, 0, panel.id)
    return this.snapshot
  }

  remove(panelId: AgentPanelId): AgentPanelRegistrySnapshot {
    this.required(panelId)
    this.panels.delete(panelId)
    this.order = this.order.filter((id) => id !== panelId)
    return this.snapshot
  }

  setEnabled(panelId: AgentPanelId, enabled: boolean): AgentPanelRegistrySnapshot {
    if (typeof enabled !== 'boolean') throw new Error('Enabled state must be a boolean.')
    const current = this.required(panelId)
    this.panels.set(panelId, { ...current, enabled })
    return this.snapshot
  }

  move(panelId: AgentPanelId, requestedIndex: number): AgentPanelRegistrySnapshot {
    this.required(panelId)
    const index = indexFor(requestedIndex, this.order.length - 1, 'Move')
    this.order = this.order.filter((id) => id !== panelId)
    this.order.splice(index, 0, panelId)
    return this.snapshot
  }

  reorder(nextOrder: readonly AgentPanelId[]): AgentPanelRegistrySnapshot {
    const candidate = validateAgentPanelRegistrySnapshot({ format: AGENT_PANEL_REGISTRY_FORMAT, order: nextOrder, panels: [...this.panels.values()] })
    this.order = [...candidate.order]
    return this.snapshot
  }

  patchRuntime(panelId: AgentPanelId, patch: AgentPanelRuntimePatch): AgentPanelRegistrySnapshot {
    const current = this.required(panelId)
    const allowed = new Set(['lifecycle', 'lifecycleDetail', 'connectionState', 'connectionDetail', 'authenticationDisposition', 'authenticationDetail'])
    const extra = Object.keys(patch).find((key) => !allowed.has(key))
    if (extra) throw new Error(`Runtime patch cannot change stable field ${extra}.`)
    const next = validateAgentPanelDescriptor({ ...current, ...patch })
    this.panels.set(panelId, next)
    return this.snapshot
  }

  private required(panelId: AgentPanelId): AgentPanelDescriptor {
    const panel = this.panels.get(panelId)
    if (!panel) throw new Error(`Panel identity ${panelId} is not registered.`)
    return panel
  }
}
