[CmdletBinding()]
param(
    [switch]$Yes,
    [ValidateRange(5, 600)]
    [int]$HealthTimeoutSeconds = 120,
    [string]$LogDirectory = ''
)

$ErrorActionPreference = 'Stop'
$bundleRoot = $PSScriptRoot
$logsPath = if ([string]::IsNullOrWhiteSpace($LogDirectory)) { Join-Path $bundleRoot 'logs' } else { [IO.Path]::GetFullPath($LogDirectory) }
$imageReference = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'
$rollbackReference = 'hermes-workbench-rollback:previous'
$previousImageReference = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
$runtimeGenerationScript = Join-Path $bundleRoot 'runtime\Runtime.Generation.ps1'
if (Test-Path -LiteralPath $runtimeGenerationScript -PathType Leaf) { . $runtimeGenerationScript }

function Wait-DockerEngine {
    param([int]$TimeoutSeconds = 180)

    & docker info *> $null
    if ($LASTEXITCODE -eq 0) { return }

    $dockerDesktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path -LiteralPath $dockerDesktop -PathType Leaf)) {
        throw 'Docker Desktop is not installed. Run Install-Hermes.ps1 first.'
    }
    Start-Process -FilePath $dockerDesktop -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        & docker info *> $null
    } until ($LASTEXITCODE -eq 0 -or [DateTime]::UtcNow -ge $deadline)
    if ($LASTEXITCODE -ne 0) { throw "Docker Desktop did not become ready within $TimeoutSeconds seconds." }
}

function Get-ImageMetadata {
    param([string]$Reference)

    $json = (& docker image inspect $Reference 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }
    $image = @($json | ConvertFrom-Json)[0]
    $digest = @($image.RepoDigests | Where-Object { $_ -like 'nousresearch/hermes-agent@sha256:*' } | Select-Object -First 1)[0]
    return [pscustomobject]@{
        Id = [string]$image.Id
        Digest = if ($digest) { [string]$digest } else { $null }
    }
}

function Get-RunningImageMetadata {
    $json = (& docker inspect photon 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }
    $container = @($json | ConvertFrom-Json)[0]
    return Get-ImageMetadata -Reference ([string]$container.Image)
}

function Wait-HermesHealth {
    param([int]$TimeoutSeconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:9119/api/status' -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                try {
                    $status = $response.Content | ConvertFrom-Json
                    $version = [string]$status.version
                    if (-not [string]::IsNullOrWhiteSpace($version)) { return $version }
                }
                catch { return 'unknown' }
                return 'unknown'
            }
        }
        catch { }
        Start-Sleep -Seconds 2
    } until ([DateTime]::UtcNow -ge $deadline)
    return $null
}

function Write-UpdateRecord {
    param(
        [string]$Status,
        [object]$Before,
        [object]$Candidate,
        [string]$RuntimeVersion
    )

    New-Item -ItemType Directory -Force -Path $logsPath | Out-Null
    $record = [ordered]@{
        protocolVersion = 1
        observedAtUtc = [DateTime]::UtcNow.ToString('o')
        status = $Status
        beforeImageId = if ($Before) { [string]$Before.Id } else { $null }
        beforeRepoDigest = if ($Before) { [string]$Before.Digest } else { $null }
        candidateImageId = if ($Candidate) { [string]$Candidate.Id } else { $null }
        candidateRepoDigest = if ($Candidate) { [string]$Candidate.Digest } else { $null }
        runtimeVersion = if ($RuntimeVersion) { $RuntimeVersion } else { $null }
        rollbackImage = $rollbackReference
    }
    [IO.File]::WriteAllText(
        (Join-Path $logsPath 'last-update.json'),
        ($record | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false)
    )
}

function Write-HermesRuntimeIdentity {
    New-Item -ItemType Directory -Force -Path $logsPath | Out-Null
    try {
        $containerJson = (& docker inspect photon 2>$null) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerJson)) { return }
        $container = @($containerJson | ConvertFrom-Json)[0]
        $imageId = [string]$container.Image
        $image = if ($imageId) { Get-ImageMetadata -Reference $imageId } else { $null }
        $revision = if ($container.Config.Labels) { [string]$container.Config.Labels.'org.opencontainers.image.revision' } else { $null }
        $identity = [ordered]@{
            protocolVersion = 1
            observedAtUtc = [DateTime]::UtcNow.ToString('o')
            containerName = 'photon'
            imageReference = [string]$container.Config.Image
            imageId = $imageId
            repoDigest = if ($image) { [string]$image.Digest } else { $null }
            revision = if ($revision) { $revision } else { $null }
        }
        [IO.File]::WriteAllText(
            (Join-Path $logsPath 'runtime-identity.json'),
            ($identity | ConvertTo-Json -Compress),
            [Text.UTF8Encoding]::new($false)
        )
    }
    catch {
        # Identity telemetry must never turn an otherwise healthy update into a failure.
    }
}

