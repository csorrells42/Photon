import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createDeveloperCancelRequest,
  createDeveloperOperationRequest,
  DesktopDeveloperServicesClient,
  normalizeDeveloperServicesFrame,
  normalizeLanguageToolingEvidence,
  workspaceRelativePath,
} from './DesktopDeveloperServicesClient'

const provider = {
  contractVersion: 1,
  providerId: 'dotnet',
  displayName: '.NET SDK',
  providerVersion: '8.0',
  languageIds: ['csharp'],
  projectKinds: ['slnx', 'csproj'],
  build: { supported: true, producesDiagnostics: true, supportsCancellation: true, targetKinds: ['slnx', 'csproj'] },
  lsp: { supported: false },
  dap: { supported: false },
  availability: { state: 'available' },
}

function v2Description(requestId = 'describe:1') {
  return {
    type: 'developerServices.describe.result',
    version: 2,
    requestId,
    workspaceRoot: 'C:\\workspace',
    targets: ['Photon.slnx', 'src/App.csproj'],
    providers: [provider],
    languageTooling: [{
      contract: 'language-tooling-providers/v1',
      source: 'trusted-host',
      evidenceId: 'desktop:dotnet:1',
      providerId: 'dotnet',
      checkedAt: '2026-08-10T12:00:00.0000000Z',
      capabilities: [{
        capabilityId: 'dotnet.roslyn-lsp',
        availability: 'available',
        code: 'verified-pinned-runtime',
        detail: 'Pinned runtime verified.',
        version: '1.2.3',
        executablePath: 'C:\\secret\\server.exe',
      }],
    }],
    availability: { state: 'available' },
  }
}

function v2Result(operation: 'build' | 'analyze', requestId: string, revision: number) {
  return {
    type: `developerServices.${operation}.result`,
    version: 2,
    requestId,
    revision,
    operation,
    workspaceRoot: 'C:\\workspace',
    succeeded: true,
    wasCancelled: false,
    stale: false,
    diagnostics: [],
    output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
  }
}

class FakeBridge {
  messages: unknown[] = []
  listener: ((event: MessageEvent) => void) | null = null
  postMessage = (message: unknown) => { this.messages.push(message) }
  addEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => { this.listener = listener }
  removeEventListener = (_type: 'message', listener: (event: MessageEvent) => void) => {
    if (this.listener === listener) this.listener = null
  }
  emit(data: unknown) { this.listener?.({ data } as MessageEvent) }
}

afterEach(() => vi.unstubAllGlobals())

