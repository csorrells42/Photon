import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { DeveloperBuildResult, DeveloperServicesDescription } from './DesktopDeveloperServicesClient'
import { DeveloperServicesPanel } from './DeveloperServicesPanel'
import type { DeveloperBuildController } from './useDeveloperBuild'
import { LANGUAGE_TOOLING_CONTRACT } from '../LanguageToolingProviders'

const description: DeveloperServicesDescription = {
  protocolVersion: 2,
  requestId: 'describe:1',
  workspaceRoot: 'C:\\workspace',
  targets: ['Photon.slnx'],
  availability: { state: 'available' },
  providers: [{
    contractVersion: 1,
    providerId: 'dotnet',
    displayName: '.NET SDK',
    providerVersion: '8',
    languageIds: ['csharp'],
    projectKinds: ['slnx'],
    build: { supported: true, producesDiagnostics: true, supportsCancellation: true, targetKinds: ['slnx'] },
    lsp: { supported: true },
    dap: { supported: true },
    availability: { state: 'available' },
  }],
  languageTooling: [{
    contract: LANGUAGE_TOOLING_CONTRACT,
    source: 'trusted-host',
    evidenceId: 'desktop:dotnet:1',
    providerId: 'dotnet',
    checkedAt: '2026-08-10T12:00:00.000Z',
    capabilities: [
      { capabilityId: 'dotnet.roslyn-lsp', availability: 'available', code: 'verified', detail: 'Verified.' },
      { capabilityId: 'dotnet.compiler', availability: 'available', code: 'verified', detail: 'Verified.' },
      { capabilityId: 'dotnet.tests', availability: 'available', code: 'verified', detail: 'Verified.' },
      { capabilityId: 'dotnet.dap', availability: 'available', code: 'verified', detail: 'Verified.' },
    ],
  }],
}

function controller(overrides: Partial<DeveloperBuildController> = {}): DeveloperBuildController {
  return {
    desktopAvailable: true,
    description,
    selectedTarget: 'Photon.slnx',
    setSelectedTarget: vi.fn(),
    configuration: 'Debug',
    setConfiguration: vi.fn(),
    describing: false,
    activeOperation: null,
    building: false,
    analyzing: false,
    busy: false,
    cancelling: false,
    error: null,
    result: null,
    refresh: vi.fn(),
    build: vi.fn(async () => undefined),
    analyze: vi.fn(async () => undefined),
    cancel: vi.fn(),
    ...overrides,
  }
}

function analysisResult(overrides: Partial<DeveloperBuildResult> = {}): DeveloperBuildResult {
  return {
    requestId: 'analyze:1',
    revision: 1,
    operation: 'analyze',
    stale: false,
    workspaceRoot: 'C:\\workspace',
    succeeded: true,
    wasCancelled: false,
    diagnostics: [],
    output: { standardOutput: '', standardError: '', truncated: false, droppedCharacters: 0 },
    ...overrides,
  }
}

