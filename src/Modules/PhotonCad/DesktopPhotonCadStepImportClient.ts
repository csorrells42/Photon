import { isPhotonCadIdentifier } from './PhotonCadContract'
import { normalizePhotonCadProjectDocument } from './DesktopPhotonCadProjectClient'
import type { PhotonCadProjectDocument } from './PhotonCadProjectContract'
import type { PhotonCadProjectWebViewBridge } from './DesktopPhotonCadProjectClient'

const REQUEST_TIMEOUT_MS = 600_000

export type PhotonCadStepImportResult = {
  contractVersion: 1
  requestId: string
  status: 'opened' | 'cancelled' | 'rejected' | 'unavailable'
  reason: string
  document?: PhotonCadProjectDocument
}

export type PhotonCadStepImportController = {
  importStep: (requestId: string) => Promise<PhotonCadStepImportResult>
  cancelPending?: () => void
  close?: () => void
}

type Options = {
  getBridge?: () => PhotonCadProjectWebViewBridge | null
  timeoutMs?: number
}

function browserBridge(): PhotonCadProjectWebViewBridge | null {
  if (typeof window === 'undefined') return null
  return (window as Window & { chrome?: { webview?: PhotonCadProjectWebViewBridge } }).chrome?.webview ?? null
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]) {
  const actual = Object.keys(value).sort()
  return actual.length === expected.length && actual.every((key, index) => key === [...expected].sort()[index])
}

export function normalizePhotonCadStepImportResult(value: unknown): PhotonCadStepImportResult | null {
  const raw = record(value)
  if (!raw || raw.contractVersion !== 1 || !isPhotonCadIdentifier(raw.requestId)
    || !isPhotonCadIdentifier(raw.reason)
    || !['opened', 'cancelled', 'rejected', 'unavailable'].includes(String(raw.status))) return null
  if (raw.status === 'opened') {
    if (!exactKeys(raw, ['contractVersion', 'document', 'reason', 'requestId', 'status'])) return null
    const document = normalizePhotonCadProjectDocument(raw.document)
    return document
      ? { contractVersion: 1, requestId: raw.requestId, status: 'opened', reason: raw.reason, document }
      : null
  }
  if (!exactKeys(raw, ['contractVersion', 'reason', 'requestId', 'status'])) return null
  return {
    contractVersion: 1,
    requestId: raw.requestId,
    status: raw.status as PhotonCadStepImportResult['status'],
    reason: raw.reason,
  }
}

export class DesktopPhotonCadStepImportClient implements PhotonCadStepImportController {
  private readonly getBridge: () => PhotonCadProjectWebViewBridge | null
  private readonly timeoutMs: number
  private bridge: PhotonCadProjectWebViewBridge | null = null
  private pending: {
    requestId: string
    resolve: (result: PhotonCadStepImportResult) => void
    reject: (error: Error) => void
    timeout: ReturnType<typeof setTimeout>
  } | null = null
  private closed = false

  private readonly receive = (event: MessageEvent) => {
    const frame = record(event.data)
    const pending = this.pending
    if (!frame || !pending) return
    if (frame.type === 'photonCad.error' && frame.requestId === pending.requestId) {
      this.finish(new Error(isPhotonCadIdentifier(frame.code) ? frame.code : 'step-import-host-error'))
      return
    }
    if (!exactKeys(frame, ['type', 'value', 'version']) || frame.type !== 'photonCad.step.import.result' || frame.version !== 1) return
    const result = normalizePhotonCadStepImportResult(frame.value)
    if (!result || result.requestId !== pending.requestId) return
    this.finish(undefined, result)
  }

  public constructor(options: Options = {}) {
    this.getBridge = options.getBridge ?? browserBridge
    this.timeoutMs = options.timeoutMs ?? REQUEST_TIMEOUT_MS
    if (!Number.isSafeInteger(this.timeoutMs) || this.timeoutMs < 1_000 || this.timeoutMs > REQUEST_TIMEOUT_MS)
      throw new Error('invalid-step-import-timeout')
  }

  public importStep(requestId: string): Promise<PhotonCadStepImportResult> {
    if (!isPhotonCadIdentifier(requestId)) return Promise.reject(new Error('invalid-step-import-request-id'))
    if (this.closed) return Promise.resolve({ contractVersion: 1, requestId, status: 'unavailable', reason: 'client-closed' })
    if (this.pending) return Promise.reject(new Error('step-import-request-already-active'))
    const bridge = this.getBridge()
    if (!bridge) return Promise.resolve({ contractVersion: 1, requestId, status: 'unavailable', reason: 'desktop-host-unavailable' })
    this.bridge = bridge
    bridge.addEventListener('message', this.receive)
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.cancelPending()
        reject(new Error('step-import-request-timeout'))
      }, this.timeoutMs)
      this.pending = { requestId, resolve, reject, timeout }
      bridge.postMessage({ type: 'photonCad.step.import', version: 1, contractVersion: 1, requestId })
    })
  }

  public cancelPending() {
    const pending = this.pending
    if (!pending) return
    this.bridge?.postMessage({
      type: 'photonCad.cancel',
      version: 1,
      contractVersion: 1,
      requestId: `${pending.requestId}:cancel`,
      targetRequestId: pending.requestId,
    })
    this.finish(new Error('step-import-request-cancelled'))
  }

  public close() {
    if (this.closed) return
    this.closed = true
    this.cancelPending()
    this.detach()
  }

  private finish(error?: Error, result?: PhotonCadStepImportResult) {
    const pending = this.pending
    if (!pending) return
    this.pending = null
    clearTimeout(pending.timeout)
    this.detach()
    if (error) pending.reject(error)
    else if (result) pending.resolve(result)
    else pending.reject(new Error('step-import-result-unavailable'))
  }

  private detach() {
    this.bridge?.removeEventListener('message', this.receive)
    this.bridge = null
  }
}
