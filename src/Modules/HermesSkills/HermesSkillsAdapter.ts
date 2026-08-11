export const HERMES_SKILLS_ADAPTER_VERSION = 1

const MAX_SKILLS = 256
const MAX_HUB_RESULTS = 100
const MAX_TEXT = 8_192
const MAX_SKILL_CONTENT = 512_000

export type HermesSkillProvenance = 'bundled' | 'hub' | 'agent' | 'unknown'
export type HermesSkillScanPolicy = 'allow' | 'ask' | 'block'

export type HermesSkill = {
  name: string
  description: string
  category: string
  enabled: boolean
  provenance: HermesSkillProvenance
  usage: number
}

export type HermesSkillHubSource = {
  id: string
  label: string
  available: boolean | null
  rateLimited: boolean
  searchable: boolean
}

export type HermesSkillHubInstalled = {
  name: string | null
  trustLevel: string | null
  scanVerdict: string | null
}

export type HermesSkillHubResult = {
  name: string
  description: string
  source: string
  identifier: string
  trustLevel: string
  repository: string | null
  tags: string[]
}

export type HermesSkillHubSources = {
  sources: HermesSkillHubSource[]
  indexAvailable: boolean
  featured: HermesSkillHubResult[]
  installed: Record<string, HermesSkillHubInstalled>
}

export type HermesSkillHubSearch = {
  results: HermesSkillHubResult[]
  sourceCounts: Record<string, number>
  timedOut: string[]
  installed: Record<string, HermesSkillHubInstalled>
}

export type HermesSkillHubPreview = HermesSkillHubResult & {
  skillMarkdown: string
  files: string[]
}

export type HermesSkillScanFinding = {
  severity: string
  category: string
  file: string
  line: number | null
  description: string
}

export type HermesSkillHubScan = {
  name: string
  identifier: string
  source: string
  trustLevel: string
  verdict: string
  summary: string
  policy: HermesSkillScanPolicy
  policyReason: string
  findings: HermesSkillScanFinding[]
  severityCounts: Record<string, number>
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function text(value: unknown, limit = MAX_TEXT) {
  return typeof value === 'string' ? value.trim().slice(0, limit) : ''
}

function number(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function boolean(value: unknown) {
  return value === true
}

function stringList(value: unknown, limit = 64) {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const normalized = text(item, 512)
    return normalized ? [normalized] : []
  }).slice(0, limit)
}

function provenance(value: unknown): HermesSkillProvenance {
  return value === 'bundled' || value === 'hub' || value === 'agent' ? value : 'unknown'
}

function policy(value: unknown): HermesSkillScanPolicy {
  return value === 'allow' || value === 'ask' ? value : 'block'
}

function normalizeInstalled(value: unknown): Record<string, HermesSkillHubInstalled> {
  const rows = Object.entries(object(value)).slice(0, MAX_SKILLS)
  return Object.fromEntries(rows.flatMap(([identifier, rawValue]) => {
    const key = text(identifier, 512)
    if (!key) return []
    const row = object(rawValue)
    return [[key, {
      name: text(row.name, 128) || null,
      trustLevel: text(row.trust_level, 64) || null,
      scanVerdict: text(row.scan_verdict, 64) || null,
    }]]
  }))
}

function normalizeHubResults(value: unknown): HermesSkillHubResult[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const row = object(item)
    const name = text(row.name, 128)
    const identifier = text(row.identifier, 512)
    if (!name || !identifier) return []
    return [{
      name,
      description: text(row.description, 2_048),
      source: text(row.source, 256),
      identifier,
      trustLevel: text(row.trust_level, 64) || 'community',
      repository: text(row.repo, 512) || null,
      tags: stringList(row.tags, 32),
    }]
  }).slice(0, MAX_HUB_RESULTS)
}

export function normalizeHermesSkills(value: unknown): HermesSkill[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    const row = object(item)
    const name = text(row.name, 128)
    if (!name) return []
    return [{
      name,
      description: text(row.description, 2_048),
      category: text(row.category, 128) || 'General',
      enabled: row.enabled !== false,
      provenance: provenance(row.provenance),
      usage: Math.max(0, Math.floor(number(row.usage))),
    }]
  }).slice(0, MAX_SKILLS)
}

export function normalizeHermesSkillHubSources(value: unknown): HermesSkillHubSources {
  const raw = object(value)
  const sources = Array.isArray(raw.sources) ? raw.sources : []
  return {
    sources: sources.flatMap((item) => {
      const row = object(item)
      const id = text(row.id, 128)
      if (!id) return []
      return [{
        id,
        label: text(row.label, 256) || id,
        available: typeof row.available === 'boolean' ? row.available : null,
        rateLimited: boolean(row.rate_limited),
        searchable: row.searchable !== false,
      }]
    }).slice(0, 32),
    indexAvailable: boolean(raw.index_available),
    featured: normalizeHubResults(raw.featured),
    installed: normalizeInstalled(raw.installed),
  }
}

export function normalizeHermesSkillHubSearch(value: unknown): HermesSkillHubSearch {
  const raw = object(value)
  const counts = Object.fromEntries(Object.entries(object(raw.source_counts)).slice(0, 32).flatMap(([key, value]) => {
    const source = text(key, 128)
    return source ? [[source, Math.max(0, Math.floor(number(value)))]] : []
  }))
  return {
    results: normalizeHubResults(raw.results),
    sourceCounts: counts,
    timedOut: stringList(raw.timed_out, 32),
    installed: normalizeInstalled(raw.installed),
  }
}

