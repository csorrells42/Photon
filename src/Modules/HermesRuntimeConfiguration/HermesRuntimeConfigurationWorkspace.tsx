import { useCallback, useEffect, useMemo, useState } from 'react'
import { Check, CircleAlert, Cloud, Cpu, KeyRound, LoaderCircle, Network, Plus, RefreshCw, Save, Server, SlidersHorizontal, Trash2, Zap } from 'lucide-react'
import { hermesModelAdapter, type HermesModelCatalog, type HermesModelSelection } from '../HermesSettings/HermesModelAdapter'
import { hermesRuntimeConfigurationClient, type HermesRuntimeConfigurationClient } from './HermesRuntimeConfigurationClient'
import { type HermesRuntimeEndpoint, type HermesRuntimeEndpointDraft, type HermesRuntimeEndpointSnapshot, type HermesRuntimeEndpointValidation, type HermesRuntimeProfileDraft, type HermesRuntimeProfileOverrides } from './contracts'
import './HermesRuntimeConfigurationWorkspace.css'

type ModelAdapter = Pick<typeof hermesModelAdapter, 'options' | 'selectDefault'>

export type HermesRuntimeConfigurationWorkspaceProps = {
  client?: HermesRuntimeConfigurationClient
  modelAdapter?: ModelAdapter
  onOpenConnections: () => void
}

const emptyDraft: HermesRuntimeEndpointDraft = {
  name: '', baseUrl: '', model: '', discoverModels: true, makeDefault: true,
}

const emptyProfile: HermesRuntimeProfileDraft = { id: '', name: '', model: '', overrides: {}, makeActive: true }

function draftFromEndpoint(endpoint: HermesRuntimeEndpoint): HermesRuntimeEndpointDraft {
  return {
    id: endpoint.id, name: endpoint.name, baseUrl: endpoint.baseUrl, model: endpoint.model,
    discoverModels: endpoint.discoverModels, makeDefault: endpoint.isCurrent,
    ...(endpoint.contextLength ? { contextLength: endpoint.contextLength } : {}),
    models: endpoint.models,
  }
}

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : 'The runtime configuration request failed.'
}

