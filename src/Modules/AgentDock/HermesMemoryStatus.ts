import { useEffect, useState } from 'react'

export type HermesMemoryStatus = {
  state: 'checking' | 'ready' | 'unavailable'
  label: string
  title: string
}

const CHECKING: HermesMemoryStatus = {
  state: 'checking',
  label: 'Memory checking',
  title: 'Checking the authenticated Hermes memory provider.',
}

const DISCONNECTED: HermesMemoryStatus = {
  state: 'unavailable',
  label: 'Memory status available after connection',
  title: 'Connect to Hermes to verify the authenticated memory provider.',
}

export function memoryStatusForConnection(connection: string): HermesMemoryStatus {
  return connection === 'open' ? CHECKING : DISCONNECTED
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export function normalizeHermesMemoryStatus(value: unknown): HermesMemoryStatus {
  const root = record(value)
  if (!root || typeof root.active !== 'string' || !Array.isArray(root.providers) || root.providers.length > 64) {
    return { state: 'unavailable', label: 'Memory unavailable', title: 'Hermes did not return a valid memory status.' }
  }
  const provider = root.providers.map(record).find((candidate) => candidate?.name === 'mem0')
  if (root.active === 'mem0' && provider?.available === true && provider.configured === true && provider.status === 'ready') {
    return { state: 'ready', label: 'Memory ready · Mem0 local', title: 'Authenticated Mem0 memory is ready through the local Docker runtime.' }
  }
  if (!provider) return { state: 'unavailable', label: 'Memory unavailable · Mem0 missing', title: 'The Mem0 provider is not installed in the active Hermes runtime.' }
  if (root.active !== 'mem0') return { state: 'unavailable', label: 'Memory unavailable · Not active', title: 'Mem0 is installed but is not the active Hermes memory provider.' }
  if (provider.status === 'needs_config') return { state: 'unavailable', label: 'Memory unavailable · Setup required', title: 'Mem0 requires configuration before it can be used.' }
  return { state: 'unavailable', label: 'Memory unavailable · Runtime not ready', title: 'The configured Mem0 provider is not ready.' }
}

export function useHermesMemoryStatus(connection: string): HermesMemoryStatus {
  const [status, setStatus] = useState<HermesMemoryStatus>(() => memoryStatusForConnection(connection))

  useEffect(() => {
    if (connection !== 'open') {
      setStatus(memoryStatusForConnection(connection))
      return
    }
    let closed = false
    let active: AbortController | null = null
    const refresh = async () => {
      active?.abort()
      const controller = new AbortController()
      active = controller
      try {
        const response = await globalThis.fetch('/api/memory', {
          credentials: 'include',
          cache: 'no-store',
          signal: controller.signal,
        })
        if (!response.ok) throw new Error('memory-status-unavailable')
        const next = normalizeHermesMemoryStatus(await response.json())
        if (!closed && !controller.signal.aborted) setStatus(next)
      } catch {
        if (!closed && !controller.signal.aborted) {
          setStatus({ state: 'unavailable', label: 'Memory unavailable', title: 'The authenticated memory status could not be verified.' })
        }
      }
    }
    void refresh()
    const interval = globalThis.setInterval(() => void refresh(), 30_000)
    return () => {
      closed = true
      active?.abort()
      globalThis.clearInterval(interval)
    }
  }, [connection])

  return status
}
