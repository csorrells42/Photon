import { useCallback, useEffect, useState } from 'react'
import {
  Braces,
  ChevronDown,
  ChevronRight,
  Code2,
  FileText,
  Folder,
  FolderOpen,
  LoaderCircle,
  RefreshCw,
} from 'lucide-react'
import { workspaceAdapter } from './WorkspaceAdapter'
import type { WorkspaceDirectory, WorkspaceEntry } from './WorkspaceAdapter'
import { WorkspaceExplorerDetails } from './WorkspaceExplorerDetails'

type Props = {
  selectedPath?: string | null
  onOpen: (entry: WorkspaceEntry) => void
}

function FileGlyph({ name }: { name: string }) {
  const extension = name.split('.').pop()?.toLowerCase()
  if (extension === 'json' || extension === 'yml' || extension === 'yaml') return <Braces size={13} />
  if (['ts', 'tsx', 'js', 'jsx', 'cs', 'py', 'css', 'html'].includes(extension ?? '')) return <Code2 size={13} />
  return <FileText size={13} />
}

export function WorkspaceExplorer({ selectedPath, onOpen }: Props) {
  const [directories, setDirectories] = useState<Record<string, WorkspaceDirectory>>({})
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(['']))
  const [loading, setLoading] = useState<Set<string>>(() => new Set(['']))
  const [errors, setErrors] = useState<Record<string, string>>({})

  const loadDirectory = useCallback(async (path: string, refresh = false) => {
    if (!refresh && directories[path]) return
    setLoading((current) => new Set(current).add(path))
    setErrors((current) => {
      const next = { ...current }
      delete next[path]
      return next
    })
    try {
      const result = await workspaceAdapter.readDirectory(path)
      setDirectories((current) => ({ ...current, [path]: result }))
    } catch (reason) {
      setErrors((current) => ({
        ...current,
        [path]: reason instanceof Error ? reason.message : 'Could not read this folder.',
      }))
    } finally {
      setLoading((current) => {
        const next = new Set(current)
        next.delete(path)
        return next
      })
    }
  }, [directories])

  useEffect(() => { void loadDirectory('') }, [loadDirectory])

  function toggle(entry: WorkspaceEntry) {
    const open = !expanded.has(entry.path)
    setExpanded((current) => {
      const next = new Set(current)
      if (open) next.add(entry.path)
      else next.delete(entry.path)
      return next
    })
    if (open) void loadDirectory(entry.path)
  }

  function renderDirectory(path: string, depth: number) {
    const directory = directories[path]
    if (!directory) {
      if (loading.has(path)) return <div className="workspace-tree-state" style={{ paddingLeft: 12 + depth * 13 }}><LoaderCircle className="spin" size={12} /> Loading…</div>
      if (errors[path]) return <div className="workspace-tree-state error" style={{ paddingLeft: 12 + depth * 13 }}>{errors[path]}</div>
      return null
    }

    return directory.entries.map((entry) => {
      const isDirectory = entry.kind === 'directory'
      const isOpen = expanded.has(entry.path)
      return (
        <div key={entry.path}>
          <button
            type="button"
            className={`workspace-tree-row ${selectedPath === entry.path ? 'selected' : ''}`}
            style={{ paddingLeft: 8 + depth * 13 }}
            onClick={() => isDirectory ? toggle(entry) : onOpen(entry)}
          >
            {isDirectory ? (isOpen ? <ChevronDown size={13} /> : <ChevronRight size={13} />) : <span className="tree-spacer" />}
            <span className={`workspace-glyph ${isDirectory ? 'folder' : ''}`}>
              {isDirectory ? (isOpen ? <FolderOpen size={13} /> : <Folder size={13} />) : <FileGlyph name={entry.name} />}
            </span>
            <span>{entry.name}</span>
          </button>
          {isDirectory && isOpen && renderDirectory(entry.path, depth + 1)}
        </div>
      )
    })
  }

  const root = directories['']
  return (
    <aside className="explorer workspace-explorer">
      <div className="panel-label">EXPLORER <button type="button" aria-label="Refresh workspace" onClick={() => void loadDirectory('', true)}><RefreshCw size={12} /></button></div>
      <div className="tree-heading"><ChevronDown size={14} /> {(root?.root || 'PHOS WORKSPACE').toUpperCase()}</div>
      <div className="workspace-tree">{renderDirectory('', 0)}</div>
      <WorkspaceExplorerDetails selectedPath={selectedPath} />
    </aside>
  )
}
