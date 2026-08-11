import type { HermesConnectionsClient } from './DesktopHermesConnectionsClient'
import type { ConnectionCatalogEntry, ConnectionMetadata, ConnectionReview } from './contracts'

export type ConnectionsSnapshot = {
  status: 'idle' | 'loading' | 'ready' | 'unavailable' | 'error'
  entries: ConnectionMetadata[]
  pendingReview: ConnectionReview | null
  working: boolean
  message: string | null
}

const initialSnapshot: ConnectionsSnapshot = {
  status: 'idle',
  entries: [],
  pendingReview: null,
  working: false,
  message: null,
}

export class HermesConnectionsController {
  private snapshot: ConnectionsSnapshot = initialSnapshot
  private readonly listeners = new Set<() => void>()
  private activeAbort: AbortController | null = null
  private generation = 0

  constructor(private readonly client: HermesConnectionsClient, readonly profileId: string) {}

  getSnapshot = () => this.snapshot
  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  async load() {
    const generation = this.beginOperation({ status: 'loading', message: null })
    const result = await this.client.list(this.activeAbort?.signal)
    if (!this.current(generation)) return
    if (result.kind === 'success') this.update({ status: 'ready', entries: result.value, working: false, message: null })
    else if (result.kind === 'unavailable') this.update({ status: 'unavailable', entries: [], working: false, message: result.message })
    else this.update({ status: 'error', working: false, message: result.message })
  }

  async beginChange(catalog: ConnectionCatalogEntry, existing?: ConnectionMetadata) {
    const generation = this.beginOperation({ message: null })
    const result = await this.client.beginChange({
      profileId: this.profileId,
      providerId: catalog.providerId,
      slotId: catalog.slotId,
      authKind: catalog.authKind,
      sourceKind: catalog.sourceKind,
      purposes: catalog.purposes,
      ...(existing ? { existingReference: existing.connectionRef } : {}),
      expectedRevision: existing?.revision ?? 0,
    }, this.activeAbort?.signal)
    if (!this.current(generation)) return
    if (result.kind === 'success') this.update({ pendingReview: result.value, working: false })
    else this.update({ working: false, message: result.message })
  }

  async beginRemove(entry: ConnectionMetadata) {
    const generation = this.beginOperation({ message: null })
    const result = await this.client.beginRemove(entry.connectionRef, entry.revision, this.activeAbort?.signal)
    if (!this.current(generation)) return
    if (result.kind === 'success') this.update({ pendingReview: result.value, working: false })
    else this.update({ working: false, message: result.message })
  }

  async commitPending() {
    const review = this.snapshot.pendingReview
    if (!review) return
    const generation = this.beginOperation({ message: null })
    const result = await this.client.commit(review.reviewHandle, this.activeAbort?.signal)
    if (!this.current(generation)) return
    if (result.kind !== 'success') {
      this.update({ pendingReview: null, working: false, message: result.message })
      return
    }
    this.update({ pendingReview: null, working: false, message: null })
    await this.load()
  }

  async cancelPending() {
    const review = this.snapshot.pendingReview
    if (!review) return
    const generation = this.beginOperation({ message: null })
    const result = await this.client.cancel(review.reviewHandle, this.activeAbort?.signal)
    if (!this.current(generation)) return
    this.update({ pendingReview: null, working: false, ...(result.kind === 'failure' ? { message: result.message } : {}) })
  }

  dispose() {
    this.generation++
    this.activeAbort?.abort()
    this.activeAbort = null
    this.listeners.clear()
  }

  private beginOperation(patch: Partial<ConnectionsSnapshot>) {
    this.generation++
    this.activeAbort?.abort()
    this.activeAbort = new AbortController()
    this.update({ ...patch, working: true })
    return this.generation
  }

  private current(generation: number) {
    return generation === this.generation
  }

  private update(patch: Partial<ConnectionsSnapshot>) {
    this.snapshot = { ...this.snapshot, ...patch }
    this.listeners.forEach((listener) => listener())
  }
}
