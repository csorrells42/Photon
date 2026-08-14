import { useEffect, useRef, useState, type MouseEvent } from 'react'
import type {
  AdvancedModelSettings,
  ExtensionWriteIntent,
  HermesExtensionSettingsSnapshot,
  ProviderModelSettings,
  ProviderValidationIntent,
  ProviderValidationResult,
} from './contracts'
import { safeText } from './safety'

interface AdvancedModelSettingsPanelProps {
  snapshot: HermesExtensionSettingsSnapshot
  busy: boolean
  assignmentOnly?: boolean
  onPreview(intent: ExtensionWriteIntent, source: HTMLElement): Promise<void>
  onValidate(intent: ProviderValidationIntent): Promise<ProviderValidationResult>
}

const liveAssignableAuxiliaryTasks = new Set(['vision', 'compression', 'title-generation'])

function stripSecretUrlParts(value: string) {
  return value.split(/[?#]/, 1)[0].replace(/^(https?:\/\/)[^/@]+@/i, '$1').slice(0, 2_048)
}

export function AdvancedModelSettingsPanel({
  snapshot,
  busy,
  assignmentOnly = false,
  onPreview,
  onValidate,
}: AdvancedModelSettingsPanelProps) {
  const [draft, setDraft] = useState<AdvancedModelSettings>(() => structuredClone(snapshot.modelSettings))
  const [validation, setValidation] = useState<Record<string, ProviderValidationResult>>({})
  const [validating, setValidating] = useState<string | null>(null)
  const secretInputs = useRef<Record<string, HTMLInputElement | null>>({})
  const availableModels = snapshot.models.filter((model) => model.available)
  const visibleAuxiliaryTasks = assignmentOnly
    ? snapshot.auxiliaryTasks.filter((task) => liveAssignableAuxiliaryTasks.has(task))
    : snapshot.auxiliaryTasks
  const unavailableLiveTasks = assignmentOnly
    ? snapshot.auxiliaryTasks.filter((task) => !liveAssignableAuxiliaryTasks.has(task))
    : []

  useEffect(() => setDraft(structuredClone(snapshot.modelSettings)), [snapshot.modelSettings])

  const modelLabel = (modelId: string) => {
    const model = snapshot.models.find((entry) => entry.id === modelId)
    return model ? `${model.label}${model.costTier === 'expensive' ? ' - expensive' : ''}` : modelId
  }

  const updateProvider = (providerId: string, update: (provider: ProviderModelSettings) => ProviderModelSettings) => {
    setDraft((current) => ({
      ...current,
      providers: current.providers.map((provider) => provider.providerId === providerId ? update(provider) : provider),
    }))
  }

  const setReference = (index: number, modelId: string) => {
    setDraft((current) => {
      const referenceModelIds = [...current.moa.referenceModelIds]
      referenceModelIds[index] = modelId
      return { ...current, moa: { ...current.moa, referenceModelIds } }
    })
  }

  const selectedPreset = snapshot.moaPresets.find((preset) => preset.id === draft.moa.presetId)
  const referenceCount = selectedPreset?.referenceCount ?? 1

  const validate = async (provider: ProviderModelSettings) => {
    if (validating) return
    setValidating(provider.providerId)
    const input = secretInputs.current[provider.providerId]
    const secretValue = input?.value ?? ''
    if (input) input.value = ''
    try {
      const result = await onValidate({
        providerId: provider.providerId,
        endpoint: provider.customEndpoint,
        secrets: secretValue ? [{ fieldName: 'provider-secret', value: secretValue }] : [],
      })
      setValidation((current) => ({
        ...current,
        [provider.providerId]: {
          providerId: safeText(result.providerId, 128),
          state: result.state,
          message: safeText(result.message, 2_048),
        },
      }))
    } finally {
      setValidating(null)
    }
  }

  const preview = (event: MouseEvent<HTMLButtonElement>) => onPreview(
    { kind: 'models', settings: structuredClone(draft) },
    event.currentTarget,
  )

  return (
    <section className="hes-panel" aria-labelledby="hes-models-heading">
      <header className="hes-panel-heading">
        <div>
          <small>ADVANCED MODEL SETTINGS</small>
          <h2 id="hes-models-heading">{assignmentOnly ? 'Current and next model assignments' : 'Global, task, MoA, and provider models'}</h2>
          <p>{assignmentOnly ? 'Only the choices Hermes currently advertises as available appear below. Review exactly one change before it is written.' : 'Expensive selections receive an additional confirmation during review.'}</p>
        </div>
      </header>

      <div className="hes-model-section">
        <h3>{assignmentOnly ? 'Choose one assignment to change' : 'Global and auxiliary task models'}</h3>
        {assignmentOnly ? <p className="hes-assignment-note">The default model is used for new sessions. Auxiliary assignments are used only for their named Hermes task; they do not alter the default model.</p> : null}
        <div className="hes-field-grid">
          <label>
            <span>Default model for new sessions</span>
            <select value={draft.defaultModelId} onChange={(event) => setDraft((current) => ({ ...current, defaultModelId: event.target.value }))}>
              {availableModels.map((model) => <option key={model.id} value={model.id}>{modelLabel(model.id)}</option>)}
            </select>
          </label>
          {visibleAuxiliaryTasks.map((task) => (
            <label key={task}>
              <span>{task} auxiliary model</span>
              <select
                value={draft.auxiliaryModels.find((entry) => entry.task === task)?.modelId ?? ''}
                onChange={(event) => setDraft((current) => ({
                  ...current,
                  auxiliaryModels: [
                    ...current.auxiliaryModels.filter((entry) => entry.task !== task),
                    { task, modelId: event.target.value },
                  ],
                }))}
              >
                <option value="">Not assigned</option>
                {availableModels.map((model) => <option key={model.id} value={model.id}>{modelLabel(model.id)}</option>)}
              </select>
            </label>
          ))}
        </div>
        {unavailableLiveTasks.length > 0 ? (
          <aside className="hes-unavailable-tasks" role="note">
            <strong>Reported but not editable in this lab</strong>
            <span>{unavailableLiveTasks.join(', ')}. Hermes reported these tasks, but this page has no verified write route for them.</span>
          </aside>
        ) : null}
      </div>

      {!assignmentOnly ? <div className="hes-model-section">
        <h3>Task overrides</h3>
        <div className="hes-field-grid">
          {snapshot.taskOverrideKinds.map((task) => (
            <label key={task}>
              <span>{task}</span>
              <select
                value={draft.taskOverrides.find((entry) => entry.task === task)?.modelId ?? ''}
                onChange={(event) => setDraft((current) => ({
                  ...current,
                  taskOverrides: [
                    ...current.taskOverrides.filter((entry) => entry.task !== task),
                    ...(event.target.value ? [{ task, modelId: event.target.value }] : []),
                  ],
                }))}
              >
                <option value="">Inherit default</option>
                {availableModels.map((model) => <option key={model.id} value={model.id}>{modelLabel(model.id)}</option>)}
              </select>
            </label>
          ))}
        </div>
      </div> : null}

      {!assignmentOnly ? <div className="hes-model-section hes-moa">
        <div className="hes-section-title">
          <div><h3>Mixture of Agents</h3><p>Choose a preset, reference models, and a separate aggregator.</p></div>
          <label className="hes-switch">
            <input
              type="checkbox"
              checked={draft.moa.enabled}
              onChange={(event) => setDraft((current) => ({ ...current, moa: { ...current.moa, enabled: event.target.checked } }))}
            />
            Enabled
          </label>
        </div>
        <div className="hes-field-grid">
          <label>
            <span>Preset</span>
            <select
              value={draft.moa.presetId}
              onChange={(event) => {
                const preset = snapshot.moaPresets.find((entry) => entry.id === event.target.value)
                setDraft((current) => ({
                  ...current,
                  moa: {
                    ...current.moa,
                    presetId: event.target.value,
                    referenceModelIds: Array.from({ length: preset?.referenceCount ?? 1 }, (_, index) => current.moa.referenceModelIds[index] ?? current.defaultModelId),
                  },
                }))
              }}
            >
              {snapshot.moaPresets.map((preset) => <option key={preset.id} value={preset.id}>{preset.label} - {preset.referenceCount} references</option>)}
            </select>
          </label>
          {Array.from({ length: referenceCount }, (_, index) => (
            <label key={`reference-${index}`}>
              <span>Reference model {index + 1}</span>
              <select value={draft.moa.referenceModelIds[index] ?? ''} onChange={(event) => setReference(index, event.target.value)}>
                {availableModels.map((model) => <option key={model.id} value={model.id}>{modelLabel(model.id)}</option>)}
              </select>
            </label>
          ))}
          <label>
            <span>Aggregator model</span>
            <select
              value={draft.moa.aggregatorModelId}
              onChange={(event) => setDraft((current) => ({ ...current, moa: { ...current.moa, aggregatorModelId: event.target.value } }))}
            >
              {availableModels.filter((model) => model.specialties.includes('aggregation') || model.specialties.includes('general')).map((model) => (
                <option key={model.id} value={model.id}>{modelLabel(model.id)}</option>
              ))}
            </select>
          </label>
        </div>
      </div> : null}

      {!assignmentOnly ? <div className="hes-model-section">
        <h3>Provider settings and validation intents</h3>
        <p className="hes-secret-policy">Endpoint values are non-secret. Credential fields are uncontrolled, write-only, cleared on submit, and never enter props, state, results, logs, rendering, copy actions, or persistence.</p>
        <div className="hes-provider-grid">
          {draft.providers.map((provider) => (
            <article key={provider.providerId} className="hes-provider-card">
              <header><strong>{provider.providerId}</strong></header>
              <div className="hes-field-grid">
                <label>
                  <span>Timeout (ms)</span>
                  <input
                    type="number"
                    min={1_000}
                    max={120_000}
                    value={provider.timeoutMs}
                    onChange={(event) => updateProvider(provider.providerId, (current) => ({ ...current, timeoutMs: Number(event.target.value) }))}
                  />
                </label>
                <label>
                  <span>Maximum retries</span>
                  <input
                    type="number"
                    min={0}
                    max={10}
                    value={provider.maxRetries}
                    onChange={(event) => updateProvider(provider.providerId, (current) => ({ ...current, maxRetries: Number(event.target.value) }))}
                  />
                </label>
              </div>
              <label className="hes-switch">
                <input
                  type="checkbox"
                  checked={provider.customEndpoint !== null}
                  onChange={(event) => updateProvider(provider.providerId, (current) => ({
                    ...current,
                    customEndpoint: event.target.checked
                      ? { baseUrl: 'https://', apiPath: '/v1', authHeaderName: 'Authorization' }
                      : null,
                  }))}
                />
                Custom endpoint
              </label>
              {provider.customEndpoint ? (
                <div className="hes-field-grid">
                  <label>
                    <span>Base URL - no query or credentials</span>
                    <input
                      type="url"
                      value={provider.customEndpoint.baseUrl}
                      onChange={(event) => updateProvider(provider.providerId, (current) => ({
                        ...current,
                        customEndpoint: current.customEndpoint ? { ...current.customEndpoint, baseUrl: stripSecretUrlParts(event.target.value) } : null,
                      }))}
                    />
                  </label>
                  <label>
                    <span>API path</span>
                    <input
                      value={provider.customEndpoint.apiPath}
                      maxLength={512}
                      onChange={(event) => updateProvider(provider.providerId, (current) => ({
                        ...current,
                        customEndpoint: current.customEndpoint ? { ...current.customEndpoint, apiPath: event.target.value } : null,
                      }))}
                    />
                  </label>
                  <label>
                    <span>Authentication header name - never its value</span>
                    <input
                      value={provider.customEndpoint.authHeaderName}
                      maxLength={128}
                      onChange={(event) => updateProvider(provider.providerId, (current) => ({
                        ...current,
                        customEndpoint: current.customEndpoint ? { ...current.customEndpoint, authHeaderName: event.target.value.replace(/[^A-Za-z0-9-]/g, '') } : null,
                      }))}
                    />
                  </label>
                </div>
              ) : null}
              <label>
                <span>Write-only credential for validation</span>
                <input
                  ref={(element) => { secretInputs.current[provider.providerId] = element }}
                  type="password"
                  autoComplete="new-password"
                  maxLength={8_192}
                  aria-describedby={`secret-policy-${provider.providerId}`}
                />
                <small id={`secret-policy-${provider.providerId}`}>Cleared immediately; the controller must not retain or return it.</small>
              </label>
              <button type="button" disabled={validating !== null} onClick={() => void validate(provider)}>
                {validating === provider.providerId ? 'Validating intent...' : 'Validate provider intent'}
              </button>
              {validation[provider.providerId] ? (
                <div className={`hes-inline-state ${validation[provider.providerId].state}`} role="status">
                  {validation[provider.providerId].message}
                </div>
              ) : null}
            </article>
          ))}
        </div>
      </div> : null}

      <div className="hes-actions">
        <button type="button" className="hes-primary" disabled={busy} onClick={(event) => void preview(event)}>
          {assignmentOnly ? 'Review assignment change' : 'Preview model settings'}
        </button>
      </div>
    </section>
  )
}
