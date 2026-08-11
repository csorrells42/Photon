import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { BrainCircuit, Check, ChevronDown, LoaderCircle } from 'lucide-react'
import { effectiveHermesReasoningEffort } from '../HermesSettings/HermesModelAdapter'
import type { HermesModelSelection, HermesReasoningControlOptions, HermesReasoningEffort } from '../HermesSettings/HermesModelAdapter'
import './HermesReasoningControl.css'

const effortOptions: Array<{ label: string; value: HermesReasoningEffort }> = [
  { label: 'Off', value: 'none' },
  { label: 'On', value: 'enabled' },
  { label: 'Minimal', value: 'minimal' },
  { label: 'Low', value: 'low' },
  { label: 'Medium', value: 'medium' },
  { label: 'High', value: 'high' },
  { label: 'Extra high', value: 'xhigh' },
  { label: 'Maximum', value: 'max' },
  { label: 'Ultra', value: 'ultra' },
]

export function hermesReasoningOptionRows(options?: HermesReasoningControlOptions) {
  const available = options?.source === 'server' || options?.source === 'compatibility' ? options.options : []
  return effortOptions
    .filter((option) => available.includes(option.value))
    .map((option) => {
      const label = options?.optionLabels[option.value] ?? option.label
      if (!options || option.value === 'none' || option.value === 'enabled') return { ...option, label }
      return {
        ...option,
        label: options.supportsToggle || (options.mandatory && options.label !== 'Effort')
          ? `On · Effort ${label}`
          : label,
      }
    })
}

export function hermesReasoningControlLabel(selection: HermesModelSelection | null) {
  const identity = `${selection?.provider ?? ''} ${selection?.model ?? ''}`.toLowerCase()
  if (/lmstudio|lm-studio|ollama|local|deepseek|qwen|glm|gpt-oss|anthropic|claude/.test(identity)) return 'Thinking'
  if (/openai|codex|(^|[\s/])o[134](?:-|\b)|gpt-5/.test(identity)) return 'Effort'
  return 'Reasoning'
}

function effortLabel(effort: HermesReasoningEffort, options?: HermesReasoningControlOptions) {
  return options?.optionLabels[effort]
    ?? effortOptions.find((option) => option.value === effort)?.label
    ?? effort
}

export function hermesReasoningControlSummary(
  options: HermesReasoningControlOptions | undefined,
  effort: HermesReasoningEffort | null,
) {
  if (options?.source === 'server-managed') return 'Runner managed'
  if (options?.source === 'unsupported') return 'Not supported'
  if (!options || (options.source !== 'server' && options.source !== 'compatibility')) return 'Model default'

  const effective = effectiveHermesReasoningEffort(effort, options)
  if (effective === 'none') return options.supportsToggle ? 'Off' : 'None'
  if (effective === 'enabled') return 'On'
  if (effective) {
    const label = effortLabel(effective, options)
    return options.supportsToggle || (options.mandatory && options.label !== 'Effort')
      ? `On · Effort ${label}`
      : label
  }

  if (options.defaultEnabled === false) return options.supportsToggle ? 'Off' : 'Model default'
  if (options.defaultEffort) {
    const label = effortLabel(options.defaultEffort, options)
    return options.supportsToggle || (options.defaultEnabled === true && options.label !== 'Effort')
      ? `On · Effort ${label}`
      : label
  }
  if (options.defaultEnabled === true) return 'On'
  return 'Model default'
}

export function nextReasoningOptionIndex(
  length: number,
  currentIndex: number,
  direction: 'first' | 'last' | 'next' | 'previous',
) {
  if (length <= 0) return -1
  if (direction === 'first') return 0
  if (direction === 'last') return length - 1
  if (direction === 'next') return (currentIndex + 1 + length) % length
  return (currentIndex - 1 + length) % length
}

