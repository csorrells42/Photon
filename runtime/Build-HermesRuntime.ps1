[CmdletBinding()]
param(
    [ValidateSet('Development', 'Release')]
    [string]$Mode = 'Development',
    [string]$SourceRoot = '',
    [string]$MetadataRoot = '',
    [string]$Repository = 'hermes-workbench-runtime',
    [string]$ExpectedSourceCommit = '',
    [string]$ExpectedUpstreamRemote = '',
    [string]$ReleaseLockPath = '',
    [switch]$PrepareOnly,
    [scriptblock]$BeforeSourceRevalidation
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Join-Path $projectRoot 'source' }
if ([string]::IsNullOrWhiteSpace($MetadataRoot)) { $MetadataRoot = Join-Path $projectRoot 'logs\runtime-builds' }

if ($Repository -notmatch '^[a-z0-9]+(?:[._/-][a-z0-9]+)*$' -or $Repository.Contains(':')) {
    throw 'runtime_repository_invalid'
}
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$metadata = [IO.Path]::GetFullPath($MetadataRoot)
$sourcePrefix = $source.TrimEnd('\') + '\'
if ($metadata -eq $source -or $metadata.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'metadata_must_be_outside_source'
}

$commit = Get-PhotonGitOutput $source @('rev-parse', 'HEAD')
if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'source_commit_invalid' }
$remote = Get-PhotonGitOutput $source @('remote', 'get-url', 'origin')
$status = Get-PhotonGitOutput $source @('status', '--porcelain=v1', '--untracked-files=all')
$dirty = -not [string]::IsNullOrWhiteSpace($status)

if ($Mode -eq 'Release') {
    if ($dirty) { throw 'release_source_dirty' }
    if ($ExpectedSourceCommit -notmatch '^[a-f0-9]{40}$' -or $commit -cne $ExpectedSourceCommit) {
        throw 'release_commit_mismatch'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedUpstreamRemote) -or
        (ConvertTo-PhotonNormalizedRemote $remote) -cne (ConvertTo-PhotonNormalizedRemote $ExpectedUpstreamRemote)) {
        throw 'release_remote_mismatch'
    }
    if ([string]::IsNullOrWhiteSpace($ReleaseLockPath)) { throw 'release_lock_path_required' }
}

$dockerfile = Join-Path $source 'Dockerfile'
$uvLock = Join-Path $source 'uv.lock'
if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) { throw 'source_dockerfile_missing' }
if (-not (Test-Path -LiteralPath $uvLock -PathType Leaf)) { throw 'source_uv_lock_missing' }
$mem0Version = Get-PhotonLockedPackageVersion $uvLock 'mem0ai'
$qdrantVersion = Get-PhotonLockedPackageVersion $uvLock 'qdrant-client'

$inventory = @(Get-PhotonSourceInventory $source)
$sourceDigest = Get-PhotonInventoryDigest $inventory
$dockerfileEntry = @($inventory | Where-Object { [string]$_.path -ceq 'Dockerfile' })
if ($dockerfileEntry.Count -ne 1) { throw 'source_dockerfile_not_manifested' }
$dockerfileDigest = [string]$dockerfileEntry[0].sha256
$modeSlug = if ($Mode -eq 'Release') { 'release' } else { 'dev' }
$tag = '{0}:{1}-{2}-{3}' -f $Repository, $modeSlug, $commit.Substring(0, 12), $sourceDigest.Substring(0, 16)
if ($tag.EndsWith(':latest', [StringComparison]::OrdinalIgnoreCase)) { throw 'latest_tag_rejected' }

$manifest = [ordered]@{
    schemaId = $script:PhotonRuntimeSourceManifestSchema
    protocolVersion = $script:PhotonRuntimeProtocolVersion
    mode = $Mode.ToLowerInvariant()
    sourceCommit = $commit
    sourceRemote = ConvertTo-PhotonNormalizedRemote $remote
    dirty = $dirty
    sourceDigest = $sourceDigest
    dockerfileSha256 = $dockerfileDigest
    files = $inventory
}

