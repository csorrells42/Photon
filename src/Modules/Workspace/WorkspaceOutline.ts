export type WorkspaceSymbolKind = 'namespace' | 'type' | 'class' | 'interface' | 'enum' | 'method' | 'function'

export type WorkspaceSymbol = {
  name: string
  kind: WorkspaceSymbolKind
  line: number
  depth: number
}

const MAXIMUM_SYMBOLS = 250
const MAXIMUM_LINE_CHARACTERS = 8_192
const IDENTIFIER = '[A-Za-z_$][A-Za-z0-9_$]*'

function indentation(line: string) {
  const prefix = line.match(/^\s*/u)?.[0] ?? ''
  return Math.min(8, Math.floor(prefix.replaceAll('\t', '  ').length / 2))
}

function matchSymbol(line: string, path: string): Omit<WorkspaceSymbol, 'line' | 'depth'> | null {
  const extension = path.split('.').pop()?.toLowerCase() ?? ''
  let match: RegExpMatchArray | null

  if (extension === 'py') {
    match = line.match(/^\s*(?:async\s+)?def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(/u)
    if (match) return { name: match[1], kind: 'function' }
    match = line.match(/^\s*class\s+([A-Za-z_][A-Za-z0-9_]*)\b/u)
    return match ? { name: match[1], kind: 'class' } : null
  }

  match = line.match(/^\s*(?:export\s+)?(?:declare\s+)?(?:abstract\s+)?(class|interface|enum|type|namespace)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b/u)
  if (match) {
    const kind = match[1] === 'namespace' ? 'namespace' : match[1] === 'type' ? 'type' : match[1] as WorkspaceSymbolKind
    return { name: match[2], kind }
  }

  match = line.match(new RegExp(`^\\s*(?:export\\s+)?(?:default\\s+)?(?:async\\s+)?function\\s+(${IDENTIFIER})\\s*\\(`, 'u'))
  if (match) return { name: match[1], kind: 'function' }
  match = line.match(new RegExp(`^\\s*(?:export\\s+)?(?:const|let|var)\\s+(${IDENTIFIER})\\s*=\\s*(?:async\\s*)?(?:\\([^)]*\\)|${IDENTIFIER})\\s*=>`, 'u'))
  if (match) return { name: match[1], kind: 'function' }

  if (['cs', 'java'].includes(extension)) {
    match = line.match(/^\s*(?:public|private|protected|internal|static|sealed|abstract|partial|final|synchronized|virtual|override|async|new|unsafe|extern|\s)+\s*(?:class|interface|enum|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)\b/u)
    if (match) return { name: match[1], kind: 'class' }
    match = line.match(/^\s*(?:(?:public|private|protected|internal|static|final|virtual|override|async|synchronized|abstract|sealed|new|unsafe|extern)\s+)+(?:[A-Za-z_$][A-Za-z0-9_$<>,.?\[\]\s:]*)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\([^;]*\)\s*(?:\{|=>|throws\b)/u)
    if (match && !['if', 'for', 'foreach', 'while', 'switch', 'catch'].includes(match[1])) return { name: match[1], kind: 'method' }
  }
  return null
}

export function extractWorkspaceOutline(path: string, content: string): readonly WorkspaceSymbol[] {
  const symbols: WorkspaceSymbol[] = []
  const lines = content.split(/\r?\n/u)
  for (let index = 0; index < lines.length && symbols.length < MAXIMUM_SYMBOLS; index += 1) {
    const line = lines[index]
    if (line.length > MAXIMUM_LINE_CHARACTERS) continue
    const symbol = matchSymbol(line, path)
    if (symbol) symbols.push({ ...symbol, line: index + 1, depth: indentation(line) })
  }
  return symbols
}
