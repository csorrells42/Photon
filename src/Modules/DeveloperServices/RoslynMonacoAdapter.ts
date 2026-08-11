import type * as Monaco from 'monaco-editor'
import { desktopRoslynLanguageClient, type RoslynLanguageOperation } from './DesktopRoslynLanguageClient'
import type { RoslynDocumentSession } from './DesktopRoslynDiagnosticsClient'

type RecordValue = Record<string, unknown>

export type RoslynMonacoContext = {
  synchronize(): Promise<RoslynDocumentSession | null>
  applyWorkspaceEdit(edit: unknown): Promise<void>
  ensureWorkspaceModel(path: string): Promise<Monaco.editor.ITextModel | null>
  navigate(target: { path: string; line: number; column: number }): void
}

function record(value: unknown): RecordValue | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as RecordValue : null
}

function text(value: unknown, maximum = 16_384) {
  return typeof value === 'string' ? value.slice(0, maximum) : ''
}

function integer(value: unknown) {
  return typeof value === 'number' && Number.isInteger(value) ? value : null
}

function range(value: unknown): Monaco.IRange | null {
  const raw = record(value)
  const start = record(raw?.start)
  const end = record(raw?.end)
  const startLine = integer(start?.line)
  const startCharacter = integer(start?.character)
  const endLine = integer(end?.line)
  const endCharacter = integer(end?.character)
  if (startLine === null || startCharacter === null || endLine === null || endCharacter === null
    || startLine < 0 || startCharacter < 0 || endLine < startLine || endCharacter < 0) return null
  return { startLineNumber: startLine + 1, startColumn: startCharacter + 1, endLineNumber: endLine + 1, endColumn: endCharacter + 1 }
}

function workspacePath(value: unknown) {
  if (typeof value !== 'string') return null
  const normalized = value.replaceAll('\\', '/').replace(/^\/+/, '')
  if (!normalized || normalized.length > 2_048 || normalized.includes('\0') || normalized.split('/').some((part) => !part || part === '.' || part === '..')) return null
  return normalized
}

type ProjectedLocation = { path: string; range: Monaco.IRange }

export function projectedLocations(value: unknown): ProjectedLocation[] {
  const candidates = Array.isArray(value) ? value : value ? [value] : []
  const output: ProjectedLocation[] = []
  for (const candidate of candidates.slice(0, 2_000)) {
    const raw = record(candidate)
    const path = workspacePath(raw?.uri ?? raw?.targetUri)
    const locationRange = range(raw?.range ?? raw?.targetSelectionRange ?? raw?.targetRange)
    if (path && locationRange) output.push({ path, range: locationRange })
  }
  return output
}

function markdown(value: unknown): Monaco.IMarkdownString[] {
  const values = Array.isArray(value) ? value : [value]
  const output: Monaco.IMarkdownString[] = []
  for (const candidate of values.slice(0, 32)) {
    if (typeof candidate === 'string') output.push({ value: candidate.slice(0, 64 * 1024), isTrusted: false, supportHtml: false })
    else {
      const raw = record(candidate)
      const content = text(raw?.value, 64 * 1024)
      if (content) output.push({ value: content, isTrusted: false, supportHtml: false })
    }
  }
  return output
}

function completionKind(monaco: typeof import('monaco-editor'), value: unknown) {
  const kinds = monaco.languages.CompletionItemKind
  const map: Record<number, Monaco.languages.CompletionItemKind> = {
    1: kinds.Text, 2: kinds.Method, 3: kinds.Function, 4: kinds.Constructor, 5: kinds.Field,
    6: kinds.Variable, 7: kinds.Class, 8: kinds.Interface, 9: kinds.Module, 10: kinds.Property,
    11: kinds.Unit, 12: kinds.Value, 13: kinds.Enum, 14: kinds.Keyword, 15: kinds.Snippet,
    16: kinds.Color, 17: kinds.File, 18: kinds.Reference, 19: kinds.Folder, 20: kinds.EnumMember,
    21: kinds.Constant, 22: kinds.Struct, 23: kinds.Event, 24: kinds.Operator, 25: kinds.TypeParameter,
  }
  return map[integer(value) ?? 0] ?? kinds.Text
}

