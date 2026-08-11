import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import {
  HERMES_MCP_EDITOR_CONTRACT_VERSION,
  describeHermesMcpEditChanges,
  hermesMcpEditorReasonText,
  isOpaqueHermesMcpReviewHandle,
  normalizeHermesMcpEditDraft,
  validateHermesMcpEditDraft,
  type HermesMcpConfiguredServerSnapshot,
  type HermesMcpEditDraft,
  type HermesMcpEditorController,
  type HermesMcpEditorReadyReview,
  type HermesMcpEditorReason,
} from './HermesMcpEditorContract'
import './HermesMcpEditor.css'

export type HermesMcpEditorProps = {
  snapshot: HermesMcpConfiguredServerSnapshot
  controller?: HermesMcpEditorController
  className?: string
  onCommitted?: () => void | Promise<void>
}

type ReviewState = {
  controller: HermesMcpEditorController
  edit: HermesMcpEditDraft
  result: HermesMcpEditorReadyReview
}

type Operation = 'idle' | 'reviewing' | 'committing'

function lines(value: string) {
  return value.split(/\r?\n/u).map((item) => item.trim()).filter(Boolean)
}

function cloneDraft(draft: HermesMcpEditDraft): HermesMcpEditDraft {
  return { ...draft, args: [...draft.args], environmentVariableNames: [...draft.environmentVariableNames] }
}

function riskCopy(risk: HermesMcpEditorReadyReview['risk']) {
  if (risk === 'transport') return 'The transport changes how Hermes reaches this server.'
  if (risk === 'source') return 'The endpoint or authentication source changes.'
  if (risk === 'command') return 'The local command, arguments, or environment contract changes.'
  return 'No elevated configuration risk was identified.'
}

function operationCopy(operation: Operation, reason: HermesMcpEditorReason | null, controller?: HermesMcpEditorController) {
  if (!controller) return 'A secure coordinator is unavailable. This editor cannot submit changes.'
  if (operation === 'reviewing') return 'Preparing a short-lived secure review…'
  if (operation === 'committing') return 'Submitting the reviewed configuration once…'
  return reason ? hermesMcpEditorReasonText(reason) : 'Ready to prepare a secure review.'
}

function discardReview(review: ReviewState | null) {
  if (!review) return
  try {
    void review.controller.discard({
      contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
      reviewHandle: review.result.reviewHandle,
    })
  } catch {
    // Discard is best-effort in the renderer. The controller's TTL and
    // single-use semantics remain the authority.
  }
}

