import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { DockGroup } from './DockGroup'
import type { DockPanelRegistration } from './DockGroup'
import type { DockGroupLayout } from './DockGroupLayout'

function registrations(count: number): readonly DockPanelRegistration<string>[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `panel-${index}`,
    label: `Panel ${index + 1}`,
    render: (controls) => <article><h2>Panel {index + 1}</h2>{controls}</article>,
  }))
}

function layout(mode: DockGroupLayout['mode'], count: number): DockGroupLayout<string> {
  const order = Array.from({ length: count }, (_, index) => `panel-${index}`)
  return {
    mode,
    order,
    activeTab: order[0],
    ...(mode === 'tabs' ? {} : { splitRatios: order.map(() => 1 / count) }),
  }
}

describe('DockGroup splitters', () => {
  it('renders one accessible vertical separator for every horizontal panel boundary', () => {
    const markup = renderToStaticMarkup(
      <DockGroup layout={layout('horizontal', 12)} panels={registrations(12)} onLayoutChange={() => undefined} />,
    )
    expect(markup.match(/role="separator"/g)).toHaveLength(11)
    expect(markup.match(/aria-orientation="vertical"/g)).toHaveLength(11)
    expect(markup.match(/tabindex="0"/g)).toHaveLength(23)
    expect(markup).toContain('aria-label="Resize Panel 1 and Panel 2"')
    expect(markup).toContain('aria-valuemin="30"')
    expect(markup).toContain('aria-valuemax="70"')
    expect(markup).toContain('aria-valuenow="50"')
    expect(markup).toContain('aria-controls=')
  })

  it('uses horizontal separator semantics for vertical layouts', () => {
    const markup = renderToStaticMarkup(
      <DockGroup layout={layout('vertical', 4)} panels={registrations(4)} onLayoutChange={() => undefined} />,
    )
    expect(markup.match(/role="separator"/g)).toHaveLength(3)
    expect(markup.match(/aria-orientation="horizontal"/g)).toHaveLength(3)
  })

  it('leaves tab mode unchanged and renders no splitters', () => {
    const markup = renderToStaticMarkup(
      <DockGroup layout={layout('tabs', 4)} panels={registrations(4)} onLayoutChange={() => undefined} />,
    )
    expect(markup).not.toContain('role="separator"')
    expect(markup.match(/role="tab"/g)).toHaveLength(4)
    expect(markup.match(/dock-panel-inactive/g)).toHaveLength(3)
  })
})
