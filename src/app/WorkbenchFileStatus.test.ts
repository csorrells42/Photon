import { describe, expect, it } from 'vitest'
import { workbenchFileStatus } from './WorkbenchFileStatus'

describe('workbenchFileStatus', () => {
  it('reports only evidence derived from the selected path', () => {
    expect(workbenchFileStatus('src/Program.cs')).toEqual({ fileName: 'Program.cs', language: 'C#' })
    expect(workbenchFileStatus('firmware\\controller.ino')).toEqual({ fileName: 'controller.ino', language: 'Arduino' })
    expect(workbenchFileStatus('README')).toEqual({ fileName: 'README', language: 'Plain Text' })
  })

  it('does not invent status without a selected file', () => {
    expect(workbenchFileStatus(null)).toBeNull()
    expect(workbenchFileStatus('  ')).toBeNull()
  })
})