export function completionItems(monaco: typeof import('monaco-editor'), value: unknown, fallbackRange: Monaco.IRange) {
  const raw = record(value)
  const source = Array.isArray(value) ? value : Array.isArray(raw?.items) ? raw.items : []
  const suggestions: Monaco.languages.CompletionItem[] = []
  for (const candidate of source.slice(0, 2_000)) {
    const item = record(candidate)
    const label = text(item?.label, 512)
    if (!item || !label) continue
    const edit = record(item.textEdit)
    const editRange = range(edit?.range) ?? fallbackRange
    const insertText = text(edit?.newText ?? item.insertText ?? label, 64 * 1024)
    suggestions.push({
      label,
      kind: completionKind(monaco, item.kind),
      detail: text(item.detail, 2_048) || undefined,
      documentation: markdown(item.documentation)[0],
      insertText,
      insertTextRules: integer(item.insertTextFormat) === 2 ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
      sortText: text(item.sortText, 512) || undefined,
      filterText: text(item.filterText, 512) || undefined,
      range: editRange,
    })
  }
  return suggestions
}

async function request(
  operation: RoslynLanguageOperation,
  position: Monaco.Position,
  token: Monaco.CancellationToken,
  context: RoslynMonacoContext,
  extra: Partial<{ endLine: number; endCharacter: number; newName: string; includeDeclaration: boolean }> = {},
) {
  const session = await context.synchronize()
  if (!session || token.isCancellationRequested) return null
  const cancellation = new AbortController()
  const subscription = token.onCancellationRequested(() => cancellation.abort())
  try {
    const response = await desktopRoslynLanguageClient.request(session, {
      operation, line: position.lineNumber - 1, character: position.column - 1, ...extra,
    }, cancellation.signal)
    return response.result
  } finally {
    subscription.dispose()
  }
}

function workspaceEdit(value: unknown) {
  const raw = record(value)
  return raw && (record(raw.changes) || Array.isArray(raw.documentChanges)) ? raw : null
}

export function registerRoslynMonacoProviders(
  monaco: typeof import('monaco-editor'),
  context: RoslynMonacoContext,
) {
  const commandId = 'hermes.roslyn.applyWorkspaceEdit'
  const command = monaco.editor.registerCommand(commandId, async (_accessor, edit: unknown) => {
    await context.applyWorkspaceEdit(edit)
  })
  const completion = monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['.', '(', '[', ' ', ':'],
    provideCompletionItems: async (model, position, _completionContext, token) => {
      const result = await request('completion', position, token, context)
      const word = model.getWordUntilPosition(position)
      const fallbackRange: Monaco.IRange = {
        startLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: word.endColumn,
      }
      return { suggestions: completionItems(monaco, result, fallbackRange) }
    },
  })
  const hover = monaco.languages.registerHoverProvider('csharp', {
    provideHover: async (_model, position, token) => {
      const result = record(await request('hover', position, token, context))
      const contents = markdown(result?.contents)
      return contents.length ? { contents, range: range(result?.range) ?? undefined } : null
    },
  })
  const definition = monaco.languages.registerDefinitionProvider('csharp', {
    provideDefinition: async (_model, position, token) => {
      const locations = projectedLocations(await request('definition', position, token, context))
      const projected: Monaco.languages.Location[] = []
      for (const location of locations) {
        const model = await context.ensureWorkspaceModel(location.path)
        if (model) projected.push({ uri: model.uri, range: location.range })
      }
      if (locations.length === 1) context.navigate({ path: locations[0].path, line: locations[0].range.startLineNumber, column: locations[0].range.startColumn })
      return projected
    },
  })
  const references = monaco.languages.registerReferenceProvider('csharp', {
    provideReferences: async (_model, position, referenceContext, token) => {
      const locations = projectedLocations(await request('references', position, token, context, { includeDeclaration: referenceContext.includeDeclaration }))
      const projected: Monaco.languages.Location[] = []
      for (const location of locations) {
        const model = await context.ensureWorkspaceModel(location.path)
        if (model) projected.push({ uri: model.uri, range: location.range })
      }
      return projected
    },
  })
  const rename = monaco.languages.registerRenameProvider('csharp', {
    provideRenameEdits: async (_model, position, newName, token) => {
      const result = await request('rename', position, token, context, { newName })
      const edit = workspaceEdit(result)
      if (!edit) return { edits: [], rejectReason: 'Roslyn did not return a workspace edit for this symbol.' }
      await context.applyWorkspaceEdit(edit)
      return { edits: [] }
    },
    resolveRenameLocation: async (model, position) => {
      const word = model.getWordAtPosition(position)
      return word ? { range: { startLineNumber: position.lineNumber, startColumn: word.startColumn, endLineNumber: position.lineNumber, endColumn: word.endColumn }, text: word.word } : null
    },
  })
  const codeActions = monaco.languages.registerCodeActionProvider('csharp', {
    provideCodeActions: async (_model, selection, actionContext, token) => {
      const result = await request('code-actions', new monaco.Position(selection.startLineNumber, selection.startColumn), token, context, {
        endLine: selection.endLineNumber - 1,
        endCharacter: selection.endColumn - 1,
      })
      const actions: Monaco.languages.CodeAction[] = []
      for (const candidate of (Array.isArray(result) ? result : []).slice(0, 256)) {
        const raw = record(candidate)
        const title = text(raw?.title, 512)
        const edit = workspaceEdit(raw?.edit)
        if (!raw || !title || !edit || raw.disabled) continue
        actions.push({
          title,
          kind: text(raw.kind, 256) || (actionContext.only ?? undefined),
          isPreferred: raw.isPreferred === true,
          command: { id: commandId, title, arguments: [edit] },
        })
      }
      return { actions, dispose() { /* No retained provider payloads. */ } }
    },
  }, { providedCodeActionKinds: ['quickfix', 'refactor', 'source'] })
  return {
    dispose() { completion.dispose(); hover.dispose(); definition.dispose(); references.dispose(); rename.dispose(); codeActions.dispose(); command.dispose() },
  }
}

