import { Fragment, useId, useRef, useState } from 'react'
import type { CSSProperties, DragEvent, KeyboardEvent, PointerEvent as ReactPointerEvent, ReactNode } from 'react'
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, GripVertical, Layers3 } from 'lucide-react'
import {
  activateDockTab,
  dockPanel,
  normalizeDockGroupRatios,
  resetDockGroupRatios,
  resizeDockGroupByKeyboard,
  resizeDockGroupFromPointer,
} from './DockGroupLayout'
import type { DockGroupLayout, DockZone } from './DockGroupLayout'
import './DockGroup.css'

export type DockPanelRegistration<TPanel extends string> = {
  id: TPanel
  label: string
  render: (controls: ReactNode) => ReactNode
}

type Props<TPanel extends string> = {
  className?: string
  layout: DockGroupLayout<TPanel>
  panels: readonly DockPanelRegistration<TPanel>[]
  onLayoutChange: (layout: DockGroupLayout<TPanel>) => void
}

const zoneDetails: readonly { zone: DockZone; label: string; icon: typeof ArrowUp }[] = [
  { zone: 'top', label: 'Dock top', icon: ArrowUp },
  { zone: 'right', label: 'Dock right', icon: ArrowRight },
  { zone: 'bottom', label: 'Dock bottom', icon: ArrowDown },
  { zone: 'left', label: 'Dock left', icon: ArrowLeft },
  { zone: 'center', label: 'Stack as tabs', icon: Layers3 },
]

const splitterPixels = 7
const minimumHorizontalPanelPixels = 180
const minimumVerticalPanelPixels = 120

type SplitterDrag<TPanel extends string> = {
  pointerId: number
  separatorIndex: number
  startCoordinate: number
  availablePixels: number
  layout: DockGroupLayout<TPanel>
}

