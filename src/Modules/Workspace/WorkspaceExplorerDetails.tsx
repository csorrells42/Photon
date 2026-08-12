import { useEffect, useRef, useState } from 'react'
import { Box, Braces, ChevronDown, ChevronRight, CircleDot, Clock3, FileClock, FunctionSquare, LoaderCircle } from 'lucide-react'
import { workspaceAdapter, type WorkspaceFile } from './WorkspaceAdapter'
import { extractWorkspaceOutline, type WorkspaceSymbolKind } from './WorkspaceOutline'

type Props = { selectedPath?: string | null }

const glyphs: Record<WorkspaceSymbolKind, typeof Box> = {
  namespace: Braces,
  type: Box,
  class: Box,
  interface: Braces,
  enum: CircleDot,
  method: FunctionSquare,
  function: FunctionSquare,
}

function formatSize(bytes: number) {
  if (bytes < 1_024) return `${bytes} B`
  if (bytes < 1_048_576) return `${(bytes / 1_024).toFixed(1)} KB`
  return `${(bytes / 1_048_576).toFixed(1)} MB`
}

function formatMoment(value: string) {
  const instant = new Date(value)
  return Number.isFinite(instant.getTime()) ? instant.toLocaleString() : 'Unknown'
}

export function WorkspaceExplorerDetails({ selectedPath }: Props) {
  const [outlineOpen, setOutlineOpen] = useState(false)
  const [timelineOpen, setTimelineOpen] = useState(false)
  const [file, setFile] = useState<WorkspaceFile | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [openedAt, setOpenedAt] = useState('')
  const generation = useRef(0)

  useEffect(() => {
    generation.current += 1
    setFile(null)
    setError('')
    setLoading(false)
    setOpenedAt(selectedPath ? new Date().toISOString() : '')
  }, [selectedPath])

  useEffect(() => {
    if ((!outlineOpen && !timelineOpen) || !selectedPath || file?.path === selectedPath || loading || error) return
    const requestGeneration = generation.current
    setLoading(true)
    void workspaceAdapter.readFile(selectedPath)
      .then((result) => { if (generation.current === requestGeneration) setFile(result) })
      .catch((reason) => { if (generation.current === requestGeneration) setError(reason instanceof Error ? reason.message : 'File details are unavailable.') })
      .finally(() => { if (generation.current === requestGeneration) setLoading(false) })
  }, [error, file?.path, loading, outlineOpen, selectedPath, timelineOpen])

  const symbols = file && selectedPath ? extractWorkspaceOutline(selectedPath, file.content) : []
  return <div className="workspace-details">
    <button type="button" className="workspace-details-heading" aria-expanded={outlineOpen} aria-controls="workspace-outline" onClick={() => { if (!outlineOpen) setError(''); setOutlineOpen((value) => !value) }}>
      {outlineOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />} OUTLINE
      {symbols.length > 0 && <span>{symbols.length}</span>}
    </button>
    {outlineOpen && <div id="workspace-outline" className="workspace-details-body" role="region" aria-label="File outline">
      {!selectedPath ? <small>Open a file to inspect its symbols.</small>
        : loading ? <small><LoaderCircle className="spin" size={12} /> Reading symbols…</small>
        : error ? <small className="error">{error}</small>
        : symbols.length === 0 ? <small>No supported declarations found.</small>
        : symbols.map((symbol) => {
          const Glyph = glyphs[symbol.kind]
          return <div className="workspace-symbol" key={`${symbol.line}:${symbol.name}`} style={{ paddingLeft: 7 + Math.min(symbol.depth, 4) * 8 }} title={`${symbol.kind} · line ${symbol.line}`}>
            <Glyph size={12} /><span>{symbol.name}</span><b>{symbol.line}</b>
          </div>
        })}
    </div>}
    <button type="button" className="workspace-details-heading" aria-expanded={timelineOpen} aria-controls="workspace-timeline" onClick={() => { if (!timelineOpen) setError(''); setTimelineOpen((value) => !value) }}>
      {timelineOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />} TIMELINE
    </button>
    {timelineOpen && <div id="workspace-timeline" className="workspace-details-body timeline" role="region" aria-label="File timeline">
      {!selectedPath ? <small>Open a file to see its local timeline.</small>
        : loading ? <small><LoaderCircle className="spin" size={12} /> Reading file facts…</small>
        : error ? <small className="error">{error}</small>
        : file ? <>
          <div><Clock3 size={12} /><span><strong>Opened in editor</strong><small>{formatMoment(openedAt)}</small></span></div>
          <div><FileClock size={12} /><span><strong>Last modified</strong><small>{formatMoment(file.modifiedAt)}</small></span></div>
          <div><Box size={12} /><span><strong>File size</strong><small>{formatSize(file.size)}</small></span></div>
        </> : null}
    </div>}
  </div>
}
