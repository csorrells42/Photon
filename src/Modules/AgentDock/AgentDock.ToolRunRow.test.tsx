import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { HermesToolRun } from '../HermesGateway/HermesRuntimeAdapter'
import { measureToolRunAnchor, preserveToolRunReadingAnchor, scheduleToolRunDetailsHeightChange, ToolRunRow } from './AgentDock'

const tool: HermesToolRun = { id: 'edit-1', name: 'edit_file', phase: 'complete', context: 'Updated one file', inlineDiff: '--- a/file.ts\n+++ b/file.ts\n@@ -1 +1 @@\n-old\n+new' }

function rect(top: number, bottom = top + 20) { return { top, bottom } as DOMRect }

describe('ToolRunRow scroll anchoring', () => {
  it('keeps details collapsed by default and renders the inert diff only when expanded', () => {
    expect(renderToStaticMarkup(<ToolRunRow tool={tool} />)).toContain('aria-expanded="false"')
    const expanded = renderToStaticMarkup(<ToolRunRow tool={tool} initiallyExpanded />)
    expect(expanded).toContain('aria-expanded="true"')
    expect(expanded).toContain('inline-diff-card unified-diff')
  })

  it('proves the interactive expansion callback preserves a reader above the threshold without forcing follow', () => {
    const conversation = { scrollTop: 300, getBoundingClientRect: () => rect(100) }
    const row = { offsetHeight: 40, getBoundingClientRect: () => rect(20, 60) }
    const before = measureToolRunAnchor(row)
    let scheduled = false
    row.offsetHeight = 160
    scheduleToolRunDetailsHeightChange(row as HTMLElement, before, (changedRow, measurement) => {
      scheduled = preserveToolRunReadingAnchor(conversation, changedRow, measurement, false)
    }, (callback) => { callback(0); return 1 })
    expect(scheduled).toBe(true)
    expect(conversation.scrollTop).toBe(420)
    expect(preserveToolRunReadingAnchor(conversation, row, { height: 160, bottom: 60 }, true)).toBe(false)
    expect(conversation.scrollTop).toBe(420)
  })

  it('does not alter a reader when a changed row is visible or unchanged', () => {
    const conversation = { scrollTop: 300, getBoundingClientRect: () => rect(100) }
    const visibleRow = { offsetHeight: 160, getBoundingClientRect: () => rect(80, 120) }
    expect(preserveToolRunReadingAnchor(conversation, visibleRow, { height: 40, bottom: 120 }, false)).toBe(false)
    expect(conversation.scrollTop).toBe(300)
  })

  it('does not move a partially visible or actively selected tool row', () => {
    const conversation = { scrollTop: 300, getBoundingClientRect: () => rect(100) }
    const row = { offsetHeight: 160, getBoundingClientRect: () => rect(20, 180) }
    expect(preserveToolRunReadingAnchor(conversation, row, { height: 40, bottom: 120 }, false)).toBe(false)
    expect(preserveToolRunReadingAnchor(conversation, row, { height: 40, bottom: 60 }, false, true)).toBe(false)
    expect(conversation.scrollTop).toBe(300)
  })
})
