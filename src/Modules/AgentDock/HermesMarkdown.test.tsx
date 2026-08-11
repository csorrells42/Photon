import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesMarkdown } from './HermesMarkdown'

describe('HermesMarkdown', () => {
  it('renders GitHub-flavored structure and copyable fenced code', () => {
    const html = renderToStaticMarkup(
      <HermesMarkdown content={'## Result\n\n- [x] Ready\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n```typescript\nconst ready = true\n```'} />,
    )

    expect(html).toContain('<h2>Result</h2>')
    expect(html).toContain('type="checkbox"')
    expect(html).toContain('<table>')
    expect(html).toContain('markdown-code-block')
    expect(html).toContain('typescript')
    expect(html).toContain('Copy')
    expect(html).toContain('const ready = true')
  })

  it('does not execute raw HTML or unsafe link protocols', () => {
    const html = renderToStaticMarkup(
      <HermesMarkdown content={'<script>alert("no")</script>\n\n[unsafe](javascript:alert(1))\n\n![tracker](https://example.com/pixel.png)'} />,
    )

    expect(html).not.toContain('<script')
    expect(html).not.toContain('javascript:')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('pixel.png')
    expect(html).toContain('Remote image not loaded: tracker')
  })

  it('opens external web links outside the Workbench navigation surface', () => {
    const html = renderToStaticMarkup(<HermesMarkdown content={'[Docs](https://example.com/docs)'} />)

    expect(html).toContain('href="https://example.com/docs"')
    expect(html).toContain('target="_blank"')
    expect(html).toContain('rel="noopener noreferrer"')
  })
})
