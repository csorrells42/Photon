import { defineConfig } from 'vite'
import type { Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash, createHmac } from 'node:crypto'

const installRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

export function resolveWorkbenchWorkspaceRoot(configured: string | undefined, fallbackRoot = installRoot) {
  const value = configured?.trim()
  if (!value) return path.resolve(fallbackRoot)
  if (value.length > 1024 || /[\u0000-\u001f]/.test(value) || !path.isAbsolute(value)) {
    throw new Error('HERMES_WORKSPACE_PATH must be a bounded absolute path.')
  }
  const resolved = path.resolve(value)
  if (resolved.toLocaleLowerCase() === path.parse(resolved).root.toLocaleLowerCase()) {
    throw new Error('HERMES_WORKSPACE_PATH cannot be a drive root.')
  }
  return resolved
}

const workspaceRoot = resolveWorkbenchWorkspaceRoot(process.env.HERMES_WORKSPACE_PATH)
const workspaceRootReal = await fs.realpath(workspaceRoot)
if (workspaceRootReal.toLocaleLowerCase() !== workspaceRoot.toLocaleLowerCase()) {
  throw new Error('HERMES_WORKSPACE_PATH cannot be a symbolic link or junction.')
}
const runtimeIdentityPath = path.join(installRoot, 'logs', 'runtime-identity.json')
const blockedSegments = new Set(['.git', 'node_modules', 'data', 'logs'])
const blockedFileNames = new Set(['credentials.json', 'auth.json', '.npmrc', '.pypirc', 'nuget.config'])
const blockedSecretExtensions = new Set(['.key', '.pem', '.pfx', '.p12'])
const editorReadMaxBytes = 2 * 1024 * 1024
const workbenchNonce = process.env.HERMES_WORKBENCH_NONCE?.trim() ?? ''

export function createWorkbenchIdentityProof(nonce: string, challenge: string) {
  if (!/^[a-f0-9]{64}$/i.test(nonce) || !/^[a-f0-9]{64}$/i.test(challenge)) return null
  return createHmac('sha256', Buffer.from(nonce, 'hex'))
    .update(`hermes-workbench-v1:${challenge.toLowerCase()}`, 'utf8')
    .digest('hex')
}

export function isBlockedWorkspacePath(relativePath: string) {
  return relativePath.split(/[\\/]+/).some((segment) => {
    const canonical = segment.toLowerCase()
    return blockedSegments.has(canonical)
      || blockedFileNames.has(canonical)
      || blockedSecretExtensions.has(path.extname(canonical))
      || canonical === '.env'
      || canonical.startsWith('.env.')
  })
}

async function safeWorkspacePath(relativePath: string) {
  const normalized = relativePath.replaceAll('\\', '/').replace(/^\/+/, '')
  if (isBlockedWorkspacePath(normalized)) throw new Error('That path is intentionally hidden from the Workbench bridge.')

  const candidate = path.resolve(workspaceRoot, normalized || '.')
  const relative = path.relative(workspaceRoot, candidate)
  if (relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Path escapes the Hermes workspace.')

  const real = await fs.realpath(candidate)
  const realRelative = path.relative(workspaceRootReal, real)
  if (realRelative.startsWith('..') || path.isAbsolute(realRelative)) throw new Error('Linked path escapes the Hermes workspace.')
  return { absolute: real, relative: relative.replaceAll('\\', '/') }
}

function workspaceBridge(): Plugin {
  return {
    name: 'hermes-workbench-readonly-workspace',
    configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        if (!request.url?.startsWith('/workbench-api/workspace/')) return next()

        const send = (status: number, body: unknown) => {
          response.statusCode = status
          response.setHeader('Content-Type', 'application/json; charset=utf-8')
          response.setHeader('Cache-Control', 'no-store')
          response.end(JSON.stringify(body))
        }

        if (request.method !== 'GET') return send(405, { error: 'The workspace bridge is read-only.' })

        try {
          const url = new URL(request.url, 'http://127.0.0.1')
          const requestedPath = url.searchParams.get('path') ?? ''
          const target = await safeWorkspacePath(requestedPath)

          if (url.pathname === '/workbench-api/workspace/tree') {
            const entries = await fs.readdir(target.absolute, { withFileTypes: true })
            const visible = entries
              .filter((entry) => !entry.isSymbolicLink() && !isBlockedWorkspacePath(entry.name))
              .map((entry) => ({
                name: entry.name,
                path: [target.relative, entry.name].filter(Boolean).join('/'),
                kind: entry.isDirectory() ? 'directory' : 'file',
              }))
              .sort((left, right) => left.kind === right.kind
                ? left.name.localeCompare(right.name)
                : left.kind === 'directory' ? -1 : 1)
            return send(200, { root: path.basename(workspaceRoot), path: target.relative, entries: visible })
          }

          if (url.pathname === '/workbench-api/workspace/file') {
            const stat = await fs.stat(target.absolute)
            if (!stat.isFile()) return send(400, { error: 'The selected path is not a file.' })
            if (stat.size > editorReadMaxBytes) return send(413, { error: 'File is larger than the 2 MB editor preview limit.' })
            const content = await fs.readFile(target.absolute)
            if (content.includes(0)) return send(415, { error: 'Binary files are not shown in the text editor.' })
            return send(200, {
              path: target.relative,
              content: content.toString('utf8'),
              size: stat.size,
              modifiedAt: stat.mtime.toISOString(),
              sha256: createHash('sha256').update(content).digest('hex'),
            })
          }

          return send(404, { error: 'Unknown workspace bridge endpoint.' })
        } catch (reason) {
          const message = reason instanceof Error ? reason.message : 'Workspace read failed.'
          return send(message.includes('escapes') || message.includes('hidden') ? 403 : 404, { error: message })
        }
      })
    },
  }
}

