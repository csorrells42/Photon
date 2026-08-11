import {
  AGENT_PANEL_REGISTRY_FORMAT,
  AGENT_PANEL_RENDER_CONTRACT,
  type AgentPanelAuthenticationDisposition,
  type AgentPanelCapability,
  type AgentPanelCapabilityAvailability,
  type AgentPanelConnectionState,
  type AgentPanelDescriptor,
  type AgentPanelDockMode,
  type AgentPanelDockZone,
  type AgentPanelLifecycle,
  type AgentPanelRegistrySnapshot,
} from './contracts'

export const AGENT_PANEL_REGISTRY_LIMITS = {
  serializedBytes: 2 * 1024 * 1024,
  panels: 4_096,
  capabilitiesPerPanel: 128,
  identifier: 128,
  label: 256,
  description: 4_096,
  detail: 4_096,
  iconKey: 128,
  accent: 64,
} as const

const IDENTIFIER = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/
const FORBIDDEN_KEY = /(?:password|passphrase|secret|token|api[_-]?key|authorization|cookie|native[_-]?(?:process|handle)|raw[_-]?(?:client|provider)|providerClient)/i
const LIFECYCLES = new Set<AgentPanelLifecycle>(['available', 'starting', 'stopping', 'unavailable', 'retired'])
const CONNECTIONS = new Set<AgentPanelConnectionState>(['not_configured', 'disconnected', 'connecting', 'connected', 'degraded', 'error', 'unavailable'])
const AUTHENTICATION = new Set<AgentPanelAuthenticationDisposition>(['none', 'host_managed', 'user_action_required', 'delegated', 'unsupported', 'unavailable'])
const CAPABILITY_AVAILABILITY = new Set<AgentPanelCapabilityAvailability>(['available', 'unavailable', 'unknown'])
const DOCK_ZONES = new Set<AgentPanelDockZone>(['top', 'right', 'bottom', 'left', 'center'])
const DOCK_MODES = new Set<AgentPanelDockMode>(['vertical', 'horizontal', 'tabs'])

