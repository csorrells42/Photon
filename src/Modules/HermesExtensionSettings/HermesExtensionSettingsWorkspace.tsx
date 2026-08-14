import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type {
  ExtensionWriteIntent,
  HermesExtensionSettingsController,
  HermesExtensionSettingsSnapshot,
  ProviderValidationIntent,
  ProviderValidationResult,
  WriteReview,
} from './contracts'
import { fakeHermesExtensionSettingsController } from './FakeHermesExtensionSettingsController'
import { AdvancedModelSettingsPanel } from './AdvancedModelSettingsPanel'
import { McpSettingsPanel } from './McpSettingsPanel'
import { ReviewDialog } from './ReviewDialog'
import { SkillStudio } from './SkillStudio'
import { ToolsetSettingsPanel } from './ToolsetSettingsPanel'
import { nextWorkspaceTab, safeText } from './safety'
import './HermesExtensionSettingsWorkspace.css'

const tabs = [
  { id: 'skills', label: 'Skill Studio' },
  { id: 'toolsets', label: 'Search & Extract' },
  { id: 'models', label: 'Advanced models' },
  { id: 'mcp', label: 'MCP configuration' },
] as const

type TabId = typeof tabs[number]['id']
export type HermesExtensionSettingsWorkspaceMode = 'preview' | 'live-model-assignments'

export interface HermesExtensionSettingsWorkspaceProps {
  controller?: HermesExtensionSettingsController
  initialSnapshot?: HermesExtensionSettingsSnapshot
  className?: string
  mode?: HermesExtensionSettingsWorkspaceMode
}

