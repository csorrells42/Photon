import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { CircleStop, LocateFixed, Mic, RotateCcw, X } from 'lucide-react'
import './QuarkCompanion.css'

export type QuarkPhase = 'idle' | 'paused' | 'listening' | 'thinking' | 'speaking' | 'error'

export type QuarkPosition = Readonly<{ x: number; y: number }>
export type QuarkBounds = Readonly<{ width: number; height: number }>
export const QUARK_POSITION_STORAGE_KEY = 'photon.quark.position.v1'
const QUARK_MARGIN = 8
const QUARK_WIDTH = 320
const QUARK_HEIGHT = 104

export function clampQuarkPosition(position: QuarkPosition, viewport: QuarkBounds, size: QuarkBounds = { width: QUARK_WIDTH, height: QUARK_HEIGHT }): QuarkPosition {
  const maxX = Math.max(QUARK_MARGIN, viewport.width - size.width - QUARK_MARGIN)
  const maxY = Math.max(QUARK_MARGIN, viewport.height - size.height - QUARK_MARGIN)
  return {
    x: Math.min(maxX, Math.max(QUARK_MARGIN, Number.isFinite(position.x) ? position.x : QUARK_MARGIN)),
    y: Math.min(maxY, Math.max(QUARK_MARGIN, Number.isFinite(position.y) ? position.y : QUARK_MARGIN)),
  }
}

export function defaultQuarkPosition(viewport: QuarkBounds): QuarkPosition {
  return clampQuarkPosition({ x: viewport.width - QUARK_WIDTH - 18, y: viewport.height - QUARK_HEIGHT - 106 }, viewport)
}

export function parseQuarkPosition(value: string | null): QuarkPosition | null {
  if (!value) return null
  try {
    const candidate: unknown = JSON.parse(value)
    if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) return null
    const { x, y } = candidate as Record<string, unknown>
    return typeof x === 'number' && typeof y === 'number' && Number.isFinite(x) && Number.isFinite(y) ? { x, y } : null
  } catch {
    return null
  }
}

type Props = {
  active: boolean
  disabled?: boolean
  reviewActive?: boolean
  phase: QuarkPhase
  reason?: string
  onExit: () => void
  onRetry: () => void
  onStop: () => void
  onTalk: () => void
}

export const QUARK_CONVERSATION_GUIDANCE = [
  'Conversation mode: reply naturally in one to three short sentences.',
  'Do not narrate tools, repeat the user, recite page contents, use tables, or produce long Markdown unless explicitly asked.',
].join(' ')

export function quarkConversationPrompt(transcript: string): string {
  return `${QUARK_CONVERSATION_GUIDANCE}\n\nUser said: ${transcript.trim()}`
}