export function HermesMcpEditor({ snapshot, controller, className = '', onCommitted }: HermesMcpEditorProps) {
  const [draft, setDraft] = useState(() => normalizeHermesMcpEditDraft(snapshot))
  const [review, setReview] = useState<ReviewState | null>(null)
  const [riskConfirmed, setRiskConfirmed] = useState(false)
  const [operation, setOperation] = useState<Operation>('idle')
  const [reason, setReason] = useState<HermesMcpEditorReason | null>(null)
  const reviewRef = useRef<ReviewState | null>(null)
  const generationRef = useRef(0)
  const operationRef = useRef(false)
  const snapshotKey = `${snapshot.serverId}\u0000${snapshot.revision}`

  const validationIssues = useMemo(() => validateHermesMcpEditDraft(draft), [draft])
  const pending = operation !== 'idle'
  const changes = useMemo(
    () => describeHermesMcpEditChanges(snapshot, review?.edit ?? draft),
    [draft, review, snapshot],
  )

  function clearReview(nextReason: HermesMcpEditorReason | null = null) {
    const current = reviewRef.current
    reviewRef.current = null
    setReview(null)
    setRiskConfirmed(false)
    discardReview(current)
    setReason(nextReason)
  }

  function invalidateReview() {
    generationRef.current += 1
    clearReview(null)
  }

  function updateDraft(update: Partial<HermesMcpEditDraft>) {
    invalidateReview()
    setDraft((current) => ({ ...current, ...update }))
  }

  useEffect(() => {
    generationRef.current += 1
    const current = reviewRef.current
    reviewRef.current = null
    discardReview(current)
    setReview(null)
    setDraft(normalizeHermesMcpEditDraft(snapshot))
    setRiskConfirmed(false)
    setOperation('idle')
    setReason(null)
    operationRef.current = false
    return () => {
      generationRef.current += 1
      const active = reviewRef.current
      reviewRef.current = null
      discardReview(active)
    }
    // The immutable server revision and controller identity are the reset
    // boundary. Other snapshot fields belong to that revision.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [snapshotKey, controller])

  async function prepareReview(event: FormEvent) {
    event.preventDefault()
    if (!controller || operationRef.current) return
    if (validationIssues.length > 0) {
      clearReview('validation-error')
      return
    }

    const previous = reviewRef.current
    reviewRef.current = null
    setReview(null)
    discardReview(previous)
    setRiskConfirmed(false)
    operationRef.current = true
    setOperation('reviewing')
    setReason(null)
    const generation = ++generationRef.current
    const reviewedEdit = cloneDraft(draft)

    try {
      const result = await controller.review({
        contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
        serverId: snapshot.serverId,
        revision: snapshot.revision,
        edit: reviewedEdit,
      })
      if (generation !== generationRef.current) {
        if (result.status === 'ready') {
          discardReview({ controller, edit: reviewedEdit, result })
        }
        return
      }
      if (
        result.contractVersion !== HERMES_MCP_EDITOR_CONTRACT_VERSION ||
        (result.status === 'ready' && !isOpaqueHermesMcpReviewHandle(result.reviewHandle))
      ) {
        setReason('unavailable')
        return
      }
      if (result.status === 'ready') {
        const nextReview = { controller, edit: reviewedEdit, result }
        reviewRef.current = nextReview
        setReview(nextReview)
      }
      setReason(result.reason)
    } catch {
      if (generation === generationRef.current) setReason('unavailable')
    } finally {
      if (generation === generationRef.current) {
        operationRef.current = false
        setOperation('idle')
      }
    }
  }

  async function commitReview() {
    const current = reviewRef.current
    if (!current || operationRef.current) return
    if (current.result.risk !== 'none' && !riskConfirmed) {
      setReason('risk-confirmation-required')
      return
    }

    operationRef.current = true
    setOperation('committing')
    setReason(null)
    const generation = ++generationRef.current
    reviewRef.current = null
    setReview(null)
    try {
      const result = await current.controller.commit({
        contractVersion: HERMES_MCP_EDITOR_CONTRACT_VERSION,
        reviewHandle: current.result.reviewHandle,
        riskConfirmed,
      })
      if (generation === generationRef.current) {
        setReason(result.contractVersion === HERMES_MCP_EDITOR_CONTRACT_VERSION ? result.reason : 'unavailable')
        if (result.contractVersion === HERMES_MCP_EDITOR_CONTRACT_VERSION && result.status === 'success') {
          await onCommitted?.()
        }
      }
    } catch {
      if (generation === generationRef.current) setReason('commit-failed')
    } finally {
      if (generation === generationRef.current) {
        operationRef.current = false
        setOperation('idle')
        setRiskConfirmed(false)
      }
    }
  }

  function cancel() {
    generationRef.current += 1
    operationRef.current = false
    setOperation('idle')
    clearReview(null)
    setDraft(normalizeHermesMcpEditDraft(snapshot))
  }

  return (
    <section className={`hermes-mcp-editor ${className}`.trim()} aria-label="MCP server edit and review">
      <header className="hermes-mcp-editor__header">
        <div><span className="hermes-mcp-editor__eyebrow">SECURE REVIEW</span><h2>Edit configured MCP server</h2></div>
        <span className="hermes-mcp-editor__server">{snapshot.name}</span>
      </header>
      <p className={`hermes-mcp-editor__status is-${operation}`} role="status" aria-live="polite">
        {operationCopy(operation, reason, controller)}
      </p>

      <form onSubmit={(event) => void prepareReview(event)}>
        <fieldset disabled={pending}>
          <legend>Safe configuration</legend>
          <div className="hermes-mcp-editor__grid">
            <label>Transport<select value={draft.transport} onChange={(event) => updateDraft({ transport: event.target.value as HermesMcpEditDraft['transport'] })}><option value="http">HTTP</option><option value="stdio">Standard I/O</option><option value="unknown">Unknown</option></select></label>
            <label>Authentication<select value={draft.auth ?? ''} onChange={(event) => updateDraft({ auth: (event.target.value || null) as HermesMcpEditDraft['auth'] })}><option value="">None</option><option value="header">Header</option><option value="oauth">OAuth</option></select></label>
            <label className="is-wide">URL<input value={draft.url} maxLength={4096} onChange={(event) => updateDraft({ url: event.target.value })} /></label>
            <label className="is-wide">Command<input value={draft.command} maxLength={1024} onChange={(event) => updateDraft({ command: event.target.value })} /></label>
            <label>Arguments, one per line<textarea value={draft.args.join('\n')} onChange={(event) => updateDraft({ args: lines(event.target.value) })} /></label>
            <label>Environment variable names<textarea value={draft.environmentVariableNames.join('\n')} onChange={(event) => updateDraft({ environmentVariableNames: lines(event.target.value) })} /></label>
            <label className="hermes-mcp-editor__check"><input type="checkbox" checked={draft.enabled} onChange={(event) => updateDraft({ enabled: event.target.checked })} /> Enabled</label>
          </div>
        </fieldset>

        <p className="hermes-mcp-editor__secrets">Existing credentials stay server-side and unchanged. Adding or rotating a credential requires the native Connections vault.</p>

        {validationIssues.length > 0 ? <p className="hermes-mcp-editor__validation" role="alert">Review the transport, endpoint, command, argument limits, and environment names.</p> : null}
        <div className="hermes-mcp-editor__actions"><button type="submit" disabled={pending || !controller}>Prepare review</button><button type="button" onClick={cancel} disabled={operation === 'committing'}>Cancel edits</button></div>
      </form>

      <section className="hermes-mcp-editor__changes" aria-label="Before and after review">
        <h3>Before and after</h3>
        {changes.length ? <ul>{changes.map((change) => <li key={change.field}><strong>{change.field} · {change.kind}</strong><span>Before: {change.before}</span><span>After: {change.after}</span></li>)}</ul> : <p>No safe configuration changes.</p>}
      </section>

      {review ? <section className="hermes-mcp-editor__commit" aria-label="Commit reviewed MCP changes"><strong>{riskCopy(review.result.risk)}</strong>{review.result.risk !== 'none' ? <label><input type="checkbox" checked={riskConfirmed} onChange={(event) => setRiskConfirmed(event.target.checked)} disabled={pending} /> I reviewed and accept this configuration risk.</label> : null}<button type="button" onClick={() => void commitReview()} disabled={pending || (review.result.risk !== 'none' && !riskConfirmed)}>Commit reviewed change</button></section> : null}
    </section>
  )
}
