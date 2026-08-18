import { useEffect, useState } from 'react'
import { Braces, FileCode2, LoaderCircle, SplitSquareHorizontal, X } from 'lucide-react'
import { MonacoEditor } from './MonacoEditor'
import { workspaceLanguageLabelForPath } from './MonacoLanguage'
import { workspaceAdapter } from './WorkspaceAdapter'
import type { WorkspaceFile } from './WorkspaceAdapter'
import { TerminalDock } from '../NativeTerminal/TerminalDock'
import type { DeveloperBuildResult } from '../DeveloperServices/DesktopDeveloperServicesClient'
import type { DesktopLanguageToolingPublication } from '../LanguageToolingProviders'
import { desktopDocumentClient } from './DesktopDocumentClient'
import type { NativeTerminalCommandRequest } from '../NativeTerminal/DesktopHostTerminalClient'

type Props = {
  path?: string | null
  onClose?: () => void
  buildResult?: DeveloperBuildResult | null
  languageToolingResult?: DesktopLanguageToolingPublication | null
  onOpenWorkspacePath?: (path: string) => void
  selection?: { path: string; line: number; column: number; nonce: number } | null
  terminalCommandRequest?: NativeTerminalCommandRequest | null
  onTerminalCommandHandled?: (nonce: number) => void
}

export function WorkspaceEditor({ path, onClose, buildResult, languageToolingResult, onOpenWorkspacePath, selection = null, terminalCommandRequest = null, onTerminalCommandHandled }: Props) {
  const [file, setFile] = useState<WorkspaceFile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [draft, setDraft] = useState('')
  const [dirty, setDirty] = useState(false)
  const [saving, setSaving] = useState(false)
  const [documentStatus, setDocumentStatus] = useState('')

  useEffect(() => {
    let cancelled = false
    if (!path) { setFile(null); setError(null); return }
    setLoading(true)
    setError(null)
    void workspaceAdapter.readFile(path)
      .then((result) => { if (!cancelled) { setFile(result); setDraft(result.content); setDirty(false); setDocumentStatus('') } })
      .catch((reason) => { if (!cancelled) { setFile(null); setError(reason instanceof Error ? reason.message : 'Could not open file.') } })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [path])

  const segments = path?.split('/') ?? []
  const filename = segments.at(-1) ?? ''
  const language = path ? workspaceLanguageLabelForPath(path) : 'Plain Text'

  async function save(saveAs = false) {
    if (!file || saving) return
    setSaving(true)
    setDocumentStatus('')
    try {
      const result = saveAs
        ? await desktopDocumentClient.saveAs(file.path, draft)
        : await desktopDocumentClient.save(file.path, draft, file.sha256)
      if (result.cancelled) return
      const nextPath = result.path ?? file.path
      setFile((current) => current ? { ...current, path: nextPath, content: draft, sha256: result.sha256 ?? current.sha256 } : current)
      setDirty(false)
      setDocumentStatus(`Saved ${nextPath}`)
      if (nextPath !== path) onOpenWorkspacePath?.(nextPath)
    } catch (reason) {
      setDocumentStatus(reason instanceof Error ? reason.message : 'The file could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  function closeEditor() {
    if (dirty && !window.confirm(`Close ${filename} without saving your changes?`)) return
    onClose?.()
  }

  useEffect(() => {
    const onCommand = (event: Event) => {
      const command = (event as CustomEvent<string>).detail
      if (command === 'save') void save(false)
      else if (command === 'saveAs') void save(true)
      else if (command === 'close') closeEditor()
    }
    window.addEventListener('photos-file-command', onCommand)
    return () => window.removeEventListener('photos-file-command', onCommand)
  })

  return (
    <main className="editor-area workspace-editor">
      <div className="editor-tabs">
        {path ? <div className="editor-tab active"><span className="tsx">{language.slice(0, 2).toUpperCase()}</span> {dirty ? '● ' : ''}{filename} <button type="button" aria-label={`Close ${filename}`} onClick={closeEditor}><X size={13} /></button></div> : <div className="editor-tab active"><FileCode2 size={13} /> Welcome</div>}
        <span className="tab-spacer" />
        <button aria-label="Split editor" title="Split editor is not available yet" disabled><SplitSquareHorizontal size={16} /></button>
        <button aria-label="Editor options" title="Editor options are not available yet" disabled>•••</button>
      </div>
      <div className="breadcrumbs">
        {segments.map((segment, index) => <span key={`${segment}-${index}`}>{index > 0 && ' › '}{segment}</span>)}
        {path && <Braces size={13} />}
      </div>
      <div className="code-editor">
        {loading ? (
          <div className="editor-empty"><LoaderCircle className="spin" size={22} /><strong>Opening {filename}…</strong></div>
        ) : error ? (
          <div className="editor-empty error"><FileCode2 size={24} /><strong>Preview unavailable</strong><p>{error}</p></div>
        ) : file ? (
          <MonacoEditor path={file.path} content={draft} onChange={(content) => { setDraft(content); setDirty(content !== file.content) }} buildResult={buildResult} languageToolingResult={languageToolingResult} position={selection?.path === file.path ? selection : null} />
        ) : (
          <div className="editor-empty"><FileCode2 size={30} /><strong>Open a file from Explorer</strong><p>The Workbench now reads the real project instead of displaying sample code.</p></div>
        )}
      </div>
      {documentStatus && <div className="document-operation-status" role="status">{saving ? 'Saving…' : documentStatus}</div>}
      <TerminalDock buildResult={buildResult} commandRequest={terminalCommandRequest} onCommandHandled={onTerminalCommandHandled} onOpenWorkspacePath={onOpenWorkspacePath} />
    </main>
  )
}
