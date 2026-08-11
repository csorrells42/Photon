[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildRequestPath,
    [string]$GenerationRoot = '',
    [switch]$TestOnly,
    [string]$InspectionFixturePath = '',
    [string]$ProbeFixturePath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')

if ([string]::IsNullOrWhiteSpace($GenerationRoot)) {
    $GenerationRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'logs\runtime-generations'
}

function Read-PhotonJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$FailureCode)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw $FailureCode }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw $FailureCode }
    try { return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
    catch { throw $FailureCode }
}

function New-PhotonPythonBootstrap {
    param([Parameter(Mandatory = $true)][string]$Source)

    if ($Source.IndexOf([char]0) -ge 0) { throw 'runtime_probe_source_invalid' }
    $sourceBytes = [Text.UTF8Encoding]::new($false).GetBytes($Source)
    # Keep the complete Windows process command line well below its 32,767
    # character ceiling. The current probe is under 1 KiB.
    if ($sourceBytes.Length -lt 1 -or $sourceBytes.Length -gt 12288) {
        throw 'runtime_probe_source_size_invalid'
    }
    $encodedSource = [Convert]::ToBase64String($sourceBytes)
    if ($encodedSource -cnotmatch '^[A-Za-z0-9+/]+={0,2}$') {
        throw 'runtime_probe_encoding_invalid'
    }
    $bootstrap = "import base64;exec(compile(base64.b64decode('$encodedSource'),'<photon-runtime-probe>','exec'))"
    if ($bootstrap.IndexOf("`r") -ge 0 -or $bootstrap.IndexOf("`n") -ge 0 -or $bootstrap.Length -gt 20000) {
        throw 'runtime_probe_bootstrap_invalid'
    }
    return $bootstrap
}

$requestFullPath = [IO.Path]::GetFullPath($BuildRequestPath)
$request = Read-PhotonJson $requestFullPath 'build_request_invalid'
if ([string]$request.schemaId -cne $script:PhotonRuntimeBuildRequestSchema -or
    [int]$request.protocolVersion -ne $script:PhotonRuntimeProtocolVersion) {
    throw 'build_request_protocol_mismatch'
}
if ([string]$request.tag -notmatch '^[a-z0-9]+(?:[._/-][a-z0-9]+)*:(?:dev|release)-[a-f0-9]{12}-[a-f0-9]{16}$' -or
    [string]$request.tag -match ':latest$') {
    throw 'build_request_tag_invalid'
}
foreach ($digestField in @('sourceDigest', 'dockerfileSha256', 'sourceManifestSha256')) {
    if ([string]$request.$digestField -notmatch '^[a-f0-9]{64}$') { throw "build_request_digest_invalid:$digestField" }
}
if ([string]$request.sourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'build_request_commit_invalid' }
$manifestPath = [IO.Path]::GetFullPath([string]$request.sourceManifestPath)
if ((Get-PhotonSha256File $manifestPath) -cne [string]$request.sourceManifestSha256) {
    throw 'source_manifest_hash_mismatch'
}
$manifest = Read-PhotonJson $manifestPath 'source_manifest_invalid'
if ([string]$manifest.schemaId -cne $script:PhotonRuntimeSourceManifestSchema -or
    [int]$manifest.protocolVersion -ne $script:PhotonRuntimeProtocolVersion -or
    [string]$manifest.sourceCommit -cne [string]$request.sourceCommit -or
    [string]$manifest.sourceDigest -cne [string]$request.sourceDigest -or
    [string]$manifest.dockerfileSha256 -cne [string]$request.dockerfileSha256) {
    throw 'source_manifest_binding_mismatch'
}

