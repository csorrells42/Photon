import {
  AGENT_PANEL_REGISTRY_FORMAT,
  AGENT_PANEL_RENDER_CONTRACT,
  type AgentPanelDescriptor,
  type AgentPanelRegistrySnapshot,
} from './contracts'

type FixtureOptions = Pick<AgentPanelDescriptor, 'id' | 'providerKind' | 'lifecycle' | 'connectionState' | 'authenticationDisposition'> & {
  label: string
  iconKey: string
  accent: string
  priority: number
}

function fixture(options: FixtureOptions): AgentPanelDescriptor {
  return {
    id: options.id,
    providerKind: options.providerKind,
    display: {
      label: options.label,
      shortLabel: options.label,
      description: `Deterministic ${options.label} panel descriptor fixture. It is not a live integration.`,
      iconKey: options.iconKey,
      accent: options.accent,
    },
    capabilities: [
      { id: 'conversation', label: 'Conversation', description: 'Provider-neutral conversation surface intent.', availability: options.lifecycle === 'available' ? 'available' : 'unknown' },
      { id: 'tool-events', label: 'Tool events', description: 'Provider-neutral tool event presentation intent.', availability: 'unknown' },
    ],
    lifecycle: options.lifecycle,
    lifecycleDetail: 'Deterministic fixture state only; no provider was contacted.',
    connectionState: options.connectionState,
    connectionDetail: 'Synthetic connection metadata only.',
    authenticationDisposition: options.authenticationDisposition,
    authenticationDetail: 'Authentication remains outside the renderer registry.',
    dock: { preferredZone: 'right', preferredMode: 'vertical', minimumWidth: 240, minimumHeight: 160, placementPriority: options.priority },
    render: { contract: AGENT_PANEL_RENDER_CONTRACT, factoryKey: `fixture.${options.id}` },
    enabled: true,
  }
}

export function createDeterministicAgentPanelFixtures(): AgentPanelRegistrySnapshot {
  const panels = [
    fixture({ id: 'hermes', providerKind: 'nous-hermes', label: 'Hermes', iconKey: 'sparkles', accent: 'violet', priority: 600, lifecycle: 'available', connectionState: 'connected', authenticationDisposition: 'host_managed' }),
    fixture({ id: 'codex', providerKind: 'openai-codex', label: 'Codex', iconKey: 'code', accent: 'green', priority: 500, lifecycle: 'available', connectionState: 'disconnected', authenticationDisposition: 'host_managed' }),
    fixture({ id: 'claude', providerKind: 'anthropic-claude', label: 'Claude', iconKey: 'message-circle', accent: 'orange', priority: 400, lifecycle: 'unavailable', connectionState: 'unavailable', authenticationDisposition: 'unavailable' }),
    fixture({ id: 'chatgpt', providerKind: 'openai-chatgpt', label: 'ChatGPT', iconKey: 'messages-square', accent: 'teal', priority: 300, lifecycle: 'unavailable', connectionState: 'not_configured', authenticationDisposition: 'delegated' }),
    fixture({ id: 'scarlett', providerKind: 'scarlett-agent', label: 'Scarlett', iconKey: 'eye', accent: 'red', priority: 200, lifecycle: 'unavailable', connectionState: 'not_configured', authenticationDisposition: 'delegated' }),
    fixture({ id: 'ali', providerKind: 'project-ali', label: 'Ali', iconKey: 'orbit', accent: 'blue', priority: 100, lifecycle: 'unavailable', connectionState: 'unavailable', authenticationDisposition: 'unsupported' }),
  ]
  return structuredClone({ format: AGENT_PANEL_REGISTRY_FORMAT, order: panels.map((panel) => panel.id), panels })
}

export const DETERMINISTIC_AGENT_PANEL_FIXTURES = createDeterministicAgentPanelFixtures()
