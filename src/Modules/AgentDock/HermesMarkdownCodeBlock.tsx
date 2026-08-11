import { Children, isValidElement, useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Check, Copy } from 'lucide-react'

export const HERMES_MARKDOWN_CODE_LIMIT = 64 * 1024
export type CodeCopyState = 'idle' | 'copied' | 'failed'
type ClipboardWriter = { writeText(text: string): Promise<void> }

export function languageFromClassName(className?: string) {
  const match = /(?:^|\s)language-([\w#+.-]+)/i.exec(className ?? '')
  return match?.[1]?.toLowerCase() ?? 'text'
}

export function normalizeLanguageLabel(language: string) {
  const labels: Record<string, string> = { ts: 'TypeScript', typescript: 'TypeScript', tsx: 'TSX', js: 'JavaScript', javascript: 'JavaScript', jsx: 'JSX', py: 'Python', python: 'Python', cs: 'C#', csharp: 'C#', sh: 'Shell', shell: 'Shell', bash: 'Bash', json: 'JSON', html: 'HTML', css: 'CSS', md: 'Markdown', markdown: 'Markdown', text: 'Text' }
  return labels[language] ?? (language.replace(/[^\w#+.-]/g, '').slice(0, 32) || 'Text')
}

export function boundedRenderedCode(value: string) {
  const text = value.replace(/\n$/, '')
  return { text: text.slice(0, HERMES_MARKDOWN_CODE_LIMIT), truncated: text.length > HERMES_MARKDOWN_CODE_LIMIT }
}

export async function copyRenderedCode(text: string, clipboard?: ClipboardWriter): Promise<Exclude<CodeCopyState, 'idle'>> {
  if (!clipboard) return 'failed'
  try { await clipboard.writeText(text); return 'copied' } catch { return 'failed' }
}

export function CodeBlock({ children }: { children: ReactNode }) {
  const [copyState, setCopyState] = useState<CodeCopyState>('idle')
  const resetTimer = useRef<number | null>(null)
  const element = Children.count(children) === 1 ? Children.only(children) : null
  const properties = isValidElement<{ className?: string; children?: ReactNode }>(element) ? element.props : undefined
  const code = boundedRenderedCode(String(properties?.children ?? ''))
  const language = languageFromClassName(properties?.className)
  const label = normalizeLanguageLabel(language)

  useEffect(() => () => { if (resetTimer.current !== null) window.clearTimeout(resetTimer.current) }, [])
  async function copy() {
    const state = await copyRenderedCode(code.text, typeof navigator === 'undefined' ? undefined : navigator.clipboard)
    setCopyState(state)
    if (state === 'copied') { if (resetTimer.current !== null) window.clearTimeout(resetTimer.current); resetTimer.current = window.setTimeout(() => setCopyState('idle'), 1_600) }
  }

  return <section className="hermes-markdown-code-block">
    <header><span className="hermes-markdown-code-language">{label}</span><button type="button" onClick={() => void copy()} aria-label={`Copy ${label} code`}>{copyState === 'copied' ? <Check size={12} /> : <Copy size={12} />}{copyState === 'copied' ? 'Copied' : copyState === 'failed' ? 'Copy unavailable' : 'Copy code'}</button></header>
    {code.truncated && <p className="hermes-markdown-code-limit" role="status">Code block limited to {HERMES_MARKDOWN_CODE_LIMIT.toLocaleString()} characters.</p>}
    <pre><code className={properties?.className}>{code.text}</code></pre>
    <span className="hermes-markdown-copy-status" role="status" aria-live="polite">{copyState === 'copied' ? 'Code copied.' : copyState === 'failed' ? 'Copy unavailable. Select the rendered code to copy it manually.' : ''}</span>
  </section>
}
