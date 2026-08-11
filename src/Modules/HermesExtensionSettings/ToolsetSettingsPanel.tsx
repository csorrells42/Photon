import { useEffect, useState, type MouseEvent } from 'react'
import type {
  ExtensionWriteIntent,
  HermesExtensionSettingsSnapshot,
  ToolCapability,
  ToolsetSelection,
  ToolsetSettings,
} from './contracts'
import {
  backendsForProviderCapability,
  modelsForSpecialty,
  providersForCapability,
} from './safety'

interface ToolsetSettingsPanelProps {
  snapshot: HermesExtensionSettingsSnapshot
  busy: boolean
  onPreview(intent: ExtensionWriteIntent, source: HTMLElement): Promise<void>
}

export function ToolsetSettingsPanel({ snapshot, busy, onPreview }: ToolsetSettingsPanelProps) {
  const [draft, setDraft] = useState<ToolsetSettings>(() => structuredClone(snapshot.toolsets))

  useEffect(() => setDraft(structuredClone(snapshot.toolsets)), [snapshot.toolsets])

  const replace = (capability: ToolCapability, next: ToolsetSelection) => {
    setDraft((current) => ({ ...current, [capability]: next }))
  }

  const changeProvider = (capability: ToolCapability, providerId: string) => {
    const provider = providersForCapability(snapshot, capability).find((entry) => entry.id === providerId)
    const backendId = provider ? backendsForProviderCapability(provider, capability)[0]?.id ?? '' : ''
    const specialtyModelId = modelsForSpecialty(snapshot, capability)[0]?.id ?? ''
    replace(capability, { providerId, backendId, specialtyModelId })
  }

  const capabilityEditor = (capability: ToolCapability) => {
    const current = draft[capability]
    const providers = providersForCapability(snapshot, capability)
    const provider = providers.find((entry) => entry.id === current.providerId)
    const backends = provider ? backendsForProviderCapability(provider, capability) : []
    const models = modelsForSpecialty(snapshot, capability)
    return (
      <article className="hes-tool-card" key={capability}>
        <header>
          <small>{capability.toUpperCase()} TOOLSET</small>
          <h3>{capability === 'search' ? 'Search provider' : 'Extract provider'}</h3>
          <p>Only advertised {capability} provider/backend combinations are offered.</p>
        </header>
        {providers.length === 0 ? (
          <div className="hes-inline-state unavailable">No available provider advertises this capability.</div>
        ) : (
          <>
            <label>
              <span>Provider</span>
              <select value={current.providerId} onChange={(event) => changeProvider(capability, event.target.value)}>
                {providers.map((entry) => <option key={entry.id} value={entry.id}>{entry.label} - {entry.provenance}</option>)}
              </select>
            </label>
            <label>
              <span>Advertised backend</span>
              <select
                value={current.backendId}
                onChange={(event) => replace(capability, { ...current, backendId: event.target.value })}
              >
                {backends.map((backend) => <option key={backend.id} value={backend.id}>{backend.label}</option>)}
              </select>
            </label>
            <label>
              <span>{capability} specialty model</span>
              <select
                value={current.specialtyModelId}
                onChange={(event) => replace(capability, { ...current, specialtyModelId: event.target.value })}
              >
                {models.map((model) => <option key={model.id} value={model.id}>{model.label} - {model.providerId}</option>)}
              </select>
            </label>
            {provider?.state === 'partial' ? <div className="hes-inline-state partial">This provider reports partial availability.</div> : null}
          </>
        )}
      </article>
    )
  }

  const preview = (event: MouseEvent<HTMLButtonElement>) => onPreview({
    kind: 'toolsets',
    search: { ...draft.search },
    extract: { ...draft.extract },
  }, event.currentTarget)

  return (
    <section className="hes-panel" aria-labelledby="hes-toolsets-heading">
      <header className="hes-panel-heading">
        <div>
          <small>INDEPENDENT PROVIDERS</small>
          <h2 id="hes-toolsets-heading">Search and Extract toolsets</h2>
          <p>Search and Extract remain independently selectable and each uses its own specialty model.</p>
        </div>
      </header>
      <div className="hes-tool-grid">
        {capabilityEditor('search')}
        {capabilityEditor('extract')}
      </div>
      <div className="hes-actions">
        <button
          type="button"
          className="hes-primary"
          disabled={busy || !draft.search.providerId || !draft.search.backendId || !draft.search.specialtyModelId || !draft.extract.providerId || !draft.extract.backendId || !draft.extract.specialtyModelId}
          onClick={(event) => void preview(event)}
        >
          Preview independent selections
        </button>
      </div>
    </section>
  )
}
