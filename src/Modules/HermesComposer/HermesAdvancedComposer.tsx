import {
  useId,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ChangeEvent,
  type KeyboardEvent,
} from 'react'
import { applyCompletion, createCompletionSession, handleCompletionKey, validateCompletionCatalogs } from './completions'
import { HermesComposerController } from './HermesComposerController'
import {
  HERMES_COMPOSER_LIMITS,
  HermesComposerInputError,
  type HermesCompletionCatalogs,
  type HermesCompletionOption,
  type HermesMessageReference,
  type HermesQueuedPrompt,
  type HermesVoiceAction,
} from './types'
import './HermesAdvancedComposer.css'

export interface HermesAdvancedComposerProps {
  readonly controller: HermesComposerController
  readonly draft: string
  readonly onDraftChange: (draft: string) => void
  readonly completionCatalogs?: HermesCompletionCatalogs
  readonly messageReferences?: readonly HermesMessageReference[]
  readonly onVoiceAction?: (action: HermesVoiceAction) => void | Promise<void>
  readonly label?: string
}

const connectionCopy = {
  ready: ['Ready', 'Prompts submit only after an explicit action.'],
  reconnecting: ['Reconnecting', 'Queued prompts are parked and will not replay automatically.'],
  offline: ['Offline', 'Draft and queue remain local to this controller instance.'],
  blocked: ['Blocked', 'Submission is disabled until the caller clears the block.'],
} as const

function assertComponentInput(props: HermesAdvancedComposerProps) {
  if (!(props.controller instanceof HermesComposerController)) throw new HermesComposerInputError('A Hermes composer controller is required.')
  if (typeof props.draft !== 'string' || props.draft.length > HERMES_COMPOSER_LIMITS.promptCharacters) {
    throw new HermesComposerInputError('Caller-owned draft exceeds the prompt bound.')
  }
  if (props.draft.includes('\u0000')) throw new HermesComposerInputError('Caller-owned draft contains a null character.')
  validateCompletionCatalogs(props.completionCatalogs ?? {})
  if (props.label !== undefined && (props.label.length === 0 || props.label.length > HERMES_COMPOSER_LIMITS.optionLabelCharacters)) {
    throw new HermesComposerInputError('Composer label is invalid or exceeds its bound.')
  }
  const references = props.messageReferences ?? []
  if (references.length > HERMES_COMPOSER_LIMITS.historyItems) throw new HermesComposerInputError('Too many message references were supplied.')
  const seen = new Set<string>()
  for (const reference of references) {
    if (reference.messageId.length === 0 || reference.messageId.length > HERMES_COMPOSER_LIMITS.messageIdCharacters
      || /[\u0000-\u001f\u007f]/u.test(reference.messageId) || reference.text.length > HERMES_COMPOSER_LIMITS.promptCharacters
      || reference.text.includes('\u0000') || seen.has(reference.messageId)
      || (reference.label !== undefined && (reference.label.length === 0
        || reference.label.length > HERMES_COMPOSER_LIMITS.optionLabelCharacters || /[\u0000-\u001f\u007f]/u.test(reference.label)))) {
      throw new HermesComposerInputError('Message references are invalid, duplicated, or exceed their bounds.')
    }
    seen.add(reference.messageId)
  }
}

function intentTitle(item: HermesQueuedPrompt) {
  switch (item.intent.kind) {
    case 'prompt': return 'Prompt'
    case 'edit-resend': return `Edit and resend · ${item.intent.messageId}`
    case 'retry': return `Retry · ${item.intent.messageId}`
    case 'regenerate': return `Regenerate · ${item.intent.messageId}`
  }
}

function intentText(item: HermesQueuedPrompt) {
  return item.intent.kind === 'prompt' || item.intent.kind === 'edit-resend' ? item.intent.text : null
}

