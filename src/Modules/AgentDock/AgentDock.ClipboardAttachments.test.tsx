import { describe, expect, it, vi } from 'vitest'
import {
  extractAgentDockClipboardImageFiles,
  handleAgentDockClipboardImagePaste,
} from './AgentDock'

function file(name: string, type: string, size = 9, lastModified = 42, bytes = new Uint8Array(size)) {
  return { name, type, size, lastModified, arrayBuffer: async () => bytes.slice().buffer } as File
}

function clipboard(options: {
  files?: File[]
  items?: Array<{ file: File | null; kind?: string; type?: string }>
  text?: string
} = {}) {
  const files = options.files ?? []
  const items = (options.items ?? []).map((item) => ({
    kind: item.kind ?? 'file',
    type: item.type ?? item.file?.type ?? '',
    getAsFile: () => item.file,
  }))
  return {
    files: { length: files.length, item: (index: number) => files[index] ?? null },
    getData: (format: string) => format === 'text/plain' ? options.text ?? '' : '',
    items,
  } as unknown as DataTransfer
}

describe('AgentDock clipboard image intake', () => {
  it('extracts image items, uses the files fallback, and deduplicates Chromium mirrors', () => {
    const png = file('screen.png', 'image/png')
    expect(extractAgentDockClipboardImageFiles(clipboard({
      items: [{ file: png }],
      files: [png],
    }))).toEqual([png])

    const fallback = file('fallback.png', 'image/png')
    expect(extractAgentDockClipboardImageFiles(clipboard({ files: [fallback] }))).toEqual([fallback])
    expect(extractAgentDockClipboardImageFiles(clipboard({
      items: [{ file: file('notes.txt', 'text/plain') }],
      files: [file('empty.png', 'image/png', 0)],
    }))).toEqual([])
  })

  it('preserves distinct item images with identical metadata while suppressing their file-list mirrors', async () => {
    const first = file('same.png', 'image/png', 9, 42, new Uint8Array([1, 0, 0, 0, 0, 0, 0, 0, 0]))
    const second = file('same.png', 'image/png', 9, 42, new Uint8Array([2, 0, 0, 0, 0, 0, 0, 0, 0]))
    expect(new Uint8Array(await first.arrayBuffer())).not.toEqual(new Uint8Array(await second.arrayBuffer()))
    expect(extractAgentDockClipboardImageFiles(clipboard({
      items: [{ file: first }, { file: second }],
      files: [first, second],
    }))).toEqual([first, second])
  })

  it('chooses one PNG from alternate Windows Bitmap and PNG representations of the same clipboard image', async () => {
    const pngBytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4, 5])
    const bitmapBytes = new Uint8Array([0x42, 0x4d, 9, 8, 7, 6, 5, 4, 3])
    const bitmap = file('image.bmp', 'image/bmp', bitmapBytes.byteLength, 100, bitmapBytes)
    const png = file('image.png', 'image/png', pngBytes.byteLength, 101, pngBytes)
    expect(new Uint8Array(await bitmap.arrayBuffer())).not.toEqual(new Uint8Array(await png.arrayBuffer()))

    const liveClipboard = clipboard({
      items: [{ file: bitmap }, { file: png }],
      files: [bitmap, png],
    })
    expect(extractAgentDockClipboardImageFiles(liveClipboard)).toEqual([png])

    const addAttachments = vi.fn()
    const preventDefault = vi.fn()
    expect(handleAgentDockClipboardImagePaste(liveClipboard, addAttachments, preventDefault)).toBe(true)
    expect(addAttachments).toHaveBeenCalledOnce()
    expect(addAttachments).toHaveBeenCalledWith([png], 'clipboard')
    expect(preventDefault).toHaveBeenCalledOnce()
  })

  it('keeps text-only paste native and prevents the native default for image-only paste', () => {
    const addAttachments = vi.fn()
    const preventDefault = vi.fn()

    expect(handleAgentDockClipboardImagePaste(
      clipboard({ text: 'plain text' }),
      addAttachments,
      preventDefault,
    )).toBe(false)
    expect(addAttachments).not.toHaveBeenCalled()
    expect(preventDefault).not.toHaveBeenCalled()

    const png = file('screen.png', 'image/png')
    expect(handleAgentDockClipboardImagePaste(
      clipboard({ items: [{ file: png }] }),
      addAttachments,
      preventDefault,
    )).toBe(true)
    expect(addAttachments).toHaveBeenCalledWith([png], 'clipboard')
    expect(preventDefault).toHaveBeenCalledOnce()
  })

  it('attaches a mixed-paste image without preventing its text from reaching the textarea', () => {
    const png = file('screen.png', 'image/png')
    const addAttachments = vi.fn()
    const preventDefault = vi.fn()

    expect(handleAgentDockClipboardImagePaste(
      clipboard({ items: [{ file: png }], text: 'Keep this caption' }),
      addAttachments,
      preventDefault,
    )).toBe(true)
    expect(addAttachments).toHaveBeenCalledWith([png], 'clipboard')
    expect(preventDefault).not.toHaveBeenCalled()
  })

})
