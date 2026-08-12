type WorkspaceLanguage = {
  id: string
  label: string
}

const languagesByExtension: Record<string, WorkspaceLanguage> = {
  ts: { id: 'typescript', label: 'TypeScript' },
  mts: { id: 'typescript', label: 'TypeScript' },
  cts: { id: 'typescript', label: 'TypeScript' },
  tsx: { id: 'typescript', label: 'TypeScript React' },
  js: { id: 'javascript', label: 'JavaScript' },
  mjs: { id: 'javascript', label: 'JavaScript' },
  cjs: { id: 'javascript', label: 'JavaScript' },
  jsx: { id: 'javascript', label: 'JavaScript React' },
  cs: { id: 'csharp', label: 'C#' },
  py: { id: 'python', label: 'Python' },
  json: { id: 'json', label: 'JSON' },
  jsonc: { id: 'json', label: 'JSON' },
  md: { id: 'markdown', label: 'Markdown' },
  markdown: { id: 'markdown', label: 'Markdown' },
  css: { id: 'css', label: 'CSS' },
  html: { id: 'html', label: 'HTML' },
  htm: { id: 'html', label: 'HTML' },
  yml: { id: 'yaml', label: 'YAML' },
  yaml: { id: 'yaml', label: 'YAML' },
  ps1: { id: 'powershell', label: 'PowerShell' },
  psm1: { id: 'powershell', label: 'PowerShell' },
  psd1: { id: 'powershell', label: 'PowerShell' },
}

const dockerfile: WorkspaceLanguage = { id: 'dockerfile', label: 'Dockerfile' }
const plainText: WorkspaceLanguage = { id: 'plaintext', label: 'Plain Text' }

function filenameFor(path: string) {
  return path.replaceAll('\\', '/').split('/').at(-1)?.toLowerCase() ?? ''
}

export function workspaceLanguageForPath(path: string) {
  const filename = filenameFor(path)
  if (filename === 'dockerfile' || filename.startsWith('dockerfile.')) return dockerfile

  const extension = filename.split('.').at(-1) ?? ''
  return languagesByExtension[extension] ?? plainText
}

export function workspaceLanguageIdForPath(path: string) {
  return workspaceLanguageForPath(path).id
}

export function workspaceLanguageLabelForPath(path: string) {
  return workspaceLanguageForPath(path).label
}