function object(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`)
  return value as Record<string, unknown>
}

function exactKeys(source: Record<string, unknown>, allowed: readonly string[], label: string) {
  const allowedSet = new Set(allowed)
  const extra = Object.keys(source).find((key) => !allowedSet.has(key))
  if (extra) throw new Error(`${label} contains unsupported field ${extra}. Current-format data is required.`)
  const missing = allowed.find((key) => !(key in source))
  if (missing) throw new Error(`${label} is missing required field ${missing}.`)
}

function identifier(value: unknown, label: string) {
  if (typeof value !== 'string' || !IDENTIFIER.test(value)) throw new Error(`${label} is malformed.`)
  return value
}

function text(value: unknown, label: string, maximum: number, required = false) {
  if (typeof value !== 'string') throw new Error(`${label} must be text.`)
  if (CONTROL_CHARACTERS.test(value)) throw new Error(`${label} contains control characters.`)
  if (required && !value.trim()) throw new Error(`${label} is required.`)
  if (value.length > maximum) throw new Error(`${label} exceeds ${maximum.toLocaleString()} characters.`)
  return value
}

function enumeration<T extends string>(value: unknown, allowed: Set<T>, label: string): T {
  if (typeof value !== 'string' || !allowed.has(value as T)) throw new Error(`${label} is invalid.`)
  return value as T
}

function safeInteger(value: unknown, label: string, minimum: number, maximum: number) {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) throw new Error(`${label} is outside the supported range.`)
  return value as number
}

function capability(value: unknown, panelId: string, index: number): AgentPanelCapability {
  const source = object(value, `Panel ${panelId} capability ${index}`)
  exactKeys(source, ['id', 'label', 'description', 'availability'], `Panel ${panelId} capability ${index}`)
  return {
    id: identifier(source.id, `Panel ${panelId} capability identity`),
    label: text(source.label, `Panel ${panelId} capability label`, AGENT_PANEL_REGISTRY_LIMITS.label, true),
    description: text(source.description, `Panel ${panelId} capability description`, AGENT_PANEL_REGISTRY_LIMITS.description),
    availability: enumeration(source.availability, CAPABILITY_AVAILABILITY, `Panel ${panelId} capability availability`),
  }
}

export function validateAgentPanelDescriptor(value: unknown): AgentPanelDescriptor {
  const source = object(value, 'Panel descriptor')
  exactKeys(source, [
    'id', 'providerKind', 'display', 'capabilities', 'lifecycle', 'lifecycleDetail', 'connectionState', 'connectionDetail',
    'authenticationDisposition', 'authenticationDetail', 'dock', 'render', 'enabled',
  ], 'Panel descriptor')
  const id = identifier(source.id, 'Panel identity')
  const display = object(source.display, `Panel ${id} display metadata`)
  exactKeys(display, ['label', 'shortLabel', 'description', 'iconKey', 'accent'], `Panel ${id} display metadata`)
  const dock = object(source.dock, `Panel ${id} dock intent`)
  exactKeys(dock, ['preferredZone', 'preferredMode', 'minimumWidth', 'minimumHeight', 'placementPriority'], `Panel ${id} dock intent`)
  const render = object(source.render, `Panel ${id} render boundary`)
  exactKeys(render, ['contract', 'factoryKey'], `Panel ${id} render boundary`)
  if (render.contract !== AGENT_PANEL_RENDER_CONTRACT) throw new Error(`Panel ${id} render contract is incompatible.`)
  if (!Array.isArray(source.capabilities) || source.capabilities.length > AGENT_PANEL_REGISTRY_LIMITS.capabilitiesPerPanel) throw new Error(`Panel ${id} capabilities are malformed or exceed the limit.`)
  const capabilities = source.capabilities.map((item, index) => capability(item, id, index))
  if (new Set(capabilities.map((item) => item.id)).size !== capabilities.length) throw new Error(`Panel ${id} has duplicate capability identities.`)
  if (typeof source.enabled !== 'boolean') throw new Error(`Panel ${id} enabled state must be a boolean.`)
  return {
    id,
    providerKind: identifier(source.providerKind, `Panel ${id} provider kind`),
    display: {
      label: text(display.label, `Panel ${id} label`, AGENT_PANEL_REGISTRY_LIMITS.label, true),
      shortLabel: text(display.shortLabel, `Panel ${id} short label`, AGENT_PANEL_REGISTRY_LIMITS.label, true),
      description: text(display.description, `Panel ${id} description`, AGENT_PANEL_REGISTRY_LIMITS.description),
      iconKey: text(display.iconKey, `Panel ${id} icon key`, AGENT_PANEL_REGISTRY_LIMITS.iconKey, true),
      accent: text(display.accent, `Panel ${id} accent`, AGENT_PANEL_REGISTRY_LIMITS.accent, true),
    },
    capabilities,
    lifecycle: enumeration(source.lifecycle, LIFECYCLES, `Panel ${id} lifecycle`),
    lifecycleDetail: text(source.lifecycleDetail, `Panel ${id} lifecycle detail`, AGENT_PANEL_REGISTRY_LIMITS.detail),
    connectionState: enumeration(source.connectionState, CONNECTIONS, `Panel ${id} connection state`),
    connectionDetail: text(source.connectionDetail, `Panel ${id} connection detail`, AGENT_PANEL_REGISTRY_LIMITS.detail),
    authenticationDisposition: enumeration(source.authenticationDisposition, AUTHENTICATION, `Panel ${id} authentication disposition`),
    authenticationDetail: text(source.authenticationDetail, `Panel ${id} authentication detail`, AGENT_PANEL_REGISTRY_LIMITS.detail),
    dock: {
      preferredZone: enumeration(dock.preferredZone, DOCK_ZONES, `Panel ${id} preferred dock zone`),
      preferredMode: enumeration(dock.preferredMode, DOCK_MODES, `Panel ${id} preferred dock mode`),
      minimumWidth: safeInteger(dock.minimumWidth, `Panel ${id} minimum width`, 0, 16_384),
      minimumHeight: safeInteger(dock.minimumHeight, `Panel ${id} minimum height`, 0, 16_384),
      placementPriority: safeInteger(dock.placementPriority, `Panel ${id} placement priority`, -1_000_000, 1_000_000),
    },
    render: { contract: AGENT_PANEL_RENDER_CONTRACT, factoryKey: identifier(render.factoryKey, `Panel ${id} factory key`) },
    enabled: source.enabled,
  }
}

export function validateAgentPanelRegistrySnapshot(value: unknown): AgentPanelRegistrySnapshot {
  const forbidden = findForbiddenField(value)
  if (forbidden) throw new Error(`Registry data contains forbidden secret, client, process, or handle field at ${forbidden}.`)
  const source = object(value, 'Agent panel registry')
  exactKeys(source, ['format', 'order', 'panels'], 'Agent panel registry')
  if (source.format !== AGENT_PANEL_REGISTRY_FORMAT) throw new Error('Registry format is incompatible. Migration is not attempted.')
  if (!Array.isArray(source.panels) || source.panels.length > AGENT_PANEL_REGISTRY_LIMITS.panels) throw new Error('Registry panel collection is malformed or exceeds the limit.')
  if (!Array.isArray(source.order) || source.order.length !== source.panels.length) throw new Error('Registry order must contain every panel exactly once.')
  const panels = source.panels.map(validateAgentPanelDescriptor)
  const ids = panels.map((panel) => panel.id)
  if (new Set(ids).size !== ids.length) throw new Error('Registry contains duplicate panel identities.')
  const order = source.order.map((item) => identifier(item, 'Registry order identity'))
  if (new Set(order).size !== order.length || order.some((id) => !ids.includes(id))) throw new Error('Registry order contains duplicate, unknown, or missing panel identities.')
  return structuredClone({ format: AGENT_PANEL_REGISTRY_FORMAT, order, panels })
}

function findForbiddenField(value: unknown, path = '$', depth = 0): string | null {
  if (depth > 16) return `${path} (excessive nesting)`
  if (Array.isArray(value)) {
    if (value.length > AGENT_PANEL_REGISTRY_LIMITS.panels * 2) return `${path} (oversized array)`
    for (let index = 0; index < value.length; index += 1) {
      const result = findForbiddenField(value[index], `${path}[${index}]`, depth + 1)
      if (result) return result
    }
    return null
  }
  if (!value || typeof value !== 'object') return null
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    if (FORBIDDEN_KEY.test(key)) return `${path}.${key}`
    const result = findForbiddenField(child, `${path}.${key}`, depth + 1)
    if (result) return result
  }
  return null
}