export function HermesExtensionSettingsWorkspace({
  controller = fakeHermesExtensionSettingsController,
  initialSnapshot,
  className = '',
  mode = 'preview',
}: HermesExtensionSettingsWorkspaceProps) {
  const assignmentOnly = mode === 'live-model-assignments'
  const visibleTabs = assignmentOnly ? tabs.filter((tab) => tab.id === 'models') : tabs
  const [snapshot, setSnapshot] = useState<HermesExtensionSettingsSnapshot | null>(initialSnapshot ?? null)
  const [loadState, setLoadState] = useState(initialSnapshot?.state ?? 'ready')
  const [loading, setLoading] = useState(!initialSnapshot)
  const [activeTab, setActiveTab] = useState<TabId>(assignmentOnly ? 'models' : 'skills')
  const [review, setReview] = useState<WriteReview | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [expensiveConfirmed, setExpensiveConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([])
  const returnFocus = useRef<HTMLElement | null>(null)

  const load = async () => {
    if (busy) return
    setLoading(true)
    setMessage(null)
    try {
      const result = await controller.load()
      setLoadState(result.state)
      if (result.snapshot) setSnapshot(result.snapshot)
      else setMessage({ kind: 'error', text: safeText(result.message, 2_048) || 'Extension settings are unavailable.' })
    } catch (reason) {
      setLoadState('error')
      setMessage({ kind: 'error', text: safeText(reason instanceof Error ? reason.message : 'Extension settings failed to load.', 2_048) })
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (initialSnapshot) return
    let active = true
    setLoading(true)
    void controller.load().then((result) => {
      if (!active) return
      setLoadState(result.state)
      if (result.snapshot) setSnapshot(result.snapshot)
      else setMessage({ kind: 'error', text: safeText(result.message, 2_048) || 'Extension settings are unavailable.' })
    }).catch((reason) => {
      if (!active) return
      setLoadState('error')
      setMessage({ kind: 'error', text: safeText(reason instanceof Error ? reason.message : 'Extension settings failed to load.', 2_048) })
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [controller, initialSnapshot])

  const previewIntent = async (intent: ExtensionWriteIntent, source: HTMLElement) => {
    if (busy || review) return
    setBusy(true)
    setMessage(null)
    returnFocus.current = source
    try {
      const nextReview = await controller.preview(intent)
      setReview(nextReview)
      setConfirmed(false)
      setExpensiveConfirmed(false)
    } catch (reason) {
      setMessage({ kind: 'error', text: safeText(reason instanceof Error ? reason.message : 'Preview failed.', 2_048) })
    } finally {
      setBusy(false)
    }
  }

  const closeReview = () => {
    if (busy) return
    setReview(null)
    setConfirmed(false)
    setExpensiveConfirmed(false)
    queueMicrotask(() => returnFocus.current?.focus())
  }

  const commit = async () => {
    if (!review || !confirmed || busy) return
    setBusy(true)
    setMessage(null)
    try {
      const result = await controller.commit({
        reviewId: review.reviewId,
        confirmed: true,
        ...(review.requiresExpensiveModelConfirmation ? { expensiveModelConfirmed: expensiveConfirmed } : {}),
      })
      if (result.status === 'success') {
        if (result.snapshot) {
          setSnapshot(result.snapshot)
          setLoadState(result.snapshot.state)
        }
        setMessage({ kind: 'success', text: safeText(result.message, 2_048) || 'Confirmed action completed.' })
        setReview(null)
        queueMicrotask(() => returnFocus.current?.focus())
      } else {
        setMessage({ kind: 'error', text: safeText(result.message, 2_048) || 'Confirmed action failed.' })
      }
      return result
    } catch (reason) {
      setMessage({ kind: 'error', text: safeText(reason instanceof Error ? reason.message : 'Confirmed action failed.', 2_048) })
    } finally {
      setBusy(false)
    }
  }

  const validateProvider = async (intent: ProviderValidationIntent): Promise<ProviderValidationResult> => {
    try {
      const result = await controller.validateProvider(intent)
      return {
        providerId: safeText(result.providerId, 128),
        state: result.state,
        message: safeText(result.message, 2_048),
      }
    } catch (reason) {
      return {
        providerId: safeText(intent.providerId, 128),
        state: 'unavailable',
        message: safeText(reason instanceof Error ? reason.message : 'Provider validation is unavailable.', 2_048),
      }
    }
  }

  const onTabKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const next = nextWorkspaceTab(index, event.key, visibleTabs.length)
    if (next === index) return
    event.preventDefault()
    setActiveTab(visibleTabs[next].id)
    tabRefs.current[next]?.focus()
  }

  return (
    <main className={`hermes-extension-settings ${assignmentOnly ? 'is-live-assignments' : ''} ${className}`.trim()}>
      <header className="hes-hero">
        <div>
          <small>{assignmentOnly ? 'WORKBENCH LAB · LIVE BETA' : 'HERMES EXTENSION SETTINGS'}</small>
          <h1>{assignmentOnly ? 'Model assignments' : 'Extension authoring and advanced configuration'}</h1>
          <p>{assignmentOnly ? 'Choose one live model assignment, inspect the exact change, and confirm it before Hermes writes it.' : 'Standalone coordinator surface - deterministic fake adapter by default - no live services'}</p>
        </div>
        <button type="button" disabled={loading || busy} onClick={() => void load()}>
          {loading ? 'Loading...' : 'Refresh'}
        </button>
      </header>

      {assignmentOnly ? (
        <section className="hes-lab-guide" aria-labelledby="hes-lab-guide-heading">
          <div>
            <small>WHAT YOU ARE LOOKING AT</small>
            <h2 id="hes-lab-guide-heading">A deliberately small live control</h2>
            <p>This is not a general extensions editor. It reads the live Hermes model catalog and changes one default or auxiliary assignment at a time.</p>
          </div>
          <dl>
            <div><dt>Default model</dt><dd>The model Hermes assigns to new sessions.</dd></div>
            <div><dt>Auxiliary models</dt><dd>Separate models for vision, compression, or title generation when Hermes advertises them.</dd></div>
            <div><dt>Before it writes</dt><dd>You review one before/after change. Expensive models require a second confirmation.</dd></div>
            <div><dt>Intentionally absent</dt><dd>Skills, MCP, provider credentials, task overrides, and multi-agent settings have no source-confirmed write route here.</dd></div>
          </dl>
        </section>
      ) : (
        <div className="hes-security-banner">
          <strong>Write-only secret boundary</strong>
          <span>Stored secret values never enter props, results, logs, copy actions, persistence, or rendering.</span>
        </div>
      )}

      {loadState === 'partial' ? <div className="hes-state partial" role="status"><strong>Partial data</strong><span>Available sections remain honest about missing provider surfaces.</span></div> : null}
      {loadState === 'unavailable' ? <div className="hes-state unavailable" role="status"><strong>Unavailable</strong><span>The controller cannot currently provide extension settings.</span></div> : null}
      {loadState === 'error' ? <div className="hes-state error" role="alert"><strong>Error</strong><span>The controller returned an error state.</span></div> : null}
      {message ? (
        <div className={`hes-message ${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'} aria-live="polite">
          <span>{message.text}</span>
          <button type="button" onClick={() => setMessage(null)} aria-label="Dismiss message">Close</button>
        </div>
      ) : null}

      {loading && !snapshot ? (
        <section className="hes-empty" aria-live="polite" aria-busy="true">
          <strong>Loading extension settings...</strong>
          <p>Waiting for the controller snapshot.</p>
        </section>
      ) : !snapshot ? (
        <section className="hes-empty">
          <strong>Extension settings unavailable</strong>
          <p>No renderer-safe snapshot was supplied.</p>
          <button type="button" onClick={() => void load()}>Try again</button>
        </section>
      ) : (
        <>
          {snapshot.notices.length > 0 ? (
            <aside className="hes-notices" aria-label="Extension settings notices">
              {snapshot.notices.map((notice) => <p key={notice.id} className={notice.level}>{notice.message}</p>)}
            </aside>
          ) : null}
          <nav className="hes-tabs" role="tablist" aria-label="Extension settings sections">
            {visibleTabs.map((tab, index) => (
              <button
                key={tab.id}
                ref={(element) => { tabRefs.current[index] = element }}
                type="button"
                role="tab"
                id={`hes-tab-${tab.id}`}
                aria-selected={activeTab === tab.id}
                aria-controls={`hes-panel-${tab.id}`}
                tabIndex={activeTab === tab.id ? 0 : -1}
                onClick={() => setActiveTab(tab.id)}
                onKeyDown={(event) => onTabKeyDown(event, index)}
              >
                {tab.label}
              </button>
            ))}
          </nav>

          <div id={`hes-panel-${activeTab}`} role="tabpanel" aria-labelledby={`hes-tab-${activeTab}`} tabIndex={0}>
            {activeTab === 'skills' ? <SkillStudio snapshot={snapshot} busy={busy} onPreview={previewIntent} /> : null}
            {activeTab === 'toolsets' ? <ToolsetSettingsPanel snapshot={snapshot} busy={busy} onPreview={previewIntent} /> : null}
            {activeTab === 'models' ? <AdvancedModelSettingsPanel snapshot={snapshot} busy={busy} assignmentOnly={assignmentOnly} onPreview={previewIntent} onValidate={validateProvider} /> : null}
            {activeTab === 'mcp' ? <McpSettingsPanel snapshot={snapshot} busy={busy} onPreview={previewIntent} /> : null}
          </div>
        </>
      )}

      {review ? (
        <ReviewDialog
          review={review}
          busy={busy}
          confirmed={confirmed}
          expensiveConfirmed={expensiveConfirmed}
          onConfirmedChange={setConfirmed}
          onExpensiveConfirmedChange={setExpensiveConfirmed}
          onCancel={closeReview}
          onCommit={commit}
        />
      ) : null}
    </main>
  )
}
