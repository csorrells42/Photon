import { describe, expect, it } from 'vitest'
import { createWorkbenchIdentityProof, isBlockedWorkspacePath, resolveWorkbenchWorkspaceRoot } from './vite.config'

describe('Workbench workspace authority', () => {
  it('uses the launcher-provided absolute workspace and keeps the install root only as a development fallback', () => {
    expect(resolveWorkbenchWorkspaceRoot('C:\\Users\\developer\\Desktop\\Hermes', 'C:\\install'))
      .toBe('C:\\Users\\developer\\Desktop\\Hermes')
    expect(resolveWorkbenchWorkspaceRoot(undefined, 'C:\\install')).toBe('C:\\install')
  })

  it.each(['workspace', '..\\escape', 'C:\\', 'C:\\bad\u0000path'])('rejects an unsafe workspace authority: %s', (candidate) => {
    expect(() => resolveWorkbenchWorkspaceRoot(candidate, 'C:\\install')).toThrow()
  })
})

describe('Workbench bridge path policy', () => {
  it.each([
    '.git/config',
    '.GIT/config',
    'NODE_MODULES/package/index.js',
    'Data/private.json',
    'LOGS/runtime.log',
    '.ENV',
    '.Env.local',
    'nested/.ENV.production',
    'AUTH.JSON',
    'nested/Credentials.Json',
    'keys/client.KEY',
    'keys/client.PEM',
    'keys/client.PFX',
    'keys/client.P12',
    '.NPMRC',
    '.PyPiRc',
    'NuGet.Config',
  ])('blocks case variants of hidden workspace paths: %s', (candidate) => {
    expect(isBlockedWorkspacePath(candidate)).toBe(true)
  })

  it('does not hide ordinary similarly named source paths', () => {
    expect(isBlockedWorkspacePath('src/catalogs/logger.ts')).toBe(false)
    expect(isBlockedWorkspacePath('src/environment.ts')).toBe(false)
  })
})

describe('Workbench host identity', () => {
  it('binds a proof to both the launch nonce and navigation challenge', () => {
    const nonce = '11'.repeat(32)
    const challenge = '22'.repeat(32)
    const proof = createWorkbenchIdentityProof(nonce, challenge)

    expect(proof).toMatch(/^[a-f0-9]{64}$/)
    expect(createWorkbenchIdentityProof('33'.repeat(32), challenge)).not.toBe(proof)
    expect(createWorkbenchIdentityProof(nonce, '44'.repeat(32))).not.toBe(proof)
    expect(createWorkbenchIdentityProof('invalid', challenge)).toBeNull()
  })
})
