import { hermesGateway } from './HermesGatewayClient'

export const HERMES_ATTACHMENT_ADAPTER_VERSION = 1
export const HERMES_IMAGE_UPLOAD_MAX_BYTES = 25 * 1024 * 1024
export const HERMES_FILE_UPLOAD_MAX_BYTES = 256 * 1024 * 1024

const IMAGE_EXTENSIONS = new Set([
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.tiff', '.tif', '.svg', '.ico',
])

export type HermesAttachmentKind = 'image' | 'file'

export type HermesStagedAttachment = {
  kind: HermesAttachmentKind
  label: string
  gatewayPath?: string
  refText?: string
}

type AttachmentRequest = <T>(method: string, params: Record<string, unknown>, timeoutMs?: number) => Promise<T>

type ImageAttachResponse = {
  attached?: boolean
  message?: string
  path?: string
}

type FileAttachResponse = {
  attached?: boolean
  message?: string
  path?: string
  ref_text?: string
}

function extensionOf(filename: string) {
  const index = filename.lastIndexOf('.')
  return index >= 0 ? filename.slice(index).toLowerCase() : ''
}

export function attachmentKind(file: Pick<File, 'name' | 'type'>): HermesAttachmentKind {
  return file.type.startsWith('image/') || IMAGE_EXTENSIONS.has(extensionOf(file.name)) ? 'image' : 'file'
}

export function validateAttachment(file: Pick<File, 'name' | 'size' | 'type'>) {
  const kind = attachmentKind(file)
  const limit = kind === 'image' ? HERMES_IMAGE_UPLOAD_MAX_BYTES : HERMES_FILE_UPLOAD_MAX_BYTES
  if (file.size <= 0) throw new Error(`${file.name || 'Attachment'} is empty.`)
  if (file.size > limit) {
    throw new Error(`${file.name || 'Attachment'} is too large. Hermes allows up to ${limit / 1024 / 1024} MB for this upload.`)
  }
  return kind
}

function readFileAsDataUrl(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.addEventListener('load', () => {
      if (typeof reader.result === 'string' && reader.result) resolve(reader.result)
      else reject(new Error(`Could not read ${file.name}.`))
    }, { once: true })
    reader.addEventListener('error', () => reject(reader.error ?? new Error(`Could not read ${file.name}.`)), { once: true })
    reader.addEventListener('abort', () => reject(new Error(`Reading ${file.name} was cancelled.`)), { once: true })
    reader.readAsDataURL(file)
  })
}

export function buildPromptWithAttachments(text: string, attachments: HermesStagedAttachment[]) {
  const visibleText = text.trim()
  const refs = attachments.map((attachment) => attachment.refText).filter((value): value is string => Boolean(value))
  return [refs.join('\n'), visibleText].filter(Boolean).join('\n\n')
    || (attachments.some((attachment) => attachment.kind === 'image') ? 'What do you see in this image?' : '')
}

export class HermesAttachmentAdapter {
  constructor(private readonly request: AttachmentRequest = hermesGateway.request.bind(hermesGateway)) {}

  async stage(sessionId: string, file: File): Promise<HermesStagedAttachment> {
    const kind = validateAttachment(file)
    const dataUrl = await readFileAsDataUrl(file)

    if (kind === 'image') {
      const result = await this.request<ImageAttachResponse>('image.attach_bytes', {
        session_id: sessionId,
        content_base64: dataUrl,
        filename: file.name,
      })
      if (!result.attached) throw new Error(result.message || `Hermes could not attach ${file.name}.`)
      return { kind, label: file.name, gatewayPath: result.path }
    }

    const result = await this.request<FileAttachResponse>('file.attach', {
      session_id: sessionId,
      path: '',
      name: file.name,
      data_url: dataUrl,
    })
    if (!result.attached || !result.ref_text) throw new Error(result.message || `Hermes could not attach ${file.name}.`)
    return { kind, label: file.name, gatewayPath: result.path, refText: result.ref_text }
  }
}

export const hermesAttachmentAdapter = new HermesAttachmentAdapter()