$labels = [ordered]@{
    'org.opencontainers.image.revision' = $commit
    'io.photon.workbench.source.digest' = $sourceDigest
    'io.photon.workbench.dockerfile.sha256' = $dockerfileDigest
    'io.photon.workbench.runtime.protocol' = [string]$script:PhotonRuntimeProtocolVersion
    'io.photon.hermes-workbench.mode' = 'authenticated'
    'io.photon.hermes-workbench.mem0.capability' = 'mem0ai+qdrant'
    'io.photon.hermes-workbench.mem0.version' = $mem0Version
    'io.photon.hermes-workbench.qdrant-client.version' = $qdrantVersion
}
$manifestPath = $null
$requestPath = $null

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotonRuntimeSource-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $temporaryRoot 'source'
try {
    if ($BeforeSourceRevalidation) { & $BeforeSourceRevalidation $source }
    Copy-PhotonSourceSnapshot $source $stage $inventory
    $sourceAfter = @(Get-PhotonSourceInventory $source)
    Assert-PhotonInventoryEqual $inventory $sourceAfter 'source_changed_during_preparation'

    $buildDirectory = Join-Path $metadata $sourceDigest
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    $manifestPath = Join-Path $buildDirectory 'source.manifest.json'
    Write-PhotonJsonFile $manifestPath $manifest
    $manifestDigest = Get-PhotonSha256File $manifestPath

    if ($Mode -eq 'Release') {
        $releaseLock = [ordered]@{
            schemaId = $script:PhotonRuntimeSourceLockSchema
            protocolVersion = $script:PhotonRuntimeProtocolVersion
            sourceCommit = $commit
            sourceRemote = ConvertTo-PhotonNormalizedRemote $remote
            sourceDigest = $sourceDigest
            dockerfileSha256 = $dockerfileDigest
            sourceManifestSha256 = $manifestDigest
            mem0Version = $mem0Version
            qdrantClientVersion = $qdrantVersion
        }
        $lockFullPath = [IO.Path]::GetFullPath($ReleaseLockPath)
        if (Test-Path -LiteralPath $lockFullPath -PathType Leaf) {
            $existing = Get-Content -Raw -LiteralPath $lockFullPath | ConvertFrom-Json
            foreach ($field in @('schemaId','protocolVersion','sourceCommit','sourceRemote','sourceDigest','dockerfileSha256','sourceManifestSha256','mem0Version','qdrantClientVersion')) {
                if ([string]$existing.$field -cne [string]$releaseLock[$field]) { throw "release_lock_mismatch:$field" }
            }
        }
        else { Write-PhotonJsonFile $lockFullPath $releaseLock -CreateNew }
    }

    $request = [ordered]@{
        schemaId = $script:PhotonRuntimeBuildRequestSchema
        protocolVersion = $script:PhotonRuntimeProtocolVersion
        mode = $Mode.ToLowerInvariant()
        tag = $tag
        sourceCommit = $commit
        sourceRemote = ConvertTo-PhotonNormalizedRemote $remote
        sourceDigest = $sourceDigest
        dockerfileSha256 = $dockerfileDigest
        sourceManifestSha256 = $manifestDigest
        sourceManifestPath = $manifestPath
        labels = $labels
    }
    $requestPath = Join-Path $buildDirectory 'runtime-build-request.json'
    Write-PhotonJsonFile $requestPath $request

    if (-not $PrepareOnly) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker_command_missing' }
        $arguments = [Collections.Generic.List[string]]::new()
        foreach ($argument in @('build', '--pull=false', '--file', (Join-Path $stage 'Dockerfile'), '--build-arg', "HERMES_GIT_SHA=$commit")) {
            $arguments.Add($argument)
        }
        foreach ($key in $labels.Keys) {
            $arguments.Add('--label')
            $arguments.Add("$key=$($labels[$key])")
        }
        $arguments.Add('--tag')
        $arguments.Add($tag)
        $arguments.Add($stage)
        & docker @arguments
        if ($LASTEXITCODE -ne 0) { throw 'runtime_image_build_failed' }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $item = Get-Item -LiteralPath $temporaryRoot -Force
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $full = [IO.Path]::GetFullPath($temporaryRoot)
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
            $full.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $full) -like 'PhotonRuntimeSource-*') {
            Remove-Item -LiteralPath $full -Recurse -Force
        }
    }
}

[pscustomobject]@{
    Tag = $tag
    Mode = $Mode
    SourceCommit = $commit
    SourceDigest = $sourceDigest
    ManifestPath = $manifestPath
    BuildRequestPath = $requestPath
    PreparedOnly = [bool]$PrepareOnly
}
