import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { SourceControlWorkspace } from './SourceControlWorkspace'
import type { GitChangeEntry, GitStatusSnapshot, SourceControlDescription } from './contracts'

function entry(path: string, kind: GitChangeEntry['kind'], additions: Partial<GitChangeEntry> = {}): GitChangeEntry {
  return {
    path, kind,
    staged: false, unstaged: false, untracked: false, conflicted: false,
    deleted: false, renamed: false, submodule: false,
    ...additions,
  }
}

const snapshot: GitStatusSnapshot = {
  protocolVersion: 1,
  requestId: 'status:1',
  repositoryId: 'opaque-repository-id',
  displayName: 'HermesAgent',
  branch: { head: 'feature/source-control', upstream: 'origin/feature/source-control', ahead: 2, behind: 1, stashCount: 3, detached: false, unborn: false },
  groups: {
    staged: [entry('staged.ts', 'added', { staged: true })],
    unstaged: [entry('unstaged.ts', 'modified', { unstaged: true })],
    untracked: [entry('<script>alert(1)</script>.txt', 'untracked', { untracked: true })],
    conflicted: [entry('conflict.ts', 'conflicted', { conflicted: true })],
    renamed: [entry('new-name.ts', 'renamed', { renamed: true, originalPath: 'old-name.ts' })],
    deleted: [entry('deleted.ts', 'deleted', { deleted: true })],
    submodules: [entry('vendor/module', 'submodule', { submodule: true })],
  },
  entryCount: 7,
  truncated: false,
}

const description: SourceControlDescription = {
  protocolVersion: 1,
  git: { state: 'available' },
  gitExtensions: { state: 'unavailable', message: 'Optional tool unavailable.' },
  operations: ['describe', 'resolveRepository', 'getStatus', 'openGitExtensions'],
  gitExtensionsSurfaces: [],
}

describe('SourceControlWorkspace', () => {
  it('renders branch data and every normalized change group', () => {
    const html = renderToStaticMarkup(<SourceControlWorkspace snapshot={snapshot} description={description} />)
    for (const expected of [
      'feature/source-control', 'origin/feature/source-control', '2</strong> ahead', '1</strong> behind', '3</strong> stashes',
      'Staged', 'Unstaged', 'Untracked', 'Conflicted', 'Renamed', 'Deleted', 'Submodules',
      'staged.ts', 'unstaged.ts', 'conflict.ts', 'new-name.ts', 'old-name.ts', 'deleted.ts', 'vendor/module',
    ]) expect(html).toContain(expected)
    expect(html).toContain('Git Extensions unavailable')
    expect(html).toContain('disabled=""')
  })

  it('renders hostile filenames as text, never markup', () => {
    const html = renderToStaticMarkup(<SourceControlWorkspace snapshot={snapshot} description={description} />)
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;.txt')
    expect(html).not.toContain('<script>alert(1)</script>')
  })

  it('renders browser, Git, and Git Extensions unavailable states non-fatally', () => {
    const browser = renderToStaticMarkup(<SourceControlWorkspace unavailableReason="browser" />)
    const git = renderToStaticMarkup(<SourceControlWorkspace unavailableReason="git" description={{ ...description, git: { state: 'unavailable', message: 'Git was not found.' } }} />)
    const optional = renderToStaticMarkup(<SourceControlWorkspace snapshot={snapshot} description={description} />)
    expect(browser).toContain('Desktop source control unavailable')
    expect(git).toContain('Git unavailable')
    expect(git).toContain('Git was not found.')
    expect(optional).toContain('Status inspection remains available.')
  })
})