function runtimeIdentityBridge(): Plugin {
  return {
    name: 'hermes-workbench-runtime-identity',
    configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        if (!request.url?.startsWith('/workbench-api/runtime-identity')) return next()

        const url = new URL(request.url, 'http://127.0.0.1')
        if (url.pathname !== '/workbench-api/runtime-identity') return next()
        const send = (status: number, body: unknown) => {
          response.statusCode = status
          response.setHeader('Content-Type', 'application/json; charset=utf-8')
          response.setHeader('Cache-Control', 'no-store')
          response.end(JSON.stringify(body))
        }
        if (request.method !== 'GET') return send(405, { error: 'Runtime identity is read-only.' })

        try {
          const raw = JSON.parse(await fs.readFile(runtimeIdentityPath, 'utf8')) as Record<string, unknown>
          const string = (key: string, pattern: RegExp, maximum = 256) => {
            const value = typeof raw[key] === 'string' ? raw[key] : ''
            return value.length <= maximum && pattern.test(value) ? value : null
          }
          const observedAtUtc = string('observedAtUtc', /^\d{4}-\d{2}-\d{2}T[^\s]{1,40}$/i, 64)
          const identity = {
            protocolVersion: raw.protocolVersion === 1 ? 1 : 0,
            observedAtUtc,
            containerName: raw.containerName === 'hermes' ? 'hermes' : null,
            imageReference: string('imageReference', /^[a-z0-9./:_-]+$/i),
            imageId: string('imageId', /^sha256:[a-f0-9]{64}$/i, 71),
            repoDigest: string('repoDigest', /^nousresearch\/hermes-agent@sha256:[a-f0-9]{64}$/i, 110),
            revision: string('revision', /^[a-f0-9]{7,64}$/i, 64),
          }
          if (identity.protocolVersion !== 1 || !identity.containerName) return send(503, { error: 'Runtime identity is unavailable.' })
          return send(200, identity)
        } catch {
          return send(503, { error: 'Runtime identity is unavailable.' })
        }
      })
    },
  }
}

function workbenchIdentityBridge(): Plugin {
  return {
    name: 'hermes-workbench-host-identity',
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        if (!request.url?.startsWith('/workbench-api/host-identity')) return next()
        const url = new URL(request.url, 'http://127.0.0.1')
        if (url.pathname !== '/workbench-api/host-identity') return next()
        response.setHeader('Content-Type', 'application/json; charset=utf-8')
        response.setHeader('Cache-Control', 'no-store')
        if (request.method !== 'GET') {
          response.statusCode = 405
          return response.end(JSON.stringify({ error: 'Workbench identity is read-only.' }))
        }
        const challenge = url.searchParams.get('challenge') ?? ''
        const proof = createWorkbenchIdentityProof(workbenchNonce, challenge)
        if (!proof) {
          response.statusCode = 503
          return response.end(JSON.stringify({ error: 'Workbench host identity is unavailable.' }))
        }
        response.statusCode = 200
        return response.end(JSON.stringify({ protocolVersion: 1, challenge: challenge.toLowerCase(), proof }))
      })
    },
  }
}

export default defineConfig({
  plugins: [react(), workspaceBridge(), runtimeIdentityBridge(), workbenchIdentityBridge()],
  server: {
    host: '127.0.0.1',
    port: 4173,
    strictPort: true,
    watch: {
      // .NET tooling can create and lock generated assemblies anywhere under src
      // (for example, parallel CLI workers under src/Tools). Chokidar must never
      // try to watch those transient outputs or one build can stop the UI server.
      ignored: ['**/bin/**', '**/obj/**', '**/.git/**', '**/artifacts/**', '**/dist/**'],
    },
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:9119',
        changeOrigin: false,
        ws: true,
      },
      '/auth': {
        target: 'http://127.0.0.1:9119',
        changeOrigin: false,
      },
    },
  },
})
