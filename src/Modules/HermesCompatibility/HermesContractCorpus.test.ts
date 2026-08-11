import { describe, expect, it } from 'vitest'
import {
  HERMES_CONTRACT_CORPUS_VERSION,
  HERMES_CONTRACT_SENTINEL,
  hermesContractCorpus as corpus,
} from './HermesContractCorpus'
import {
  HERMES_SYSTEM_ADAPTER_VERSION,
  normalizeHermesIdentity,
  normalizeHermesProviders,
  normalizeHermesStatus,
  normalizeHermesSystemStats,
} from '../HermesSystem/HermesSystemAdapter'
import {
  HERMES_COMPATIBILITY_ADAPTER_VERSION,
  normalizeRuntimeIdentity,
} from '../HermesSystem/HermesCompatibilityAdapter'
import {
  decodeHermesGatewayFrames,
  normalizeHermesWsTicket,
} from '../HermesGateway/HermesGatewayClient'
import {
  HERMES_RUNTIME_ADAPTER_VERSION,
  mergeHermesToolEvent,
  normalizeHermesApproval,
  normalizeHermesInteractivePrompt,
} from '../HermesGateway/HermesRuntimeAdapter'
import {
  HERMES_NOTIFICATION_ADAPTER_VERSION,
  mergeHermesNotification,
} from '../HermesGateway/HermesNotificationAdapter'
import {
  HERMES_MODEL_ADAPTER_VERSION,
  normalizeHermesModelCatalog,
} from '../HermesSettings/HermesModelAdapter'
import { storedMessageText } from '../HermesSessions/HermesSessionApi'
import {
  buildPromptWithAttachments,
  HERMES_ATTACHMENT_ADAPTER_VERSION,
  type HermesStagedAttachment,
} from '../HermesGateway/HermesAttachmentAdapter'
import {
  HERMES_CONSOLE_ADAPTER_VERSION,
  normalizeConsoleFrame,
} from '../HermesConsole/HermesConsoleClient'
import {
  HERMES_MCP_ADAPTER_VERSION,
  normalizeHermesMcpCatalog,
  normalizeHermesMcpServers,
} from '../HermesMcp/HermesMcpAdapter'
import {
  HERMES_SKILLS_ADAPTER_VERSION,
  normalizeHermesSkillHubScan,
  normalizeHermesSkillHubSources,
  normalizeHermesSkills,
} from '../HermesSkills/HermesSkillsAdapter'
import {
  HERMES_TOOLSETS_ADAPTER_VERSION,
  normalizeHermesToolsetConfig,
  normalizeHermesToolsets,
} from '../HermesSkills/HermesToolsetsAdapter'

