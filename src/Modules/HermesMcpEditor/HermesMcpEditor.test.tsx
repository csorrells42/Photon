import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { HermesMcpEditor } from './HermesMcpEditor'
import {
  HERMES_MCP_EDITOR_CONTRACT_VERSION,
  canonicalHermesMcpReviewBinding,
  describeHermesMcpEditChanges,
  hermesMcpEditorReasonText,
  normalizeHermesMcpEditDraft,
  validateHermesMcpEditDraft,
  type HermesMcpConfiguredServerSnapshot,
  type HermesMcpEditorController,
  type HermesMcpEditorReviewRequest,
} from './HermesMcpEditorContract'

const snapshot: HermesMcpConfiguredServerSnapshot = {
  contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
  serverId: 'search-server',
  revision: 'mcp-rev:opaque-revision',
  name: 'Search server',
  transport: 'http',
  url: 'https://old.example/mcp',
  command: '',
  args: [],
  environmentVariableNames: ['SEARCH_TOKEN'],
  auth: 'header',
  enabled: true,
}

function request(overrides: Partial<HermesMcpEditorReviewRequest> = {}): HermesMcpEditorReviewRequest {
  return {
    contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
    serverId: snapshot.serverId,
    revision: snapshot.revision,
    edit: normalizeHermesMcpEditDraft(snapshot),
    ...overrides,
  }
}

describe('HermesMcpEditor v3', () => {
  it('renders a disabled, secret-free editor when no secure controller is supplied', () => {
    const markup = renderToStaticMarkup(<HermesMcpEditor snapshot={snapshot} />)
    expect(markup).toContain('MCP server edit and review')
    expect(markup).toContain('A secure coordinator is unavailable')
    expect(markup).toContain('Prepare review')
    expect(markup).toContain('disabled=""')
    expect(markup).toContain('Existing credentials stay server-side and unchanged')
    expect(markup).not.toContain('type="password"')
    expect(markup).not.toContain('Replacement secret')
    expect(markup).not.toContain('reviewHandle')
  })

  it('uses bounded fixed copy and canonical secret-free review data', () => {
    const edit = { ...normalizeHermesMcpEditDraft(snapshot), url: 'https://new.example/mcp' }
    const binding = canonicalHermesMcpReviewBinding(request({ edit }))
    expect(binding).toContain('https://new.example/mcp')
    expect(binding).not.toContain('secret-value')
    expect(hermesMcpEditorReasonText('commit-failed')).toBe('The secure commit failed. No success was recorded.')
    expect(hermesMcpEditorReasonText('secret-rebind-required')).toContain('native Connections vault')
    expect(describeHermesMcpEditChanges(snapshot, edit)).toContainEqual(expect.objectContaining({ field: 'URL' }))
    expect(validateHermesMcpEditDraft({ ...edit, url: 'file:///unsafe' })).toContain('invalid-http-url')
  })

  it('rejects credential-bearing endpoint and command material before review', () => {
    const http = normalizeHermesMcpEditDraft(snapshot)
    expect(validateHermesMcpEditDraft({ ...http, url: 'https://user:password@example.test/mcp' }))
      .toContain('invalid-http-url')
    expect(validateHermesMcpEditDraft({ ...http, url: 'https://example.test/mcp?api_key=inline-secret' }))
      .toContain('invalid-http-url')
    expect(validateHermesMcpEditDraft({ ...http, url: 'https://example.test/mcp#token=inline-secret' }))
      .toContain('invalid-http-url')

    const stdio = {
      ...http,
      transport: 'stdio' as const,
      url: '',
      command: 'npx',
      args: ['-y', 'example-mcp', '--api-key=inline-secret'],
      auth: null,
    }
    expect(validateHermesMcpEditDraft(stdio)).toContain('invalid-argument')
    expect(validateHermesMcpEditDraft({ ...stdio, args: ['OPENAI_API_KEY=synthetic-value'] }))
      .toContain('invalid-argument')
    expect(validateHermesMcpEditDraft({ ...stdio, args: ['--endpoint=https://example.test/mcp?token=synthetic-value'] }))
      .toContain('invalid-argument')
  })

  it('preserves the complete reviewed display instead of truncating a changed value', () => {
    const longArgument = `--label=${'x'.repeat(700)}`
    const before = { ...snapshot, transport: 'stdio' as const, url: '', command: 'npx', args: [] }
    const edit = { ...normalizeHermesMcpEditDraft(before), args: [longArgument] }
    const change = describeHermesMcpEditChanges(before, edit).find((item) => item.field === 'Arguments')
    expect(change?.after).toBe(longArgument)
  })

  it('renders no raw controller text or secret input surface', () => {
    const controller: HermesMcpEditorController = {
      review: vi.fn(async () => ({
        contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
        status: 'unavailable' as const,
        reason: 'unavailable' as const,
      })),
      commit: vi.fn(async () => ({
        contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
        status: 'unavailable' as const,
        reason: 'unavailable' as const,
      })),
      discard: vi.fn(),
    }
    const markup = renderToStaticMarkup(<HermesMcpEditor snapshot={snapshot} controller={controller} />)
    expect(markup).not.toContain('password')
    expect(markup).not.toContain('token value')
    expect(markup).toContain('Ready to prepare a secure review')
  })
})