export function QuarkCompanion({ active, disabled, reviewActive, phase, reason, onExit, onRetry, onStop, onTalk }: Props) {
  const rootRef = useRef<HTMLElement>(null)
  const dragRef = useRef<{ pointerId: number; dx: number; dy: number; moved: boolean } | null>(null)
  const [position, setPosition] = useState<QuarkPosition | null>(null)
  const [nativeCompanion, setNativeCompanion] = useState(false)
  const facingEdge = position && typeof window !== 'undefined' && position.x < window.innerWidth / 2 ? 'left' : 'right'
  const stoppable = phase === 'listening' || phase === 'thinking' || phase === 'speaking'
  const status = phase === 'listening'
    ? 'Listening...'
    : phase === 'thinking'
      ? 'Thinking...'
      : phase === 'speaking'
        ? 'Talking...'
        : phase === 'error'
          ? (reason || 'I lost the audio path.')
          : phase === 'paused'
            ? 'Paused - tap the mic'
            : active
              ? 'Ready to chat'
              : 'Talk with Quark'

  useEffect(() => {
    const host = (window as Window & { chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void; removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void } } }).chrome?.webview
    if (!host) return
    const receive = (event: MessageEvent) => {
      const message = event.data as { type?: unknown; action?: unknown }
      if (message?.type === 'quark.companion.ready') setNativeCompanion(true)
      if (message?.type !== 'quark.companion.action') return
      if (message.action === 'talk') onTalk()
      else if (message.action === 'stop') onStop()
      else if (message.action === 'exit') onExit()
    }
    host.addEventListener('message', receive)
    host.postMessage({ type: 'quark.companion.connect', version: 1 })
    return () => host.removeEventListener('message', receive)
  }, [onExit, onStop, onTalk])

  useEffect(() => {
    if (!nativeCompanion) return
    const host = (window as Window & { chrome?: { webview?: { postMessage: (message: unknown) => void } } }).chrome?.webview
    host?.postMessage({ type: 'quark.companion.sync', version: 1, active, phase, reason: reason || '', reviewActive: Boolean(reviewActive) })
  }, [active, nativeCompanion, phase, reason, reviewActive])

  useEffect(() => {
    const viewport = { width: window.innerWidth, height: window.innerHeight }
    const stored = parseQuarkPosition(window.localStorage.getItem(QUARK_POSITION_STORAGE_KEY))
    setPosition(clampQuarkPosition(stored ?? defaultQuarkPosition(viewport), viewport, {
      width: rootRef.current?.offsetWidth || QUARK_WIDTH,
      height: rootRef.current?.offsetHeight || QUARK_HEIGHT,
    }))
    const onResize = () => setPosition((current) => current && clampQuarkPosition(current, {
      width: window.innerWidth,
      height: window.innerHeight,
    }, {
      width: rootRef.current?.offsetWidth || QUARK_WIDTH,
      height: rootRef.current?.offsetHeight || QUARK_HEIGHT,
    }))
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])

  useEffect(() => {
    if (position) window.localStorage.setItem(QUARK_POSITION_STORAGE_KEY, JSON.stringify(position))
  }, [position])

  function resetPosition() {
    setPosition(defaultQuarkPosition({ width: window.innerWidth, height: window.innerHeight }))
  }

  const companion = <section
      ref={rootRef}
      className={`quark-companion is-${phase} faces-${facingEdge} ${active ? 'is-active' : ''} ${reviewActive ? 'is-reviewing' : ''}`}
      aria-label="Quark conversation companion"
      style={position ? { left: `${position.x}px`, top: `${position.y}px` } : undefined}
    >
      <div
        className="quark-stage"
        role="img"
        aria-label="Quark, Photon conversation companion. Drag to move."
        title="Drag Quark to move him"
        onPointerDown={(event) => {
          if (event.button !== 0) return
          const bounds = rootRef.current?.getBoundingClientRect()
          if (!bounds) return
          dragRef.current = { pointerId: event.pointerId, dx: event.clientX - bounds.left, dy: event.clientY - bounds.top, moved: false }
          event.currentTarget.setPointerCapture(event.pointerId)
        }}
        onPointerMove={(event) => {
          const drag = dragRef.current
          if (!drag || drag.pointerId !== event.pointerId) return
          drag.moved = drag.moved || Math.abs(event.movementX) + Math.abs(event.movementY) > 1
          setPosition(clampQuarkPosition({ x: event.clientX - drag.dx, y: event.clientY - drag.dy }, {
            width: window.innerWidth,
            height: window.innerHeight,
          }, {
            width: rootRef.current?.offsetWidth || QUARK_WIDTH,
            height: rootRef.current?.offsetHeight || QUARK_HEIGHT,
          }))
        }}
        onPointerUp={(event) => {
          if (dragRef.current?.pointerId === event.pointerId) dragRef.current = null
          if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
        }}
        onPointerCancel={() => { dragRef.current = null }}
      >
        <i className="quark-shadow" />
        <div className="quark-sprite">
          <i className="quark-antenna" />
          <i className="quark-ear quark-ear-left" />
          <i className="quark-ear quark-ear-right" />
          <div className="quark-head">
            <i className="quark-eye quark-eye-left" />
            <i className="quark-eye quark-eye-right" />
            <i className="quark-cheek quark-cheek-left" />
            <i className="quark-cheek quark-cheek-right" />
            <i className="quark-mouth" />
          </div>
          <div className="quark-body"><i /></div>
          <i className="quark-foot quark-foot-left" />
          <i className="quark-foot quark-foot-right" />
        </div>
        {phase === 'thinking' && <span className="quark-thought">...</span>}
        {phase === 'speaking' && <><span className="quark-sound one">)</span><span className="quark-sound two">)</span></>}
      </div>

      <div className="quark-controls">
        <button
          type="button"
          className="quark-talk"
          aria-label={phase === 'listening' ? 'Stop listening and send to Photon' : stoppable ? 'Stop Quark response' : active ? 'Talk to Quark' : 'Start conversation with Quark'}
          aria-pressed={active}
          disabled={disabled}
          onClick={stoppable && phase !== 'listening' ? onStop : onTalk}
          title="Have a short, natural voice conversation with Photon"
        >
          {stoppable ? <CircleStop size={13} /> : <Mic size={13} />}
        </button>
        <span role="status" aria-live="polite">{status}</span>
        {phase === 'error' && (
          <button type="button" className="quark-small-action" onClick={onRetry} aria-label="Retry Quark microphone">
            <RotateCcw size={11} />
          </button>
        )}
        {active && (
          <button type="button" className="quark-small-action" onClick={onExit} aria-label="Exit Quark conversation mode" title="Exit conversation mode">
            <X size={12} />
          </button>
        )}
        <button type="button" className="quark-small-action" onClick={resetPosition} aria-label="Reset Quark position" title="Reset Quark position">
          <LocateFixed size={11} />
        </button>
      </div>
    </section>
  if (nativeCompanion) return null
  return typeof document === 'undefined' ? companion : createPortal(companion, document.body)
}
