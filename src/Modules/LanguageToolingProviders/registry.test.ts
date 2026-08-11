import { describe, expect, it } from 'vitest'
import {
  LANGUAGE_TOOLING_CONTRACT,
  buildLanguageToolingReports,
  createLanguageToolingHostRequest,
  type TrustedHostProviderEvidence,
} from '.'

function evidence(
  providerId: TrustedHostProviderEvidence['providerId'],
  capabilityId: string,
  availability: 'available' | 'unavailable' | 'error' = 'available',
): TrustedHostProviderEvidence {
  return {
    contract: LANGUAGE_TOOLING_CONTRACT,
    source: 'trusted-host',
    evidenceId: `desktop:${providerId}:1`,
    providerId,
    checkedAt: '2026-08-10T12:00:00.000Z',
    capabilities: [{ capabilityId, availability, code: 'host-verified', detail: 'Verified by the trusted desktop host.' }],
  }
}

describe('language tooling provider reports', () => {
  it('never turns static catalog declarations into availability claims', () => {
    const reports = buildLanguageToolingReports()
    expect(reports.map((provider) => provider.id)).toEqual([
      'dotnet', 'java-jdt', 'arduino', 'python', 'gcc', 'raspberry-pi',
    ])
    expect(reports.flatMap((provider) => provider.capabilities).every((item) => item.availability === 'unknown')).toBe(true)
  })

  it('accepts only exact trusted-host evidence for the matching capability', () => {
    const reports = buildLanguageToolingReports([{
      contract: LANGUAGE_TOOLING_CONTRACT,
      source: 'trusted-host',
      evidenceId: 'desktop-sidecar-1',
      providerId: 'dotnet',
      checkedAt: '2026-08-10T12:00:00.000Z',
      capabilities: [{
        capabilityId: 'dotnet.dap',
        availability: 'available',
        code: 'verified',
        detail: 'Pinned NetCoreDbg sidecar verified.',
        version: '3.1.3-1062',
      }],
    }])
    const dotnet = reports.find((provider) => provider.id === 'dotnet')!
    expect(dotnet.capabilities.find((item) => item.id === 'dotnet.dap')).toMatchObject({
      availability: 'available',
      evidenceId: 'desktop-sidecar-1',
      version: '3.1.3-1062',
    })
    expect(dotnet.capabilities.find((item) => item.id === 'dotnet.roslyn-lsp')?.availability).toBe('unknown')
  })

  it('bounds and sanitizes host display text', () => {
    const reports = buildLanguageToolingReports([{
      contract: LANGUAGE_TOOLING_CONTRACT,
      source: 'trusted-host',
      evidenceId: 'evidence',
      providerId: 'python',
      checkedAt: '2026-08-10T12:00:00.000Z',
      capabilities: [{
        capabilityId: 'python.compiler',
        availability: 'error',
        code: 'bad code <script>',
        detail: 'line one\nline two\u202e',
      }],
    }])
    const capability = reports.find((provider) => provider.id === 'python')!.capabilities.find((item) => item.id === 'python.compiler')!
    expect(capability.code).toBe('badcodescript')
    expect(capability.detail).toBe('line one line two ')
  })

  it('keeps malformed, duplicate, and cross-provider evidence unknown', () => {
    const invalidTime = { ...evidence('python', 'python.compiler'), checkedAt: 'now' }
    const duplicate = evidence('dotnet', 'dotnet.dap')
    const crossProvider = evidence('python', 'arduino.compiler')
    const reports = buildLanguageToolingReports([invalidTime, duplicate, duplicate, crossProvider])
    expect(reports.find((provider) => provider.id === 'python')!.capabilities.every((item) => item.availability === 'unknown')).toBe(true)
    expect(reports.find((provider) => provider.id === 'dotnet')!.capabilities.every((item) => item.availability === 'unknown')).toBe(true)
  })

  it('selects each provider by its own declarations without capability bleed', () => {
    const reports = buildLanguageToolingReports([
      evidence('java-jdt', 'java-jdt.lsp'),
      evidence('arduino', 'arduino.compiler'),
      evidence('python', 'python.tests'),
      evidence('dotnet', 'dotnet.dap'),
      evidence('gcc', 'gcc.compiler'),
      evidence('raspberry-pi', 'raspberry-pi.deploy'),
    ])
    expect(reports.map((provider) => [provider.id, provider.capabilities.filter((item) => item.availability === 'available').map((item) => item.id)])).toEqual([
      ['dotnet', ['dotnet.dap']],
      ['java-jdt', ['java-jdt.lsp']],
      ['arduino', ['arduino.compiler']],
      ['python', ['python.tests']],
      ['gcc', ['gcc.compiler']],
      ['raspberry-pi', ['raspberry-pi.deploy']],
    ])
  })

  it('gates data-only host requests on exact capability evidence', () => {
    const intent = {
      operation: 'compile',
      requestId: 'compile:1',
      workspaceId: 'workspace:1',
      providerId: 'arduino',
      targetPath: 'examples/Blink',
      boardFqbn: 'arduino:avr:uno',
    } as const
    expect(() => createLanguageToolingHostRequest(intent)).toThrow(/not been verified/i)
    const request = createLanguageToolingHostRequest(intent, [evidence('arduino', 'arduino.compiler')])
    expect(request).toMatchObject({ operation: 'compile', providerId: 'arduino', targetPath: 'examples/Blink', mode: 'check' })
    expect(Object.keys(request)).not.toEqual(expect.arrayContaining(['command', 'executable', 'arguments', 'environment', 'shell']))
  })

  it('rejects paths, board identifiers, and test selections that could escape structured intent', () => {
    for (const targetPath of ['../outside.py', 'C:\\outside.py', '/outside.py', 'src//bad.py']) {
      expect(() => createLanguageToolingHostRequest({
        operation: 'compile', requestId: 'compile:1', workspaceId: 'workspace:1', providerId: 'python', targetPath,
      }, [evidence('python', 'python.compiler')])).toThrow(/workspace-relative/i)
    }
    expect(() => createLanguageToolingHostRequest({
      operation: 'compile', requestId: 'compile:2', workspaceId: 'workspace:1', providerId: 'arduino',
      targetPath: 'Blink', boardFqbn: 'arduino:avr:uno && bad',
    }, [evidence('arduino', 'arduino.compiler')])).toThrow(/board identifier/i)
    expect(() => createLanguageToolingHostRequest({
      operation: 'run-tests', requestId: 'tests:1', workspaceId: 'workspace:1', providerId: 'python',
      targetPath: 'tests', selection: 'safe_test; Remove-Item *',
    }, [evidence('python', 'python.tests')])).toThrow(/test selection/i)
  })

  it('uses opaque trusted-host identifiers for Raspberry Pi operations', () => {
    const remoteEvidence = [
      evidence('raspberry-pi', 'raspberry-pi.inspect'),
    ]
    expect(createLanguageToolingHostRequest({
      operation: 'inspect-remote-target',
      requestId: 'probe:1',
      workspaceId: 'workspace:1',
      providerId: 'raspberry-pi',
      targetId: 'shop-pi:1',
    }, remoteEvidence)).toMatchObject({ targetId: 'shop-pi:1' })

    expect(() => createLanguageToolingHostRequest({
      operation: 'deploy-file',
      requestId: 'deploy:1',
      workspaceId: 'workspace:1',
      providerId: 'raspberry-pi',
      targetId: 'shop-pi:1',
      sourcePath: 'build/app',
      destinationId: '/tmp/raw-path',
    }, [evidence('raspberry-pi', 'raspberry-pi.deploy')])).toThrow(/destination identifier/i)
  })
})