$expectedLabels = [ordered]@{}
foreach ($property in $request.labels.PSObject.Properties) { $expectedLabels[$property.Name] = [string]$property.Value }
$requiredLabelKeys = @(
    'org.opencontainers.image.revision',
    'io.photon.workbench.source.digest',
    'io.photon.workbench.dockerfile.sha256',
    'io.photon.workbench.runtime.protocol',
    'io.photon.hermes-workbench.mode',
    'io.photon.hermes-workbench.mem0.capability',
    'io.photon.hermes-workbench.mem0.version',
    'io.photon.hermes-workbench.qdrant-client.version'
)
foreach ($key in $requiredLabelKeys) {
    if (-not $expectedLabels.Contains($key)) { throw "build_request_label_missing:$key" }
}
if ($expectedLabels['org.opencontainers.image.revision'] -cne [string]$request.sourceCommit -or
    $expectedLabels['io.photon.workbench.source.digest'] -cne [string]$request.sourceDigest -or
    $expectedLabels['io.photon.workbench.dockerfile.sha256'] -cne [string]$request.dockerfileSha256 -or
    $expectedLabels['io.photon.workbench.runtime.protocol'] -cne [string]$script:PhotonRuntimeProtocolVersion -or
    $expectedLabels['io.photon.hermes-workbench.mode'] -cne 'authenticated' -or
    $expectedLabels['io.photon.hermes-workbench.mem0.capability'] -cne 'mem0ai+qdrant') {
    throw 'build_request_label_binding_mismatch'
}

$probeCode = @'
import importlib.metadata as md
import json
from pathlib import Path
from agent import workbench_identity
required = [
    "/opt/hermes/agent/workbench_identity.py",
    "/opt/hermes/hermes_cli/mcp_editor.py",
    "/opt/hermes/hermes_cli/memory_archive.py",
    "/opt/hermes/hermes_cli/dashboard_auth/live_principals.py",
]
print(json.dumps({
    "mem0Version": md.version("mem0ai"),
    "qdrantClientVersion": md.version("qdrant-client"),
    "workbenchMode": workbench_identity.is_workbench_product_mode(),
    "identityMarker": workbench_identity.WORKBENCH_IDENTITY_MARKER,
    "requiredFiles": {path: Path(path).is_file() for path in required},
}, sort_keys=True))
'@
$probeBootstrap = New-PhotonPythonBootstrap -Source $probeCode

if ($TestOnly) {
    if ([string]::IsNullOrWhiteSpace($InspectionFixturePath) -or [string]::IsNullOrWhiteSpace($ProbeFixturePath)) {
        throw 'test_fixtures_required'
    }
    $inspection = Read-PhotonJson ([IO.Path]::GetFullPath($InspectionFixturePath)) 'inspection_fixture_invalid'
    $tagImage = $inspection.tag
    $idImage = $inspection.image
    $probe = Read-PhotonJson ([IO.Path]::GetFullPath($ProbeFixturePath)) 'probe_fixture_invalid'
}
else {
    if (-not [string]::IsNullOrWhiteSpace($InspectionFixturePath) -or -not [string]::IsNullOrWhiteSpace($ProbeFixturePath)) {
        throw 'test_fixture_requires_test_only'
    }
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker_command_missing' }
    $tagJson = (& docker image inspect ([string]$request.tag) 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagJson)) { throw 'runtime_tag_missing' }
    try { $tagImage = @($tagJson | ConvertFrom-Json)[0] } catch { throw 'runtime_tag_inspect_invalid' }
    $imageId = [string]$tagImage.Id
    $idJson = (& docker image inspect $imageId 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($idJson)) { throw 'runtime_image_missing' }
    try { $idImage = @($idJson | ConvertFrom-Json)[0] } catch { throw 'runtime_image_inspect_invalid' }

    $probeJson = (& docker run --rm --network none --env HERMES_WORKBENCH=1 --env HERMES_WORKBENCH_AUTHENTICATED_MEM0=1 --entrypoint python $imageId -c $probeBootstrap 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($probeJson)) { throw 'runtime_probe_failed' }
    try { $probe = $probeJson | ConvertFrom-Json } catch { throw 'runtime_probe_invalid' }
}

