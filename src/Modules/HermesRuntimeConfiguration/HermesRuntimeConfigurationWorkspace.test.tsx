import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { HermesRuntimeConfigurationWorkspace } from './HermesRuntimeConfigurationWorkspace'

describe('Hermes runtime configuration workspace', () => {
  it('renders hosted and local runtime controls without a renderer secret input', () => {
    const endpointSnapshot = { current: { provider: '', model: '', baseUrl: '' }, endpoints: [] }
    const modelCatalog = {
      currentProvider: '', currentModel: '', providers: [],
      reasoningControl: { label: 'Reasoning' as const, optionLabels: {}, options: [], source: 'unverified' as const },
    }
    const markup = renderToStaticMarkup(<HermesRuntimeConfigurationWorkspace
      client={{
        list: vi.fn(async () => endpointSnapshot),
        save: vi.fn(async () => endpointSnapshot),
        validate: vi.fn(async () => ({ ok: true, reachable: true, message: '', models: [], modelDetails: [], runtimeKind: 'unknown' as const })),
        activate: vi.fn(async () => endpointSnapshot),
        delete: vi.fn(async () => endpointSnapshot),
        saveProfile: vi.fn(async () => endpointSnapshot),
        activateProfile: vi.fn(async () => endpointSnapshot),
        deleteProfile: vi.fn(async () => endpointSnapshot),
      }}
      modelAdapter={{
        options: vi.fn(async () => modelCatalog),
        selectDefault: vi.fn(async () => ({ confirmRequired: false, deferred: false, model: '' })),
      }}
      onOpenConnections={() => undefined}
    />)
    expect(markup).toContain('Hosted providers')
    expect(markup).toContain('Local / LAN endpoint')
    expect(markup).toContain('host.docker.internal')
    expect(markup).toContain('Open Connections')
    expect(markup).toContain('Named model profiles')
    expect(markup).toContain('LM Studio remains fully authoritative')
    expect(markup).not.toContain('type="password"')
    expect(markup).not.toMatch(/API Key|secret value/i)
  })
})