describe('Hermes compatibility contract corpus v1', () => {
  it('pins every participating adapter to an explicit v1 contract', () => {
    expect(HERMES_CONTRACT_CORPUS_VERSION).toBe(1)
    expect([
      HERMES_SYSTEM_ADAPTER_VERSION,
      HERMES_COMPATIBILITY_ADAPTER_VERSION,
      HERMES_RUNTIME_ADAPTER_VERSION,
      HERMES_MODEL_ADAPTER_VERSION,
      HERMES_ATTACHMENT_ADAPTER_VERSION,
      HERMES_CONSOLE_ADAPTER_VERSION,
      HERMES_NOTIFICATION_ADAPTER_VERSION,
      HERMES_MCP_ADAPTER_VERSION,
      HERMES_SKILLS_ADAPTER_VERSION,
      HERMES_TOOLSETS_ADAPTER_VERSION,
    ]).toEqual([1, 1, 1, 1, 1, 1, 1, 1, 1, 1])
  })

  it('normalizes public status, auth, identity, stats, and immutable runtime identity without secret-shaped extras', () => {
    const normalized = {
      status: normalizeHermesStatus(corpus.publicStatus),
      providers: normalizeHermesProviders(corpus.authProviders),
      identity: normalizeHermesIdentity(corpus.identity),
      stats: normalizeHermesSystemStats(corpus.systemStats),
      runtime: normalizeRuntimeIdentity(corpus.runtimeIdentity),
    }
    expect(normalized.status).toMatchObject({ version: '0.20.0', overall: 'ok', activeSessions: 2, gateway: { running: true } })
    expect(normalized.providers).toEqual([{ name: 'local', displayName: 'Local account', supportsPassword: true }])
    expect(normalized.identity).toMatchObject({ userId: 'owner', organizationId: 'local', provider: 'local' })
    expect(normalized.stats).toMatchObject({ operatingSystem: 'Linux 6.8', memory: { percent: 40 }, process: { pid: 42 } })
    expect(normalized.runtime?.imageId).toBe(corpus.runtimeIdentity.imageId)
    expect(JSON.stringify(normalized)).not.toContain(HERMES_CONTRACT_SENTINEL)
  })

  it('accepts the bounded ticket and decodes response, error, method-event, and direct-event JSON-RPC lines', () => {
    expect(normalizeHermesWsTicket(corpus.wsTicket)).toBe('fixture-ticket.abc_123')
    expect(normalizeHermesWsTicket({ ticket: 'contains whitespace' })).toBeNull()
    expect(normalizeHermesWsTicket({ ticket: 'x'.repeat(8_193) })).toBeNull()
    const frames = decodeHermesGatewayFrames(corpus.gatewayLines)
    expect(frames).toHaveLength(4)
    expect(frames[0]).toMatchObject({ kind: 'response', id: 'workbench-1', result: { session_id: 'session-1' } })
    expect(frames[1]).toEqual({ kind: 'response', id: 'workbench-2', error: 'fixture failure' })
    expect(frames[2]).toMatchObject({ kind: 'event', event: { type: 'message.delta', session_id: 'session-1' } })
    expect(frames[3]).toMatchObject({ kind: 'event', event: { type: 'reasoning.end', session_id: 'session-1' } })
  })

  it('normalizes model, session text, tool lifecycle, approvals, prompts, and attachment references', () => {
    const model = normalizeHermesModelCatalog(corpus.modelOptions)
    expect(model).toMatchObject({ currentModel: 'model-a', currentProvider: 'provider-a' })
    expect(model.providers[0]?.models).toEqual(['model-a', 'model-b'])
    expect(JSON.stringify(model)).not.toContain(HERMES_CONTRACT_SENTINEL)
    expect(storedMessageText(corpus.storedMessage)).toBe('Hello Workbench')

    const running = mergeHermesToolEvent([], corpus.toolStart, 'fallback')
    const completed = mergeHermesToolEvent(running, corpus.toolComplete, 'fallback')
    expect(completed[0]).toMatchObject({ id: 'tool-1', name: 'find_symbol', phase: 'complete', durationSeconds: 0.25 })
    expect(normalizeHermesApproval(corpus.approval, null)).toMatchObject({ command: 'dotnet test', allowPermanent: false })
    expect(normalizeHermesApproval(corpus.approval, null)?.choices).not.toContain('always')
    expect(normalizeHermesInteractivePrompt(corpus.clarify, null)).toMatchObject({ kind: 'clarify', choices: ['Workbench', 'Upstream'] })
    const secretPrompt = normalizeHermesInteractivePrompt(corpus.secretRequest, null)
    expect(secretPrompt).toMatchObject({ kind: 'secret', envVar: 'FIXTURE_API_KEY' })
    expect(JSON.stringify(secretPrompt)).not.toContain(HERMES_CONTRACT_SENTINEL)

    const notices = mergeHermesNotification([], corpus.notificationShow, 1_000)
    expect(notices[0]).toMatchObject({ id: 'credits.usage', message: 'Used $75', detail: '$100 cap', level: 'warning' })
    expect(JSON.stringify(notices)).not.toContain(HERMES_CONTRACT_SENTINEL)
    expect(mergeHermesNotification(notices, corpus.notificationClear, 1_001)).toEqual([])

    expect(buildPromptWithAttachments('Review this', corpus.attachments as unknown as HermesStagedAttachment[]))
      .toBe('@file:/tmp/notes.txt\n\nReview this')
  })

  it('normalizes known Console frames and preserves future types only as bounded unknown frames', () => {
    const frames = corpus.consoleFrames.map(normalizeConsoleFrame)
    expect(frames[0]).toMatchObject({ type: 'ready', profile: 'default' })
    expect(frames[1]).toMatchObject({ type: 'output', stream: 'stdout', data: 'ok' })
    expect(frames[2]).toMatchObject({ type: 'confirm_required', command: 'cron delete 3' })
    expect(frames[3]).toEqual({ type: 'unknown', data: 'future-safe' })
  })

  it('normalizes MCP servers and the Nous catalog without returning secret values or private extras', () => {
    const servers = normalizeHermesMcpServers(corpus.mcpServers)
    const catalog = normalizeHermesMcpCatalog(corpus.mcpCatalog)

    expect(servers).toEqual([expect.objectContaining({
      name: 'serena',
      transport: 'http',
      editable: false,
      editorBlockReason: 'requires-trusted-reentry',
      environmentVariableNames: [],
      tools: null,
      url: null,
    })])
    expect(catalog.catalog[0]).toMatchObject({
      name: 'fixture-server',
      transport: 'stdio',
      authType: 'api_key',
      installRef: 'fixture-ref',
      requiredEnvironment: [{ name: 'FIXTURE_API_KEY', prompt: 'Fixture key', required: true }],
    })
    expect(catalog.diagnostics).toEqual([{ name: 'fixture-server', kind: 'warning', message: 'Synthetic diagnostic' }])
    expect(JSON.stringify({ servers, catalog })).not.toContain(HERMES_CONTRACT_SENTINEL)
  })

  it('normalizes installed skills, hub provenance, and scan findings without private paths', () => {
    const skills = normalizeHermesSkills(corpus.skills)
    const sources = normalizeHermesSkillHubSources(corpus.skillHubSources)
    const scan = normalizeHermesSkillHubScan(corpus.skillHubScan)
    expect(skills).toEqual([expect.objectContaining({ name: 'fixture-skill', provenance: 'hub', usage: 3 })])
    expect(sources).toMatchObject({ indexAvailable: true, featured: [{ identifier: 'official/fixture-skill', trustLevel: 'builtin' }] })
    expect(scan).toMatchObject({ identifier: 'official/fixture-skill', policy: 'ask', findings: [{ severity: 'medium', line: 12 }] })
    expect(JSON.stringify({ skills, sources, scan })).not.toContain(HERMES_CONTRACT_SENTINEL)
  })

  it('normalizes toolsets and provider readiness without returning secret values or configuration extras', () => {
    const toolsets = normalizeHermesToolsets(corpus.toolsets)
    const config = normalizeHermesToolsetConfig(corpus.toolsetConfig)
    expect(toolsets).toEqual([expect.objectContaining({ name: 'web', enabled: true, tools: ['web_search', 'web_extract'] })])
    expect(config).toMatchObject({ name: 'web', activeProvider: 'Brave Search', providers: [{ name: 'Brave Search', status: 'ready', environment: [{ key: 'BRAVE_SEARCH_API_KEY', isSet: true }] }] })
    expect(JSON.stringify({ toolsets, config })).not.toContain(HERMES_CONTRACT_SENTINEL)
  })
})
