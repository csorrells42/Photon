import { useEffect, useRef, type KeyboardEvent } from 'react'
import type { CommitResult, WriteReview } from './contracts'

export interface ReviewDialogProps {
  review: WriteReview
  busy: boolean
  confirmed: boolean
  expensiveConfirmed: boolean
  onConfirmedChange(value: boolean): void
  onExpensiveConfirmedChange(value: boolean): void
  onCancel(): void
  onCommit(): Promise<CommitResult | void>
}

export function ReviewDialog({
  review,
  busy,
  confirmed,
  expensiveConfirmed,
  onConfirmedChange,
  onExpensiveConfirmedChange,
  onCancel,
  onCommit,
}: ReviewDialogProps) {
  const dialogRef = useRef<HTMLElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    headingRef.current?.focus()
  }, [review.reviewId])

  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape' && !busy) {
      event.preventDefault()
      onCancel()
    }
    if (event.key === 'Tab') {
      const focusable = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(
        'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])',
      ) ?? [])
      const first = focusable[0]
      const last = focusable.at(-1)
      if (event.shiftKey && document.activeElement === first && last) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last && first) {
        event.preventDefault()
        first.focus()
      }
    }

  }
  return (
    <div className="hes-review-backdrop" role="presentation">
      <section
        className="hes-review"
        role="dialog"
        aria-modal="true"
        aria-labelledby="hes-review-title"
        ref={dialogRef}
        onKeyDown={onKeyDown}
      >
        <header>
          <div>
            <small>REVIEW BEFORE COMMIT</small>
            <h2 id="hes-review-title" ref={headingRef} tabIndex={-1}>{review.title}</h2>
          </div>
          <button type="button" onClick={onCancel} disabled={busy} aria-label="Close review">Close</button>
        </header>
        <div className="hes-before-after">
          <section>
            <h3>Before</h3>
            {review.before.map((line, index) => <pre key={`before-${index}`}>{line}</pre>)}
          </section>
          <section>
            <h3>After</h3>
            {review.after.map((line, index) => <pre key={`after-${index}`}>{line}</pre>)}
          </section>
        </div>
        {review.warnings.length > 0 ? (
          <div className="hes-warning" role="note">
            <strong>Review notes</strong>
            <ul>{review.warnings.map((warning, index) => <li key={`warning-${index}`}>{warning}</li>)}</ul>
          </div>
        ) : null}
        <div className="hes-confirmations">
          <label>
            <input type="checkbox" checked={confirmed} onChange={(event) => onConfirmedChange(event.target.checked)} />
            I reviewed the before/after values and confirm this action.
          </label>
          {review.requiresExpensiveModelConfirmation ? (
            <label className="hes-expensive">
              <input type="checkbox" checked={expensiveConfirmed} onChange={(event) => onExpensiveConfirmedChange(event.target.checked)} />
              I explicitly approve the selected expensive model configuration.
            </label>
          ) : null}
        </div>
        <footer>
          <button type="button" onClick={onCancel} disabled={busy}>Cancel</button>
          <button
            type="button"
            className="hes-primary"
            disabled={busy || !confirmed || (review.requiresExpensiveModelConfirmation && !expensiveConfirmed)}
            onClick={() => void onCommit()}
          >
            {busy ? 'Committing...' : 'Commit confirmed action'}
          </button>
        </footer>
      </section>
    </div>
  )
}
