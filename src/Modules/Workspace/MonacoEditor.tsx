import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import type { editor } from 'monaco-editor'
import { workspaceLanguageIdForPath } from './MonacoLanguage'
import type { DeveloperBuildResult } from '../DeveloperServices/DesktopDeveloperServicesClient'
import type { DesktopLanguageToolingPublication } from '../LanguageToolingProviders'
import {
  desktopRoslynDiagnosticsClient,
  type RoslynDocumentSession,
} from '../DeveloperServices/DesktopRoslynDiagnosticsClient'
import {
  applyWorkspaceTextEdits,
  collectWorkspaceTextEdits,
  registerRoslynMonacoProviders,
} from '../DeveloperServices/RoslynMonacoAdapter'
import { workspaceAdapter } from './WorkspaceAdapter'
import { desktopDocumentClient } from './DesktopDocumentClient'
import { desktopDotNetDebuggerController } from '../DeveloperServices/DesktopDotNetDebuggerClient'
import {
  advanceMonacoDiagnosticsState,
  initialMonacoDiagnosticsState,
  monacoDiagnosticOwners,
  monacoProblemSummaryLabel,
  projectMonacoDiagnostics,
  type MonacoDiagnosticsState,
  type MonacoProblemSummary,
} from './MonacoDiagnostics'
import './MonacoEditor.css'

type Props = {
  path: string
  content: string
  onChange?: (content: string) => void
  buildResult?: DeveloperBuildResult | null
  languageToolingResult?: DesktopLanguageToolingPublication | null
  position?: { line: number; column: number; nonce: number } | null
}

type MonacoApi = typeof import('monaco-editor')

const themeName = 'hermes-workbench'
const narrowLayout = '(max-width: 1050px)'

function isNarrowLayout() {
  return typeof window !== 'undefined' && window.matchMedia(narrowLayout).matches
}

function workspaceModelUri(monaco: MonacoApi, path: string) {
  return monaco.Uri.from({ scheme: 'hermes-workspace', path: `/${path.replaceAll('\\', '/')}` })
}

function configureEditor(monaco: MonacoApi, instance: editor.IStandaloneCodeEditor, path: string, content: string, ownedModels: Set<editor.ITextModel>) {
  const uri = workspaceModelUri(monaco, path)
  let model = monaco.editor.getModel(uri)
  if (!model) {
    model = monaco.editor.createModel(content, workspaceLanguageIdForPath(path), uri)
    ownedModels.add(model)
  }
  const previous = instance.getModel()
  if (previous !== model) {
    instance.setModel(model)
    if (previous?.uri.scheme === 'inmemory') previous.dispose()
  }

  monaco.editor.setModelLanguage(model, workspaceLanguageIdForPath(path))
  if (model.getValue() !== content) model.setValue(content)
  instance.updateOptions({ minimap: { enabled: !isNarrowLayout() } })
  instance.layout()
}

function applyDiagnostics(
  monaco: MonacoApi,
  instance: editor.IStandaloneCodeEditor,
  path: string,
  diagnosticsState: MonacoDiagnosticsState,
  liveDiagnosticsState: MonacoDiagnosticsState,
  languageToolingState: MonacoDiagnosticsState,
): MonacoProblemSummary {
  const model = instance.getModel()
  if (!model) return { errors: 0, warnings: 0, information: 0, omitted: 0 }
  const projection = projectMonacoDiagnostics(diagnosticsState, path, model, monaco.MarkerSeverity)
  const liveProjection = projectMonacoDiagnostics(liveDiagnosticsState, path, model, monaco.MarkerSeverity)
  const languageToolingProjection = projectMonacoDiagnostics(languageToolingState, path, model, monaco.MarkerSeverity)
  const selectedMarkers = {
    [monacoDiagnosticOwners[0]]: projection.markers[monacoDiagnosticOwners[0]],
    [monacoDiagnosticOwners[1]]: liveDiagnosticsState.result
      ? liveProjection.markers[monacoDiagnosticOwners[1]]
      : projection.markers[monacoDiagnosticOwners[1]],
    [monacoDiagnosticOwners[2]]: languageToolingProjection.markers[monacoDiagnosticOwners[2]],
    [monacoDiagnosticOwners[3]]: languageToolingProjection.markers[monacoDiagnosticOwners[3]],
  }
  for (const owner of monacoDiagnosticOwners) {
    monaco.editor.setModelMarkers(model, owner, selectedMarkers[owner])
  }
  const allMarkers = monacoDiagnosticOwners.flatMap((owner) => selectedMarkers[owner])
  return {
    errors: allMarkers.filter((marker) => marker.severity === monaco.MarkerSeverity.Error).length,
    warnings: allMarkers.filter((marker) => marker.severity === monaco.MarkerSeverity.Warning).length,
    information: allMarkers.filter((marker) => marker.severity === monaco.MarkerSeverity.Info).length,
    omitted: projection.summary.omitted + liveProjection.summary.omitted + languageToolingProjection.summary.omitted,
  }
}

