import { afterEach, describe, expect, it, vi } from 'vitest'
import { DesktopHermesConnectionsClient, type HermesConnectionsClient } from './DesktopHermesConnectionsClient'
import { HermesConnectionsController } from './HermesConnectionsController'
import {
  assertSafeChangeIntent,
  type ConnectionCatalogEntry,
  type ConnectionClientResult,
  type ConnectionMetadata,
  type ConnectionReview,
  hermesConnectionsProtocolVersion,
  normalizeCatalogEntry,
  normalizeConnectionMetadata,
  normalizeConnectionReview,
} from './contracts'

const connectionRef = `hcv2_${'A'.repeat(43)}`
const reviewHandle = `hcr2_${'B'.repeat(43)}`
const metadata: ConnectionMetadata = {
  connectionRef,
  profileId: 'profile-1',
  providerId: 'openai',
  slotId: 'primary',
  authKind: 'oauth',
  sourceKind: 'native',
  purposes: ['model:chat', 'oauth-refresh'],
  revision: 1,
  updatedAt: '2026-08-10T12:00:00Z',
  configured: true,
}
const catalog: ConnectionCatalogEntry = {
  providerId: 'openai',
  displayName: 'OpenAI',
  slotId: 'primary',
  authKind: 'oauth',
  sourceKind: 'native',
  purposes: ['model:chat', 'oauth-refresh'],
  supportsNativeChange: true,
}
const review: ConnectionReview = {
  reviewHandle,
  action: 'change',
  profileId: 'profile-1',
  providerId: 'openai',
  slotId: 'primary',
  expectedRevision: 0,
  expiresAt: '2026-08-10T12:02:00Z',
}

describe('Hermes Connections v2 contracts', () => {
  it('accepts bounded metadata and rejects malformed or raw-shaped values', () => {
    expect(normalizeConnectionMetadata(metadata)).toEqual(metadata)
    expect(normalizeConnectionMetadata({ ...metadata, connectionRef: 'openai-primary' })).toBeNull()
    expect(normalizeConnectionMetadata({ ...metadata, revision: 0 })).toBeNull()
    expect(normalizeConnectionMetadata({ ...metadata, purposes: ['arbitrary'] })).toBeNull()
    expect(normalizeConnectionMetadata({ ...metadata, configured: false })).toBeNull()
    expect(normalizeConnectionMetadata({ ...metadata, token: 'must-not-enter-renderer' })).toBeNull()
  })

  it('normalizes catalog and review without a credential value field', () => {
    expect(normalizeCatalogEntry(catalog)).toEqual(catalog)
    expect(normalizeConnectionReview(review)).toEqual(review)
    expect(normalizeConnectionReview({ ...review, action: 'remove' })).toBeNull()
    const intent = assertSafeChangeIntent({ ...catalog, profileId: 'profile-1', expectedRevision: 0 })
    expect(Object.keys(intent).sort()).toEqual(['authKind', 'expectedRevision', 'profileId', 'providerId', 'purposes', 'slotId', 'sourceKind'])
    expect(JSON.stringify(intent)).not.toMatch(/secret|password|token|value/i)
  })
})

