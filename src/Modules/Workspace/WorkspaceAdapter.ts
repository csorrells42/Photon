export const WORKSPACE_ADAPTER_VERSION = 1

export type WorkspaceEntry = {
  name: string
  path: string
  kind: 'directory' | 'file'
}

export type WorkspaceDirectory = {
  root: string
  path: string
  entries: WorkspaceEntry[]
}

export type WorkspaceFile = {
  path: string
  content: string
  size: number
  modifiedAt: string
  sha256: string
}

type FetchLike = typeof fetch

async function responseJson<T>(response: Response): Promise<T> {
  const body = await response.json() as T & { error?: string }
  if (!response.ok) throw new Error(body.error || `Workspace bridge returned HTTP ${response.status}.`)
  return body
}

export class WorkspaceAdapter {
  constructor(private readonly fetcher: FetchLike = (...arguments_) => globalThis.fetch(...arguments_)) {}

  async readDirectory(path = ''): Promise<WorkspaceDirectory> {
    const response = await this.fetcher(`/workbench-api/workspace/tree?path=${encodeURIComponent(path)}`, {
      credentials: 'same-origin',
    })
    return responseJson<WorkspaceDirectory>(response)
  }

  async readFile(path: string): Promise<WorkspaceFile> {
    const response = await this.fetcher(`/workbench-api/workspace/file?path=${encodeURIComponent(path)}`, {
      credentials: 'same-origin',
    })
    return responseJson<WorkspaceFile>(response)
  }
}

export const workspaceAdapter = new WorkspaceAdapter()