function defineHermesTheme(monaco: MonacoApi) {
  monaco.editor.defineTheme(themeName, {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '6d778c' },
      { token: 'keyword', foreground: 'b99bff' },
      { token: 'string', foreground: '80cfb7' },
      { token: 'number', foreground: 'e1b778' },
      { token: 'type', foreground: '7cc7f0' },
    ],
    colors: {
      'editor.background': '#0c0f17',
      'editor.foreground': '#d9dfeb',
      'editor.lineHighlightBackground': '#17132a',
      'editor.selectionBackground': '#6654b75c',
      'editor.inactiveSelectionBackground': '#51448652',
      'editorCursor.foreground': '#74d4ba',
      'editorLineNumber.foreground': '#596275',
      'editorLineNumber.activeForeground': '#a99cf0',
      'editorGutter.background': '#0c0f17',
      'editorIndentGuide.background1': '#ffffff0b',
      'editorIndentGuide.activeBackground1': '#8e7be040',
      'editorWhitespace.foreground': '#ffffff10',
      'editorWidget.background': '#121625',
      'editorWidget.border': '#7564bc73',
      'editorHoverWidget.background': '#151928',
      'editorHoverWidget.border': '#7564bc73',
      'editorSuggestWidget.background': '#121625',
      'editorSuggestWidget.border': '#7564bc73',
      'editorError.foreground': '#ef7388',
      'editorWarning.foreground': '#e2b15f',
      'editorInfo.foreground': '#75bfe8',
      'scrollbarSlider.background': '#7060ad59',
      'scrollbarSlider.hoverBackground': '#8b79ce80',
      'scrollbarSlider.activeBackground': '#a18de0a6',
    },
  })
  monaco.editor.setTheme(themeName)
}

