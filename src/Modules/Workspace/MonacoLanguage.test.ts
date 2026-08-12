import { describe, expect, it } from 'vitest'
import { workspaceLanguageForPath } from './MonacoLanguage'

describe('workspace Monaco language mapping', () => {
  it.each([
    ['src/app.ts', 'typescript'],
    ['src/App.tsx', 'typescript'],
    ['scripts/build.js', 'javascript'],
    ['components/Widget.jsx', 'javascript'],
    ['Host/Program.cs', 'csharp'],
    ['tools/check.py', 'python'],
    ['package.json', 'json'],
    ['README.md', 'markdown'],
    ['app.css', 'css'],
    ['index.html', 'html'],
    ['compose.yaml', 'yaml'],
    ['scripts/install.ps1', 'powershell'],
    ['Dockerfile', 'dockerfile'],
  ])('maps %s to %s', (path, id) => {
    expect(workspaceLanguageForPath(path).id).toBe(id)
  })

  it('handles Windows separators, Dockerfile variants, and unknown files', () => {
    expect(workspaceLanguageForPath('scripts\\publish.PSM1').id).toBe('powershell')
    expect(workspaceLanguageForPath('containers/Dockerfile.dev').id).toBe('dockerfile')
    expect(workspaceLanguageForPath('assets/logo.bin')).toEqual({ id: 'plaintext', label: 'Plain Text' })
  })
})