export function HermesAdvancedComposer(props: HermesAdvancedComposerProps) {
  assertComponentInput(props)
  const {
    controller,
    draft,
    onDraftChange,
    completionCatalogs = {},
    messageReferences = [],
    onVoiceAction,
    label = 'Advanced Hermes composer',
  } = props
  const snapshot = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const completionId = useId()
  const [cursor, setCursor] = useState(draft.length)
  const [selectedCompletion, setSelectedCompletion] = useState(0)
  const [completionClosed, setCompletionClosed] = useState(false)
  const [editingMessageId, setEditingMessageId] = useState<string | null>(null)
  const [notice, setNotice] = useState('')

  const completion = useMemo(() => completionClosed
    ? null
    : createCompletionSession(draft, Math.min(cursor, draft.length), completionCatalogs, selectedCompletion),
  [completionCatalogs, completionClosed, cursor, draft, selectedCompletion])
  const [connectionLabel, connectionDescription] = connectionCopy[snapshot.connection]

  const restoreFocus = (selection = Math.min(cursor, draft.length)) => {
    const run = () => {
      textareaRef.current?.focus()
      textareaRef.current?.setSelectionRange(selection, selection)
    }
    if (typeof globalThis.requestAnimationFrame === 'function') globalThis.requestAnimationFrame(run)
    else globalThis.setTimeout(run, 0)
  }

  const changeDraft = (event: ChangeEvent<HTMLTextAreaElement>) => {
    if (event.target.value.length > HERMES_COMPOSER_LIMITS.promptCharacters) {
      setNotice(`Prompt limit is ${HERMES_COMPOSER_LIMITS.promptCharacters.toLocaleString()} characters.`)
      return
    }
    onDraftChange(event.target.value)
    setCursor(event.target.selectionStart)
    setSelectedCompletion(0)
    setCompletionClosed(false)
    setNotice('')
  }

  const chooseCompletion = (option: HermesCompletionOption) => {
    if (!completion) return
    try {
      const insertion = applyCompletion(draft, completion, option)
      onDraftChange(insertion.text)
      setCursor(insertion.cursor)
      setSelectedCompletion(0)
      setCompletionClosed(true)
      setNotice(`${option.kind === 'mention' ? 'Mention' : 'Command'} inserted.`)
      restoreFocus(insertion.cursor)
    } catch {
      setNotice('That completion could not be inserted safely.')
    }
  }

  const queueDraft = () => {
    try {
      if (editingMessageId) controller.enqueueEditResend(editingMessageId, draft)
      else controller.enqueuePrompt(draft)
      onDraftChange('')
      setCursor(0)
      setEditingMessageId(null)
      setCompletionClosed(true)
      setNotice('Prompt added to the queue. It has not been submitted.')
      restoreFocus(0)
    } catch (reason) {
      setNotice(reason instanceof HermesComposerInputError ? reason.message : 'Prompt could not be queued safely.')
    }
  }

  const submitNext = async () => {
    const result = await controller.submitNext()
    setNotice(result.status === 'submitted'
      ? 'Next queued prompt submitted once.'
      : result.status === 'failed'
        ? 'Submission failed. Use Recover to explicitly queue it again.'
        : result.status === 'cancelled'
          ? 'Submission cancelled. It was not automatically replayed.'
          : result.status === 'unavailable'
            ? 'Submission is unavailable until the connection is ready.'
            : result.status === 'busy'
              ? 'A submission is already in progress.'
              : 'The queue is empty.')
    restoreFocus()
  }

  const onComposerKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (completion) {
      const result = handleCompletionKey(completion, event.key)
      if (result.action !== 'none') {
        event.preventDefault()
        if (result.action === 'navigate') setSelectedCompletion(result.selectedIndex)
        if (result.action === 'select') chooseCompletion(result.option)
        if (result.action === 'close') {
          setCompletionClosed(true)
          setNotice('Completions closed.')
          restoreFocus()
        }
        return
      }
    }
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault()
      queueDraft()
    } else if (event.altKey && event.key === 'Enter') {
      event.preventDefault()
      void submitNext()
    }
  }

  const requestVoice = async (action: HermesVoiceAction) => {
    if (!onVoiceAction) return
    try {
      await onVoiceAction(action)
      setNotice(`Voice ${action} requested. State remains caller-controlled.`)
    } catch {
      setNotice('The caller rejected the voice action.')
    }
    restoreFocus()
  }

  const beginEditResend = (reference: HermesMessageReference) => {
    onDraftChange(reference.text)
    setEditingMessageId(reference.messageId)
    setCursor(reference.text.length)
    setCompletionClosed(true)
    setNotice('Edit and resend intent prepared. Queue it when ready.')
    restoreFocus(reference.text.length)
  }

  const queueReferencedIntent = (kind: 'retry' | 'regenerate', sourceMessageId: string) => {
    try {
      if (kind === 'retry') controller.enqueueRetry(sourceMessageId)
      else controller.enqueueRegenerate(sourceMessageId)
      setNotice(`${kind === 'retry' ? 'Retry' : 'Regenerate'} intent queued.`)
      restoreFocus()
    } catch (reason) {
      setNotice(reason instanceof HermesComposerInputError ? reason.message : 'Message intent could not be queued safely.')
    }
  }

  const recoverHistory = (historyId: string) => {
    try {
      controller.recover(historyId)
      setNotice('Recovery explicitly added a new queue item.')
      restoreFocus()
    } catch (reason) {
      setNotice(reason instanceof HermesComposerInputError ? reason.message : 'History could not be recovered safely.')
    }
  }

  const completionOptionId = completion ? `${completionId}-option-${completion.selectedIndex}` : undefined
  const voicePrimary = snapshot.voice.kind === 'listening' ? 'stop' : snapshot.voice.kind === 'processing' ? 'cancel' : 'start'
  const voiceLabel = snapshot.voice.kind === 'idle'
    ? 'Start voice input'
    : snapshot.voice.kind === 'listening'
      ? 'Finish voice input'
      : snapshot.voice.kind === 'processing'
        ? 'Cancel voice processing'
        : 'Try voice input again'

  return (
    <section className="hermes-advanced-composer" aria-label={label}>
      <header className="hc-header">
        <div>
          <span className="hc-kicker">Hermes Workbench</span>
          <h2>{label}</h2>
          <p>Caller-owned draft · explicit queue · no automatic replay</p>
        </div>
        <div className={`hc-connection hc-connection--${snapshot.connection}`} role="status" aria-live="polite">
          <span aria-hidden="true" />
          <strong>{connectionLabel}</strong>
          <small>{connectionDescription}</small>
        </div>
      </header>

      <div className="hc-composer-shell">
        {editingMessageId && (
          <div className="hc-edit-banner" role="status">
            Editing a resend for message <span>{editingMessageId}</span>
            <button type="button" onClick={() => { setEditingMessageId(null); setNotice('Edit and resend cancelled.'); restoreFocus() }}>
              Cancel edit
            </button>
          </div>
        )}
        <label className="hc-input-label" htmlFor={`${completionId}-input`}>
          Prompt
          <span>{draft.length.toLocaleString()} / {HERMES_COMPOSER_LIMITS.promptCharacters.toLocaleString()}</span>
        </label>
        <div className="hc-input-wrap">
          <textarea
            id={`${completionId}-input`}
            ref={textareaRef}
            value={draft}
            maxLength={HERMES_COMPOSER_LIMITS.promptCharacters}
            rows={6}
            role="combobox"
            aria-autocomplete="list"
            aria-expanded={Boolean(completion)}
            aria-controls={completion ? `${completionId}-listbox` : undefined}
            aria-activedescendant={completionOptionId}
            aria-describedby={`${completionId}-help ${completionId}-notice`}
            placeholder="Write a prompt. Type @ for mentions or / for commands."
            onChange={changeDraft}
            onKeyDown={onComposerKeyDown}
            onClick={(event) => { setCursor(event.currentTarget.selectionStart); setCompletionClosed(false) }}
            onKeyUp={(event) => setCursor(event.currentTarget.selectionStart)}
            onSelect={(event) => setCursor(event.currentTarget.selectionStart)}
          />
          {completion && (
            <div id={`${completionId}-listbox`} className="hc-completions" role="listbox" aria-label={`${completion.kind} completions`}>
              <div className="hc-completion-heading">
                <span>{completion.trigger}</span>
                <strong>{completion.kind === 'mention' ? 'Mention someone or something' : 'Run a command'}</strong>
                <small>↑↓ navigate · Enter/Tab select · Esc close</small>
              </div>
              {completion.options.map((option, index) => (
                <button
                  id={`${completionId}-option-${index}`}
                  key={`${option.kind}:${option.id}`}
                  type="button"
                  role="option"
                  aria-selected={index === completion.selectedIndex}
                  className={index === completion.selectedIndex ? 'is-selected' : undefined}
                  tabIndex={-1}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => chooseCompletion(option)}
                >
                  <span aria-hidden="true">{completion.trigger}</span>
                  <strong>{option.value}</strong>
                  {option.description && <small>{option.description}</small>}
                  {option.kind === 'command' && option.usage && <code>{option.usage}</code>}
                </button>
              ))}
            </div>
          )}
        </div>
        <p id={`${completionId}-help`} className="hc-help">
          Ctrl/Cmd+Enter queues the draft. Alt+Enter submits the next queued item. Enter alone adds a new line.
        </p>

        <div className="hc-actions">
          <button type="button" className="hc-primary" onClick={queueDraft} disabled={draft.trim().length === 0 || snapshot.queue.length >= HERMES_COMPOSER_LIMITS.queueItems}>
            {editingMessageId ? 'Queue edit & resend' : 'Add to queue'}
          </button>
          <button type="button" onClick={() => void submitNext()} disabled={snapshot.connection !== 'ready' || snapshot.queue.length === 0 || Boolean(snapshot.inFlight)}>
            Submit next
          </button>
          {snapshot.inFlight && <button type="button" className="hc-danger" onClick={() => controller.cancelSubmission()}>Cancel submission</button>}
          <span className={`hc-voice hc-voice--${snapshot.voice.kind}`} role="status" aria-live="polite">
            <button type="button" onClick={() => void requestVoice(voicePrimary)} disabled={!onVoiceAction} aria-label={voiceLabel}>
              <span aria-hidden="true">●</span> {voiceLabel}
            </button>
            {snapshot.voice.kind === 'listening' && (
              <button type="button" onClick={() => void requestVoice('cancel')} disabled={!onVoiceAction}>Cancel voice</button>
            )}
            {snapshot.voice.kind === 'error' && <small>{snapshot.voice.message}</small>}
          </span>
        </div>
        <p id={`${completionId}-notice`} className="hc-notice" role="status" aria-live="polite">{notice}</p>
      </div>

      <div className="hc-grid">
        <section className="hc-panel" aria-labelledby={`${completionId}-queue-title`}>
          <header>
            <div><span className="hc-kicker">Explicit order</span><h3 id={`${completionId}-queue-title`}>Prompt queue</h3></div>
            <strong>{snapshot.queue.length} / {HERMES_COMPOSER_LIMITS.queueItems}</strong>
          </header>
          {snapshot.queue.length === 0 ? (
            <p className="hc-empty">Nothing queued. Reconnects never submit on their own.</p>
          ) : (
            <ol className="hc-queue-list">
              {snapshot.queue.map((item, index) => {
                const text = intentText(item)
                return (
                  <li key={item.id}>
                    <div className="hc-queue-meta"><span>{index + 1}</span><strong>{intentTitle(item)}</strong></div>
                    {text === null ? <p>{intentTitle(item)}</p> : (
                      <textarea
                        value={text}
                        maxLength={HERMES_COMPOSER_LIMITS.promptCharacters}
                        aria-label={`Edit queued ${intentTitle(item)}`}
                        onChange={(event) => {
                          try { controller.editQueueItem(item.id, event.target.value); setNotice('Queued prompt updated.') }
                          catch { setNotice('Queued prompt could not be updated safely.') }
                        }}
                      />
                    )}
                    <div className="hc-row-actions">
                      <button type="button" onClick={() => controller.moveQueueItem(item.id, index - 1)} disabled={index === 0} aria-label={`Move ${intentTitle(item)} up`}>↑</button>
                      <button type="button" onClick={() => controller.moveQueueItem(item.id, index + 1)} disabled={index === snapshot.queue.length - 1} aria-label={`Move ${intentTitle(item)} down`}>↓</button>
                      <button type="button" className="hc-danger" onClick={() => { controller.removeQueueItem(item.id); setNotice('Queued item removed without submission.'); restoreFocus() }}>Remove</button>
                    </div>
                  </li>
                )
              })}
            </ol>
          )}
        </section>

        <section className="hc-panel" aria-labelledby={`${completionId}-history-title`}>
          <header>
            <div><span className="hc-kicker">Bounded audit</span><h3 id={`${completionId}-history-title`}>Submission history</h3></div>
            {snapshot.history.length > 0 && <button type="button" onClick={() => controller.clearHistory()}>Clear</button>}
          </header>
          {snapshot.history.length === 0 ? <p className="hc-empty">No submissions in this controller instance.</p> : (
            <ul className="hc-history-list">
              {[...snapshot.history].reverse().map((entry) => (
                <li key={entry.id}>
                  <span className={`hc-outcome hc-outcome--${entry.outcome}`}>{entry.outcome}</span>
                  <strong>{entry.intent.kind}</strong>
                  {entry.code && <small>{entry.code}</small>}
                  {entry.outcome !== 'succeeded' && (
                    <button type="button" onClick={() => recoverHistory(entry.id)}>
                      Recover to queue
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      {messageReferences.length > 0 && (
        <section className="hc-panel hc-message-actions" aria-labelledby={`${completionId}-message-title`}>
          <header><div><span className="hc-kicker">Caller-supplied IDs</span><h3 id={`${completionId}-message-title`}>Message actions</h3></div></header>
          <ul>
            {messageReferences.map((reference) => (
              <li key={reference.messageId}>
                <div><strong>{reference.label ?? 'Message'}</strong><p>{reference.text}</p></div>
                <div className="hc-row-actions">
                  <button type="button" onClick={() => beginEditResend(reference)}>Edit & resend</button>
                  <button type="button" onClick={() => queueReferencedIntent('retry', reference.messageId)}>Retry</button>
                  <button type="button" onClick={() => queueReferencedIntent('regenerate', reference.messageId)}>Regenerate</button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}
    </section>
  )
}
