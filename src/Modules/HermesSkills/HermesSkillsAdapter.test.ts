import { describe, expect, it, vi } from 'vitest'
import {
  HermesSkillsAdapter,
  normalizeHermesSkillHubPreview,
  normalizeHermesSkillHubScan,
  normalizeHermesSkillHubSources,
  normalizeHermesSkills,
} from './HermesSkillsAdapter'

describe('HermesSkillsAdapter v1', () => {
  it('normalizes installed skills without retaining paths or arbitrary extras', () => {
    const normalized = normalizeHermesSkills([{
      name: 'planning', description: 'Plan work', category: 'Engineering', enabled: true,
      provenance: 'hub', usage: 7, path: '/home/hermes/.hermes/skills/planning', secret: 'discard-me',
    }, { name: '' }])
    expect(normalized).toEqual([{
      name: 'planning', description: 'Plan work', category: 'Engineering', enabled: true,
      provenance: 'hub', usage: 7,
    }])
    expect(JSON.stringify(normalized)).not.toContain('discard-me')
    expect(JSON.stringify(normalized)).not.toContain('/home/hermes')
  })

  it('normalizes source, preview, and scan data through bounded public shapes', () => {
    const sources = normalizeHermesSkillHubSources({
      sources: [{ id: 'hermes-index', label: 'Hermes Index', available: true, searchable: true }],
      index_available: true,
      featured: [{ name: 'Fixture', identifier: 'official/fixture', trust_level: 'trusted', tags: ['safe'] }],
      installed: { 'official/fixture': { name: 'Fixture', trust_level: 'trusted', scan_verdict: 'safe', path: '/private' } },
    })
    expect(sources).toMatchObject({ indexAvailable: true, sources: [{ id: 'hermes-index', available: true }] })
    expect(sources.installed['official/fixture']).toEqual({ name: 'Fixture', trustLevel: 'trusted', scanVerdict: 'safe' })

    const preview = normalizeHermesSkillHubPreview({
      name: 'Fixture', identifier: 'official/fixture', trust_level: 'trusted', skill_md: '# Fixture', files: ['SKILL.md'], raw_bundle: 'discard-me',
    })
    expect(preview).toMatchObject({ name: 'Fixture', skillMarkdown: '# Fixture', files: ['SKILL.md'] })
    expect(JSON.stringify(preview)).not.toContain('discard-me')

    const scan = normalizeHermesSkillHubScan({
      name: 'Fixture', identifier: 'official/fixture', trust_level: 'trusted', verdict: 'caution', policy: 'ask',
      policy_reason: 'Review one finding', findings: [{ severity: 'medium', category: 'network', file: 'SKILL.md', line: 9, description: 'Uses network' }],
      severity_counts: { medium: 1 }, quarantine_path: '/private/quarantine',
    })
    expect(scan).toMatchObject({ policy: 'ask', findings: [{ severity: 'medium', line: 9 }] })
    expect(JSON.stringify(scan)).not.toContain('/private/quarantine')
  })

  it('requires a matching non-blocked scan before posting an install', async () => {
    const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({ ok: true, pid: 7, name: 'skills-install' }), { status: 200 }))
    const adapter = new HermesSkillsAdapter(request)
    const scan = normalizeHermesSkillHubScan({ name: 'Fixture', identifier: 'official/fixture', policy: 'allow' })!
    await expect(adapter.install('different/skill', scan)).rejects.toThrow('matching security scan')
    expect(request).not.toHaveBeenCalled()

    await adapter.install('official/fixture', scan)
    expect(request).toHaveBeenCalledOnce()
    expect(JSON.parse(String(request.mock.calls[0]?.[1]?.body))).toEqual({ identifier: 'official/fixture' })
  })

  it('blocks denied scans and requires explicit acceptance for ask decisions', async () => {
    const request = vi.fn()
    const adapter = new HermesSkillsAdapter(request)
    const blocked = normalizeHermesSkillHubScan({ name: 'Bad', identifier: 'bad/skill', policy: 'block', policy_reason: 'Critical finding' })!
    await expect(adapter.install('bad/skill', blocked)).rejects.toThrow('blocked this skill')

    const caution = normalizeHermesSkillHubScan({ name: 'Maybe', identifier: 'maybe/skill', policy: 'ask' })!
    await expect(adapter.install('maybe/skill', caution)).rejects.toThrow('explicit caution acceptance')
    expect(request).not.toHaveBeenCalled()
  })

  it('encodes hub identifiers and profile names in preview and scan requests', async () => {
    const request = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/preview')) return new Response(JSON.stringify({ name: 'Fixture', identifier: 'team/fixture' }), { status: 200 })
      return new Response(JSON.stringify({ name: 'Fixture', identifier: 'team/fixture', policy: 'allow' }), { status: 200 })
    })
    const adapter = new HermesSkillsAdapter(request)
    await adapter.preview('team/fixture', 'Chris profile')
    await adapter.scan('team/fixture', 'Chris profile')
    expect(String(request.mock.calls[0]?.[0])).toContain('identifier=team%2Ffixture&profile=Chris%20profile')
    expect(String(request.mock.calls[1]?.[0])).toContain('identifier=team%2Ffixture&profile=Chris%20profile')
  })
})
