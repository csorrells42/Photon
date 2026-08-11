import { describe, expect, it } from 'vitest'
import {
  HERMES_FILE_UPLOAD_MAX_BYTES,
  HERMES_IMAGE_UPLOAD_MAX_BYTES,
  attachmentKind,
  buildPromptWithAttachments,
  validateAttachment,
} from './HermesAttachmentAdapter'

describe('Hermes attachment compatibility adapter', () => {
  it('recognizes upstream image extensions even when the browser omits the MIME type', () => {
    expect(attachmentKind({ name: 'diagram.WEBP', type: '' })).toBe('image')
    expect(attachmentKind({ name: 'notes.md', type: 'text/markdown' })).toBe('file')
  })

  it('enforces the upstream image cap and desktop file-upload guard', () => {
    expect(validateAttachment({ name: 'photo.png', type: 'image/png', size: HERMES_IMAGE_UPLOAD_MAX_BYTES })).toBe('image')
    expect(() => validateAttachment({ name: 'photo.png', type: 'image/png', size: HERMES_IMAGE_UPLOAD_MAX_BYTES + 1 })).toThrow('25 MB')
    expect(() => validateAttachment({ name: 'archive.zip', type: 'application/zip', size: HERMES_FILE_UPLOAD_MAX_BYTES + 1 })).toThrow('256 MB')
  })

  it('submits only gateway-safe file refs and supplies the upstream image-only fallback', () => {
    expect(buildPromptWithAttachments('Review this', [
      { kind: 'file', label: 'report.txt', refText: '@file:.hermes/desktop-attachments/report.txt' },
      { kind: 'image', label: 'screen.png', gatewayPath: '/gateway/images/screen.png' },
    ])).toBe('@file:.hermes/desktop-attachments/report.txt\n\nReview this')

    expect(buildPromptWithAttachments('', [{ kind: 'image', label: 'screen.png' }]))
      .toBe('What do you see in this image?')
  })
})