export function collectWorkspaceTextEdits(value: unknown) {
  const raw = workspaceEdit(value)
  const output = new Map<string, Array<{ range: Monaco.IRange; newText: string }>>()
  const add = (pathValue: unknown, editsValue: unknown) => {
    const path = workspacePath(pathValue)
    if (!path || !Array.isArray(editsValue)) return
    const edits = output.get(path) ?? []
    for (const candidate of editsValue.slice(0, 2_000)) {
      const item = record(candidate)
      const editRange = range(item?.range)
      const newText = text(item?.newText, 4 * 1024 * 1024)
      if (item && editRange && typeof item.newText === 'string') edits.push({ range: editRange, newText })
    }
    if (edits.length) output.set(path, edits)
  }
  const changes = record(raw?.changes)
  if (changes) for (const [path, edits] of Object.entries(changes)) add(path, edits)
  if (Array.isArray(raw?.documentChanges)) {
    for (const change of raw.documentChanges) {
      const item = record(change)
      const document = record(item?.textDocument)
      if (item && document) add(document.uri, item.edits)
    }
  }
  return output
}

export function applyWorkspaceTextEdits(content: string, edits: ReadonlyArray<{ range: Monaco.IRange; newText: string }>) {
  const lineStarts = [0]
  for (let index = 0; index < content.length; index++) if (content.charCodeAt(index) === 10) lineStarts.push(index + 1)
  const offset = (line: number, column: number) => {
    if (!Number.isInteger(line) || !Number.isInteger(column) || line < 1 || line > lineStarts.length || column < 1) return null
    const lineStart = lineStarts[line - 1]
    const lineEnd = line === lineStarts.length ? content.length : lineStarts[line] - 1
    const candidate = lineStart + column - 1
    return candidate <= lineEnd ? candidate : null
  }
  const projected = edits.map((edit) => ({
    start: offset(edit.range.startLineNumber, edit.range.startColumn),
    end: offset(edit.range.endLineNumber, edit.range.endColumn),
    newText: edit.newText,
  }))
  if (projected.some((edit) => edit.start === null || edit.end === null || edit.end < edit.start)) throw new Error('Roslyn returned an edit outside the current document.')
  const ordered = (projected as Array<{ start: number; end: number; newText: string }>).sort((left, right) => right.start - left.start || right.end - left.end)
  for (let index = 1; index < ordered.length; index++) if (ordered[index - 1].start < ordered[index].end) throw new Error('Roslyn returned overlapping workspace edits.')
  let output = content
  for (const edit of ordered) output = output.slice(0, edit.start) + edit.newText + output.slice(edit.end)
  return output
}
