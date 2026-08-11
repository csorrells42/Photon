import { useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  Check,
  ChevronRight,
  LoaderCircle,
  RefreshCw,
  Search,
  ShieldCheck,
  Sparkles,
  X,
  Zap,
} from 'lucide-react'
import type {
  HermesApprovalMode,
  HermesModelCatalog,
  HermesModelSelection,
} from './HermesModelAdapter'

type ModelConfirmation = {
  message: string
  selection: HermesModelSelection
}

type Props = {
  approvalMode: HermesApprovalMode
  approvalModeSaving: boolean
  catalog: HermesModelCatalog | null
  loading: boolean
  onCancelConfirmation: () => void
  onClose: () => void
  onConfirmSelection: () => Promise<boolean>
  onRefresh: () => Promise<void> | void
  onSelect: (selection: HermesModelSelection) => Promise<boolean>
  onSetApprovalMode: (mode: HermesApprovalMode) => Promise<void>
  pendingConfirmation: ModelConfirmation | null
  selection: HermesModelSelection | null
  switching: boolean
}

const approvalCopy: Record<HermesApprovalMode, { label: string; detail: string }> = {
  manual: { label: 'Manual', detail: 'Ask before protected commands' },
  smart: { label: 'Smart', detail: 'Auto-review, ask when uncertain' },
  off: { label: 'Off', detail: 'Bypass command approvals' },
}

function shortModelName(model: string) {
  const pieces = model.split('/')
  return pieces[pieces.length - 1] || model
}

export function ModelControlPopover({
  approvalMode,
  approvalModeSaving,
  catalog,
  loading,
  onCancelConfirmation,
  onClose,
  onConfirmSelection,
  onRefresh,
  onSelect,
  onSetApprovalMode,
  pendingConfirmation,
  selection,
  switching,
}: Props) {
  const [query, setQuery] = useState('')
  const [confirmApprovalOff, setConfirmApprovalOff] = useState(false)

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  const providers = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return (catalog?.providers ?? []).flatMap((provider) => {
      const models = provider.models.filter((model) =>
        !needle || model.toLowerCase().includes(needle) || provider.name.toLowerCase().includes(needle))
      return models.length ? [{ provider, models }] : []
    })
  }, [catalog, query])

  function choose(provider: string, model: string) {
    void onSelect({ provider, model }).then((switched) => {
      if (switched) onClose()
    }).catch(() => undefined)
  }

  function changeApproval(mode: HermesApprovalMode) {
    if (mode === 'off') {
      setConfirmApprovalOff(true)
      return
    }
    void onSetApprovalMode(mode).catch(() => undefined)
  }

  return (
    <section className="model-popover" aria-label="Hermes model and safety controls">
      <header>
        <div><Sparkles size={14} /><span><strong>Hermes runtime</strong><small>Model and safety controls</small></span></div>
        <div>
          <button type="button" aria-label="Refresh models" disabled={loading} onClick={() => void onRefresh()}>
            <RefreshCw size={13} className={loading ? 'spin' : ''} />
          </button>
          <button type="button" aria-label="Close model controls" onClick={onClose}><X size={14} /></button>
        </div>
      </header>

      {pendingConfirmation ? (
        <div className="model-confirmation">
          <span><AlertTriangle size={16} /></span>
          <div>
            <strong>Confirm higher-cost model</strong>
            <p>{pendingConfirmation.message}</p>
            <code>{pendingConfirmation.selection.model}</code>
            <footer>
              <button type="button" disabled={switching} onClick={onCancelConfirmation}>Cancel</button>
              <button type="button" className="danger" disabled={switching} onClick={() => {
                void onConfirmSelection().then((switched) => { if (switched) onClose() }).catch(() => undefined)
              }}>{switching ? 'Switching…' : 'Use this model'}</button>
            </footer>
          </div>
        </div>
      ) : (
        <>
          <label className="model-search">
            <Search size={13} />
            <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search authenticated models" />
          </label>

          <div className="model-catalog">
            {loading && !catalog ? <div className="model-empty"><LoaderCircle size={16} className="spin" /> Loading models…</div> : null}
            {!loading && providers.length === 0 ? <div className="model-empty">No matching authenticated models.</div> : null}
            {providers.map(({ provider, models }) => (
              <section className="model-provider" key={provider.slug}>
                <header><span>{provider.name}</span><small>{models.length}</small></header>
                {provider.warning && <p>{provider.warning}</p>}
                {models.map((model) => {
                  const selected = selection?.provider === provider.slug && selection.model === model
                  const unavailable = !provider.authenticated || provider.unavailableModels.includes(model)
                  const capability = provider.capabilities[model]
                  return (
                    <button
                      type="button"
                      className={selected ? 'selected' : ''}
                      disabled={unavailable || switching}
                      key={model}
                      onClick={() => choose(provider.slug, model)}
                    >
                      <span><strong>{shortModelName(model)}</strong><small>{model}</small></span>
                      <span className="model-badges">
                        {capability?.reasoning && <i title="Reasoning supported">R</i>}
                        {capability?.fast && <Zap size={10} aria-label="Fast mode supported" />}
                        {selected ? <Check size={13} /> : <ChevronRight size={12} />}
                      </span>
                    </button>
                  )
                })}
              </section>
            ))}
          </div>
        </>
      )}

      <section className="approval-mode-control">
        <header><ShieldCheck size={13} /><span>Command approvals</span><small>{approvalModeSaving ? 'Saving…' : approvalCopy[approvalMode].label}</small></header>
        {confirmApprovalOff ? (
          <div className="approval-off-confirm">
            <p><AlertTriangle size={12} /> Turning approvals off lets Hermes run protected commands without asking.</p>
            <div>
              <button type="button" onClick={() => setConfirmApprovalOff(false)}>Keep approvals</button>
              <button type="button" className="danger" onClick={() => {
                setConfirmApprovalOff(false)
                void onSetApprovalMode('off').catch(() => undefined)
              }}>Turn off</button>
            </div>
          </div>
        ) : (
          <div className="approval-mode-options">
            {(['manual', 'smart', 'off'] as const).map((mode) => (
              <button type="button" className={approvalMode === mode ? 'selected' : ''} disabled={approvalModeSaving} key={mode} onClick={() => changeApproval(mode)}>
                <strong>{approvalCopy[mode].label}</strong><small>{approvalCopy[mode].detail}</small>
              </button>
            ))}
          </div>
        )}
      </section>
    </section>
  )
}
