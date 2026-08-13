import type { HermesGatewayEvent } from './HermesGatewayClient'
import type {
  DeveloperBuildResult,
  DeveloperConfiguration,
  DeveloperServicesDescription,
} from '../DeveloperServices/DesktopDeveloperServicesClient'
import { runWindowsAdministratorOperation } from './DesktopWindowsAdministratorClient'

type NativeDeveloperOperation = 'describe' | 'build' | 'analyze' | 'targets' | 'run' | 'stop' | 'administrator'

type NativeDeveloperClient = {
  describe(): Promise<DeveloperServicesDescription>
  build(targetPath: string, configuration?: DeveloperConfiguration): { promise: Promise<DeveloperBuildResult> }
  analyze(targetPath: string, configuration?: DeveloperConfiguration): { promise: Promise<DeveloperBuildResult> }
}

type GatewayResponder = {
  request(method: string, params?: Record<string, unknown>, timeoutMs?: number): Promise<unknown>
}

type NativeDebugger = {
  getSnapshot(): {
    state: string
    targets: readonly string[]
    selectedProgram: string
    output: string
    error: string
  }
  refreshTargets(configuration: DeveloperConfiguration): Promise<void>
  setSelectedProgram(value: string): void
  launch(stopAtEntry?: boolean, argumentsList?: readonly string[]): Promise<void>
  disconnect(): Promise<void>
}

type NativeDeveloperRequest = {
  requestId: string
  operation: NativeDeveloperOperation
  projectPath?: string
  programPath?: string
  arguments: string[]
  configuration: DeveloperConfiguration
  script?: string
  reason?: string
}

function text(value: unknown, maximum: number) {
  return typeof value === 'string' ? value.trim().slice(0, maximum) : ''
}

function workspacePath(value: unknown) {
  const candidate = text(value, 2_048).replaceAll('\\', '/')
  if (!candidate || candidate.startsWith('/') || /^[a-zA-Z]:/.test(candidate)) return null
  const parts = candidate.split('/')
  if (parts.some((part) => !part || part === '.' || part === '..')) return null
  return /\.(?:sln|slnx|csproj)$/i.test(candidate) ? candidate : null
}

function programPath(value: unknown) {
  const candidate = text(value, 2_048).replaceAll('\\', '/')
  if (!candidate || candidate.startsWith('/') || /^[a-zA-Z]:/.test(candidate)) return null
  const parts = candidate.split('/')
  if (parts.some((part) => !part || part === '.' || part === '..')) return null
  return /\.(?:exe|dll)$/i.test(candidate) ? candidate : null
}

function programArguments(value: unknown) {
  if (value === undefined) return []
  if (!Array.isArray(value) || value.length > 32) return null
  const result = value.map((argument) => text(argument, 1_024))
  return result.some((argument) => argument.includes('\0')) ? null : result
}

export function normalizeNativeDeveloperRequest(event: HermesGatewayEvent): NativeDeveloperRequest | null {
  if (event.type !== 'developer.native.request') return null
  const requestId = text(event.payload?.request_id, 64)
  const operation = text(event.payload?.action, 32).toLowerCase()
  const configuration = event.payload?.configuration === 'Release' ? 'Release' : 'Debug'
  if (!/^[a-f0-9]{8}$/i.test(requestId)
    || !['describe', 'build', 'analyze', 'targets', 'run', 'stop', 'administrator'].includes(operation)) return null
  const args = programArguments(event.payload?.arguments)
  if (args === null) return null
  if (operation === 'describe' || operation === 'targets' || operation === 'stop') {
    return { requestId, operation: operation as NativeDeveloperOperation, arguments: args, configuration }
  }
  if (operation === 'run') {
    const program = programPath(event.payload?.program_path)
    return program ? { requestId, operation, programPath: program, arguments: args, configuration } : null
  }
  if (operation === 'administrator') {
    const script = text(event.payload?.script, 16 * 1_024)
    const reason = text(event.payload?.reason, 512)
    return script && reason
      ? { requestId, operation, arguments: args, configuration, script, reason }
      : null
  }
  const project = workspacePath(event.payload?.project_path)
  return project ? { requestId, operation: operation as NativeDeveloperOperation, projectPath: project, arguments: args, configuration } : null
}

