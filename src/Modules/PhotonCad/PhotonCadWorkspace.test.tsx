import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PHOTON_CAD_CONTRACT_VERSION, type PhotonCadRuntimeDescription } from './PhotonCadContract'
import { PhotonCadWorkspace } from './PhotonCadWorkspace'
import {
  PhotonCadWorkspaceFixture,
  photonCadWorkspaceFixtureBom,
  photonCadWorkspaceFixtureProject,
  photonCadWorkspaceFixtureRuntime,
} from './PhotonCadWorkspaceFixture'

describe('PhotonCadWorkspace', () => {
  it('renders an editor-like document and workflow tab structure without duplicating the shell sidebars', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture />)

    expect(markup).toContain('aria-label="CAD documents"')
    expect(markup).toContain('aria-label="CAD workflow"')
    expect(markup).toContain('role="tabpanel"')
    expect(markup).toContain('aria-selected="true"')
    expect(markup).toContain('Conveyor Drive Gearbox')
    expect(markup).toContain('Library')
    expect(markup).toContain('Design')
    expect(markup).toContain('Assemble')
    expect(markup).toContain('Verify')
    expect(markup).toContain('Release')
    expect(markup).not.toContain('Activity rail')
    expect(markup).not.toContain('Photon agent')
  })

  it('renders catalog coverage, human parameters, units, and declared provenance honestly', () => {
    const markup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="library" />)

    expect(markup).toContain('Operation library')
    expect(markup).toContain('4 discovered · 4 available · 0 unavailable')
    expect(markup).toContain('Precision box')
    expect(markup).toContain('Length')
    expect(markup).toContain('Overall X dimension.')
    expect(markup).toContain('mm')
    expect(markup).toContain('Declared provenance')
    expect(markup).toContain('build123d')
    expect(markup).toContain('Apache-2.0')
    expect(markup).toContain('does not independently attest')
  })

  it('keeps the viewer boundary injectable and makes missing evidence unmistakable', () => {
    const emptyMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="design" />)
    const injectedMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        previewSurface={<div aria-label="Injected bounded preview">Injected renderer boundary</div>}
        evidenceMode="fixture"
      />,
    )

    expect(emptyMarkup).toContain('aria-label="3D preview"')
    expect(emptyMarkup).toContain('No geometry renderer connected')
    expect(emptyMarkup).toContain('Preview visibility is never treated as a passing check')
    expect(injectedMarkup).toContain('Injected renderer boundary')
    expect(injectedMarkup).not.toContain('No geometry renderer connected')
    expect(injectedMarkup).toContain('No preview receipt')
  })

  it('limits autonomous language to scratch drafting and never advertises a canonical write', () => {
    const scratchMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="design" />)
    const canonicalMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={{ ...photonCadWorkspaceFixtureProject, mode: 'canonical', dirty: false }}
        initialStage="design"
      />,
    )

    expect(scratchMarkup).toContain('Scratch workspace')
    expect(scratchMarkup).toContain('Autonomy stays inside scratch drafting')
    expect(scratchMarkup).toContain('Run scratch draft')
    expect(canonicalMarkup).toContain('Canonical workspace')
    expect(canonicalMarkup).toContain('Canonical designs accept suggestions only')
    expect(canonicalMarkup).toContain('Prepare suggestion')
    expect(canonicalMarkup).not.toContain('Apply automatically')
  })

  it('renders assembly context and a supplied BOM without inventing missing rows', () => {
    const populated = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="assemble" />)
    const empty = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={[]}
        initialStage="assemble"
      />,
    )
    const industrialPartNumber = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={[{ partNumber: 'AUGER / FLIGHT 12 GA', description: 'Formed flight section', quantity: 1, unit: 'each', sourceEntityId: 'part-auger-flight' }]}
        initialStage="assemble"
      />,
    )

    expect(populated).toContain('Assembly context')
    expect(populated).toContain('Bill of materials')
    expect(populated).toContain('Part number')
    expect(populated).toContain('GBX-110')
    expect(populated).toContain('6205-2RS')
    expect(populated).toContain('5 discrete parts')
    expect(empty).toContain('No BOM evidence')
    expect(empty).toContain('does not invent missing rows')
    expect(industrialPartNumber).toContain('AUGER / FLIGHT 12 GA')
  })

  it('separates preview, verification, and release evidence', () => {
    const verifyMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="verify" />)
    const releaseMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture initialStage="release" />)

    expect(verifyMarkup).toContain('Verification plan')
    expect(verifyMarkup).toContain('Valid solids')
    expect(verifyMarkup).toContain('Assembly structure')
    expect(verifyMarkup).toContain('No verification result')
    expect(verifyMarkup).toContain('Passing status is never inferred')
    expect(verifyMarkup).toContain('Preview answers')
    expect(releaseMarkup).toContain('Review before write')
    expect(releaseMarkup).toContain('Preparing never creates files')
    expect(releaseMarkup).toContain('STEP is the portable source of truth.')
    expect(releaseMarkup).toContain('Autodesk Inventor is an optional later bridge')
    expect(releaseMarkup).toContain('No release review')
    expect(releaseMarkup).not.toContain('Package plan ready')
  })

  it('offers reviewed BOM exports in XLSX, CSV, and PDF without claiming a file exists', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspaceFixture initialStage="release" initialReleaseOutput="bom-export" />,
    )

    expect(markup).toContain('BOM export')
    expect(markup).toContain('Excel workbook')
    expect(markup).toContain('CSV data')
    expect(markup).toContain('PDF report')
    expect(markup).toContain('authoritative revision-bound digest')
    expect(markup).toContain('No BOM export review')
    expect(markup).toContain('Prepare export')
    expect(markup).toContain('Export reviewed BOM')
    expect(markup).not.toContain('BOM export plan ready')
  })

  it('makes quote and invoice output a preview, page review, approval, then output workflow', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspaceFixture initialStage="release" initialReleaseOutput="commercial-document" />,
    )

    expect(markup).toContain('Draft quote')
    expect(markup).toContain('Draft invoice')
    expect(markup).toContain('Your company')
    expect(markup).toContain('Customer')
    expect(markup).toContain('Line pricing')
    expect(markup).toContain('Add labor')
    expect(markup).toContain('Markup (%)')
    expect(markup).toContain('Discount (USD)')
    expect(markup).toContain('Tax (%)')
    expect(markup).toContain('Freight (USD)')
    expect(markup).toContain('Terms')
    expect(markup).toContain('Notes')
    expect(markup).toContain('Rendered all-page preview')
    expect(markup).toContain('No document preview')
    expect(markup).toContain('Approve exact preview')
    expect(markup).toContain('Create approved output')
    expect(markup).toContain('No email, accounting post, payment, charge, paid status, or delivery side effect')
    expect(markup).toContain('only after every rendered page is shown and a human explicitly approves')
    expect(markup.match(/disabled=""/gu)?.length ?? 0).toBeGreaterThanOrEqual(3)
  })

  it('marks fixture data and unavailable runtime states without claiming live success', () => {
    const fixtureMarkup = renderToStaticMarkup(<PhotonCadWorkspaceFixture />)
    const unavailableRuntime: PhotonCadRuntimeDescription = {
      contractVersion: PHOTON_CAD_CONTRACT_VERSION,
      status: 'unavailable',
      reason: 'unavailable',
    }
    const unavailableMarkup = renderToStaticMarkup(<PhotonCadWorkspace runtime={unavailableRuntime} project={null} />)

    expect(fixtureMarkup).toContain('Demonstration fixture')
    expect(fixtureMarkup).toContain('Fixture catalog')
    expect(fixtureMarkup).not.toContain('Runtime ready')
    expect(fixtureMarkup).toContain('No CAD runtime action, geometry render, verification pass, or release success is implied.')
    expect(unavailableMarkup).toContain('Runtime unavailable')
    expect(unavailableMarkup).toContain('The verified CAD runtime is not installed')
    expect(unavailableMarkup).toContain('No catalog evidence')
    expect(unavailableMarkup).toContain('No geometry snapshot')
  })

  it('shows a live catalog in browse-only mode without exposing any persistence-implying action', () => {
    const liveRuntime: PhotonCadRuntimeDescription = { ...photonCadWorkspaceFixtureRuntime, status: 'available', reason: 'ready' }
    const libraryMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} />,
    )
    const verifyMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} initialStage="verify" />,
    )
    const releaseMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace runtime={liveRuntime} project={photonCadWorkspaceFixtureProject} initialStage="release" />,
    )
    const bomMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={liveRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        bomDigest={`sha256:${'d'.repeat(64)}`}
        initialStage="release"
        initialReleaseOutput="bom-export"
      />,
    )
    const commercialMarkup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={liveRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        bomDigest={`sha256:${'d'.repeat(64)}`}
        initialStage="release"
        initialReleaseOutput="commercial-document"
      />,
    )

    expect(libraryMarkup).toContain('Catalog connected · editing locked')
    expect(libraryMarkup).toContain('Browse-only catalog')
    expect(libraryMarkup).toContain('data-operation-access="locked"')
    expect(libraryMarkup).not.toContain('Runtime ready')
    expect(libraryMarkup).toMatch(/<input[^>]*disabled=""[^>]*type="number"/u)
    expect(libraryMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Run scratch draft<\/button>/u)
    expect(verifyMarkup).toMatch(/<input[^>]*type="checkbox"[^>]*disabled=""/u)
    expect(verifyMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Run selected checks<\/button>/u)
    expect(releaseMarkup).toMatch(/<fieldset[^>]*class="pcad-release-output"[^>]*disabled=""/u)
    expect(releaseMarkup).toMatch(/<input[^>]*type="checkbox"[^>]*disabled=""/u)
    expect(bomMarkup).toMatch(/<fieldset[^>]*class="pcad-format-list"[^>]*disabled=""/u)
    expect(commercialMarkup).toMatch(/<span>Document number<\/span><input disabled=""/u)
    expect(commercialMarkup).toMatch(/<button[^>]*disabled=""[^>]*>[\s\S]*?Prepare preview<\/button>/u)
  })

  it('renders fixture exports from dynamic contract types', () => {
    const markup = renderToStaticMarkup(
      <PhotonCadWorkspace
        runtime={photonCadWorkspaceFixtureRuntime}
        project={photonCadWorkspaceFixtureProject}
        bom={photonCadWorkspaceFixtureBom}
        initialStage="assemble"
      />,
    )

    expect(photonCadWorkspaceFixtureRuntime.contractVersion).toBe(PHOTON_CAD_CONTRACT_VERSION)
    expect(photonCadWorkspaceFixtureProject.contractVersion).toBe(PHOTON_CAD_CONTRACT_VERSION)
    expect(markup).toContain('GBX-100 Gearbox')
  })

  it('keeps generated ARIA relationships unique and exposes one keyboard entry into the model tree', () => {
    const markup = renderToStaticMarkup(
      <>
        <PhotonCadWorkspaceFixture initialStage="design" />
        <PhotonCadWorkspaceFixture initialStage="verify" />
      </>,
    )
    const ids = [...markup.matchAll(/id="([^"]*pcad-(?:tab|panel)-[^"]+)"/gu)].map((match) => match[1])
    const controls = [...markup.matchAll(/aria-controls="([^"]+)"/gu)].map((match) => match[1])

    expect(ids.length).toBeGreaterThan(0)
    expect(new Set(ids).size).toBe(ids.length)
    expect(controls.every((id) => ids.includes(id))).toBe(true)
    expect(markup).toContain('role="tree"')
    expect(markup).toContain('aria-multiselectable="true"')
    expect(markup).toContain('role="treeitem"')
    expect(markup).toContain('tabindex="0"')
    expect(markup).toContain('tabindex="-1"')
  })
})
