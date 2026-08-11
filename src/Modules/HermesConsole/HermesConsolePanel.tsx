import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ChevronLeft, ChevronRight, CircleStop, Clipboard, LoaderCircle, RefreshCw, Search, ShieldAlert, TerminalSquare, X } from 'lucide-react'
import { HERMES_CONSOLE_ADAPTER_VERSION } from './HermesConsoleClient'
import { CONSOLE_FIND_QUERY_MAX_LENGTH, consoleViewLinesAfterClear, copyConsoleText, findConsoleMatchResult, isConsoleNearBottom, isConsoleSelectionContained, nextConsoleMatchIndex, nextConsoleViewClearBoundary, normalizeConsoleFindQuery } from './HermesConsoleErgonomics'
import './HermesConsolePanel.css'
import { useHermesConsole } from './useHermesConsole'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'

type RenderLine = { id: string; kind: string; text: string }

function ConsoleLine({ line, matches, activeMatch }: { line: RenderLine; matches: ReturnType<typeof findConsoleMatchResult>['matches']; activeMatch: ReturnType<typeof findConsoleMatchResult>['matches'][number] | undefined }) {
  const lineMatches = matches.filter((match) => match.lineId === line.id)
  if (!lineMatches.length) return <pre className={line.kind}>{line.text}</pre>
  const fragments: ReactNode[] = []
  let cursor = 0
  for (const match of lineMatches) {
    if (match.start > cursor) fragments.push(line.text.slice(cursor, match.start))
    const isActive = activeMatch?.lineId === match.lineId && activeMatch.start === match.start
    fragments.push(<mark className={isActive ? 'active' : undefined} data-console-match-active={isActive ? 'true' : undefined} key={`${match.start}:${match.end}`}>{line.text.slice(match.start, match.end)}</mark>)
    cursor = match.end
  }
  if (cursor < line.text.length) fragments.push(line.text.slice(cursor))
  return <pre className={line.kind}>{fragments}</pre>
}