describe('DeveloperServicesPanel', () => {
  it('shows protocol v2 with separate build/analyze and explicit debugger actions', () => {
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller()} />)
    expect(markup).toContain('protocol v2')
    expect(markup).toContain('Build target')
    expect(markup).toContain('Analyze target')
    expect(markup).toContain('Debugger provider')
    expect(markup).toContain('.NET Debugger')
    expect(markup).toContain('Launch')
    expect(markup).toContain('Attach')
    expect(markup).toContain('.NET Tests')
    expect(markup).toContain('Run tests')
  })

  it('renders honest operation-specific cancellation state', () => {
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller({
      activeOperation: 'analyze',
      analyzing: true,
      busy: true,
      cancelling: true,
    })} />)
    expect(markup).toContain('Cancelling analysis')
    expect(markup).not.toContain('Build target')
    expect(markup).not.toContain('Analyze target')
  })

  it('announces operation-specific results through an accessible status', () => {
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller({
      result: analysisResult({ succeeded: false, wasCancelled: true }),
    })} />)
    expect(markup).toContain('role="status"')
    expect(markup).toContain('aria-live="polite"')
    expect(markup).toContain('Analysis cancelled')
  })

  it('renders capability readiness from trusted evidence instead of raw provider IDs', () => {
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller()} />)
    expect(markup).toContain('.NET / Roslyn')
    expect(markup).toContain('Host verified')
    expect(markup).not.toContain('2/4 host verified')
    expect(markup).toContain('Java / Eclipse JDT')
    expect(markup).toContain('Adapter pending')
    expect(markup).toContain('GNU C / C++')
    expect(markup).toContain('Raspberry Pi')
  })

  it('keeps current-file tool actions honest when the pinned payload is unavailable', () => {
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller()} selectedWorkspacePath="firmware/blink.ino" />)
    expect(markup).toContain('Current file tooling')
    expect(markup).toContain('Arduino board FQBN')
    expect(markup).toContain('Check with Arduino')
    expect(markup).toContain('Arduino toolchain is not installed')
    expect(markup).toContain('disabled=""')
  })

  it('projects trusted C++ evidence while retaining the native-host action gate', () => {
    const gccDescription = {
      ...description,
      languageTooling: [...description.languageTooling, {
        contract: LANGUAGE_TOOLING_CONTRACT,
        source: 'trusted-host' as const,
        evidenceId: 'desktop:gcc:1',
        providerId: 'gcc' as const,
        checkedAt: '2026-08-11T12:00:00.000Z',
        capabilities: [{
          capabilityId: 'gcc.compiler',
          availability: 'available' as const,
          code: 'verified-container-gcc-runtime',
          detail: 'GCC and G++ are bound to the immutable Hermes runtime.',
        }],
      }],
    }
    const markup = renderToStaticMarkup(
      <DeveloperServicesPanel controller={controller({ description: gccDescription })} selectedWorkspacePath="native/dragon.cpp" />,
    )
    expect(markup).toContain('GNU C/C++ source')
    expect(markup).toContain('Host verified')
    expect(markup).toMatch(/<button(?=[^>]*developer-build-button)(?=[^>]*disabled)[^>]*>[\s\S]*?Check with GCC<\/button>/)
    expect(markup).not.toContain('GCC toolchain is not installed')
  })

  it('offers bounded Python inspection while keeping unverified operations disabled', () => {
    const pythonDescription = {
      ...description,
      languageTooling: [...description.languageTooling, {
        contract: LANGUAGE_TOOLING_CONTRACT,
        source: 'trusted-host' as const,
        evidenceId: 'desktop:python:1',
        providerId: 'python' as const,
        checkedAt: '2026-08-10T12:00:00.000Z',
        capabilities: [{ capabilityId: 'python.project', availability: 'available' as const, code: 'verified', detail: 'Bounded inspection.' }],
      }],
    }
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller({ description: pythonDescription })} selectedWorkspacePath="tools/check.py" />)
    expect(markup).toContain('Python tooling')
    expect(markup).toContain('Inspect project')
    expect(markup).toContain('Check syntax')
    expect(markup).toContain('Run unittest')
    expect(markup).toContain('Verify Serena language service')
    expect(markup.match(/disabled=""/g)?.length).toBeGreaterThanOrEqual(3)
  })

  it('enables all four Python operations only from trusted 4/4 evidence', () => {
    const pythonDescription = {
      ...description,
      languageTooling: [...description.languageTooling, {
        contract: LANGUAGE_TOOLING_CONTRACT,
        source: 'trusted-host' as const,
        evidenceId: 'desktop:python:4',
        providerId: 'python' as const,
        checkedAt: '2026-08-11T12:00:00.000Z',
        capabilities: [
          { capabilityId: 'python.project', availability: 'available' as const, code: 'verified', detail: 'Bounded inspection.' },
          { capabilityId: 'python.lsp', availability: 'available' as const, code: 'verified', detail: 'Serena served the file.' },
          { capabilityId: 'python.compiler', availability: 'available' as const, code: 'verified', detail: 'Pinned syntax authority.' },
          { capabilityId: 'python.tests', availability: 'available' as const, code: 'verified', detail: 'Pinned unittest authority.' },
        ],
      }],
    }
    const markup = renderToStaticMarkup(<DeveloperServicesPanel controller={controller({ description: pythonDescription })} selectedWorkspacePath="tools/check.py" />)
    expect(markup).toContain('Python</b>')
    expect(markup).toContain('Host verified')
    expect(markup).toContain('Inspect project')
    expect(markup).toContain('Check syntax')
    expect(markup).toContain('Run unittest')
    expect(markup).toContain('Verify Serena language service')
  })
})
