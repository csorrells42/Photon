import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChangesView } from './ChangesView'
import {
  desktopSourceControlClient,
  type DesktopSourceControlClient,
} from './DesktopSourceControlClient'
import type { GitStatusSnapshot, SourceControlDescription } from './contracts'
import './SourceControlWorkspace.css'

export type SourceControlWorkspaceProps = {
  client?: DesktopSourceControlClient
  workspaceRelativePath?: string
  snapshot?: GitStatusSnapshot
  description?: SourceControlDescription
  unavailableReason?: 'browser' | 'git'
}

type WorkspaceState = 'loading' | 'ready' | 'browser-unavailable' | 'git-unavailable' | 'error'

export function SourceControlWorkspace({
  client = desktopSourceControlClient,
  workspaceRelativePath = '.',
  snapshot: suppliedSnapshot,
  description: suppliedDescription,
  unavailableReason,
}: SourceControlWorkspaceProps) {
  const supplied = suppliedSnapshot !== undefined || suppliedDescription !== undefined || unavailableReason !== undefined
  const [snapshot, setSnapshot] = useState<GitStatusSnapshot | undefined>(suppliedSnapshot)
  const [description, setDescription] = useState<SourceControlDescription | undefined>(suppliedDescription)
  const [repositoryId, setRepositoryId] = useState(suppliedSnapshot?.repositoryId)
  const [state, setState] = useState<WorkspaceState>(() => unavailableReason === 'browser'
    ? 'browser-unavailable'
    : unavailableReason === 'git'
      ? 'git-unavailable'
      : suppliedSnapshot
        ? 'ready'
        : 'loading')
  const [message, setMessage] = useState('')
  const [refreshing, setRefreshing] = useState(false)

  const refresh = useCallback(async () => {
    if (!repositoryId) return
    setRefreshing(true)
    try {
      const result = await client.getStatus(repositoryId)
      if (result.succeeded && result.snapshot) {
        setSnapshot(result.snapshot)
        setState('ready')
        setMessage('')
      } else {
        setState('error')
        setMessage(result.error?.message ?? 'Source control status is unavailable.')
      }
    } catch (error) {
      setState('error')
      setMessage(error instanceof Error ? error.message : 'Source control status is unavailable.')
    } finally {
      setRefreshing(false)
    }
  }, [client, repositoryId])

  useEffect(() => {
    if (supplied) return
    let current = true
    async function load() {
      if (!client.available) {
        if (current) setState('browser-unavailable')
        return
      }
      try {
        const service = await client.describe()
        if (!current) return
        setDescription(service)
        if (service.git.state !== 'available') {
          setState('git-unavailable')
          return
        }
        const resolved = await client.resolveRepository(workspaceRelativePath)
        if (!current) return
        if (!resolved.succeeded || !resolved.repositoryId) throw new Error(resolved.error?.message ?? 'No Git repository was found.')
        setRepositoryId(resolved.repositoryId)
        const status = await client.getStatus(resolved.repositoryId)
        if (!current) return
        if (!status.succeeded || !status.snapshot) throw new Error(status.error?.message ?? 'Source control status is unavailable.')
        setSnapshot(status.snapshot)
        setState('ready')
      } catch (error) {
        if (!current) return
        setState('error')
        setMessage(error instanceof Error ? error.message : 'Source control is unavailable.')
      }
    }
    void load()
    return () => { current = false }
  }, [client, supplied, workspaceRelativePath])

  const branchLabel = useMemo(() => {
    if (!snapshot) return 'No branch'
    if (snapshot.branch.detached) return 'Detached HEAD'
    if (snapshot.branch.unborn) return `${snapshot.branch.head ?? 'New branch'} (unborn)`
    return snapshot.branch.head ?? 'No branch'
  }, [snapshot])

  async function openGitExtensions() {
    if (!repositoryId) return
    const result = await client.openGitExtensions(repositoryId, 'browse')
    if (!result.succeeded) setMessage(result.error?.message ?? 'Git Extensions could not be opened.')
  }

  if (state !== 'ready' || !snapshot) {
    const content = state === 'browser-unavailable'
      ? ['Desktop source control unavailable', 'Open this workspace in the Hermes desktop app to inspect Git changes.']
      : state === 'git-unavailable'
        ? ['Git unavailable', description?.git.message ?? 'Install or configure Git to inspect this workspace.']
        : state === 'loading'
          ? ['Reading source control', 'Checking repository availability…']
          : ['Source control unavailable', message || 'The repository could not be inspected.']
    return (
      <section className="source-control-workspace source-control-workspace--state" aria-live="polite">
        <div className="source-control-state__mark" aria-hidden="true">⌘</div>
        <h2>{content[0]}</h2>
        <p>{content[1]}</p>
      </section>
    )
  }

  const gitExtensionsAvailable = description?.gitExtensions.state === 'available'
  return (
    <section className="source-control-workspace" aria-label="Source control">
      <header className="source-control-header">
        <div>
          <p className="source-control-eyebrow">Source control</p>
          <h2>{snapshot.displayName}</h2>
          <div className="source-control-branch">
            <span className="source-control-branch__name">⑂ {branchLabel}</span>
            {snapshot.branch.upstream && <span title="Upstream branch">↗ {snapshot.branch.upstream}</span>}
          </div>
        </div>
        <div className="source-control-actions">
          <button type="button" onClick={() => void refresh()} disabled={refreshing}>{refreshing ? 'Refreshing…' : 'Refresh'}</button>
          <button type="button" onClick={() => void openGitExtensions()} disabled={!gitExtensionsAvailable} title={gitExtensionsAvailable ? 'Open repository in Git Extensions' : 'Git Extensions is not installed or configured'}>
            Open Git Extensions
          </button>
        </div>
      </header>
      <div className="source-control-summary" aria-label="Branch summary">
        <span><strong>{snapshot.branch.ahead}</strong> ahead</span>
        <span><strong>{snapshot.branch.behind}</strong> behind</span>
        <span><strong>{snapshot.branch.stashCount}</strong> stashes</span>
        <span><strong>{snapshot.entryCount}</strong> changes</span>
      </div>
      {description?.gitExtensions.state !== 'available' && (
        <p className="source-control-notice" role="status">Git Extensions unavailable. Status inspection remains available.</p>
      )}
      {message && <p className="source-control-notice source-control-notice--error" role="alert">{message}</p>}
      <ChangesView groups={snapshot.groups} />
    </section>
  )
}
