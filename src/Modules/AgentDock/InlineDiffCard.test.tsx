import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HERMES_TOOL_DETAIL_LIMIT, HERMES_TOOL_TRUNCATION_SUFFIX } from '../HermesGateway/HermesRuntimeAdapter'
import { createInlineDiffView, InlineDiffCard } from './InlineDiffCard'

const threeFileDiff = [
  'diff --git a/modify.ts b/modify.ts',
  '--- a/modify.ts',
  '+++ b/modify.ts',
  '@@ -1,2 +1,2 @@',
  ' const safe = true',
  '-const oldName = 1',
  '+const newName = 1',
  'diff --git a/new.ts b/new.ts',
  'new file mode 100644',
  '--- /dev/null',
  '+++ b/new.ts',
  '@@ -0,0 +1 @@',
  '+export const created = true',
  'diff --git a/delete.ts b/delete.ts',
  'deleted file mode 100644',
  '--- a/delete.ts',
  '+++ /dev/null',
  '@@ -1 +0,0 @@',
  '-export const removed = true',
].join('\n')

describe('InlineDiffCard', () => {
  it('parses add, modify, and delete hunks without creating navigation actions', () => {
    const view = createInlineDiffView(threeFileDiff)

    expect(view.kind).toBe('unified-diff')
    expect(view.files).toHaveLength(3)
    expect(view.files[0].hunks[0].lines.map((line) => line.kind)).toEqual(['context', 'remove', 'add'])
    expect(view.files[1]).toMatchObject({ before: '/dev/null', after: 'b/new.ts' })
    expect(view.files[2]).toMatchObject({ before: 'a/delete.ts', after: '/dev/null' })
  })

  it('renders paths and HTML-looking content as escaped text', () => {
    const raw = '--- a/<script>.ts\n+++ b/<script>.ts\n@@ -1 +1 @@\n-alert(1)\n+safe()'
    const html = renderToStaticMarkup(<InlineDiffCard raw={raw} defaultExpanded />)

    expect(html).toContain('open=""')
    expect(html).toContain('Unified diff')
    expect(html).toContain('data-kind="remove"')
    expect(html).toContain('data-kind="add"')
    expect(html).toContain('&lt;script&gt;.ts')
    expect(html).not.toContain('<script>')
    expect(html).not.toContain('href=')
    expect(html).toContain('Copy raw diff')
  })

  it('falls back to collapsed inert text for malformed content', () => {
    const html = renderToStaticMarkup(<InlineDiffCard raw={'not a diff\n<script>still text</script>'} />)

    expect(html).not.toContain('open=""')
    expect(html).toContain('Plain-text changes')
    expect(html).toContain('Unrecognized diff format')
    expect(html).toContain('&lt;script&gt;still text&lt;/script&gt;')
    expect(html).not.toContain('<script>')
  })

  it('caps oversized input before parsing and reports truncation', () => {
    const value = 'x'.repeat(HERMES_TOOL_DETAIL_LIMIT + 1)
    const fromAdapter = `${'y'.repeat(HERMES_TOOL_DETAIL_LIMIT)}${HERMES_TOOL_TRUNCATION_SUFFIX}`

    expect(createInlineDiffView(value)).toMatchObject({
      kind: 'plain-text',
      truncated: true,
      raw: 'x'.repeat(HERMES_TOOL_DETAIL_LIMIT),
    })
    expect(createInlineDiffView(fromAdapter)).toMatchObject({
      truncated: true,
      raw: 'y'.repeat(HERMES_TOOL_DETAIL_LIMIT),
    })
    expect(renderToStaticMarkup(<InlineDiffCard raw={value} defaultExpanded />)).toContain('capped at 8,000 characters')
  })
})
