import {
  HERMES_COMPOSER_LIMITS,
  HermesComposerInputError,
  type HermesCommandOption,
  type HermesCompletionCatalogs,
  type HermesCompletionInsertion,
  type HermesCompletionOption,
  type HermesCompletionSession,
  type HermesMentionOption,
} from './types'

function boundedText(value: string, maximum: number, field: string) {
  if (typeof value !== 'string' || value.length === 0 || value.length > maximum || /[\u0000-\u001f\u007f]/u.test(value)) {
    throw new HermesComposerInputError(`${field} is invalid or exceeds its bound.`)
  }
  return value
}

function description(value: string | undefined) {
  if (value === undefined) return undefined
  return boundedText(value, HERMES_COMPOSER_LIMITS.descriptionCharacters, 'Completion description')
}

function normalizeMention(option: HermesMentionOption): HermesCompletionOption {
  return Object.freeze({
    kind: 'mention' as const,
    id: boundedText(option.id, HERMES_COMPOSER_LIMITS.optionIdCharacters, 'Mention ID'),
    value: boundedText(option.label, HERMES_COMPOSER_LIMITS.optionLabelCharacters, 'Mention label'),
    description: description(option.description),
  })
}

function normalizeCommand(option: HermesCommandOption): HermesCompletionOption {
  const name = boundedText(option.name, HERMES_COMPOSER_LIMITS.optionLabelCharacters, 'Command name')
  if (/\s/u.test(name) || name.startsWith('/')) throw new HermesComposerInputError('Command names cannot contain whitespace or a leading slash.')
  return Object.freeze({
    kind: 'command' as const,
    id: boundedText(option.id, HERMES_COMPOSER_LIMITS.optionIdCharacters, 'Command ID'),
    value: name,
    description: description(option.description),
    usage: option.usage === undefined
      ? undefined
      : boundedText(option.usage, HERMES_COMPOSER_LIMITS.descriptionCharacters, 'Command usage'),
  })
}

function normalizeCatalog<T>(items: readonly T[] | undefined, convert: (item: T) => HermesCompletionOption) {
  if (!items) return []
  if (items.length > HERMES_COMPOSER_LIMITS.catalogItems) {
    throw new HermesComposerInputError(`Completion catalogs support up to ${HERMES_COMPOSER_LIMITS.catalogItems} items.`)
  }
  const seen = new Set<string>()
  return items.map((item) => {
    const normalized = convert(item)
    const key = `${normalized.kind}:${normalized.id}`
    if (seen.has(key)) throw new HermesComposerInputError('Completion catalog IDs must be unique within a catalog.')
    seen.add(key)
    return normalized
  })
}

export function validateCompletionCatalogs(catalogs: HermesCompletionCatalogs) {
  normalizeCatalog(catalogs.mentions, normalizeMention)
  normalizeCatalog(catalogs.commands, normalizeCommand)
}

export function createCompletionSession(
  text: string,
  cursor: number,
  catalogs: HermesCompletionCatalogs,
  selectedIndex = 0,
): HermesCompletionSession | null {
  if (text.length > HERMES_COMPOSER_LIMITS.promptCharacters || cursor < 0 || cursor > text.length) return null
  const beforeCursor = text.slice(0, cursor)
  const match = /(^|\s)([@/])([^\s@/]*)$/u.exec(beforeCursor)
  if (!match) return null

  const trigger = match[2] as '@' | '/'
  const query = match[3]
  const kind = trigger === '@' ? 'mention' : 'command'
  const catalog = trigger === '@'
    ? normalizeCatalog(catalogs.mentions, normalizeMention)
    : normalizeCatalog(catalogs.commands, normalizeCommand)
  const normalizedQuery = query.toLocaleLowerCase()
  const options = catalog
    .filter((option) => option.value.toLocaleLowerCase().includes(normalizedQuery))
    .sort((left, right) => {
      const leftStarts = left.value.toLocaleLowerCase().startsWith(normalizedQuery)
      const rightStarts = right.value.toLocaleLowerCase().startsWith(normalizedQuery)
      return leftStarts === rightStarts ? left.value.localeCompare(right.value) : leftStarts ? -1 : 1
    })
    .slice(0, HERMES_COMPOSER_LIMITS.completionItems)

  if (options.length === 0) return null
  const replaceStart = cursor - query.length - 1
  return Object.freeze({
    kind,
    trigger,
    query,
    replaceStart,
    replaceEnd: cursor,
    options: Object.freeze(options),
    selectedIndex: Math.max(0, Math.min(selectedIndex, options.length - 1)),
  })
}

export type HermesCompletionKeyResult =
  | { readonly action: 'navigate'; readonly selectedIndex: number }
  | { readonly action: 'select'; readonly option: HermesCompletionOption }
  | { readonly action: 'close' }
  | { readonly action: 'none' }

export function handleCompletionKey(session: HermesCompletionSession, key: string): HermesCompletionKeyResult {
  switch (key) {
    case 'ArrowDown': return { action: 'navigate', selectedIndex: (session.selectedIndex + 1) % session.options.length }
    case 'ArrowUp': return { action: 'navigate', selectedIndex: (session.selectedIndex - 1 + session.options.length) % session.options.length }
    case 'Home': return { action: 'navigate', selectedIndex: 0 }
    case 'End': return { action: 'navigate', selectedIndex: session.options.length - 1 }
    case 'Enter':
    case 'Tab': return { action: 'select', option: session.options[session.selectedIndex] }
    case 'Escape': return { action: 'close' }
    default: return { action: 'none' }
  }
}

export function applyCompletion(
  text: string,
  session: HermesCompletionSession,
  option: HermesCompletionOption,
): HermesCompletionInsertion {
  if (!session.options.some((candidate) => candidate.kind === option.kind && candidate.id === option.id)) {
    throw new HermesComposerInputError('The selected completion is not part of the active session.')
  }
  const replacement = `${session.trigger}${option.value} `
  const next = `${text.slice(0, session.replaceStart)}${replacement}${text.slice(session.replaceEnd)}`
  if (next.length > HERMES_COMPOSER_LIMITS.promptCharacters) {
    throw new HermesComposerInputError('The completed prompt exceeds the prompt bound.')
  }
  return { text: next, cursor: session.replaceStart + replacement.length }
}