$tagId = [string]$tagImage.Id
$imageId = [string]$idImage.Id
if ($tagId -notmatch '^sha256:[a-f0-9]{64}$' -or $imageId -notmatch '^sha256:[a-f0-9]{64}$' -or $tagId -cne $imageId) {
    throw 'runtime_tag_image_id_mismatch'
}
$actualLabels = $idImage.Config.Labels
if ($null -eq $actualLabels) { throw 'runtime_labels_missing' }
foreach ($key in $expectedLabels.Keys) {
    $property = $actualLabels.PSObject.Properties[$key]
    if ($null -eq $property -or [string]$property.Value -cne [string]$expectedLabels[$key]) {
        throw "runtime_label_mismatch:$key"
    }
}
if ([string]$probe.mem0Version -cne $expectedLabels['io.photon.hermes-workbench.mem0.version'] -or
    [string]$probe.qdrantClientVersion -cne $expectedLabels['io.photon.hermes-workbench.qdrant-client.version']) {
    throw 'runtime_memory_dependency_mismatch'
}
if (-not [bool]$probe.workbenchMode -or
    [string]$probe.identityMarker -cne '[photos-agape-aphthartos:workbench-identity:v1]') {
    throw 'runtime_workbench_identity_mismatch'
}
$requiredFiles = @(
    '/opt/hermes/agent/workbench_identity.py',
    '/opt/hermes/hermes_cli/mcp_editor.py',
    '/opt/hermes/hermes_cli/memory_archive.py',
    '/opt/hermes/hermes_cli/dashboard_auth/live_principals.py'
)
foreach ($path in $requiredFiles) {
    $property = $probe.requiredFiles.PSObject.Properties[$path]
    if ($null -eq $property -or -not [bool]$property.Value) { throw "runtime_patched_file_missing:$path" }
}

$generationId = ([string]$request.sourceDigest).Substring(0, 16) + '-' + $imageId.Substring(7, 12)
$generationBase = [IO.Path]::GetFullPath($GenerationRoot)
if (Test-Path -LiteralPath $generationBase) {
    $generationBaseItem = Get-Item -LiteralPath $generationBase -Force
    if (-not $generationBaseItem.PSIsContainer -or ($generationBaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'generation_root_invalid'
    }
}
else { New-Item -ItemType Directory -Path $generationBase -Force | Out-Null }
$generationPath = Join-Path $generationBase $generationId
if (Test-Path -LiteralPath $generationPath) { throw 'generation_already_exists' }
New-Item -ItemType Directory -Path $generationPath | Out-Null
try {
    $lock = [ordered]@{
        schemaId = $script:PhotonRuntimeImageLockSchema
        protocolVersion = $script:PhotonRuntimeProtocolVersion
        generationId = $generationId
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
        imageTag = [string]$request.tag
        imageId = $imageId
        sourceCommit = [string]$request.sourceCommit
        sourceRemote = [string]$request.sourceRemote
        sourceDigest = [string]$request.sourceDigest
        dockerfileSha256 = [string]$request.dockerfileSha256
        sourceManifestSha256 = [string]$request.sourceManifestSha256
        labels = $expectedLabels
        probe = [ordered]@{
            mem0Version = [string]$probe.mem0Version
            qdrantClientVersion = [string]$probe.qdrantClientVersion
            workbenchIdentityMarker = [string]$probe.identityMarker
            patchedFiles = $requiredFiles
        }
    }
    $lockPath = Join-Path $generationPath 'runtime-image.lock.json'
    Write-PhotonJsonFile $lockPath $lock -CreateNew
    $envPath = Join-Path $generationPath 'runtime-image.env'
    [IO.File]::WriteAllText($envPath, "HERMES_IMAGE_REFERENCE=$imageId`n", [Text.UTF8Encoding]::new($false))
    (Get-Item -LiteralPath $lockPath -Force).IsReadOnly = $true
    (Get-Item -LiteralPath $envPath -Force).IsReadOnly = $true
}
catch {
    if (Test-Path -LiteralPath $generationPath -PathType Container) { Remove-Item -LiteralPath $generationPath -Recurse -Force }
    throw
}

[pscustomobject]@{
    GenerationId = $generationId
    GenerationPath = $generationPath
    ImageId = $imageId
    ImageTag = [string]$request.tag
}
