[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$updateScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Update-Hermes.ps1'
$global:mockOldImageId = 'sha256:' + ('a' * 64)
$global:mockNewImageId = 'sha256:' + ('b' * 64)
$global:approvedImageReference = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesUpdateSmoke-' + [Guid]::NewGuid().ToString('N'))

function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Label)
    if ([string]$Actual -cne [string]$Expected) {
        throw "$Label expected '$Expected' but received '$Actual'."
    }
}

function Invoke-UpdateCase {
    param(
        [ValidateSet('already-current', 'updated', 'rolled-back')]
        [string]$Case
    )

    $caseRoot = Join-Path $tempRoot $Case
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    $global:mockPulled = $false
    $global:mockDesiredImage = $global:mockOldImageId
    $global:mockActiveImage = $global:mockOldImageId
    $global:mockComposeUpCount = 0
    $global:mockRollbackTagCreated = $false
    $global:mockCandidateId = if ($Case -eq 'already-current') { $global:mockOldImageId } else { $global:mockNewImageId }
    $global:mockCase = $Case

    function docker {
        $command = $args -join ' '
        $global:LASTEXITCODE = 0

        if ($command -eq 'info') { return }
        if ($command -eq 'compose pull gateway') {
            $global:mockPulled = $true
            $global:mockDesiredImage = $global:mockCandidateId
            return
        }
        if ($command -like 'image inspect *') {
            $reference = [string]$args[2]
            $id = if ($reference -eq $global:approvedImageReference) {
                if ($global:mockPulled) { $global:mockCandidateId } else { $global:mockOldImageId }
            } elseif ($reference -eq $global:mockNewImageId) { $global:mockNewImageId } else { $global:mockOldImageId }
            $hex = $id.Substring(7)
            return (@([ordered]@{
                Id = $id
                RepoDigests = @("nousresearch/hermes-agent@sha256:$hex")
            }) | ConvertTo-Json -Compress)
        }
        if ($command -like 'tag *') {
            $source = [string]$args[1]
            $target = [string]$args[2]
            if ($target -eq 'hermes-workbench-rollback:previous') {
                Assert-Equal $source $global:mockOldImageId 'rollback source image'
                $global:mockRollbackTagCreated = $true
            } else { throw "Unexpected mock Docker tag: $command" }
            return
        }
        if ($command -like 'compose up *') {
            $global:mockComposeUpCount++
            $global:mockActiveImage = if ($env:HERMES_IMAGE_REFERENCE -eq 'hermes-workbench-rollback:previous') {
                $global:mockOldImageId
            } else {
                $global:mockDesiredImage
            }
            return
        }
        if ($command -eq 'inspect hermes') {
            return (@([ordered]@{
                Image = $global:mockActiveImage
                Config = [ordered]@{
                    Image = $global:approvedImageReference
                    Labels = [ordered]@{ 'org.opencontainers.image.revision' = '1234567abcdef' }
                }
            }) | ConvertTo-Json -Depth 5 -Compress)
        }
        throw "Unexpected mock Docker command: $command"
    }

    function Invoke-WebRequest {
        param([switch]$UseBasicParsing, [string]$Uri, [int]$TimeoutSec)
        if ($global:mockCase -eq 'rolled-back' -and $global:mockActiveImage -eq $global:mockNewImageId) {
            throw 'Simulated candidate health failure.'
        }
        $version = if ($global:mockActiveImage -eq $global:mockNewImageId) { '0.21.0-test' } else { '0.20.0-test' }
        return [pscustomobject]@{ StatusCode = 200; Content = "{`"version`":`"$version`"}" }
    }

    $caught = $null
    $priorImageReference = 'caller-owned-image-reference'
    $env:HERMES_IMAGE_REFERENCE = $priorImageReference
    try {
        & $updateScript -Yes -HealthTimeoutSeconds 5 -LogDirectory $caseRoot
    }
    catch { $caught = $_ }

    $recordPath = Join-Path $caseRoot 'last-update.json'
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        $reason = if ($caught) { "$($caught.Exception.Message) at $($caught.ScriptStackTrace)" } else { 'no exception was returned' }
        throw "$Case did not write last-update.json: $reason"
    }
    $record = Get-Content -Raw -LiteralPath $recordPath | ConvertFrom-Json
    Assert-Equal $env:HERMES_IMAGE_REFERENCE $priorImageReference "$Case caller environment restoration"
    Assert-Equal $record.status $Case "$Case status"
    Assert-Equal $record.beforeImageId $global:mockOldImageId "$Case before image"
    Assert-Equal $record.candidateImageId $global:mockCandidateId "$Case candidate image"

    if ($Case -eq 'already-current') {
        if ($caught) { throw $caught }
        Assert-Equal $global:mockComposeUpCount 0 'already-current recreation count'
        Assert-Equal $global:mockRollbackTagCreated $false 'already-current rollback tag'
    } elseif ($Case -eq 'updated') {
        if ($caught) { throw $caught }
        Assert-Equal $global:mockComposeUpCount 1 'updated recreation count'
        Assert-Equal $global:mockRollbackTagCreated $true 'updated rollback tag'
        Assert-Equal $record.runtimeVersion '0.21.0-test' 'updated runtime version'
    } else {
        if (-not $caught -or $caught.Exception.Message -notlike '*restored automatically*') {
            throw 'The rollback case did not surface the expected controlled failure.'
        }
        Assert-Equal $global:mockComposeUpCount 2 'rollback recreation count'
        Assert-Equal $global:mockActiveImage $global:mockOldImageId 'rollback active image'
        Assert-Equal $record.runtimeVersion '0.20.0-test' 'rollback runtime version'
    }

    if ($Case -ne 'already-current') {
        $identity = Get-Content -Raw -LiteralPath (Join-Path $caseRoot 'runtime-identity.json') | ConvertFrom-Json
        Assert-Equal $identity.imageId $global:mockActiveImage "$Case recorded runtime image"
    }

    [pscustomobject]@{
        Case = $Case
        Status = [string]$record.status
        Recreates = $global:mockComposeUpCount
        ActiveImage = $global:mockActiveImage
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $fixtureRoot = Join-Path $tempRoot 'bundle'
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    Copy-Item -LiteralPath $updateScript -Destination (Join-Path $fixtureRoot 'Update-Hermes.ps1')
    $updateScript = Join-Path $fixtureRoot 'Update-Hermes.ps1'
    $results = @(
        Invoke-UpdateCase -Case 'already-current'
        Invoke-UpdateCase -Case 'updated'
        Invoke-UpdateCase -Case 'rolled-back'
    )
    $results | Format-Table -AutoSize
    Write-Host 'Hermes updater smoke tests passed without calling the real Docker executable.' -ForegroundColor Green
}
finally {
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'HermesUpdateSmoke-*' -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        $item = Get-Item -LiteralPath $resolved -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
