import { AgentPanelRegistry } from './AgentPanelRegistry'
import type { AgentPanelRegistrySnapshot } from './contracts'
import { AGENT_PANEL_REGISTRY_LIMITS, validateAgentPanelRegistrySnapshot } from './registryValidation'

export function serializeAgentPanelRegistry(value: AgentPanelRegistry | AgentPanelRegistrySnapshot): string {
  const snapshot = validateAgentPanelRegistrySnapshot(value instanceof AgentPanelRegistry ? value.snapshot : value)
  const text = JSON.stringify(snapshot)
  const byteCount = new TextEncoder().encode(text).byteLength
  if (byteCount > AGENT_PANEL_REGISTRY_LIMITS.serializedBytes) throw new Error('Serialized agent panel registry exceeds the current-format size limit.')
  return text
}

export function parseAgentPanelRegistry(text: string): AgentPanelRegistrySnapshot {
  if (typeof text !== 'string' || !text) throw new Error('Serialized agent panel registry is empty.')
  if (new TextEncoder().encode(text).byteLength > AGENT_PANEL_REGISTRY_LIMITS.serializedBytes) throw new Error('Serialized agent panel registry exceeds the current-format size limit.')
  let value: unknown
  try { value = JSON.parse(text) }
  catch { throw new Error('Serialized agent panel registry is not valid JSON.') }
  return validateAgentPanelRegistrySnapshot(value)
}

export function createAgentPanelRegistryFromSerialized(text: string) {
  return new AgentPanelRegistry(parseAgentPanelRegistry(text))
}
