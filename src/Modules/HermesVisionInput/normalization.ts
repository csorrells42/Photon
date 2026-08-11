import {
  HERMES_VISION_INPUT_CONTRACT_VERSION,
  HERMES_VISION_INPUT_MAX_BYTES,
  HermesVisionInputError,
  type HermesVisionByteCarrier,
  type HermesVisionDisplayError,
  type HermesVisionMediaType,
  type HermesVisionPathCarrier,
  type HermesVisionRawInput,
  type NormalizedHermesVisionInput,
} from './contracts'

const EXTENSION_MEDIA_TYPES: Readonly<Record<string, HermesVisionMediaType>> = {
  '.bmp': 'image/bmp',
  '.gif': 'image/gif',
  '.ico': 'image/vnd.microsoft.icon',
  '.jpeg': 'image/jpeg',
  '.jpg': 'image/jpeg',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.tif': 'image/tiff',
  '.tiff': 'image/tiff',
  '.webp': 'image/webp',
}

const MEDIA_TYPE_ALIASES: Readonly<Record<string, HermesVisionMediaType>> = {
  'image/bmp': 'image/bmp',
  'image/gif': 'image/gif',
  'image/ico': 'image/vnd.microsoft.icon',
  'image/jpeg': 'image/jpeg',
  'image/jpg': 'image/jpeg',
  'image/png': 'image/png',
  'image/svg+xml': 'image/svg+xml',
  'image/tiff': 'image/tiff',
  'image/vnd.microsoft.icon': 'image/vnd.microsoft.icon',
  'image/webp': 'image/webp',
  'image/x-icon': 'image/vnd.microsoft.icon',
}

const SAFE_IMAGE_NAME = 'image'
const MAX_DISPLAY_NAME_LENGTH = 160

function invalidInput(message = 'The selected image is invalid.') {
  return new HermesVisionInputError('invalid-input', message)
}

function bytePrefixMatches(bytes: Uint8Array, prefix: readonly number[]) {
  return bytes.length >= prefix.length && prefix.every((value, index) => bytes[index] === value)
}

function ascii(bytes: Uint8Array, start: number, length: number) {
  return String.fromCharCode(...bytes.slice(start, start + length))
}

function mediaTypeFromBytes(bytes: Uint8Array): HermesVisionMediaType | null {
  if (bytePrefixMatches(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return 'image/png'
  if (bytePrefixMatches(bytes, [0xff, 0xd8, 0xff])) return 'image/jpeg'
  if (ascii(bytes, 0, 6) === 'GIF87a' || ascii(bytes, 0, 6) === 'GIF89a') return 'image/gif'
  if (ascii(bytes, 0, 4) === 'RIFF' && ascii(bytes, 8, 4) === 'WEBP') return 'image/webp'
  if (ascii(bytes, 0, 2) === 'BM') return 'image/bmp'
  if (bytePrefixMatches(bytes, [0x49, 0x49, 0x2a, 0x00]) || bytePrefixMatches(bytes, [0x4d, 0x4d, 0x00, 0x2a])) return 'image/tiff'
  if (bytePrefixMatches(bytes, [0x00, 0x00, 0x01, 0x00])) return 'image/vnd.microsoft.icon'

  const textPrefix = new TextDecoder().decode(bytes.slice(0, 4096)).replace(/^\uFEFF/, '').trimStart()
  if (/^(?:<\?xml\b[^>]*>\s*)?<svg\b/i.test(textPrefix)) return 'image/svg+xml'
  return null
}

function mediaTypeFromPath(path: string): HermesVisionMediaType | null {
  const filename = path.split(/[\\/]/).pop() ?? ''
  const dot = filename.lastIndexOf('.')
  return dot >= 0 ? EXTENSION_MEDIA_TYPES[filename.slice(dot).toLowerCase()] ?? null : null
}

function declaredMediaType(value: string | undefined): HermesVisionMediaType | null {
  if (!value?.trim()) return null
  return MEDIA_TYPE_ALIASES[value.split(';', 1)[0].trim().toLowerCase()] ?? null
}

export function safeHermesVisionDisplayName(value: string | undefined, fallbackPath?: string) {
  const candidate = value?.trim() || fallbackPath?.split(/[\\/]/).filter(Boolean).pop() || SAFE_IMAGE_NAME
  const basename = candidate.split(/[\\/]/).filter(Boolean).pop() || SAFE_IMAGE_NAME
  const clean = basename.replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim()
  return (clean || SAFE_IMAGE_NAME).slice(0, MAX_DISPLAY_NAME_LENGTH)
}

function normalizeBytes(raw: HermesVisionRawInput, displayName: string): HermesVisionByteCarrier | null {
  if (raw.bytes === undefined) return null
  const bytes = raw.bytes instanceof Uint8Array
    ? new Uint8Array(raw.bytes)
    : new Uint8Array(raw.bytes.slice(0))

  if (bytes.byteLength === 0) {
    throw new HermesVisionInputError('empty-bytes', 'The selected image is empty.')
  }
  if (bytes.byteLength > HERMES_VISION_INPUT_MAX_BYTES) {
    throw new HermesVisionInputError('too-large', 'The selected image is too large. Hermes accepts images up to 25 MB.')
  }

  const detected = mediaTypeFromBytes(bytes)
  if (!detected) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image format is not supported.')
  }
  const declared = declaredMediaType(raw.declaredMediaType)
  if (raw.declaredMediaType?.trim() && !declared) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image format is not supported.')
  }
  if (declared && declared !== detected) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image contents do not match its declared format.')
  }

  return { kind: 'bytes', bytes, byteLength: bytes.byteLength, filename: displayName, mediaType: detected }
}

function normalizePath(raw: HermesVisionRawInput): HermesVisionPathCarrier | null {
  if (raw.path === undefined) return null
  if (!raw.path.trim() || /[\u0000-\u001f\u007f]/.test(raw.path)) {
    throw invalidInput('The selected image path is invalid.')
  }
  const detected = mediaTypeFromPath(raw.path)
  if (!detected) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image path does not use a supported image extension.')
  }
  const declared = declaredMediaType(raw.declaredMediaType)
  if (raw.declaredMediaType?.trim() && !declared) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image format is not supported.')
  }
  if (declared && declared !== detected) {
    throw new HermesVisionInputError('unsupported-format', 'The selected image path does not match its declared format.')
  }
  return { kind: 'path', path: raw.path, mediaType: detected }
}

export function normalizeHermesVisionInput(raw: HermesVisionRawInput): NormalizedHermesVisionInput {
  const id = raw.id?.trim()
  if (!id || /[\u0000-\u001f\u007f]/.test(id)) throw invalidInput()

  const displayName = safeHermesVisionDisplayName(raw.displayName, raw.path)
  const byteCarrier = normalizeBytes(raw, displayName)
  const pathCarrier = normalizePath(raw)
  const carriers = [byteCarrier, pathCarrier].filter((carrier): carrier is HermesVisionByteCarrier | HermesVisionPathCarrier => carrier !== null)
  if (carriers.length === 0) {
    throw invalidInput('Choose an image or capture a screenshot before attaching it.')
  }

  return {
    contractVersion: HERMES_VISION_INPUT_CONTRACT_VERSION,
    id,
    origin: raw.origin,
    displayName,
    carriers,
  }
}

export function toHermesVisionDisplayError(reason: unknown): HermesVisionDisplayError {
  if (reason instanceof HermesVisionInputError) return { code: reason.code, message: reason.message }
  return { code: 'provider-failed', message: 'Hermes could not attach the image. Try again.' }
}

