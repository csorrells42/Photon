import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesMarkdown } from './HermesMarkdown'
import { boundedRenderedCode, copyRenderedCode, HERMES_MARKDOWN_CODE_LIMIT, normalizeLanguageLabel } from './HermesMarkdownCodeBlock'

describe('TH2 Markdown code affordances', () => {
  it('labels known, unknown, and missing languages without loading a language module', () => {
    expect(normalizeLanguageLabel('ts')).toBe('TypeScript')
    expect(normalizeLanguageLabel('made-up')).toBe('made-up')
    expect(normalizeLanguageLabel('')).toBe('Text')
  })
  it('renders incomplete fences as escaped text with a safe fallback', () => {
    const html = renderToStaticMarkup(<HermesMarkdown content={'```typescript\nconst partial = "<script>"'} />)
    expect(html).toContain('TypeScript')
    expect(html).toContain('&lt;script&gt;')
    expect(html).not.toContain('<script>')
  })
  it('copies only the bounded rendered text and handles success or denial', async () => {
    const long = boundedRenderedCode('x'.repeat(HERMES_MARKDOWN_CODE_LIMIT + 1))
    const writes: string[] = []
    expect(long.truncated).toBe(true)
    expect(await copyRenderedCode(long.text, { writeText: async (value) => { writes.push(value) } })).toBe('copied')
    expect(writes[0]).toHaveLength(HERMES_MARKDOWN_CODE_LIMIT)
    expect(await copyRenderedCode('safe', undefined)).toBe('failed')
    expect(await copyRenderedCode('safe', { writeText: async () => { throw new Error('denied') } })).toBe('failed')
  })
  it('keeps long code inside a labeled bounded code block', () => {
    const html = renderToStaticMarkup(<HermesMarkdown content={`\`\`\`\n${'z'.repeat(HERMES_MARKDOWN_CODE_LIMIT + 1)}\n\`\`\``} />)
    expect(html).toContain('hermes-markdown-code-block')
    expect(html).toContain('Code block limited to')
    expect(html).toContain('Copy Text code')
  })
})
