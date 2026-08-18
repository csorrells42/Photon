import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { HermesModelManagementWorkspace, terminalCommandForExternalProvider } from './HermesModelManagementWorkspace'
import type { HermesModelManagementAdapter, HermesOAuthProvider } from './HermesModelManagementAdapter'

function externalProvider(cliCommand: string): HermesOAuthProvider {
  return {
    id: 'external', name: 'External', flow: 'external', cliCommand,
    docsUrl: '', disconnectCommand: '', disconnectHint: '', disconnectable: false,
    status: { loggedIn: false, source: '', sourceLabel: '', expiresAt: '', error: '' },
  }
}

describe('Photon Hermes model management', () => {
  it('routes only the external setup commands Hermes explicitly supports', () => {
    expect(terminalCommandForExternalProvider(externalProvider('hermes auth add qwen-oauth')))
      .toBe('docker exec -it hermes hermes auth add qwen-oauth')
    expect(terminalCommandForExternalProvider(externalProvider('copilot /login'))).toBe('copilot /login')
    expect(terminalCommandForExternalProvider(externalProvider('claude setup-token'))).toBe('claude setup-token')
    expect(terminalCommandForExternalProvider(externalProvider('powershell -Command Remove-Item C:\\'))).toBeNull()
  })

  it('renders the native Photon control plane rather than a Hermes dashboard link', () => {
    const adapter = {
      modelCatalog: vi.fn(), oauthProviders: vi.fn(), environment: vi.fn(), credentialPool: vi.fn(), auxiliary: vi.fn(), fallback: vi.fn(), moa: vi.fn(),
      setEnvironment: vi.fn(), clearEnvironment: vi.fn(), startOAuth: vi.fn(), submitOAuthCode: vi.fn(), pollOAuth: vi.fn(), cancelOAuth: vi.fn(), disconnectOAuth: vi.fn(), addCredentialPoolEntry: vi.fn(), removeCredentialPoolEntry: vi.fn(), setAssignment: vi.fn(), saveFallback: vi.fn(), saveMoa: vi.fn(),
    } as unknown as HermesModelManagementAdapter
    const markup = renderToStaticMarkup(<HermesModelManagementWorkspace adapter={adapter} />)

    expect(markup).toContain('Models, subscriptions &amp; routing')
    expect(markup).toContain('Main model')
    expect(markup).toContain('Subscriptions')
    expect(markup).toContain('API keys')
    expect(markup).toContain('Rotation pools')
    expect(markup).toContain('Local &amp; custom')
    expect(markup).toContain('Routing')
    expect(markup).toContain('Mixture of Agents')
    expect(markup).not.toContain('127.0.0.1:9119/models')
    expect(markup).not.toContain('Hermes Dashboard')
  })
})