export function MonacoEditor({ path, content, onChange, buildResult = null, languageToolingResult = null, position = null }: Props) {
  const hostRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null)
  const monacoRef = useRef<MonacoApi | null>(null)
  const currentFileRef = useRef({ path, content, onChange, position })
  const previousPathRef = useRef<string | null>(null)
  const previousBuildResultRef = useRef<DeveloperBuildResult | null | undefined>(undefined)
  const diagnosticsStateRef = useRef(initialMonacoDiagnosticsState())
  const liveDiagnosticsStateRef = useRef(initialMonacoDiagnosticsState())
  const languageToolingStateRef = useRef(initialMonacoDiagnosticsState())
  const previousLanguageToolingResultRef = useRef<DesktopLanguageToolingPublication | null | undefined>(undefined)
  const languageSessionRef = useRef<(RoslynDocumentSession & { text: string }) | null>(null)
  const languageGenerationRef = useRef(0)
  const languageChangeTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const roslynProvidersRef = useRef<{ dispose(): void } | null>(null)
  const ownedModelsRef = useRef(new Set<editor.ITextModel>())
  const debugDecorationsRef = useRef<editor.IEditorDecorationsCollection | null>(null)
  const [problemSummary, setProblemSummary] = useState<MonacoProblemSummary>({ errors: 0, warnings: 0, information: 0, omitted: 0 })
  const debugSnapshot = useSyncExternalStore(desktopDotNetDebuggerController.subscribe, desktopDotNetDebuggerController.getSnapshot, desktopDotNetDebuggerController.getSnapshot)

  currentFileRef.current = { path, content, onChange, position }
  if (previousBuildResultRef.current !== buildResult) {
    diagnosticsStateRef.current = advanceMonacoDiagnosticsState(diagnosticsStateRef.current, buildResult)
    previousBuildResultRef.current = buildResult
  }
  if (previousLanguageToolingResultRef.current !== languageToolingResult) {
    const projected: DeveloperBuildResult | null = languageToolingResult ? {
      requestId: languageToolingResult.requestId,
      revision: languageToolingResult.revision,
      operation: 'analyze',
      stale: false,
      workspaceRoot: '',
      succeeded: languageToolingResult.result.succeeded,
      wasCancelled: languageToolingResult.result.code === 'cancelled',
      diagnostics: languageToolingResult.result.diagnostics.map((diagnostic) => ({
        filePath: diagnostic.filePath,
        severity: diagnostic.severity,
        code: diagnostic.code,
        message: diagnostic.message,
        source: languageToolingResult.providerId,
        range: {
          start: { line: diagnostic.startLine, column: diagnostic.startColumn },
          end: { line: diagnostic.endLine, column: diagnostic.endColumn },
        },
      })),
      output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
    } : null
    languageToolingStateRef.current = advanceMonacoDiagnosticsState(languageToolingStateRef.current, projected)
    previousLanguageToolingResultRef.current = languageToolingResult
  }

  useEffect(() => {
    let disposed = false
    let mediaQuery: MediaQueryList | undefined
    let updateMinimap: (() => void) | undefined

    void import('monaco-editor').then((monaco) => {
      if (disposed || !hostRef.current) return

      monacoRef.current = monaco
      defineHermesTheme(monaco)
      const instance = monaco.editor.create(hostRef.current, {
        automaticLayout: true,
        contextmenu: true,
        domReadOnly: false,
        fixedOverflowWidgets: true,
        fontFamily: 'Cascadia Code, Consolas, ui-monospace, monospace',
        fontLigatures: true,
        fontSize: 12,
        glyphMargin: true,
        lineHeight: 19,
        lineNumbers: 'on',
        lineNumbersMinChars: 3,
        minimap: { enabled: !isNarrowLayout(), maxColumn: 100, renderCharacters: false, scale: 1 },
        overviewRulerLanes: 0,
        padding: { top: 12, bottom: 12 },
        readOnly: false,
        renderValidationDecorations: 'on',
        renderLineHighlight: 'all',
        roundedSelection: false,
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        stickyScroll: { enabled: false },
        tabSize: 2,
        wordWrap: 'off',
      })

      editorRef.current = instance
      debugDecorationsRef.current = instance.createDecorationsCollection()
      instance.onDidChangeModelContent(() => {
        const value = instance.getValue()
        if (value !== currentFileRef.current.content) currentFileRef.current.onChange?.(value)
      })
      instance.onMouseDown((event) => {
        if (event.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN
          && event.target.type !== monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS) return
        const line = event.target.position?.lineNumber
        if (line) desktopDotNetDebuggerController.toggleBreakpoint(currentFileRef.current.path, line)
      })
      configureEditor(monaco, instance, currentFileRef.current.path, currentFileRef.current.content, ownedModelsRef.current)
      roslynProvidersRef.current = registerRoslynMonacoProviders(monaco, {
        synchronize: synchronizeRoslynDocument,
        applyWorkspaceEdit: applyRoslynWorkspaceEdit,
        ensureWorkspaceModel,
        navigate: (target) => window.dispatchEvent(new CustomEvent('hermes-workspace-navigate', { detail: target })),
      })
      if (currentFileRef.current.position) revealPosition(instance, currentFileRef.current.position)
      setProblemSummary(applyDiagnostics(monaco, instance, currentFileRef.current.path, diagnosticsStateRef.current, liveDiagnosticsStateRef.current, languageToolingStateRef.current))
      previousPathRef.current = currentFileRef.current.path

      if (typeof window !== 'undefined') {
        mediaQuery = window.matchMedia(narrowLayout)
        updateMinimap = () => instance.updateOptions({ minimap: { enabled: !mediaQuery?.matches } })
        mediaQuery.addEventListener('change', updateMinimap)
      }
    })

    return () => {
      disposed = true
      if (mediaQuery && updateMinimap) mediaQuery.removeEventListener('change', updateMinimap)
      roslynProvidersRef.current?.dispose()
      roslynProvidersRef.current = null
      const model = editorRef.current?.getModel()
      if (model && monacoRef.current) {
        for (const owner of monacoDiagnosticOwners) monacoRef.current.editor.setModelMarkers(model, owner, [])
      }
      editorRef.current?.dispose()
      debugDecorationsRef.current = null
      editorRef.current = null
      monacoRef.current = null
      for (const owned of ownedModelsRef.current) owned.dispose()
      ownedModelsRef.current.clear()
    }
  }, [])

  useEffect(() => {
    const monaco = monacoRef.current
    const instance = editorRef.current
    if (!monaco || !instance) return

    configureEditor(monaco, instance, path, content, ownedModelsRef.current)
    if (previousPathRef.current !== path) {
      instance.setPosition({ lineNumber: 1, column: 1 })
      instance.setScrollPosition({ scrollTop: 0, scrollLeft: 0 })
      previousPathRef.current = path
    }
    setProblemSummary(applyDiagnostics(monaco, instance, path, diagnosticsStateRef.current, liveDiagnosticsStateRef.current, languageToolingStateRef.current))
  }, [path, content])

  useEffect(() => {
    const instance = editorRef.current
    if (!instance || !position) return
    revealPosition(instance, position)
  }, [path, position?.nonce])

  useEffect(() => {
    const monaco = monacoRef.current
    const instance = editorRef.current
    if (!monaco || !instance) return
    setProblemSummary(applyDiagnostics(monaco, instance, path, diagnosticsStateRef.current, liveDiagnosticsStateRef.current, languageToolingStateRef.current))
  }, [path, buildResult, languageToolingResult])

  useEffect(() => {
    const collection = debugDecorationsRef.current
    if (!collection) return
    const decorations: editor.IModelDeltaDecoration[] = (debugSnapshot.breakpoints[path] ?? []).map((line) => ({
      range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 },
      options: { isWholeLine: false, glyphMarginClassName: 'workspace-debug-breakpoint', glyphMarginHoverMessage: { value: `Breakpoint at ${path}:${line}` } },
    }))
    if (debugSnapshot.stoppedLocation?.path === path) decorations.push({
      range: { startLineNumber: debugSnapshot.stoppedLocation.line, startColumn: 1, endLineNumber: debugSnapshot.stoppedLocation.line, endColumn: 1 },
      options: { isWholeLine: true, className: 'workspace-debug-stopped-line', glyphMarginClassName: 'workspace-debug-stopped' },
    })
    collection.set(decorations)
  }, [path, debugSnapshot.breakpoints, debugSnapshot.stoppedLocation])

  useEffect(() => {
    const generation = ++languageGenerationRef.current
    liveDiagnosticsStateRef.current = initialMonacoDiagnosticsState()
    languageSessionRef.current = null
    if (languageChangeTimerRef.current) clearTimeout(languageChangeTimerRef.current)
    const refreshMarkers = () => {
      const monaco = monacoRef.current
      const instance = editorRef.current
      if (monaco && instance) setProblemSummary(applyDiagnostics(monaco, instance, path, diagnosticsStateRef.current, liveDiagnosticsStateRef.current, languageToolingStateRef.current))
    }
    refreshMarkers()
    if (workspaceLanguageIdForPath(path) !== 'csharp' || !desktopRoslynDiagnosticsClient.available) return

    const unsubscribe = desktopRoslynDiagnosticsClient.onDiagnostics((published) => {
      const session = languageSessionRef.current
      if (!session || published.sessionId !== session.sessionId || published.documentPath !== session.documentPath
        || published.revision < session.revision || generation !== languageGenerationRef.current) return
      const result: DeveloperBuildResult = {
        requestId: `roslyn:${published.sessionId}`,
        revision: published.revision,
        operation: 'analyze',
        stale: false,
        workspaceRoot: '',
        succeeded: !published.diagnostics.some((diagnostic) => diagnostic.severity === 'error'),
        wasCancelled: false,
        diagnostics: published.diagnostics,
        output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
      }
      liveDiagnosticsStateRef.current = advanceMonacoDiagnosticsState(liveDiagnosticsStateRef.current, result)
      refreshMarkers()
    })

    let disposed = false
    const openedText = currentFileRef.current.content
    void desktopRoslynDiagnosticsClient.open(path, openedText, 1).then((session) => {
      if (disposed || generation !== languageGenerationRef.current) {
        void desktopRoslynDiagnosticsClient.close(session).catch(() => undefined)
        return
      }
      languageSessionRef.current = { ...session, text: openedText }
      const latest = desktopRoslynDiagnosticsClient.latestDiagnostics(session)
      if (latest) {
        const active = languageSessionRef.current
        if (active && latest.revision >= active.revision) {
          const result: DeveloperBuildResult = {
            requestId: `roslyn:${latest.sessionId}`, revision: latest.revision, operation: 'analyze', stale: false,
            workspaceRoot: '', succeeded: !latest.diagnostics.some((diagnostic) => diagnostic.severity === 'error'),
            wasCancelled: false, diagnostics: latest.diagnostics,
            output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
          }
          liveDiagnosticsStateRef.current = advanceMonacoDiagnosticsState(liveDiagnosticsStateRef.current, result)
          refreshMarkers()
        }
      }
      const currentText = currentFileRef.current.content
      if (currentText !== openedText) void changeRoslynDocument(currentText, generation)
    }).catch(() => {
      if (!disposed && generation === languageGenerationRef.current) refreshMarkers()
    })

    return () => {
      disposed = true
      unsubscribe()
      if (languageChangeTimerRef.current) clearTimeout(languageChangeTimerRef.current)
      const session = languageSessionRef.current
      languageSessionRef.current = null
      if (session) void desktopRoslynDiagnosticsClient.close(session).catch(() => undefined)
    }
  }, [path])

  useEffect(() => {
    const session = languageSessionRef.current
    if (!session || session.documentPath !== path || session.text === content) return
    if (languageChangeTimerRef.current) clearTimeout(languageChangeTimerRef.current)
    const generation = languageGenerationRef.current
    languageChangeTimerRef.current = setTimeout(() => void changeRoslynDocument(content, generation), 350)
  }, [content, path])

  async function changeRoslynDocument(nextText: string, generation: number) {
    const current = languageSessionRef.current
    if (!current || generation !== languageGenerationRef.current || current.text === nextText) return
    const previous = current
    const next = { ...current, revision: current.revision + 1, text: nextText }
    languageSessionRef.current = next
    try {
      await desktopRoslynDiagnosticsClient.change(next, nextText, next.revision)
    } catch {
      if (languageSessionRef.current === next) languageSessionRef.current = previous
    }
  }

  async function synchronizeRoslynDocument() {
    const current = languageSessionRef.current
    if (!current || current.documentPath !== currentFileRef.current.path) return null
    if (current.text !== currentFileRef.current.content) await changeRoslynDocument(currentFileRef.current.content, languageGenerationRef.current)
    return languageSessionRef.current
  }

  async function ensureWorkspaceModel(targetPath: string) {
    const monaco = monacoRef.current
    if (!monaco) return null
    const uri = workspaceModelUri(monaco, targetPath)
    const existing = monaco.editor.getModel(uri)
    if (existing) return existing
    if (ownedModelsRef.current.size >= 64) return null
    try {
      const file = await workspaceAdapter.readFile(targetPath)
      const model = monaco.editor.createModel(file.content, workspaceLanguageIdForPath(file.path), uri)
      ownedModelsRef.current.add(model)
      return model
    } catch {
      return null
    }
  }

  async function applyRoslynWorkspaceEdit(edit: unknown) {
    const changes = collectWorkspaceTextEdits(edit)
    if (!changes.size) throw new Error('Roslyn did not return any applicable text edits.')
    const monaco = monacoRef.current
    if (!monaco) throw new Error('The workspace editor is unavailable.')
    const currentPath = currentFileRef.current.path
    const planned: Array<{ path: string; next: string; sha256?: string; current: boolean }> = []
    for (const [targetPath, edits] of changes) {
      const current = targetPath.toLowerCase() === currentPath.toLowerCase()
      if (current) {
        const model = editorRef.current?.getModel()
        if (!model) throw new Error('The active C# document is unavailable.')
        planned.push({ path: targetPath, next: applyWorkspaceTextEdits(model.getValue(), edits), current: true })
      } else {
        const file = await workspaceAdapter.readFile(targetPath)
        planned.push({ path: file.path, next: applyWorkspaceTextEdits(file.content, edits), sha256: file.sha256, current: false })
      }
    }
    for (const item of planned.filter((candidate) => !candidate.current)) {
      await desktopDocumentClient.save(item.path, item.next, item.sha256 ?? '')
      const model = monaco.editor.getModel(workspaceModelUri(monaco, item.path))
      if (model && model.getValue() !== item.next) model.setValue(item.next)
    }
    const current = planned.find((candidate) => candidate.current)
    const model = editorRef.current?.getModel()
    if (current && model && model.getValue() !== current.next) model.setValue(current.next)
  }

  return (
    <div className="workspace-monaco">
      <div ref={hostRef} className="workspace-monaco__host" aria-label="Workspace editor" />
      <div className="workspace-monaco__problems" role="status" aria-live="polite" aria-atomic="true">
        <strong>Problems</strong>
        <span>{monacoProblemSummaryLabel(problemSummary)}</span>
      </div>
    </div>
  )
}

function revealPosition(instance: editor.IStandaloneCodeEditor, requested: { line: number; column: number }) {
  const model = instance.getModel()
  if (!model) return
  const lineNumber = Math.min(Math.max(1, Math.trunc(requested.line)), model.getLineCount())
  const column = Math.min(Math.max(1, Math.trunc(requested.column)), model.getLineMaxColumn(lineNumber))
  const position = { lineNumber, column }
  instance.setPosition(position)
  instance.revealPositionInCenter(position)
  instance.focus()
}