function projectDescription(value: DeveloperServicesDescription) {
  return {
    ok: true,
    operation: 'describe',
    availability: value.availability,
    targets: value.targets,
    providers: value.providers.map((provider) => ({
      providerId: provider.providerId,
      displayName: provider.displayName,
      providerVersion: provider.providerVersion,
      languageIds: provider.languageIds,
      projectKinds: provider.projectKinds,
      build: provider.build,
      lsp: provider.lsp,
      dap: provider.dap,
      availability: provider.availability,
    })),
  }
}

function projectBuild(value: DeveloperBuildResult, projectPath: string) {
  return {
    ok: value.succeeded && !value.stale && !value.wasCancelled,
    operation: value.operation,
    projectPath,
    revision: value.revision,
    succeeded: value.succeeded,
    stale: value.stale,
    wasCancelled: value.wasCancelled,
    exitCode: value.exitCode,
    failureCode: value.failureCode,
    failureMessage: value.failureMessage,
    diagnostics: value.diagnostics,
    output: value.output,
  }
}

async function respond(gateway: GatewayResponder, requestId: string, result: unknown) {
  await gateway.request('developer.native.respond', {
    request_id: requestId,
    text: JSON.stringify(result),
  }, 15_000)
}

export async function handleNativeDeveloperRequest(
  event: HermesGatewayEvent,
  gateway: GatewayResponder,
  client: NativeDeveloperClient,
  debuggerController: NativeDebugger,
) {
  const request = normalizeNativeDeveloperRequest(event)
  const requestId = text(event.payload?.request_id, 64)
  if (!request) {
    if (/^[a-f0-9]{8}$/i.test(requestId)) {
      await respond(gateway, requestId, { ok: false, error: 'The native developer request is invalid.' })
    }
    return false
  }

  try {
    let result: unknown
    if (request.operation === 'describe') {
      result = projectDescription(await client.describe())
    } else if (request.operation === 'build' || request.operation === 'analyze') {
      result = projectBuild(
        await client[request.operation](request.projectPath!, request.configuration).promise,
        request.projectPath!,
      )
    } else if (request.operation === 'targets') {
      await debuggerController.refreshTargets(request.configuration)
      const snapshot = debuggerController.getSnapshot()
      result = { ok: !snapshot.error, operation: 'targets', targets: snapshot.targets, state: snapshot.state, error: snapshot.error || undefined }
    } else if (request.operation === 'run') {
      await debuggerController.refreshTargets(request.configuration)
      let snapshot = debuggerController.getSnapshot()
      if (!snapshot.targets.includes(request.programPath!)) throw new Error('The selected program is not a discovered Windows debug target.')
      debuggerController.setSelectedProgram(request.programPath!)
      await debuggerController.launch(false, request.arguments)
      snapshot = debuggerController.getSnapshot()
      result = {
        ok: !snapshot.error && !['inactive', 'faulted', 'unknown'].includes(snapshot.state),
        operation: 'run',
        programPath: request.programPath,
        state: snapshot.state,
        output: text(snapshot.output, 256 * 1024),
        error: snapshot.error || undefined,
      }
    } else if (request.operation === 'administrator') {
      const elevated = await runWindowsAdministratorOperation(request.script!, request.reason!)
      result = {
        ok: elevated.succeeded,
        operation: 'administrator',
        code: elevated.code,
        message: elevated.message,
        ...(elevated.exitCode === undefined ? {} : { exitCode: elevated.exitCode }),
        output: elevated.output,
        outputTruncated: elevated.outputTruncated,
      }
    } else {
      await debuggerController.disconnect()
      const snapshot = debuggerController.getSnapshot()
      result = { ok: !snapshot.error, operation: 'stop', state: snapshot.state, error: snapshot.error || undefined }
    }
    await respond(gateway, request.requestId, result)
  } catch (reason) {
    await respond(gateway, request.requestId, {
      ok: false,
      operation: request.operation,
      ...(request.projectPath ? { projectPath: request.projectPath } : {}),
      ...(request.programPath ? { programPath: request.programPath } : {}),
      error: text(reason instanceof Error ? reason.message : String(reason), 2_000)
        || 'The native developer operation failed.',
    })
  }
  return true
}
