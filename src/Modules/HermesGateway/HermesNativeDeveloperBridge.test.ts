import { describe, expect, it, vi } from 'vitest'
import { handleNativeDeveloperRequest, normalizeNativeDeveloperRequest } from './HermesNativeDeveloperBridge'

const event = (payload: Record<string, unknown>) => ({ type: 'developer.native.request', payload })
const debuggerController = () => ({
  getSnapshot: vi.fn(() => ({ state: 'inactive', targets: [] as string[], selectedProgram: '', output: '', error: '' })),
  refreshTargets: vi.fn(async () => undefined),
  setSelectedProgram: vi.fn(),
  launch: vi.fn(async () => undefined),
  disconnect: vi.fn(async () => undefined),
})

describe('Hermes native developer bridge', () => {
  it('accepts only correlated workspace-relative typed operations', () => {
    expect(normalizeNativeDeveloperRequest(event({ request_id: 'a1b2c3d4', action: 'build', project_path: 'Chess/Chess.csproj', configuration: 'Release' })))
      .toEqual({ requestId: 'a1b2c3d4', operation: 'build', projectPath: 'Chess/Chess.csproj', arguments: [], configuration: 'Release' })
    expect(normalizeNativeDeveloperRequest(event({ request_id: 'a1b2c3d4', action: 'build', project_path: 'C:\\escape.csproj' }))).toBeNull()
    expect(normalizeNativeDeveloperRequest(event({ request_id: 'a1b2c3d4', action: 'build', project_path: '../escape.csproj' }))).toBeNull()
    expect(normalizeNativeDeveloperRequest(event({ request_id: 'wrong', action: 'describe' }))).toBeNull()
  })

  it('runs a native build and returns a bounded typed projection', async () => {
    const request = vi.fn(async (_method: string, _params?: Record<string, unknown>, _timeoutMs?: number) => ({ resolved: true }))
    const build = vi.fn(() => ({ promise: Promise.resolve({
      requestId: 'native-1', revision: 7, operation: 'build' as const, stale: false,
      workspaceRoot: 'C:\\Users\\private', succeeded: true, exitCode: 0, wasCancelled: false,
      diagnostics: [], output: { standardOutput: 'Build succeeded.', standardError: '', truncated: false, droppedCharacters: 0 },
    }) }))
    const handled = await handleNativeDeveloperRequest(
      event({ request_id: 'a1b2c3d4', action: 'build', project_path: 'Chess/Chess.csproj', configuration: 'Release' }),
      { request },
      { describe: vi.fn(), build, analyze: vi.fn() },
      debuggerController(),
    )
    expect(handled).toBe(true)
    expect(build).toHaveBeenCalledWith('Chess/Chess.csproj', 'Release')
    expect(request).toHaveBeenCalledWith('developer.native.respond', expect.objectContaining({ request_id: 'a1b2c3d4' }), 15_000)
    const payload = JSON.parse((request.mock.calls[0][1] as { text: string }).text)
    expect(payload).toMatchObject({ ok: true, operation: 'build', projectPath: 'Chess/Chess.csproj', exitCode: 0 })
    expect(JSON.stringify(payload)).not.toContain('Users')
  })

  it('returns a correlated typed failure instead of dropping the tool wait', async () => {
    const request = vi.fn(async (_method: string, _params?: Record<string, unknown>, _timeoutMs?: number) => ({ resolved: true }))
    await handleNativeDeveloperRequest(
      event({ request_id: 'deadbeef', action: 'analyze', project_path: 'Chess/Chess.csproj' }),
      { request },
      { describe: vi.fn(), build: vi.fn(), analyze: vi.fn(() => ({ promise: Promise.reject(new Error('SDK unavailable')) })) },
      debuggerController(),
    )
    const payload = JSON.parse((request.mock.calls[0][1] as { text: string }).text)
    expect(payload).toEqual({ ok: false, operation: 'analyze', projectPath: 'Chess/Chess.csproj', error: 'SDK unavailable' })
  })

  it('launches only an exact discovered Windows target and can stop it', async () => {
    const request = vi.fn(async (_method: string, _params?: Record<string, unknown>, _timeoutMs?: number) => ({ resolved: true }))
    let snapshot = { state: 'inactive', targets: ['Chess/bin/Debug/net10.0-windows/Chess.exe'], selectedProgram: '', output: '', error: '' }
    const debuggerHost = {
      getSnapshot: vi.fn(() => snapshot),
      refreshTargets: vi.fn(async () => undefined),
      setSelectedProgram: vi.fn((selectedProgram: string) => { snapshot = { ...snapshot, selectedProgram } }),
      launch: vi.fn(async () => { snapshot = { ...snapshot, state: 'running' } }),
      disconnect: vi.fn(async () => { snapshot = { ...snapshot, state: 'inactive' } }),
    }
    const client = { describe: vi.fn(), build: vi.fn(), analyze: vi.fn() }
    await handleNativeDeveloperRequest(
      event({ request_id: 'feedbeef', action: 'run', program_path: 'Chess/bin/Debug/net10.0-windows/Chess.exe' }),
      { request }, client, debuggerHost,
    )
    expect(debuggerHost.setSelectedProgram).toHaveBeenCalledWith('Chess/bin/Debug/net10.0-windows/Chess.exe')
    expect(debuggerHost.launch).toHaveBeenCalledWith(false, [])
    expect(JSON.parse((request.mock.calls[0][1] as { text: string }).text)).toMatchObject({ ok: true, operation: 'run', state: 'running' })

    await handleNativeDeveloperRequest(event({ request_id: 'deadbeef', action: 'stop' }), { request }, client, debuggerHost)
    expect(debuggerHost.disconnect).toHaveBeenCalled()
  })
})