export function HermesConsolePanel({ embedded = false }: { embedded?: boolean }) {
  const [assistantName] = useAssistantDisplayName()
  const [command, setCommand] = useState('')
  const [history, setHistory] = useState<string[]>([])
  const [historyIndex, setHistoryIndex] = useState(-1)
  const [findOpen, setFindOpen] = useState(false)
  const [findQuery, setFindQuery] = useState('')
  const [activeMatchIndex, setActiveMatchIndex] = useState(-1)
  const [isFollowing, setIsFollowing] = useState(true)
  const [clearedAfterId, setClearedAfterId] = useState<string | null>(null)
  const [copyFeedback, setCopyFeedback] = useState<{ tone: 'error' | 'success'; text: string } | null>(null)
  const outputRef = useRef<HTMLDivElement>(null)
  const findInputRef = useRef<HTMLInputElement>(null)
  const { busy, cancel, confirm, connect, connection, lines, pendingConfirmation, prompt, run } = useHermesConsole()
  const visibleLines = useMemo(() => consoleViewLinesAfterClear(lines, clearedAfterId), [clearedAfterId, lines])
  const findResult = useMemo(() => findConsoleMatchResult(visibleLines, findQuery), [findQuery, visibleLines])
  const matches = findResult.matches
  const activeMatch = matches[activeMatchIndex]

  const jumpToLatest = () => {
    const element = outputRef.current
    if (element) element.scrollTop = element.scrollHeight
    setIsFollowing(true)
  }

  useEffect(() => {
    if (matches.length && (activeMatchIndex < 0 || activeMatchIndex >= matches.length)) setActiveMatchIndex(0)
    if (!matches.length && activeMatchIndex !== -1) setActiveMatchIndex(-1)
  }, [activeMatchIndex, matches.length])

  useEffect(() => {
    if (isFollowing) jumpToLatest()
  }, [isFollowing, pendingConfirmation, visibleLines])

  useEffect(() => {
    if (activeMatch) outputRef.current?.querySelector<HTMLElement>('mark[data-console-match-active="true"]')?.scrollIntoView({ behavior: 'auto', block: 'center' })
  }, [activeMatch])

  useEffect(() => {
    if (!findOpen) return
    findInputRef.current?.focus()
  }, [findOpen])

  useEffect(() => {
    const element = outputRef.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const transcript = element.querySelector<HTMLElement>('.console-transcript')
    if (!transcript) return
    const observer = new ResizeObserver(() => { if (isFollowing) element.scrollTop = element.scrollHeight })
    observer.observe(transcript)
    return () => observer.disconnect()
  }, [isFollowing])

  function submit() {
    const clean = command.trim()
    if (!run(clean)) return
    setHistory((current) => [clean, ...current.filter((item) => item !== clean)].slice(0, 50))
    setHistoryIndex(-1)
    setCommand('')
  }

  function moveFind(direction: 'previous' | 'next') {
    setActiveMatchIndex((current) => nextConsoleMatchIndex(current, matches.length, direction))
  }

  function clearView() {
    setClearedAfterId((current) => nextConsoleViewClearBoundary(visibleLines, current))
    setFindQuery('')
    setCopyFeedback({ tone: 'success', text: 'View cleared. Hermes history is unchanged.' })
  }

  async function copyVisibleText() {
    const output = outputRef.current
    const selection = window.getSelection()
    const selectedText = isConsoleSelectionContained(output, selection) ? selection!.toString() : ''
    const text = selectedText || visibleLines.map((line) => line.text).join('\n')
    if (!text) { setCopyFeedback({ tone: 'error', text: 'Nothing visible to copy.' }); return }
    if (!navigator.clipboard?.writeText) { setCopyFeedback({ tone: 'error', text: 'Clipboard access is unavailable.' }); return }
    const result = await copyConsoleText(text, navigator.clipboard.writeText.bind(navigator.clipboard))
    setCopyFeedback(result.ok ? { tone: 'success', text: 'Copied visible console text.' } : { tone: 'error', text: 'Copy was not permitted.' })
  }

  const body = <>
    {!embedded && <div className="terminal-tabs">
      <span className="active"><TerminalSquare size={12} /> {assistantName.toLocaleUpperCase()} CONSOLE</span><span>OUTPUT</span><span>PROBLEMS <b>0</b></span><span className="terminal-spacer" />
      <small><i className={`console-status ${connection}`} /> v{HERMES_CONSOLE_ADAPTER_VERSION}</small>
      {busy && <button type="button" aria-label="Cancel command" onClick={cancel}><CircleStop size={13} /></button>}
      <button type="button" aria-label="Reconnect console" onClick={() => void connect()}><RefreshCw className={connection === 'connecting' ? 'spin' : ''} size={13} /></button>
    </div>}
    <div className="hermes-console-workspace">
      <div className="hermes-console-tools" aria-label="Console transcript controls">
        <button type="button" aria-expanded={findOpen} aria-controls="hermes-console-find" onClick={() => setFindOpen((open) => !open)}><Search size={12} /> Find</button>
        {findOpen && <div className="hermes-console-find" id="hermes-console-find">
          <input ref={findInputRef} aria-label="Find in console" maxLength={CONSOLE_FIND_QUERY_MAX_LENGTH} value={findQuery} onChange={(event) => setFindQuery(normalizeConsoleFindQuery(event.target.value))} onKeyDown={(event) => { if (event.key === 'Escape') { setFindOpen(false); outputRef.current?.focus() } else if (event.key === 'Enter') { event.preventDefault(); moveFind(event.shiftKey ? 'previous' : 'next') } }} />
          <small>{matches.length ? `${activeMatchIndex + 1} of ${matches.length}${findResult.truncated ? '+' : ''}` : findResult.truncated ? 'No matches in search limit' : '0 matches'}</small>
          <button type="button" aria-label="Previous match" onClick={() => moveFind('previous')} disabled={!matches.length}><ChevronLeft size={12} /></button>
          <button type="button" aria-label="Next match" onClick={() => moveFind('next')} disabled={!matches.length}><ChevronRight size={12} /></button>
          <button type="button" aria-label="Close find" onClick={() => setFindOpen(false)}><X size={12} /></button>
        </div>}
        <span className="hermes-console-spacer" />
        {!isFollowing && <button type="button" onClick={jumpToLatest}>Jump to latest</button>}
        <button type="button" aria-label="Copy selected text or visible console transcript" onClick={() => void copyVisibleText()}><Clipboard size={12} /> Copy</button>
        <button type="button" aria-label="Clear console view only; Hermes history is unchanged" onClick={clearView}>Clear view</button>
        {copyFeedback && <small aria-live="polite" className={`console-feedback ${copyFeedback.tone === 'error' ? 'error' : ''}`}>{copyFeedback.text}</small>}
      </div>
      <div className="console-output" ref={outputRef} tabIndex={0} role="region" aria-label={`${assistantName} runtime console transcript`} aria-describedby="hermes-console-search-status" onScroll={() => { const element = outputRef.current; if (element) setIsFollowing(isConsoleNearBottom(element)) }}>
        <span id="hermes-console-search-status" className="console-sr-status" aria-live="polite">{findQuery ? matches.length ? `Match ${activeMatchIndex + 1} of ${matches.length}${findResult.truncated ? '; additional matches omitted by the search limit.' : '.'}` : findResult.truncated ? 'No match found before the search limit.' : 'No matches.' : 'Console transcript ready.'}</span>
        <div className="console-transcript">
          {visibleLines.length ? visibleLines.map((line) => <ConsoleLine key={line.id} line={line} matches={matches} activeMatch={activeMatch} />) : <p className="console-empty">No output in this view. Clear view does not delete Hermes history.</p>}
          {pendingConfirmation && <div className="console-confirmation"><ShieldAlert size={14} /><span><strong>Confirmation required</strong><small>{pendingConfirmation.message}</small></span><button type="button" onClick={cancel}>Cancel</button><button type="button" className="confirm" onClick={confirm}>Run</button></div>}
        </div>
        <form onSubmit={(event) => { event.preventDefault(); submit() }}><span className="prompt">{prompt || `${assistantName.toLocaleLowerCase()}> `}</span><input aria-label={`${assistantName} runtime console command`} autoComplete="off" disabled={connection !== 'open' || busy || Boolean(pendingConfirmation)} placeholder={connection === 'open' ? 'Type help to list commands' : connection === 'connecting' ? 'Connecting…' : 'Console unavailable'} value={command} onChange={(event) => setCommand(event.target.value)} onKeyDown={(event) => { if (event.key === 'ArrowUp' && history.length) { event.preventDefault(); const next = Math.min(historyIndex + 1, history.length - 1); setHistoryIndex(next); setCommand(history[next]) } else if (event.key === 'ArrowDown') { event.preventDefault(); const next = historyIndex - 1; setHistoryIndex(next); setCommand(next >= 0 ? history[next] : '') } }} />{busy && <LoaderCircle className="spin" size={12} />}</form>
      </div>
    </div>
  </>
  return embedded ? <div className="hermes-console-surface">{body}</div> : <section className="terminal-panel hermes-console-panel">{body}</section>
}