export function HermesReasoningControl({
  disabled,
  effort,
  modelSelection,
  onChange,
  options,
}: {
  disabled: boolean
  effort: HermesReasoningEffort | null
  modelSelection: HermesModelSelection | null
  onChange: (effort: HermesReasoningEffort) => void
  options?: HermesReasoningControlOptions
}) {
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const [placement, setPlacement] = useState({ bottom: 0, left: 0, width: 0 })
  const boundOptions = options && modelSelection
    && options.targetModel === modelSelection.model
    && options.targetProvider === modelSelection.provider
    ? options
    : undefined
  const label = boundOptions?.label ?? hermesReasoningControlLabel(modelSelection)
  const optionRows = hermesReasoningOptionRows(boundOptions)
  const supported = optionRows.length > 0
  const serverPublished = boundOptions?.source === 'server'
  const serverManaged = boundOptions?.source === 'server-managed'
  const unsupported = boundOptions?.source === 'unsupported'
  const disabledControl = disabled || !supported
  const effectiveEffort = effectiveHermesReasoningEffort(effort, boundOptions)
  const selectedLabel = hermesReasoningControlSummary(boundOptions, effort)

  const focusMenuItem = (direction: 'first' | 'last' | 'next' | 'previous') => {
    const items = [...(menuRef.current?.querySelectorAll<HTMLButtonElement>('[role="option"]') ?? [])]
    if (!items.length) return
    const currentIndex = items.indexOf(document.activeElement as HTMLButtonElement)
    const nextIndex = nextReasoningOptionIndex(items.length, currentIndex, direction)
    items[nextIndex]?.focus()
  }

  useEffect(() => {
    if (!open) return
    const positionMenu = () => {
      const rect = triggerRef.current?.getBoundingClientRect()
      if (!rect) return
      const width = Math.max(148, rect.width)
      setPlacement({
        bottom: Math.max(4, window.innerHeight - rect.top + 5),
        left: Math.max(4, Math.min(rect.left, window.innerWidth - width - 4)),
        width,
      })
    }
    const closeOutside = (event: PointerEvent) => {
      const target = event.target as Node
      if (!triggerRef.current?.contains(target) && !menuRef.current?.contains(target)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false)
        triggerRef.current?.focus()
      }
    }
    positionMenu()
    document.addEventListener('pointerdown', closeOutside)
    document.addEventListener('keydown', closeOnEscape)
    window.addEventListener('resize', positionMenu)
    window.addEventListener('scroll', positionMenu, true)
    const selected = menuRef.current?.querySelector<HTMLButtonElement>('[role="option"][aria-selected="true"]')
      ?? menuRef.current?.querySelector<HTMLButtonElement>('[role="option"]')
    selected?.focus()
    return () => {
      document.removeEventListener('pointerdown', closeOutside)
      document.removeEventListener('keydown', closeOnEscape)
      window.removeEventListener('resize', positionMenu)
      window.removeEventListener('scroll', positionMenu, true)
    }
  }, [open])

  useEffect(() => {
    if (disabledControl) setOpen(false)
  }, [disabledControl])

  return (
    <div className="hermes-reasoning-control" title={supported
      ? `${label} applies to this conversation only. ${serverPublished ? 'Available choices and defaults are published by the model server.' : 'Available choices are enforced by the selected model adapter.'} When no choice is configured, Hermes sends no override. Other generation settings are unchanged.`
      : serverManaged
        ? 'This model runner owns reasoning configuration. Hermes sends no per-conversation override.'
      : unsupported
        ? 'The selected model does not expose a reasoning control. Hermes will leave it at the model default.'
        : 'Hermes could not verify reasoning controls for the selected model. It will not invent or send an override.'}>
      <button
        ref={triggerRef}
        type="button"
        className="hermes-reasoning-trigger"
        aria-label={`${label} for this conversation`}
        aria-expanded={open}
        aria-haspopup="listbox"
        disabled={disabledControl}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            setOpen(true)
          }
        }}
      >
        {disabled ? <LoaderCircle className="spin" size={13} /> : <BrainCircuit size={13} />}
        <span>{label}</span>
        <strong>{selectedLabel}</strong>
        <ChevronDown size={13} />
      </button>
      {open && createPortal(
        <div
          ref={menuRef}
          className="hermes-reasoning-menu"
          role="listbox"
          aria-label={`${label} levels`}
          style={{ bottom: placement.bottom, left: placement.left, width: placement.width }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') {
              event.preventDefault()
              focusMenuItem('next')
            } else if (event.key === 'ArrowUp') {
              event.preventDefault()
              focusMenuItem('previous')
            } else if (event.key === 'Home') {
              event.preventDefault()
              focusMenuItem('first')
            } else if (event.key === 'End') {
              event.preventDefault()
              focusMenuItem('last')
            }
          }}
        >
          {optionRows.map((option) => (
            <button
              key={option.value}
              type="button"
              role="option"
              aria-selected={effectiveEffort === option.value}
              data-effort={option.value}
              onClick={() => {
                setOpen(false)
                onChange(option.value)
                triggerRef.current?.focus()
              }}
            >
              <span>{option.label}</span>
              {effectiveEffort === option.value && <Check size={13} />}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}