export function normalizeHermesSkillHubPreview(value: unknown): HermesSkillHubPreview | null {
  const raw = object(value)
  const base = normalizeHubResults([raw])[0]
  if (!base) return null
  return {
    ...base,
    skillMarkdown: text(raw.skill_md, MAX_SKILL_CONTENT),
    files: stringList(raw.files, 256),
  }
}

export function normalizeHermesSkillHubScan(value: unknown): HermesSkillHubScan | null {
  const raw = object(value)
  const name = text(raw.name, 128)
  const identifier = text(raw.identifier, 512)
  if (!name || !identifier) return null
  const findings = Array.isArray(raw.findings) ? raw.findings : []
  return {
    name,
    identifier,
    source: text(raw.source, 256),
    trustLevel: text(raw.trust_level, 64) || 'community',
    verdict: text(raw.verdict, 64) || 'unknown',
    summary: text(raw.summary, 2_048),
    policy: policy(raw.policy),
    policyReason: text(raw.policy_reason, 2_048),
    findings: findings.flatMap((item) => {
      const row = object(item)
      const description = text(row.description, 2_048)
      if (!description) return []
      const rawLine = number(row.line, -1)
      return [{
        severity: text(row.severity, 64) || 'unknown',
        category: text(row.category, 128) || 'unspecified',
        file: text(row.file, 512),
        line: rawLine >= 0 ? Math.floor(rawLine) : null,
        description,
      }]
    }).slice(0, 256),
    severityCounts: Object.fromEntries(Object.entries(object(raw.severity_counts)).slice(0, 16).flatMap(([key, value]) => {
      const severity = text(key, 64)
      return severity ? [[severity, Math.max(0, Math.floor(number(value)))]] : []
    })),
  }
}

async function readJson(response: Response) {
  const value = await response.json().catch(() => ({}))
  if (response.ok) return value
  const raw = object(value)
  throw new Error(text(raw.detail, 2_048) || text(raw.error, 2_048) || `Hermes request failed (${response.status}).`)
}

function query(profile?: string) {
  const normalized = text(profile, 128)
  return normalized ? `?profile=${encodeURIComponent(normalized)}` : ''
}

export class HermesSkillsAdapter {
  constructor(private readonly request: FetchLike = (input, init) => fetch(input, init)) {}

  async skills(profile?: string) {
    return normalizeHermesSkills(await readJson(await this.request(`/api/skills${query(profile)}`, { credentials: 'include' })))
  }

  async sources(profile?: string) {
    return normalizeHermesSkillHubSources(await readJson(await this.request(`/api/skills/hub/sources${query(profile)}`, { credentials: 'include' })))
  }

  async search(searchText: string, source = 'all', profile?: string) {
    const q = text(searchText, 256)
    if (!q) return normalizeHermesSkillHubSearch({})
    const suffix = profile ? `&profile=${encodeURIComponent(text(profile, 128))}` : ''
    const url = `/api/skills/hub/search?q=${encodeURIComponent(q)}&source=${encodeURIComponent(text(source, 128) || 'all')}&limit=50${suffix}`
    return normalizeHermesSkillHubSearch(await readJson(await this.request(url, { credentials: 'include' })))
  }

  async preview(identifier: string, profile?: string) {
    const suffix = profile ? `&profile=${encodeURIComponent(text(profile, 128))}` : ''
    const value = await readJson(await this.request(`/api/skills/hub/preview?identifier=${encodeURIComponent(text(identifier, 512))}${suffix}`, { credentials: 'include' }))
    const normalized = normalizeHermesSkillHubPreview(value)
    if (!normalized) throw new Error('Hermes returned an invalid skill preview.')
    return normalized
  }

  async scan(identifier: string, profile?: string) {
    const suffix = profile ? `&profile=${encodeURIComponent(text(profile, 128))}` : ''
    const value = await readJson(await this.request(`/api/skills/hub/scan?identifier=${encodeURIComponent(text(identifier, 512))}${suffix}`, { credentials: 'include' }))
    const normalized = normalizeHermesSkillHubScan(value)
    if (!normalized) throw new Error('Hermes returned an invalid skill scan.')
    return normalized
  }

  async toggle(name: string, enabled: boolean, profile?: string) {
    await readJson(await this.request('/api/skills/toggle', {
      method: 'PUT', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: text(name, 128), enabled, profile: text(profile, 128) || undefined }),
    }))
  }

  async install(identifier: string, scan: HermesSkillHubScan, acceptCaution = false, profile?: string) {
    const normalizedIdentifier = text(identifier, 512)
    if (!normalizedIdentifier || scan.identifier !== normalizedIdentifier) throw new Error('A matching security scan is required before installation.')
    if (scan.policy === 'block') throw new Error(`Hermes security policy blocked this skill: ${scan.policyReason || scan.summary}`)
    if (scan.policy === 'ask' && !acceptCaution) throw new Error('This skill requires explicit caution acceptance before installation.')
    return readJson(await this.request('/api/skills/hub/install', {
      method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ identifier: normalizedIdentifier, profile: text(profile, 128) || undefined }),
    }))
  }

  async uninstall(name: string, profile?: string) {
    return readJson(await this.request('/api/skills/hub/uninstall', {
      method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: text(name, 128), profile: text(profile, 128) || undefined }),
    }))
  }

  async update(profile?: string) {
    return readJson(await this.request('/api/skills/hub/update', {
      method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ profile: text(profile, 128) || undefined }),
    }))
  }
}

export const hermesSkillsAdapter = new HermesSkillsAdapter()