describe('DesktopHermesConnectionsClient', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('correlates metadata-only v2 frames and posts no raw operation', async () => {
    const listeners = new Set<(event: MessageEvent) => void>()
    const posted: unknown[] = []
    const bridge = {
      addEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.add(listener),
      removeEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.delete(listener),
      postMessage: (message: unknown) => {
        posted.push(message)
        const request = message as { requestId: string }
        queueMicrotask(() => listeners.forEach((listener) => listener({ data: {
          type: 'connections.list.result',
          version: hermesConnectionsProtocolVersion,
          requestId: request.requestId,
          entries: [metadata],
        } } as MessageEvent)))
      },
    }
    vi.stubGlobal('window', {
      __HERMES_DESKTOP_HOST__: { capabilities: { connections: true, connectionsVersion: 2 } },
      chrome: { webview: bridge },
      setTimeout,
      clearTimeout,
    })
    const result = await new DesktopHermesConnectionsClient().list()
    expect(result).toEqual({ kind: 'success', value: [metadata] })
    expect(posted).toHaveLength(1)
    expect(JSON.stringify(posted[0])).not.toMatch(/secret|password|token|reveal|export/i)
    expect((posted[0] as { type: string }).type).toBe('connections.list')
  })

  it('is honestly unavailable without the exact native capability', async () => {
    vi.stubGlobal('window', { setTimeout, clearTimeout })
    await expect(new DesktopHermesConnectionsClient().list()).resolves.toEqual({
      kind: 'unavailable',
      message: 'Native Connections & Credentials is not registered in this build.',
    })
  })

  it('does not time out while the native credential dialog is waiting for the user', async () => {
    vi.useFakeTimers()
    const listeners = new Set<(event: MessageEvent) => void>()
    const bridge = {
      addEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.add(listener),
      removeEventListener: (_type: 'message', listener: (event: MessageEvent) => void) => listeners.delete(listener),
      postMessage: (message: unknown) => {
        const request = message as { requestId: string }
        window.setTimeout(() => listeners.forEach((listener) => listener({ data: {
          type: 'connections.review.ready',
          version: hermesConnectionsProtocolVersion,
          requestId: request.requestId,
          review,
        } } as MessageEvent)), 30_000)
      },
    }
    vi.stubGlobal('window', {
      __HERMES_DESKTOP_HOST__: { capabilities: { connections: true, connectionsVersion: 2 } },
      chrome: { webview: bridge },
      setTimeout,
      clearTimeout,
    })
    const pending = new DesktopHermesConnectionsClient().beginChange({
      profileId: 'profile-1',
      providerId: catalog.providerId,
      slotId: catalog.slotId,
      authKind: catalog.authKind,
      sourceKind: catalog.sourceKind,
      purposes: catalog.purposes,
      expectedRevision: 0,
    })
    await vi.advanceTimersByTimeAsync(30_000)
    await expect(pending).resolves.toEqual({ kind: 'success', value: review })
    vi.useRealTimers()
  })
})

describe('HermesConnectionsController', () => {
  it('keeps collection, review, commit and refresh separate', async () => {
    const client = new FakeClient()
    const controller = new HermesConnectionsController(client, 'profile-1')
    await controller.load()
    expect(controller.getSnapshot().entries).toEqual([])
    await controller.beginChange(catalog)
    expect(controller.getSnapshot().pendingReview).toEqual(review)
    expect(client.lastIntent && JSON.stringify(client.lastIntent)).not.toMatch(/secret|password|token|value/i)
    expect(client.commits).toBe(0)
    await controller.commitPending()
    expect(client.commits).toBe(1)
    expect(controller.getSnapshot().entries).toEqual([metadata])
    expect(controller.getSnapshot().pendingReview).toBeNull()
    controller.dispose()
  })
})

class FakeClient implements HermesConnectionsClient {
  entries: ConnectionMetadata[] = []
  lastIntent: unknown = null
  commits = 0

  available() { return true }
  async list(): Promise<ConnectionClientResult<ConnectionMetadata[]>> { return { kind: 'success', value: this.entries } }
  async beginChange(intent: unknown): Promise<ConnectionClientResult<ConnectionReview>> {
    this.lastIntent = intent
    return { kind: 'success', value: review }
  }
  async forceOpenRouterSession() {
    return { kind: 'success' as const, value: { providerId: 'openrouter' as const, revision: 1, sessionOnly: true as const } }
  }
  async beginRemove(): Promise<ConnectionClientResult<ConnectionReview>> { return { kind: 'failure', code: 'unused', message: 'unused', retryable: false } }
  async commit(): Promise<ConnectionClientResult<ConnectionMetadata | null>> {
    this.commits++
    this.entries = [metadata]
    return { kind: 'success', value: metadata }
  }
  async cancel(): Promise<ConnectionClientResult<null>> { return { kind: 'success', value: null } }
}
