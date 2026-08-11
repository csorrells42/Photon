import {
  HERMES_PROFILE_RUNTIME_CONTRACT,
  type AdapterResponse,
  type ConfigurationFieldSchema,
  type ConfigurationValue,
  type CorrelationId,
  type EditableProfileDocument,
  type ProfileId,
  type ProfileImportData,
  type ProfileImportInspection,
  type ProfileRuntimeSnapshot,
  type ProviderRuntimeStatus,
  type RequestContext,
} from './contracts'

export const PROFILE_RUNTIME_LIMITS = {
  importBytes: 256 * 1024,
  profiles: 64,
  documents: 3,
  documentCharacters: 64 * 1024,
  fields: 128,
  fieldText: 4 * 1024,
  providers: 64,
  permissions: 64,
  terminalBackends: 32,
} as const

const SECRET_KEY = /(?:secret|token|password|api[_-]?key|credential(?:value)?|private[_-]?key|authorization|cookie)/i
const CONTROL_CHARACTERS = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Expected an object.')
  return value as Record<string, unknown>
}

function safeText(value: unknown, label: string, maximum: number, required = true): string {
  if (typeof value !== 'string') throw new Error(`${label} must be text.`)
  const result = value.replace(CONTROL_CHARACTERS, '')
  if (required && !result.trim()) throw new Error(`${label} is required.`)
  if (result.length > maximum) throw new Error(`${label} exceeds the ${maximum.toLocaleString()} character limit.`)
  return result
}

export function assertSafeProfileId(value: unknown, label = 'Profile identity'): asserts value is ProfileId {
  if (typeof value !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(value)) {
    throw new Error(`${label} is malformed.`)
  }
}

export function createRequestContext(profileId: ProfileId, correlationId: CorrelationId, expectedRevision?: number): RequestContext {
  assertSafeProfileId(profileId)
  if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(correlationId)) throw new Error('Correlation identity is malformed.')
  return { contract: HERMES_PROFILE_RUNTIME_CONTRACT, profileId, correlationId, expectedRevision }
}

export function assertMatchingResponse<T>(response: AdapterResponse<T>, request: RequestContext): AdapterResponse<T> {
  if (response.contract !== HERMES_PROFILE_RUNTIME_CONTRACT) throw new Error('The adapter returned an incompatible contract version.')
  if (response.profileId !== request.profileId) throw new Error('The adapter returned a cross-profile result.')
  if (response.correlationId !== request.correlationId) throw new Error('The adapter returned a mismatched correlation result.')
  if (!Number.isSafeInteger(response.revision) || response.revision < 0) throw new Error('The adapter returned a malformed revision.')
  return response
}

function findSecretPath(value: unknown, path = '$', depth = 0): string | null {
  if (depth > 12) return `${path} (nesting exceeds 12 levels)`
  if (Array.isArray(value)) {
    if (value.length > 512) return `${path} (array exceeds 512 entries)`
    for (let index = 0; index < value.length; index += 1) {
      const found = findSecretPath(value[index], `${path}[${index}]`, depth + 1)
      if (found) return found
    }
    return null
  }
  if (!value || typeof value !== 'object') return null
  const entries = Object.entries(value as Record<string, unknown>)
  if (entries.length > PROFILE_RUNTIME_LIMITS.fields) return `${path} (object exceeds ${PROFILE_RUNTIME_LIMITS.fields} fields)`
  for (const [key, child] of entries) {
    if (SECRET_KEY.test(key)) return `${path}.${key}`
    const found = findSecretPath(child, `${path}.${key}`, depth + 1)
    if (found) return found
  }
  return null
}

function configurationValue(value: unknown, label: string): ConfigurationValue {
  if (value === null || typeof value === 'boolean') return value
  if (typeof value === 'number' && Number.isSafeInteger(value)) return value
  if (typeof value === 'string') return safeText(value, label, PROFILE_RUNTIME_LIMITS.fieldText, false)
  throw new Error(`${label} must be null, text, a boolean, or a safe integer.`)
}

function importIntent(value: unknown): ProfileImportData['intent'] {
  const source = record(value)
  const optionalText = (key: string, maximum: number) => source[key] === null || source[key] === undefined
    ? null
    : safeText(source[key], `intent.${key}`, maximum, false) || null
  return {
    modelId: optionalText('modelId', 256),
    projectPath: optionalText('projectPath', 2_048),
    worktreePath: optionalText('worktreePath', 2_048),
    note: safeText(source.note ?? '', 'intent.note', 2_048, false),
  }
}

