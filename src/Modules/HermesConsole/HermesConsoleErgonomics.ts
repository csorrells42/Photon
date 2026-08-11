export const CONSOLE_FIND_QUERY_MAX_LENGTH = 256
export const CONSOLE_FIND_MAX_MATCHES = 200
export const CONSOLE_FIND_LINE_MAX_LENGTH = 16 * 1024
export const CONSOLE_FIND_TOTAL_MAX_LENGTH = 256 * 1024
export const CONSOLE_COPY_MAX_LENGTH = 64 * 1024
export const CONSOLE_BOTTOM_THRESHOLD = 96

export type ConsoleTranscriptLine = { id: string; text: string }
export type ConsoleFindMatch = { lineId: string; start: number; end: number }
export type ConsoleScrollMetrics = { scrollHeight: number; scrollTop: number; clientHeight: number }
export type ConsoleFindResult = { matches: ConsoleFindMatch[]; truncated: boolean }

export function normalizeConsoleFindQuery(query: string) {
  return query.slice(0, CONSOLE_FIND_QUERY_MAX_LENGTH)
}

export function findConsoleMatches(lines: readonly ConsoleTranscriptLine[], rawQuery: string): ConsoleFindMatch[] {
  return findConsoleMatchResult(lines, rawQuery).matches
}

export function findConsoleMatchResult(lines: readonly ConsoleTranscriptLine[], rawQuery: string): ConsoleFindResult {
  const query = normalizeConsoleFindQuery(rawQuery).toLowerCase()
  if (!query) return { matches: [], truncated: false }
  const matches: ConsoleFindMatch[] = []
  let remainingCharacters = CONSOLE_FIND_TOTAL_MAX_LENGTH
  let truncated = false
  for (const line of lines) {
    if (remainingCharacters <= 0) return { matches, truncated: true }
    const scannedLength = Math.min(line.text.length, CONSOLE_FIND_LINE_MAX_LENGTH, remainingCharacters)
    const text = line.text.slice(0, scannedLength).toLowerCase()
    remainingCharacters -= scannedLength
    if (scannedLength < line.text.length) truncated = true
    let start = text.indexOf(query)
    while (start !== -1) {
      matches.push({ lineId: line.id, start, end: start + query.length })
      if (matches.length === CONSOLE_FIND_MAX_MATCHES) return { matches, truncated: true }
      start = text.indexOf(query, start + query.length)
    }
  }
  return { matches, truncated }
}

export function nextConsoleMatchIndex(current: number, count: number, direction: 'previous' | 'next') {
  if (count <= 0) return -1
  if (current < 0 || current >= count) return direction === 'previous' ? count - 1 : 0
  return (current + (direction === 'previous' ? count - 1 : 1)) % count
}

export function consoleDistanceFromBottom(metrics: ConsoleScrollMetrics) {
  return Math.max(0, metrics.scrollHeight - metrics.scrollTop - metrics.clientHeight)
}

export function isConsoleNearBottom(metrics: ConsoleScrollMetrics, threshold = CONSOLE_BOTTOM_THRESHOLD) {
  return consoleDistanceFromBottom(metrics) <= Math.max(0, threshold)
}

export function shouldFollowConsoleUpdate(isFollowing: boolean) {
  return isFollowing
}

export function boundedConsoleCopyText(text: string) {
  return text.slice(0, CONSOLE_COPY_MAX_LENGTH)
}

export async function copyConsoleText(text: string, writeText: (value: string) => Promise<void>) {
  const boundedText = boundedConsoleCopyText(text)
  try {
    await writeText(boundedText)
    return { ok: true as const, text: boundedText }
  } catch {
    return { ok: false as const, text: '' }
  }
}

export function consoleViewLinesAfterClear<T extends { id: string }>(lines: readonly T[], clearedAfterId: string | null) {
  if (!clearedAfterId) return lines
  const clearIndex = lines.findIndex((line) => line.id === clearedAfterId)
  return clearIndex === -1 ? lines : lines.slice(clearIndex + 1)
}

export function nextConsoleViewClearBoundary<T extends { id: string }>(visibleLines: readonly T[], previousBoundary: string | null) {
  return visibleLines[visibleLines.length - 1]?.id ?? previousBoundary
}

export function isConsoleSelectionContained(
  output: Pick<Node, 'contains'> | null,
  selection: Pick<Selection, 'isCollapsed' | 'anchorNode' | 'focusNode'> | null,
) {
  return Boolean(
    output
    && selection
    && !selection.isCollapsed
    && selection.anchorNode
    && selection.focusNode
    && output.contains(selection.anchorNode)
    && output.contains(selection.focusNode),
  )
}