export function HermesRuntimeConfigurationWorkspace({
  client = hermesRuntimeConfigurationClient,
  modelAdapter = hermesModelAdapter,
  onOpenConnections,
}: HermesRuntimeConfigurationWorkspaceProps) {
  const [catalog, setCatalog] = useState<HermesModelCatalog | null>(null)
  const [snapshot, setSnapshot] = useState<HermesRuntimeEndpointSnapshot | null>(null)
  const [draft, setDraft] = useState<HermesRuntimeEndpointDraft>(emptyDraft)
  const [selection, setSelection] = useState<HermesModelSelection>({ provider: '', model: '' })
  const [discoveredModels, setDiscoveredModels] = useState<string[]>([])
  const [validation, setValidation] = useState<HermesRuntimeEndpointValidation | null>(null)
  const [profileDraft, setProfileDraft] = useState<HermesRuntimeProfileDraft>(emptyProfile)
  const [working, setWorking] = useState<string | null>('load')
  const [notice, setNotice] = useState<{ tone: 'good' | 'warn' | 'bad'; text: string } | null>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    setWorking('load')
    setNotice(null)
    try {
      const [nextCatalog, nextSnapshot] = await Promise.all([modelAdapter.options(undefined, true), client.list(signal)])
      setCatalog(nextCatalog)
      setSnapshot(nextSnapshot)
      const provider = nextCatalog.providers.find((item) => item.slug === nextCatalog.currentProvider)
        ?? nextCatalog.providers.find((item) => item.authenticated)
      setSelection({ provider: provider?.slug ?? '', model: nextCatalog.currentModel || provider?.models[0] || '' })
      const current = nextSnapshot.endpoints.find((endpoint) => endpoint.isCurrent) ?? nextSnapshot.endpoints[0]
      if (current) {
        setDraft(draftFromEndpoint(current))
        setDiscoveredModels(current.models)
        const active = current.profiles.find((profile) => profile.model === current.model && profile.isActive)
        setProfileDraft(active ? { ...active, makeActive: true } : { ...emptyProfile, model: current.model })
      }
    } catch (reason) {
      if (!signal?.aborted) setNotice({ tone: 'bad', text: message(reason) })
    } finally {
      if (!signal?.aborted) setWorking(null)
    }
  }, [client, modelAdapter])

  useEffect(() => {
    const abort = new AbortController()
    void load(abort.signal)
    return () => abort.abort()
  }, [load])

  const selectedProvider = catalog?.providers.find((provider) => provider.slug === selection.provider)
  const hostedModels = selectedProvider?.models ?? []
  const endpointModels = useMemo(() => [...new Set([...discoveredModels, draft.model].filter(Boolean))], [discoveredModels, draft.model])

  async function useHostedProvider() {
    setWorking('hosted')
    setNotice(null)
    try {
      let result = await modelAdapter.selectDefault(selection)
      if (result.confirmRequired) {
        if (!window.confirm(result.confirmMessage || 'This provider marks the selected model as expensive. Use it as the default?')) {
          setNotice({ tone: 'warn', text: 'The default model was not changed.' })
          return
        }
        result = await modelAdapter.selectDefault(selection, true)
      }
      const [nextCatalog, nextSnapshot] = await Promise.all([
        modelAdapter.options(undefined, true),
        client.list(),
      ])
      setCatalog(nextCatalog)
      setSnapshot(nextSnapshot)
      setNotice({ tone: 'good', text: `${selection.model} is now the default for new Photon conversations.` })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function testEndpoint() {
    setWorking('test')
    setNotice(null)
    try {
      const result = await client.validate(draft)
      setDiscoveredModels(result.models)
      setValidation(result)
      if (!draft.model && result.models[0]) setDraft((current) => ({ ...current, model: result.models[0] }))
      setNotice({
        tone: result.ok ? 'good' : result.reachable ? 'warn' : 'bad',
        text: result.ok
          ? `Endpoint connected${result.models.length ? ` · ${result.models.length} model${result.models.length === 1 ? '' : 's'} discovered` : ''}.`
          : result.message || 'The endpoint validation failed.',
      })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function saveEndpoint() {
    setWorking('save')
    setNotice(null)
    try {
      const next = await client.save({ ...draft, ...(discoveredModels.length ? { models: discoveredModels } : {}) })
      setSnapshot(next)
      const saved = next.endpoints.find((endpoint) => endpoint.id === draft.id)
        ?? next.endpoints.find((endpoint) => endpoint.name === draft.name)
      if (saved) setDraft(draftFromEndpoint(saved))
      if (draft.makeDefault) setCatalog(await modelAdapter.options(undefined, true))
      setNotice({ tone: 'good', text: `${draft.name} was saved${draft.makeDefault ? ' and made the default for new conversations' : ''}.` })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function activateEndpoint(endpoint: HermesRuntimeEndpoint) {
    setWorking(`activate:${endpoint.id}`)
    try {
      const next = await client.activate(endpoint.id)
      setSnapshot(next)
      setCatalog(await modelAdapter.options(undefined, true))
      setNotice({ tone: 'good', text: `${endpoint.name} is now the default for new conversations.` })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function deleteEndpoint(endpoint: HermesRuntimeEndpoint) {
    if (!window.confirm(`Delete ${endpoint.name}?`)) return
    setWorking(`delete:${endpoint.id}`)
    try {
      const next = await client.delete(endpoint.id)
      setSnapshot(next)
      if (draft.id === endpoint.id) { setDraft(emptyDraft); setDiscoveredModels([]) }
      setNotice({ tone: 'good', text: `${endpoint.name} was removed.` })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  const savedEndpoint = draft.id ? snapshot?.endpoints.find((endpoint) => endpoint.id === draft.id) : undefined
  const selectedDetail = validation?.modelDetails.find((model) => model.id === draft.model)
  const activeProfile = savedEndpoint?.profiles.find((profile) => profile.model === draft.model && profile.isActive)

  function setProfileNumber(field: keyof HermesRuntimeProfileOverrides, value: string) {
    setProfileDraft((current) => ({
      ...current,
      model: draft.model,
      overrides: { ...current.overrides, [field]: value === '' ? undefined : Number(value) },
    }))
  }

  async function chooseProfile(profileId: string) {
    if (!savedEndpoint || !draft.model) return
    setWorking('profile-activate')
    try {
      const next = await client.activateProfile(savedEndpoint.id, draft.model, profileId || null)
      setSnapshot(next)
      const selected = savedEndpoint.profiles.find((profile) => profile.id === profileId)
      setProfileDraft(selected ? { ...selected, makeActive: true } : { ...emptyProfile, model: draft.model })
      setNotice({ tone: 'good', text: profileId ? 'The selected profile will override only its listed fields.' : 'Server defaults restored. Photon will send no profile tuning fields.' })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function saveProfile() {
    if (!savedEndpoint) return
    setWorking('profile-save')
    try {
      const next = await client.saveProfile(savedEndpoint.id, { ...profileDraft, model: draft.model, makeActive: true })
      setSnapshot(next)
      setNotice({ tone: 'good', text: `${profileDraft.name} saved and activated for ${draft.model}. Only the displayed overrides will be sent.` })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  async function deleteProfile() {
    if (!savedEndpoint || !profileDraft.id) return
    setWorking('profile-delete')
    try {
      const next = await client.deleteProfile(savedEndpoint.id, profileDraft.id)
      setSnapshot(next)
      setProfileDraft({ ...emptyProfile, model: draft.model })
      setNotice({ tone: 'good', text: 'Profile removed. Server defaults are active for this model.' })
    } catch (reason) { setNotice({ tone: 'bad', text: message(reason) }) }
    finally { setWorking(null) }
  }

  return (
    <section className="runtime-configuration" aria-label="Model runtime configuration">
      <header className="runtime-configuration__header">
        <div><small>MODEL RUNTIME</small><h2>Provider &amp; endpoint configuration</h2><p>Choose a hosted provider or connect Photon to an OpenAI-compatible model server on this computer or your LAN.</p></div>
        <button type="button" onClick={() => void load()} disabled={working !== null}><RefreshCw className={working === 'load' ? 'spin' : ''} size={14} /> Refresh</button>
      </header>

      {notice && <div className={`runtime-configuration__notice is-${notice.tone}`} role="status">{notice.tone === 'good' ? <Check size={14} /> : <CircleAlert size={14} />}{notice.text}</div>}

      <div className="runtime-configuration__current">
        <span><Cpu size={18} /></span><div><small>ACTIVE DEFAULT</small><strong>{snapshot?.current.model || catalog?.currentModel || 'No model selected'}</strong><p>{snapshot?.current.provider || catalog?.currentProvider || 'Provider unavailable'}{snapshot?.current.baseUrl ? ` · ${snapshot.current.baseUrl}` : ''}</p></div>
      </div>

      <div className="runtime-configuration__columns">
        <article className="runtime-configuration__panel">
          <header><span><Cloud size={16} /></span><div><strong>Hosted providers</strong><small>OpenRouter and authenticated provider catalogs</small></div></header>
          <label><span>Provider</span><select value={selection.provider} onChange={(event) => {
            const provider = catalog?.providers.find((item) => item.slug === event.target.value)
            setSelection({ provider: event.target.value, model: provider?.models[0] ?? '' })
          }}>{catalog?.providers.filter((provider) => provider.authenticated).map((provider) => <option key={provider.slug} value={provider.slug}>{provider.name}</option>)}</select></label>
          <label><span>Model ID</span><input list="runtime-hosted-models" value={selection.model} onChange={(event) => setSelection((current) => ({ ...current, model: event.target.value }))} placeholder="provider/model" /><datalist id="runtime-hosted-models">{hostedModels.map((model) => <option value={model} key={model} />)}</datalist></label>
          <div className="runtime-configuration__credential"><KeyRound size={14} /><span><strong>Credentials stay native</strong><small>Connect or replace provider keys in Connections. Runtime never displays them.</small></span><button type="button" onClick={onOpenConnections}>Open Connections</button></div>
          <button className="is-primary" type="button" disabled={!selection.provider || !selection.model || working !== null} onClick={() => void useHostedProvider()}>{working === 'hosted' ? <LoaderCircle className="spin" size={14} /> : <Zap size={14} />} Use for new conversations</button>
        </article>

        <article className="runtime-configuration__panel">
          <header><span><Network size={16} /></span><div><strong>Local / LAN endpoint</strong><small>Ollama, LM Studio, vLLM, llama.cpp, or GPT-OSS</small></div></header>
          <div className="runtime-configuration__presets"><button type="button" onClick={() => setDraft((current) => ({ ...current, name: current.name || 'Local Ollama', baseUrl: 'http://host.docker.internal:11434/v1' }))}>Ollama</button><button type="button" onClick={() => setDraft((current) => ({ ...current, name: current.name || 'Local LM Studio', baseUrl: 'http://host.docker.internal:1234/v1' }))}>LM Studio</button></div>
          <div className="runtime-configuration__row"><label><span>Name</span><input value={draft.name} onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))} placeholder="Local GPT-OSS" /></label><label><span>Provider ID</span><input value={draft.id ?? ''} onChange={(event) => setDraft((current) => ({ ...current, id: event.target.value || undefined }))} placeholder="local-gpt-oss" /></label></div>
          <label><span>OpenAI-compatible base URL</span><input value={draft.baseUrl} onChange={(event) => setDraft((current) => ({ ...current, baseUrl: event.target.value }))} placeholder="http://host.docker.internal:11434/v1" /></label>
          <label><span>Model ID</span><input list="runtime-endpoint-models" value={draft.model} onChange={(event) => { setDraft((current) => ({ ...current, model: event.target.value, contextLength: undefined })); setProfileDraft({ ...emptyProfile, model: event.target.value }) }} placeholder="gpt-oss:20b" /><datalist id="runtime-endpoint-models">{endpointModels.map((model) => <option value={model} key={model} />)}</datalist></label>
          <div className="runtime-configuration__checks"><label><input type="checkbox" checked={draft.discoverModels} onChange={(event) => setDraft((current) => ({ ...current, discoverModels: event.target.checked }))} /> Discover models</label><label><input type="checkbox" checked={draft.makeDefault} onChange={(event) => setDraft((current) => ({ ...current, makeDefault: event.target.checked }))} /> Use for new conversations</label></div>
          <p className="runtime-configuration__hint">Photon runs inside Docker. Use <code>host.docker.internal</code> for a model server running on this PC, or a reachable LAN IP. Keyless local endpoints work here; hosted keys remain in Connections.</p>
          <div className="runtime-configuration__actions"><button type="button" disabled={!draft.baseUrl || working !== null} onClick={() => void testEndpoint()}>{working === 'test' ? <LoaderCircle className="spin" size={14} /> : <Zap size={14} />} Test &amp; discover</button><button className="is-primary" type="button" disabled={!draft.name || !draft.baseUrl || !draft.model || working !== null} onClick={() => void saveEndpoint()}>{working === 'save' ? <LoaderCircle className="spin" size={14} /> : <Save size={14} />} Save endpoint</button><button type="button" onClick={() => { setDraft(emptyDraft); setDiscoveredModels([]); setValidation(null); setProfileDraft(emptyProfile) }}><Plus size={14} /> New</button></div>

          {validation && <section className="runtime-configuration__server-truth" aria-label="Server-reported runtime settings">
            <header><Server size={14} /><strong>{validation.runtimeKind === 'lm-studio' ? 'LM Studio native settings' : 'OpenAI-compatible endpoint'}</strong><small>Server reported</small></header>
            {selectedDetail ? <>
              <div className="runtime-configuration__model-facts"><span><small>Model</small><strong>{selectedDetail.displayName || selectedDetail.id}</strong></span><span><small>Loaded</small><strong>{selectedDetail.loaded ? 'Yes' : 'No'}</strong></span><span><small>Format</small><strong>{[selectedDetail.format, selectedDetail.quantization?.name].filter(Boolean).join(' · ') || 'Not reported'}</strong></span><span><small>Maximum context</small><strong>{selectedDetail.maxContextLength?.toLocaleString() || 'Not reported'}</strong></span></div>
              {selectedDetail.loadedInstances[0] && <dl>{Object.entries(selectedDetail.loadedInstances[0].config).map(([key, value]) => <div key={key}><dt>{key.replaceAll('_', ' ')}</dt><dd>{String(value)}</dd></div>)}</dl>}
              <p>Values shown above came from the server. Anything not reported remains under LM Studio control and is not guessed by Photon.</p>
            </> : <p>The server did not report native settings for the selected model. Photon will not invent them.</p>}
          </section>}

          <section className="runtime-configuration__profiles" aria-label="Named model profiles">
            <header><SlidersHorizontal size={14} /><div><strong>Named model profiles</strong><small>Optional explicit overrides scoped to this endpoint and exact model</small></div></header>
            {!savedEndpoint ? <p>Save the endpoint first. Until then, LM Studio remains fully authoritative.</p> : <>
              {savedEndpoint.externalOverridesPresent && <div className="runtime-configuration__profile-warning"><CircleAlert size={13} /> This endpoint has external request overrides not managed by this screen.</div>}
              <label><span>Active profile</span><select value={activeProfile?.id ?? ''} disabled={!draft.model || working !== null} onChange={(event) => void chooseProfile(event.target.value)}><option value="">Server defaults — send no profile fields</option>{savedEndpoint.profiles.filter((profile) => profile.model === draft.model).map((profile) => <option key={profile.id} value={profile.id}>{profile.name}</option>)}</select></label>
              <div className="runtime-configuration__row"><label><span>Profile name</span><input value={profileDraft.name} onChange={(event) => setProfileDraft((current) => ({ ...current, name: event.target.value }))} placeholder="My balanced profile" /></label><label><span>Profile ID</span><input value={profileDraft.id} onChange={(event) => setProfileDraft((current) => ({ ...current, id: event.target.value }))} placeholder="balanced" /></label></div>
              <div className="runtime-configuration__profile-grid">
                {([['temperature', 'Temperature'], ['topP', 'Top P'], ['topK', 'Top K'], ['minP', 'Min P'], ['repeatPenalty', 'Repeat penalty'], ['maxTokens', 'Max output tokens'], ['seed', 'Seed']] as const).map(([field, label]) => <label key={field}><span>{label} <em>Inherit when blank</em></span><input inputMode="decimal" value={profileDraft.overrides[field] ?? ''} onChange={(event) => setProfileNumber(field, event.target.value)} placeholder="Inherit" /></label>)}
              </div>
              <p>Reasoning effort is controlled per conversation beside the Photon text box. Context, GPU offload, batching, and other LM Studio load settings are never overridden here.</p>
              <div className="runtime-configuration__actions"><button className="is-primary" type="button" disabled={!profileDraft.id || !profileDraft.name || !draft.model || working !== null} onClick={() => void saveProfile()}>{working === 'profile-save' ? <LoaderCircle className="spin" size={14} /> : <Save size={14} />} Save &amp; activate</button><button type="button" className="is-danger" disabled={!profileDraft.id || working !== null} onClick={() => void deleteProfile()}><Trash2 size={14} /> Delete profile</button></div>
            </>}
          </section>
        </article>
      </div>

      <section className="runtime-configuration__saved"><header><strong>Saved OpenAI-compatible endpoints</strong><small>{snapshot?.endpoints.length ?? 0} configured</small></header>{snapshot?.endpoints.length ? <div>{snapshot.endpoints.map((endpoint) => <article key={endpoint.id}><span className={endpoint.isCurrent ? 'is-current' : ''}><Server size={15} /></span><button type="button" className="runtime-configuration__saved-main" onClick={() => { setDraft(draftFromEndpoint(endpoint)); setDiscoveredModels(endpoint.models); setValidation(null); setProfileDraft({ ...emptyProfile, model: endpoint.model }) }}><strong>{endpoint.name}{endpoint.isCurrent ? ' · Active' : ''}</strong><small>{endpoint.baseUrl} · {endpoint.model}{endpoint.hasCredential ? ' · credential configured' : ''}</small></button><button type="button" disabled={endpoint.isCurrent || working !== null} onClick={() => void activateEndpoint(endpoint)}>Use</button>{endpoint.source !== 'direct-config' && <button type="button" className="is-danger" disabled={working !== null} onClick={() => void deleteEndpoint(endpoint)} title="Delete endpoint"><Trash2 size={14} /></button>}</article>)}</div> : <p>No local or custom endpoints are configured yet.</p>}</section>
    </section>
  )
}
