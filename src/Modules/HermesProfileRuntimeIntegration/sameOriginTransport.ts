import type {
  LiveTransport,
  LiveTransportResponse,
} from '../HermesProfileRuntimeLive'

export type SameOriginProfileRuntimeFetch = (
  input: string,
  init: {
    readonly method: 'GET'
    readonly credentials: 'include'
    readonly signal: AbortSignal
    readonly headers: Readonly<Record<string, string>>
  },
) => Promise<LiveTransportResponse>

const SAFE_HEADERS = Object.freeze({ Accept: 'application/json' })

export function createSameOriginProfileRuntimeTransport(
  fetchTransport: SameOriginProfileRuntimeFetch,
): LiveTransport {
  if (typeof fetchTransport !== 'function') throw new Error('An injected same-origin fetch implementation is required.')

  return async (url, init) => {
    assertSameOriginApiUrl(url)
    if (init.method !== 'GET') throw new Error('Profile runtime live transport permits GET only.')
    assertLiveHeaders(init.headers)

    return fetchTransport(url, {
      method: 'GET',
      credentials: 'include',
      signal: init.signal,
      headers: SAFE_HEADERS,
    })
  }
}

function assertSameOriginApiUrl(value: string): void {
  if (
    typeof value !== 'string'
    || value.length === 0
    || value.length > 2_048
    || !value.startsWith('/api/')
    || value.startsWith('//')
    || value.includes('\\')
    || value.includes('#')
    || /[\u0000-\u001f\u007f]/.test(value)
  ) {
    throw new Error('Profile runtime transport requires a bounded same-origin /api/ URL.')
  }

  const parsed = new URL(value, 'https://hermes.invalid')
  if (parsed.origin !== 'https://hermes.invalid' || !parsed.pathname.startsWith('/api/')) {
    throw new Error('Profile runtime transport rejected a cross-origin URL.')
  }
}

function assertLiveHeaders(headers: Readonly<Record<string, string>>): void {
  const entries = Object.entries(headers)
  if (
    entries.length !== 1
    || entries[0]?.[0].toLowerCase() !== 'accept'
    || entries[0]?.[1].toLowerCase() !== 'application/json'
  ) {
    throw new Error('Profile runtime transport rejected non-allowlisted headers.')
  }
}