describe('desktop developer-services protocol', () => {
  it('normalizes a strict v2 provider description and discards extra fields', () => {
    const frame = normalizeDeveloperServicesFrame({ ...v2Description(), secret: 'discard-me' })
    expect(frame).toMatchObject({
      type: 'describe',
      value: {
        protocolVersion: 2,
        targets: ['Photon.slnx', 'src/App.csproj'],
        providers: [{ providerId: 'dotnet', build: { supported: true }, availability: { state: 'available' } }],
        languageTooling: [{ providerId: 'dotnet', capabilities: [{ capabilityId: 'dotnet.roslyn-lsp', availability: 'available' }] }],
      },
    })
    expect(JSON.stringify(frame)).not.toContain('discard-me')
    expect(JSON.stringify(frame)).not.toContain('server.exe')
  })

  it('fails closed on malformed or unknown language-tooling evidence', () => {
    expect(normalizeLanguageToolingEvidence([{ ...v2Description().languageTooling[0], source: 'renderer' }])).toBeNull()
    expect(normalizeLanguageToolingEvidence([{ ...v2Description().languageTooling[0], providerId: 'invented' }])).toBeNull()
    expect(normalizeLanguageToolingEvidence([{
      ...v2Description().languageTooling[0],
      capabilities: [{
        capabilityId: 'dotnet.compiler', availability: 'available', code: '', detail: 'missing proof code',
      }],
    }])).toBeNull()
  })

  it('fails closed on malformed v2 envelopes and never truncates request identifiers', () => {
    expect(normalizeDeveloperServicesFrame({ ...v2Description(), targets: undefined })).toBeNull()
    expect(normalizeDeveloperServicesFrame({ ...v2Description(), requestId: 'x'.repeat(129) })).toBeNull()
    expect(normalizeDeveloperServicesFrame({ ...v2Result('build', 'build:1', 1), output: undefined })).toBeNull()
    expect(normalizeDeveloperServicesFrame({ ...v2Result('build', 'build:1', 1), stale: 'false' })).toBeNull()
    expect(normalizeDeveloperServicesFrame({ type: 'developerServices.cancel.result', version: 2, requestId: 'build:1', accepted: 'yes' })).toBeNull()
    expect(normalizeDeveloperServicesFrame({ type: 'developerServices.error', version: 2, requestId: 'build:1', message: 'Nope', retryable: false })).toBeNull()
  })

  it('retains bounded legacy v1 description and build compatibility', () => {
    const description = normalizeDeveloperServicesFrame({ ...v2Description(), version: 1 })
    const result = normalizeDeveloperServicesFrame({
      type: 'developerServices.build.result',
      version: 1,
      requestId: 'build:legacy',
      workspaceRoot: 'C:\\workspace',
      succeeded: false,
      diagnostics: [{
        filePath: 'C:\\workspace\\src\\Program.cs',
        severity: 'error',
        code: 'CS1002',
        message: '; expected',
        source: 'msbuild',
        range: { start: { line: 4, column: 8 }, end: { line: 4, column: 8 } },
      }],
      output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
    })
    expect(description).toMatchObject({ type: 'describe', value: { protocolVersion: 1 } })
    expect(result).toMatchObject({ type: 'operation-result', value: { operation: 'build', revision: 0, diagnostics: [{ code: 'CS1002' }] } })
  })

  it('creates exact v2 build, analyze, and cancel requests', () => {
    expect(createDeveloperOperationRequest('build', 'build:1', 7, 'Photon.slnx', 'Release')).toEqual({
      type: 'developerServices.build', version: 2, requestId: 'build:1', revision: 7, targetPath: 'Photon.slnx', configuration: 'release',
    })
    expect(createDeveloperOperationRequest('analyze', 'analyze:2', 8, 'src/App.csproj', 'Debug')).toEqual({
      type: 'developerServices.analyze', version: 2, requestId: 'analyze:2', revision: 8, targetPath: 'src/App.csproj', configuration: 'debug',
    })
    expect(createDeveloperCancelRequest('analyze:2')).toEqual({ type: 'developerServices.cancel', version: 2, requestId: 'analyze:2' })
    expect(() => createDeveloperOperationRequest('build', 'build:3', 9, '..\\outside.csproj', 'Debug')).toThrow(/workspace-relative/)
  })

  it('resolves pending work only for an exact kind, request, and revision identity', async () => {
    const bridge = new FakeBridge()
    vi.stubGlobal('window', { chrome: { webview: bridge } })
    const client = new DesktopDeveloperServicesClient()
    const operation = client.build('Photon.slnx', 'Debug', 23)
    let settled = false
    void operation.promise.finally(() => { settled = true })
    bridge.emit(v2Result('analyze', operation.requestId, 23))
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(v2Result('build', operation.requestId, 22))
    await Promise.resolve()
    expect(settled).toBe(false)
    bridge.emit(v2Result('build', operation.requestId, 23))
    await expect(operation.promise).resolves.toMatchObject({ operation: 'build', revision: 23 })
    client.close()
  })

  it('maps only files inside the explicit workspace root', () => {
    expect(workspaceRelativePath('C:\\workspace\\src\\Program.cs', 'C:\\workspace')).toBe('src/Program.cs')
    expect(workspaceRelativePath('C:\\workspace-other\\Program.cs', 'C:\\workspace')).toBeNull()
  })
})