export function inspectProfileImport(text: string): ProfileImportInspection {
  const byteCount = new TextEncoder().encode(text).byteLength
  if (byteCount === 0) throw new Error('Import text is empty.')
  if (byteCount > PROFILE_RUNTIME_LIMITS.importBytes) throw new Error(`Import text exceeds ${PROFILE_RUNTIME_LIMITS.importBytes.toLocaleString()} bytes.`)

  let parsed: unknown
  try { parsed = JSON.parse(text) }
  catch { throw new Error('Import text is not valid JSON.') }

  const secretPath = findSecretPath(parsed)
  if (secretPath) throw new Error(`Import contains a forbidden secret-bearing or oversized field at ${secretPath}.`)
  const source = record(parsed)
  if (source.format !== HERMES_PROFILE_RUNTIME_CONTRACT) throw new Error('Import format is missing or incompatible.')

  const rawDocuments = record(source.documents ?? {})
  const documents: ProfileImportData['documents'] = {}
  const allowedDocuments: EditableProfileDocument['kind'][] = ['persona', 'soul', 'context']
  for (const kind of allowedDocuments) {
    if (rawDocuments[kind] !== undefined) documents[kind] = safeText(rawDocuments[kind], kind, PROFILE_RUNTIME_LIMITS.documentCharacters, false)
  }

  const rawConfiguration = record(source.configuration ?? {})
  if (Object.keys(rawConfiguration).length > PROFILE_RUNTIME_LIMITS.fields) throw new Error('Import configuration has too many fields.')
  const configuration = Object.fromEntries(Object.entries(rawConfiguration).map(([key, value]) => {
    if (!/^[A-Za-z][A-Za-z0-9._-]{0,127}$/.test(key)) throw new Error(`Configuration key ${key || '(empty)'} is malformed.`)
    return [key, configurationValue(value, `configuration.${key}`)]
  }))

  const data: ProfileImportData = {
    format: HERMES_PROFILE_RUNTIME_CONTRACT,
    name: safeText(source.name, 'Profile name', 128),
    documents,
    intent: importIntent(source.intent ?? {}),
    configuration,
  }
  const warnings: string[] = []
  for (const key of Object.keys(source)) {
    if (!['format', 'name', 'documents', 'intent', 'configuration'].includes(key)) warnings.push(`Ignored unsupported top-level field: ${key}`)
  }
  return { data, warnings: warnings.slice(0, 32), characterCount: text.length }
}

export function validateConfigurationValues(
  schema: ConfigurationFieldSchema[],
  values: Record<string, ConfigurationValue>,
): Record<string, ConfigurationValue> {
  if (schema.length > PROFILE_RUNTIME_LIMITS.fields) throw new Error('Configuration schema exceeds the supported field count.')
  const schemaByKey = new Map(schema.map((field) => [field.key, field]))
  const result: Record<string, ConfigurationValue> = {}
  for (const [key, value] of Object.entries(values)) {
    const field = schemaByKey.get(key)
    if (!field) throw new Error(`Configuration field ${key} is not declared by the schema.`)
    if (field.disposition !== 'manageable') throw new Error(`${field.label} is ${field.disposition} and cannot be changed here.`)
    if (value === null) {
      if (field.required) throw new Error(`${field.label} is required.`)
      result[key] = null
    } else if (field.kind === 'boolean') {
      if (typeof value !== 'boolean') throw new Error(`${field.label} must be a boolean.`)
      result[key] = value
    } else if (field.kind === 'integer') {
      if (!Number.isSafeInteger(value)) throw new Error(`${field.label} must be a safe integer.`)
      const numberValue = value as number
      if (field.minimum !== undefined && numberValue < field.minimum) throw new Error(`${field.label} is below its minimum.`)
      if (field.maximum !== undefined && numberValue > field.maximum) throw new Error(`${field.label} is above its maximum.`)
      result[key] = numberValue
    } else {
      if (typeof value !== 'string') throw new Error(`${field.label} must be text.`)
      const maximum = Math.min(field.maximumLength ?? PROFILE_RUNTIME_LIMITS.fieldText, PROFILE_RUNTIME_LIMITS.fieldText)
      if (value.length > maximum) throw new Error(`${field.label} exceeds its character limit.`)
      if (field.kind === 'enum' && !field.options?.includes(value)) throw new Error(`${field.label} is not an allowed option.`)
      result[key] = value
    }
  }
  for (const field of schema) if (field.required && !(field.key in result)) throw new Error(`${field.label} is required.`)
  return result
}

export function createSecretFreeExport(snapshot: ProfileRuntimeSnapshot): string {
  const profile = snapshot.profiles.find((item) => item.id === snapshot.profileId)
  if (!profile) throw new Error('The active profile is missing from the snapshot.')
  const data: ProfileImportData = {
    format: HERMES_PROFILE_RUNTIME_CONTRACT,
    name: profile.name,
    documents: Object.fromEntries(snapshot.documents.map((document) => [document.kind, document.text])),
    intent: { ...snapshot.intent },
    configuration: { ...snapshot.configuration },
  }
  return JSON.stringify(data, null, 2)
}

export function sanitizeProviderOrigin(value: string | null): string | null {
  if (!value) return null
  try {
    const parsed = new URL(value)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
    return parsed.origin
  } catch { return null }
}

export function assertNoSecretRoundTrip(providers: ProviderRuntimeStatus[]): void {
  const providerKeys = new Set(['id', 'label', 'oauth', 'credentialPool', 'configuredCredentialSlots', 'customEndpoint', 'disposition', 'detail'])
  const endpointKeys = new Set(['state', 'origin'])
  for (const [index, provider] of providers.entries()) {
    const extraProviderKey = Object.keys(provider).find((key) => !providerKeys.has(key))
    if (extraProviderKey) throw new Error(`Provider status contains a forbidden field at $[${index}].${extraProviderKey}.`)
    const extraEndpointKey = Object.keys(provider.customEndpoint).find((key) => !endpointKeys.has(key))
    if (extraEndpointKey) throw new Error(`Provider endpoint status contains a forbidden field at $[${index}].customEndpoint.${extraEndpointKey}.`)
    if (!Number.isSafeInteger(provider.configuredCredentialSlots) || provider.configuredCredentialSlots < 0 || provider.configuredCredentialSlots > 1_000) {
      throw new Error(`Provider credential slot count is malformed at $[${index}].`)
    }
    if (provider.customEndpoint.origin !== sanitizeProviderOrigin(provider.customEndpoint.origin)) {
      throw new Error(`Provider endpoint must expose only a safe HTTP origin at $[${index}].`)
    }
  }
}