export function DockGroup<TPanel extends string>({ className = '', layout, panels, onLayoutChange }: Props<TPanel>) {
  const [draggedPanel, setDraggedPanel] = useState<TPanel | null>(null)
  const [activeSeparator, setActiveSeparator] = useState<number | null>(null)
  const groupRef = useRef<HTMLElement>(null)
  const splitterDragRef = useRef<SplitterDrag<TPanel> | null>(null)
  const groupId = useId().replaceAll(':', '')
  const registrations = new Map(panels.map((panel) => [panel.id, panel]))
  const splitRatios = normalizeDockGroupRatios(layout.splitRatios, layout.order.length)

  function applyDock(panel: TPanel, zone: DockZone) {
    onLayoutChange(dockPanel(layout, panel, zone))
    setDraggedPanel(null)
  }

  function handleKeyboardDock(event: KeyboardEvent, panel: TPanel) {
    const zone = event.key === 'ArrowUp' ? 'top'
      : event.key === 'ArrowRight' ? 'right'
        : event.key === 'ArrowDown' ? 'bottom'
          : event.key === 'ArrowLeft' ? 'left'
            : event.key === 'Enter' || event.key === ' ' ? 'center'
              : null
    if (!zone) return
    event.preventDefault()
    applyDock(panel, zone)
  }

  function handleGuideDrop(event: DragEvent, zone: DockZone) {
    event.preventDefault()
    const transferred = event.dataTransfer.getData('text/plain') as TPanel
    const panel = draggedPanel ?? transferred
    if (registrations.has(panel)) applyDock(panel, zone)
  }

  function coordinateForPointer(event: ReactPointerEvent) {
    return layout.mode === 'horizontal' ? event.clientX : event.clientY
  }

  function handleSplitterPointerDown(event: ReactPointerEvent<HTMLDivElement>, separatorIndex: number) {
    if (event.button !== 0 || layout.mode === 'tabs' || !groupRef.current) return
    event.preventDefault()
    const bounds = groupRef.current.getBoundingClientRect()
    const axisPixels = layout.mode === 'horizontal'
      ? Math.max(bounds.width, groupRef.current.scrollWidth)
      : Math.max(bounds.height, groupRef.current.scrollHeight)
    const availablePixels = Math.max(1, axisPixels - splitterPixels * Math.max(0, layout.order.length - 1))
    splitterDragRef.current = {
      pointerId: event.pointerId,
      separatorIndex,
      startCoordinate: coordinateForPointer(event),
      availablePixels,
      layout: { ...layout, splitRatios: [...splitRatios] },
    }
    setActiveSeparator(separatorIndex)
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  function handleSplitterPointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = splitterDragRef.current
    if (!drag || drag.pointerId !== event.pointerId) return
    const minimumPixels = drag.layout.mode === 'horizontal'
      ? minimumHorizontalPanelPixels
      : minimumVerticalPanelPixels
    onLayoutChange(resizeDockGroupFromPointer(
      drag.layout,
      drag.separatorIndex,
      coordinateForPointer(event) - drag.startCoordinate,
      drag.availablePixels,
      minimumPixels,
    ))
  }

  function finishSplitterPointer(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = splitterDragRef.current
    if (!drag || drag.pointerId !== event.pointerId) return
    if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId)
    }
    splitterDragRef.current = null
    setActiveSeparator(null)
  }

  function handleSplitterKeyDown(event: KeyboardEvent<HTMLDivElement>, separatorIndex: number) {
    const next = resizeDockGroupByKeyboard(layout, separatorIndex, event.key, event.shiftKey)
    if (!next) return
    event.preventDefault()
    onLayoutChange(next)
  }

  const style = { '--dock-panel-count': Math.max(1, panels.length) } as CSSProperties
  return (
    <section ref={groupRef} className={`workbench-dock-group dock-${layout.mode} ${className}`.trim()} style={style}>
      <div className="workbench-dock-tabs" role="tablist" aria-label="Docked assistants">
        {layout.order.map((id) => {
          const panel = registrations.get(id)
          if (!panel) return null
          return <button type="button" role="tab" aria-selected={layout.activeTab === id} className={layout.activeTab === id ? 'active' : ''} key={id} onClick={() => onLayoutChange(activateDockTab(layout, id))}>{panel.label}</button>
        })}
      </div>

      {layout.order.map((id, panelIndex) => {
        const panel = registrations.get(id)
        if (!panel) return null
        const panelDomId = `${groupId}-dock-panel-${panelIndex}`
        const controls = (
          <div className="workbench-dock-controls">
            <span
              className="workbench-dock-handle"
              role="button"
              tabIndex={0}
              draggable
              aria-label={`Move ${panel.label}. Use arrow keys to dock, or Enter to stack as a tab.`}
              title={`Drag ${panel.label} to a docking target`}
              onDragStart={(event) => {
                setDraggedPanel(id)
                event.dataTransfer.effectAllowed = 'move'
                event.dataTransfer.setData('text/plain', id)
              }}
              onDragEnd={() => setDraggedPanel(null)}
              onKeyDown={(event) => handleKeyboardDock(event, id)}
            ><GripVertical size={14} /></span>
          </div>
        )
        const nextPanel = panelIndex < layout.order.length - 1
          ? registrations.get(layout.order[panelIndex + 1])
          : null
        const pairTotal = nextPanel ? splitRatios[panelIndex] + splitRatios[panelIndex + 1] : 1
        const minimumRatio = Math.min(0.05, pairTotal / 2)
        const valueMinimum = Math.round(minimumRatio / pairTotal * 100)
        const valueNow = Math.round(splitRatios[panelIndex] / pairTotal * 100)
        return (
          <Fragment key={id}>
            <div
              id={panelDomId}
              className={`workbench-dock-panel panel-${id} ${layout.mode === 'tabs' && layout.activeTab !== id ? 'dock-panel-inactive' : ''} ${draggedPanel === id ? 'dragging' : ''}`}
              data-panel-id={id}
              style={layout.mode === 'tabs' ? undefined : { flexBasis: 0, flexGrow: splitRatios[panelIndex], flexShrink: 1 }}
            >{panel.render(controls)}</div>
            {layout.mode !== 'tabs' && nextPanel && (
              <div
                className={`workbench-dock-separator ${activeSeparator === panelIndex ? 'active' : ''}`}
                role="separator"
                tabIndex={0}
                aria-label={`Resize ${panel.label} and ${nextPanel.label}`}
                aria-orientation={layout.mode === 'horizontal' ? 'vertical' : 'horizontal'}
                aria-controls={`${panelDomId} ${groupId}-dock-panel-${panelIndex + 1}`}
                aria-valuemin={valueMinimum}
                aria-valuemax={100 - valueMinimum}
                aria-valuenow={valueNow}
                title="Drag or use arrow keys to resize. Hold Shift for a larger step. Press Enter or double-click to reset all panels equally."
                onPointerDown={(event) => handleSplitterPointerDown(event, panelIndex)}
                onPointerMove={handleSplitterPointerMove}
                onPointerUp={finishSplitterPointer}
                onPointerCancel={finishSplitterPointer}
                onLostPointerCapture={() => { splitterDragRef.current = null; setActiveSeparator(null) }}
                onKeyDown={(event) => handleSplitterKeyDown(event, panelIndex)}
                onDoubleClick={() => onLayoutChange(resetDockGroupRatios(layout))}
              />
            )}
          </Fragment>
        )
      })}

      {draggedPanel && (
        <div className="workbench-dock-overlay" aria-label="Docking targets">
          <div className="workbench-dock-guide">
            {zoneDetails.map(({ zone, label, icon: Icon }) => (
              <button
                type="button"
                className={`dock-zone-${zone}`}
                aria-label={label}
                title={label}
                key={zone}
                onDragEnter={(event) => event.preventDefault()}
                onDragOver={(event) => { event.preventDefault(); event.dataTransfer.dropEffect = 'move' }}
                onDrop={(event) => handleGuideDrop(event, zone)}
              ><Icon size={18} /></button>
            ))}
          </div>
        </div>
      )}
    </section>
  )
}