if (-not $Yes) {
    Write-Host 'Hermes Workbench updater' -ForegroundColor Cyan
    Write-Host 'This verifies the approved immutable Hermes image and briefly restarts the Hermes agent only when needed.'
    Write-Host 'Your settings, credentials, sessions, workspace, Serena, and Workbench source stay in place.'
    Write-Host 'Any active Hermes turn will be interrupted. The previous image is retained for automatic rollback.' -ForegroundColor Yellow
    $confirmation = Read-Host 'Type UPDATE to continue'
    if ($confirmation -cne 'UPDATE') {
        Write-Host 'Update cancelled. Nothing was changed.' -ForegroundColor DarkGray
        return
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed or is missing from PATH. Run Install-Hermes.ps1 first.'
}
Push-Location $bundleRoot
try {
    Wait-DockerEngine
    if (Get-Command Get-PhotonCurrentRuntimeGeneration -ErrorAction SilentlyContinue) {
        $committedRuntime = Get-PhotonCurrentRuntimeGeneration -BundleRoot $bundleRoot
        if ($committedRuntime) {
            throw "A verified runtime generation is active ($($committedRuntime.GenerationId)). Build, verify, and adopt a new immutable generation instead of replacing it with the legacy upstream updater."
        }
    }
    $env:HERMES_IMAGE_REFERENCE = $imageReference
    $before = Get-RunningImageMetadata
    if (-not $before -or [string]::IsNullOrWhiteSpace([string]$before.Id)) {
        throw 'The installed Hermes image was not found. Run Install-Hermes.ps1 first.'
    }

    Write-Host 'Checking the approved Hermes image...' -ForegroundColor Cyan
    & docker compose pull gateway
    if ($LASTEXITCODE -ne 0) {
        Write-UpdateRecord -Status 'pull-failed' -Before $before -Candidate $null -RuntimeVersion $null
        throw 'The Hermes image download failed. The installed container was not replaced.'
    }

    $candidate = Get-ImageMetadata -Reference $imageReference
    if (-not $candidate -or [string]::IsNullOrWhiteSpace([string]$candidate.Id)) {
        Write-UpdateRecord -Status 'candidate-missing' -Before $before -Candidate $null -RuntimeVersion $null
        throw 'Docker downloaded Hermes but could not inspect the candidate image. The installed container was not replaced.'
    }

    if ([string]$candidate.Id -eq [string]$before.Id) {
        Write-UpdateRecord -Status 'already-current' -Before $before -Candidate $candidate -RuntimeVersion $null
        Write-Host 'Hermes is already current. No container was replaced.' -ForegroundColor Green
        return
    }

    & docker tag ([string]$before.Id) $rollbackReference
    if ($LASTEXITCODE -ne 0) { throw 'Could not retain the previous Hermes image for rollback. The container was not replaced.' }

    Write-Host 'Applying the downloaded Hermes image...' -ForegroundColor Cyan
    $env:HERMES_IMAGE_REFERENCE = $imageReference
    & docker compose up -d --no-deps --force-recreate gateway
    if ($LASTEXITCODE -ne 0) { $runtimeVersion = $null }
    else { $runtimeVersion = Wait-HermesHealth -TimeoutSeconds $HealthTimeoutSeconds }

    if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
        Write-Warning 'The candidate image did not become healthy. Restoring the previous Hermes image...'
        $env:HERMES_IMAGE_REFERENCE = $rollbackReference
        & docker compose up -d --no-deps --force-recreate gateway
        $rolledBackVersion = if ($LASTEXITCODE -eq 0) { Wait-HermesHealth -TimeoutSeconds $HealthTimeoutSeconds } else { $null }
        if ([string]::IsNullOrWhiteSpace($rolledBackVersion)) {
            Write-UpdateRecord -Status 'rollback-health-failed' -Before $before -Candidate $candidate -RuntimeVersion $null
            throw "Hermes update and automatic rollback both failed health verification. The preserved image is $rollbackReference."
        }
        Write-HermesRuntimeIdentity
        Write-UpdateRecord -Status 'rolled-back' -Before $before -Candidate $candidate -RuntimeVersion $rolledBackVersion
        throw "The candidate Hermes image failed its health check. Hermes $rolledBackVersion was restored automatically."
    }

    Write-HermesRuntimeIdentity
    Write-UpdateRecord -Status 'updated' -Before $before -Candidate $candidate -RuntimeVersion $runtimeVersion
    Write-Host "Hermes updated successfully to runtime $runtimeVersion." -ForegroundColor Green
    Write-Host 'The compatibility panel will identify any upstream contract that needs review.' -ForegroundColor DarkGray
    Write-Host 'If Hermes Workbench was closed, start it normally with Launch Hermes.cmd.' -ForegroundColor DarkGray
}
finally {
    Pop-Location
    if ($null -eq $previousImageReference) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
    else { $env:HERMES_IMAGE_REFERENCE = $previousImageReference }
}
