export type WorkbenchFileStatus = { fileName: string; language: string }

const languageByExtension: Readonly<Record<string, string>> = Object.freeze({
  c: 'C', cc: 'C++', cpp: 'C++', cs: 'C#', css: 'CSS', h: 'C/C++ Header', hpp: 'C++ Header',
  html: 'HTML', ino: 'Arduino', java: 'Java', js: 'JavaScript', json: 'JSON', jsx: 'JavaScript React',
  md: 'Markdown', py: 'Python', rs: 'Rust', ts: 'TypeScript', tsx: 'TypeScript React', yaml: 'YAML', yml: 'YAML',
})

export function workbenchFileStatus(path: string | null): WorkbenchFileStatus | null {
  const value = path?.trim()
  if (!value) return null
  const fileName = value.split(/[\\/]/u).filter(Boolean).at(-1) ?? ''
  if (!fileName || fileName.length > 512) return null
  const extension = fileName.includes('.') ? fileName.slice(fileName.lastIndexOf('.') + 1).toLocaleLowerCase() : ''
  return { fileName, language: languageByExtension[extension] ?? 'Plain Text' }
}
