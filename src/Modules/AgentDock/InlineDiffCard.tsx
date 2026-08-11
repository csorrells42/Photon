import { useState } from 'react'
import {
  HERMES_TOOL_DETAIL_LIMIT,
  HERMES_TOOL_TRUNCATION_SUFFIX,
} from '../HermesGateway/HermesRuntimeAdapter'

export type InlineDiffLine = {
  kind: 'add' | 'remove' | 'context'
  text: string
}

export type InlineDiffHunk = {
  header?: string
  lines: InlineDiffLine[]
}

export type InlineDiffFile = {
  before?: string
  after?: string
  hunks: InlineDiffHunk[]
}

export type InlineDiffViewV1 = {
  kind: 'unified-diff' | 'plain-text'
  files: InlineDiffFile[]
  raw: string
  truncated: boolean
}

type Props = {
  raw: string
  defaultExpanded?: boolean
}

function retainedRaw(value: string) {
  const adapterTruncated = value.endsWith(HERMES_TOOL_TRUNCATION_SUFFIX)
  const withoutNotice = adapterTruncated
    ? value.slice(0, -HERMES_TOOL_TRUNCATION_SUFFIX.length)
    : value
  const truncated = adapterTruncated || withoutNotice.length > HERMES_TOOL_DETAIL_LIMIT

  return {
    raw: withoutNotice.slice(0, HERMES_TOOL_DETAIL_LIMIT),
    truncated,
  }
}

function parseUnifiedDiff(raw: string): InlineDiffFile[] | null {
  const lines = raw.split('\n').map((line) => line.endsWith('\r') ? line.slice(0, -1) : line)
  const files: InlineDiffFile[] = []
  let currentFile: InlineDiffFile | null = null
  let currentHunk: InlineDiffHunk | null = null

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index]
    const nextLine = lines[index + 1]

    if (line.startsWith('--- ') && nextLine?.startsWith('+++ ')) {
      currentFile = {
        before: line.slice(4).trim() || undefined,
        after: nextLine.slice(4).trim() || undefined,
        hunks: [],
      }
      files.push(currentFile)
      currentHunk = null
      index += 1
      continue
    }

    if (currentFile && line.startsWith('@@')) {
      currentHunk = { header: line, lines: [] }
      currentFile.hunks.push(currentHunk)
      continue
    }

    if (!currentHunk) continue

    if (line.startsWith('+')) currentHunk.lines.push({ kind: 'add', text: line.slice(1) })
    else if (line.startsWith('-')) currentHunk.lines.push({ kind: 'remove', text: line.slice(1) })
    else if (line.startsWith(' ')) currentHunk.lines.push({ kind: 'context', text: line.slice(1) })
    else if (line === '' || line.startsWith('\\ No newline at end of file')) {
      currentHunk.lines.push({ kind: 'context', text: line })
    } else if (
      line.startsWith('diff --git ')
      || /^(?:index|new file mode|deleted file mode|similarity index|rename from|rename to)\s/.test(line)
    ) {
      currentHunk = null
    } else {
      return null
    }
  }

  return files.length > 0 && files.every((file) => file.hunks.length > 0) ? files : null
}

export function createInlineDiffView(rawValue: string): InlineDiffViewV1 {
  const retained = retainedRaw(rawValue)
  const files = parseUnifiedDiff(retained.raw)
  return {
    kind: files ? 'unified-diff' : 'plain-text',
    files: files ?? [],
    raw: retained.raw,
    truncated: retained.truncated,
  }
}

function fileLabel(file: InlineDiffFile) {
  const before = file.before || 'unknown'
  const after = file.after || 'unknown'
  if (before === '/dev/null') return `New file · ${after}`
  if (after === '/dev/null') return `Deleted file · ${before}`
  return before === after ? before : `${before} → ${after}`
}

export function InlineDiffCard({ raw, defaultExpanded = false }: Props) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const view = createInlineDiffView(raw)

  const copyRawDiff = async () => {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable')
      await navigator.clipboard.writeText(view.raw)
      setCopyState('copied')
    } catch {
      setCopyState('failed')
    }
  }

  return (
    <details className={`inline-diff-card ${view.kind}`} open={defaultExpanded || undefined}>
      <summary>
        <span>{view.kind === 'unified-diff' ? 'Unified diff' : 'Plain-text changes'}</span>
        {view.kind === 'unified-diff' && <small>{view.files.length} {view.files.length === 1 ? 'file' : 'files'}</small>}
      </summary>
      <div className="inline-diff-body">
        <div className="inline-diff-actions">
          <span>{view.kind === 'plain-text' ? 'Unrecognized diff format; displayed as inert text.' : 'Review only · no files are opened or changed.'}</span>
          <button type="button" onClick={() => void copyRawDiff()}>Copy raw diff</button>
          <em aria-live="polite">{copyState === 'copied' ? 'Copied' : copyState === 'failed' ? 'Copy unavailable' : ''}</em>
        </div>
        {view.truncated && (
          <p className="inline-diff-truncated">Diff was capped at {HERMES_TOOL_DETAIL_LIMIT.toLocaleString()} characters before rendering.</p>
        )}
        {view.kind === 'plain-text' ? (
          <pre className="inline-diff-plain">{view.raw}</pre>
        ) : (
          <div className="inline-diff-files">
            {view.files.map((file, fileIndex) => (
              <section className="inline-diff-file" key={`${file.before ?? ''}-${file.after ?? ''}-${fileIndex}`}>
                <header>{fileLabel(file)}</header>
                {file.hunks.map((hunk, hunkIndex) => (
                  <div className="inline-diff-hunk" key={`${hunk.header ?? ''}-${hunkIndex}`}>
                    {hunk.header && <div className="inline-diff-hunk-header">{hunk.header}</div>}
                    {hunk.lines.map((line, lineIndex) => (
                      <div className="inline-diff-line" data-kind={line.kind} key={`${line.kind}-${lineIndex}`}>
                        <span aria-hidden="true">{line.kind === 'add' ? '+' : line.kind === 'remove' ? '−' : ' '}</span>
                        <code>{line.text}</code>
                      </div>
                    ))}
                  </div>
                ))}
              </section>
            ))}
          </div>
        )}
      </div>
    </details>
  )
}
