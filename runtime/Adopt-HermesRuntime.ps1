[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GenerationId,
    [switch]$PreflightOnly,
    [switch]$Yes,
    [switch]$RepairPreviousFromRuntimeIdentity,
    [ValidateRange(10, 600)][int]$HealthTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $bundleRoot 'launcher.settings.json'
. (Join-Path $PSScriptRoot 'Runtime.Generation.ps1')

$memoryVectorLock = Read-PhotonRuntimeJson `
    -Path (Join-Path $PSScriptRoot 'memory-vector.lock.json') `
    -FailureCode 'runtime_adoption_memory_vector_lock_invalid'
if ([string]$memoryVectorLock.schemaId -cne 'photon-memory-vector-lock/v1' -or
    [string]$memoryVectorLock.serviceName -cne 'memory-vector' -or
    [string]$memoryVectorLock.image -cnotmatch '^[a-z0-9./-]+@sha256:[a-f0-9]{64}$' -or
    [string]$memoryVectorLock.endpoint -cne [string]$script:PhotonRuntimeRequiredEnvironment.HERMES_MEM0_QDRANT_URL -or
    @($memoryVectorLock.publishedPorts).Count -ne 0) {
    throw 'runtime_adoption_memory_vector_lock_binding_mismatch'
}
$memoryVectorImage = [string]$memoryVectorLock.image

function Resolve-HermesWorkspacePath {
    param([AllowNull()][string]$ConfiguredPath)

    $configured = if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) { 'workspace' } else { $ConfiguredPath.Trim() }
    if ($configured.Length -gt 1024 -or $configured.IndexOfAny([char[]](0..31)) -ge 0) {
        throw 'WorkspacePath contains invalid characters or is too long.'
    }
    if ($configured -match '^(\\\\[?.]\\|\\\\|//|\\\?\\)') {
        throw 'WorkspacePath must be a local drive path or a bundle-relative path.'
    }

    $isRooted = [IO.Path]::IsPathRooted($configured)
    $resolved = [IO.Path]::GetFullPath($(if ($isRooted) { $configured } else { Join-Path $bundleRoot $configured }))
    $bundleFull = [IO.Path]::GetFullPath($bundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedTrimmed = $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [IO.Path]::GetPathRoot($resolvedTrimmed)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        [string]::Equals($resolvedTrimmed, $pathRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkspacePath cannot be a drive root.'
    }
    if ([string]::Equals($resolvedTrimmed, $bundleFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkspacePath cannot be the install bundle root.'
    }
    if (-not $isRooted -and
        -not $resolvedTrimmed.StartsWith($bundleFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A relative WorkspacePath must remain inside the install bundle.'
    }

    $relativeToRoot = $resolvedTrimmed.Substring($pathRoot.Length)
    if ($relativeToRoot.Contains(':')) {
        throw 'WorkspacePath cannot contain an alternate data stream.'
    }
    if (-not (Test-Path -LiteralPath $resolvedTrimmed -PathType Container)) {
        throw "WorkspacePath must already exist as a directory: $resolvedTrimmed"
    }

    $blockedRoots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [string]$env:OneDrive
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    foreach ($blocked in $blockedRoots) {
        if ([string]::Equals($resolvedTrimmed, $blocked, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'WorkspacePath cannot be a profile, Desktop, or OneDrive root.'
        }
    }

    $cursor = $resolvedTrimmed
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "WorkspacePath cannot traverse a reparse point: $cursor"
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = $parent
    }
    return $resolvedTrimmed
}

function Get-ConfiguredWorkspacePath {
    $configured = $null
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $configured = [string](Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).WorkspacePath
    }
    return Resolve-HermesWorkspacePath -ConfiguredPath $configured
}

function Get-RunningHermesRuntime {
    $result = Invoke-PhotonRuntimeDocker -Arguments @('inspect', '--type', 'container', 'hermes') -AllowFailure
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) { return $null }
    try { $container = @($result.Output | ConvertFrom-Json -ErrorAction Stop)[0] }
    catch { throw 'runtime_adoption_container_inspect_invalid' }
    if ([string]$container.Image -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'runtime_adoption_running_image_invalid' }
    return [pscustomobject]@{
        ImageId = [string]$container.Image
        ImageReference = [string]$container.Config.Image
        EnvironmentEntries = @($container.Config.Env | ForEach-Object { [string]$_ })
    }
}

function Get-ValidatedPreviousRuntimeEvidence {
    param([Parameter(Mandatory = $true)]$Candidate, [Parameter(Mandatory = $true)]$PriorCurrent)

    $path = Join-Path $bundleRoot 'logs\runtime-identity.json'
    $evidence = Read-PhotonRuntimeJson -Path $path -FailureCode 'runtime_adoption_previous_evidence_invalid'
    $observed = [DateTime]::MinValue
    $committed = [DateTime]::MinValue
    if ([int]$evidence.protocolVersion -ne 1 -or [string]$evidence.containerName -cne 'hermes' -or
        -not [DateTime]::TryParse([string]$evidence.observedAtUtc, [ref]$observed) -or
        -not [DateTime]::TryParse([string]$PriorCurrent.Commit.committedAtUtc, [ref]$committed) -or
        $observed.ToUniversalTime() -ge $committed.ToUniversalTime() -or
        [string]$evidence.imageId -cnotmatch '^sha256:[a-f0-9]{64}$' -or
        [string]$evidence.imageId -ceq [string]$Candidate.ImageId -or
        [string]$evidence.repoDigest -cnotmatch '^[^\s@]+@sha256:[a-f0-9]{64}$' -or
        [string]$evidence.imageReference -cne [string]$evidence.repoDigest -or
        ([string]$evidence.repoDigest).Substring(([string]$evidence.repoDigest).IndexOf('@') + 1) -cne [string]$evidence.imageId) {
        throw 'runtime_adoption_previous_evidence_binding_mismatch'
    }
    $result = Invoke-PhotonRuntimeDocker -Arguments @('image', 'inspect', [string]$evidence.imageId)
    try { $image = @($result.Output | ConvertFrom-Json -ErrorAction Stop)[0] }
    catch { throw 'runtime_adoption_previous_evidence_image_invalid' }
    if ([string]$image.Id -cne [string]$evidence.imageId -or
        [string]$evidence.repoDigest -notin @($image.RepoDigests | ForEach-Object { [string]$_ })) {
        throw 'runtime_adoption_previous_evidence_image_mismatch'
    }
    return [pscustomobject]@{
        generationId = $null
        imageId = [string]$evidence.imageId
        imageReference = [string]$evidence.imageReference
    }
}

function Resolve-PreviousRuntime {
    param(
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)]$Before,
        [AllowNull()]$PriorCurrent
    )

    if ([string]$Before.ImageId -cne [string]$Candidate.ImageId) {
        return [pscustomobject]@{
            generationId = if ($PriorCurrent) { [string]$PriorCurrent.GenerationId } else { $null }
            imageId = [string]$Before.ImageId
            imageReference = [string]$Before.ImageReference
        }
    }
    if ($PriorCurrent -and [string]$PriorCurrent.Commit.previous.imageId -cne [string]$Candidate.ImageId) {
        return $PriorCurrent.Commit.previous
    }
    if ($RepairPreviousFromRuntimeIdentity -and $PriorCurrent) {
        return Get-ValidatedPreviousRuntimeEvidence -Candidate $Candidate -PriorCurrent $PriorCurrent
    }
    throw 'runtime_adoption_previous_self_reference_requires_validated_evidence'
}

function Test-ComposeCandidate {
    param([Parameter(Mandatory = $true)][string]$ImageId)
    $prior = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
    try {
        $env:HERMES_IMAGE_REFERENCE = $ImageId
        $result = Invoke-PhotonRuntimeDocker -Arguments @('compose', 'config', '--images')
    }
    finally {
        if ($null -eq $prior) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
        else { $env:HERMES_IMAGE_REFERENCE = $prior }
    }
    $images = @($result.Output -split '\r?\n' |
        ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    if ($images.Count -ne 2 -or
        $ImageId -notin $images -or
        $memoryVectorImage -notin $images) {
        throw 'runtime_adoption_compose_image_mismatch'
    }
}

function Wait-HermesRuntimeHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $source = @'
import json
import urllib.request
with urllib.request.urlopen("http://127.0.0.1:9119/api/status", timeout=5) as response:
    payload = json.loads(response.read().decode("utf-8"))
version = payload.get("version")
if not isinstance(version, str) or not version.strip():
    raise RuntimeError("runtime_version_missing")
print(version.strip())
'@
    $encoded = [Convert]::ToBase64String([Text.UTF8Encoding]::new($false).GetBytes($source))
    $bootstrap = "import base64;exec(compile(base64.b64decode('$encoded'),'<photon-adoption-health>','exec'))"
    do {
        $result = Invoke-PhotonRuntimeDocker -Arguments @('exec', 'hermes', 'python', '-c', $bootstrap) -AllowFailure
        if ($result.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($result.Output)) { return $result.Output.Trim() }
        Start-Sleep -Seconds 2
    } until ([DateTime]::UtcNow -ge $deadline)
    return $null
}

function Ensure-MemoryVectorRuntime {
    param(
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$GatewayImageId
    )

    $prior = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
    try {
        # Compose evaluates the required gateway image interpolation even when
        # only the independent memory-vector service is selected.
        $env:HERMES_IMAGE_REFERENCE = $GatewayImageId
        $start = Invoke-PhotonRuntimeDocker -Arguments @('compose', 'up', '-d', '--no-deps', 'memory-vector') -AllowFailure
        if ($start.ExitCode -ne 0) { throw "runtime_adoption_memory_vector_compose_failed:$($start.ExitCode)" }
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            $idResult = Invoke-PhotonRuntimeDocker -Arguments @('compose', 'ps', '-q', 'memory-vector') -AllowFailure
            $containerId = $idResult.Output.Trim()
            if ($idResult.ExitCode -eq 0 -and $containerId -match '^[a-f0-9]{64}$') {
                $inspectResult = Invoke-PhotonRuntimeDocker -Arguments @('inspect', '--type', 'container', $containerId) -AllowFailure
                if ($inspectResult.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($inspectResult.Output)) {
                    try { $container = @($inspectResult.Output | ConvertFrom-Json -ErrorAction Stop)[0] }
                    catch { $container = $null }
                    if ($null -ne $container -and
                        [string]$container.Config.Image -ceq $memoryVectorImage -and
                        [bool]$container.State.Running -and
                        [string]$container.State.Health.Status -ceq 'healthy') {
                        return $containerId
                    }
                }
            }
            Start-Sleep -Seconds 1
        } until ([DateTime]::UtcNow -ge $deadline)
        throw 'runtime_adoption_memory_vector_unhealthy'
    }
    finally {
        if ($null -eq $prior) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
        else { $env:HERMES_IMAGE_REFERENCE = $prior }
    }
}

function Invoke-ComposeImage {
    param([Parameter(Mandatory = $true)][string]$ImageId)
    $prior = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
    try {
        $env:HERMES_IMAGE_REFERENCE = $ImageId
        $compose = Invoke-PhotonRuntimeDocker -Arguments @('compose', 'up', '-d', '--no-deps', '--force-recreate', 'gateway') -AllowFailure
        if ($compose.ExitCode -ne 0) { throw "runtime_adoption_gateway_compose_failed:$($compose.ExitCode)" }
    }
    finally {
        if ($null -eq $prior) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
        else { $env:HERMES_IMAGE_REFERENCE = $prior }
    }
}

function Write-GenerationCommit {
    param(
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)]$Previous,
        [Parameter(Mandatory = $true)]$Deployment
    )
    $base = Get-PhotonRuntimeGenerationBase -BundleRoot $bundleRoot
    $path = Join-Path $base 'runtime-generation.current.json'
    $temporary = Join-Path $base ('.runtime-generation.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = Join-Path $base ('.runtime-generation.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $commit = [ordered]@{
        schemaId = $script:PhotonRuntimeGenerationCommitSchema
        protocolVersion = $script:PhotonRuntimeProtocolVersion
        committedAtUtc = [DateTime]::UtcNow.ToString('o')
        currentGenerationId = [string]$Candidate.GenerationId
        currentImageId = [string]$Candidate.ImageId
        currentLockSha256 = [string]$Candidate.LockSha256
        currentEnvSha256 = [string]$Candidate.EnvSha256
        deployment = [ordered]@{
            schemaId = [string]$Deployment.SchemaId
            composeSha256 = [string]$Deployment.ComposeSha256
            fingerprintSha256 = [string]$Deployment.FingerprintSha256
            requiredEnvironment = [ordered]@{
                HERMES_WORKBENCH = [string]$Deployment.RequiredEnvironment.HERMES_WORKBENCH
                HERMES_WORKBENCH_AUTHENTICATED_MEM0 = [string]$Deployment.RequiredEnvironment.HERMES_WORKBENCH_AUTHENTICATED_MEM0
                HERMES_MEM0_MODEL_URL = [string]$Deployment.RequiredEnvironment.HERMES_MEM0_MODEL_URL
                HERMES_MEM0_QDRANT_URL = [string]$Deployment.RequiredEnvironment.HERMES_MEM0_QDRANT_URL
            }
        }
        previous = [ordered]@{
            generationId = if ([string]::IsNullOrWhiteSpace([string]$Previous.generationId)) { $null } else { [string]$Previous.generationId }
            imageId = [string]$Previous.imageId
            imageReference = [string]$Previous.imageReference
        }
    }
    try {
        Write-PhotonJsonFile -Path $temporary -Value $commit -CreateNew
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Assert-PhotonRuntimePlainItem -Path $path -Kind File | Out-Null
            [IO.File]::Replace($temporary, $path, $backup, $true)
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        else { [IO.File]::Move($temporary, $path) }
    }
    finally {
        foreach ($extra in @($temporary, $backup)) {
            if (Test-Path -LiteralPath $extra -PathType Leaf) { Remove-Item -LiteralPath $extra -Force -ErrorAction SilentlyContinue }
        }
    }
    return $path
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker_command_missing' }
Push-Location $bundleRoot
$previousEnvironment = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
$previousWorkspaceEnvironment = [Environment]::GetEnvironmentVariable('HERMES_HOST_WORKSPACE_PATH', 'Process')
try {
    $env:HERMES_HOST_WORKSPACE_PATH = Get-ConfiguredWorkspacePath
    $candidate = Get-PhotonRuntimeGeneration -BundleRoot $bundleRoot -GenerationId $GenerationId
    $deployment = Get-PhotonRuntimeDeploymentSpec -BundleRoot $bundleRoot
    Assert-PhotonRuntimeDockerImage -Generation $candidate
    Test-ComposeCandidate -ImageId $candidate.ImageId
    $before = Get-RunningHermesRuntime
    if ($null -eq $before) { throw 'runtime_adoption_running_container_required' }
    $beforeImage = Invoke-PhotonRuntimeDocker -Arguments @('image', 'inspect', [string]$before.ImageId)
    try { $beforeImageObject = @($beforeImage.Output | ConvertFrom-Json -ErrorAction Stop)[0] }
    catch { throw 'runtime_adoption_prior_image_inspect_invalid' }
    if ([string]$beforeImageObject.Id -cne [string]$before.ImageId) { throw 'runtime_adoption_prior_image_missing' }
    $priorCurrent = Get-PhotonCurrentRuntimeGeneration -BundleRoot $bundleRoot -AllowLegacyDeployment -AllowDeploymentMismatch -AllowRequiredEnvironmentUpgrade
    if ($priorCurrent -and [string]$priorCurrent.ImageId -cne [string]$before.ImageId) {
        throw 'runtime_adoption_pointer_container_mismatch'
    }
    $previous = Resolve-PreviousRuntime -Candidate $candidate -Before $before -PriorCurrent $priorCurrent
    $runningDeploymentMatches = Test-PhotonRuntimeEnvironmentMatches -EnvironmentEntries $before.EnvironmentEntries -Deployment $deployment
    $committedDeploymentMatches = $priorCurrent -and [bool]$priorCurrent.DeploymentMatchesCurrent
    $deploymentRecreateRequired = -not ($runningDeploymentMatches -and $committedDeploymentMatches)

    if ($PreflightOnly) {
        [pscustomobject]@{
            Status = 'preflight-passed'
            CandidateGenerationId = $candidate.GenerationId
            CandidateImageId = $candidate.ImageId
            BeforeImageId = $before.ImageId
            PriorGenerationId = if ($priorCurrent) { $priorCurrent.GenerationId } else { $null }
            DeploymentFingerprintSha256 = $deployment.FingerprintSha256
            DeploymentRecreateRequired = $deploymentRecreateRequired
            PreviousImageId = [string]$previous.imageId
        }
        return
    }
    if (-not $Yes) {
        Write-Host "Candidate generation: $($candidate.GenerationId)" -ForegroundColor Cyan
        Write-Host "Candidate image:      $($candidate.ImageId)"
        Write-Host "Current image:        $($before.ImageId)"
        $confirmation = Read-Host 'Type ADOPT to switch the Hermes container to this verified generation'
        if ($confirmation -cne 'ADOPT') { Write-Host 'Adoption cancelled. Nothing changed.'; return }
    }

    $base = Get-PhotonRuntimeGenerationBase -BundleRoot $bundleRoot
    $mutexPath = Join-Path $base 'runtime-generation.adoption.lock'
    $mutex = [IO.File]::Open($mutexPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $memoryVectorContainerId = Ensure-MemoryVectorRuntime -TimeoutSeconds $HealthTimeoutSeconds -GatewayImageId $candidate.ImageId
        if ([string]$before.ImageId -cne [string]$candidate.ImageId -or $deploymentRecreateRequired) {
            Invoke-ComposeImage -ImageId $candidate.ImageId
        }
        $after = Get-RunningHermesRuntime
        if ($null -eq $after -or [string]$after.ImageId -cne [string]$candidate.ImageId -or
            -not (Test-PhotonRuntimeEnvironmentMatches -EnvironmentEntries $after.EnvironmentEntries -Deployment $deployment)) {
            throw 'runtime_adoption_post_compose_deployment_mismatch'
        }
        $runtimeVersion = Wait-HermesRuntimeHealth -TimeoutSeconds $HealthTimeoutSeconds
        if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
            Write-Warning 'The verified candidate did not become healthy. Restoring the exact prior image ID.'
            Invoke-ComposeImage -ImageId $before.ImageId
            $rollbackVersion = Wait-HermesRuntimeHealth -TimeoutSeconds $HealthTimeoutSeconds
            if ([string]::IsNullOrWhiteSpace($rollbackVersion)) {
                throw "runtime_adoption_and_exact_rollback_failed:$($before.ImageId)"
            }
            throw "runtime_adoption_health_failed_exact_rollback_restored:$($before.ImageId)"
        }
        try {
            $commitPath = Write-GenerationCommit -Candidate $candidate -Previous $previous -Deployment $deployment
        }
        catch {
            $commitFailure = $_
            if ([string]$before.ImageId -cne [string]$candidate.ImageId) {
                Write-Warning 'The candidate was healthy, but its generation commit failed. Restoring the exact prior image ID.'
                Invoke-ComposeImage -ImageId $before.ImageId
                $commitRollbackVersion = Wait-HermesRuntimeHealth -TimeoutSeconds $HealthTimeoutSeconds
                if ([string]::IsNullOrWhiteSpace($commitRollbackVersion)) {
                    throw "runtime_adoption_commit_and_exact_rollback_failed:$($before.ImageId):$($commitFailure.Exception.Message)"
                }
            }
            throw "runtime_adoption_commit_failed_exact_rollback_restored:$($before.ImageId):$($commitFailure.Exception.Message)"
        }
        [pscustomobject]@{
            Status = 'adopted'
            GenerationId = $candidate.GenerationId
            ImageId = $candidate.ImageId
            PreviousImageId = [string]$previous.imageId
            RuntimeVersion = $runtimeVersion
            MemoryVectorContainerId = $memoryVectorContainerId
            CommitPath = $commitPath
        }
    }
    finally {
        $mutex.Dispose()
        Remove-Item -LiteralPath $mutexPath -Force -ErrorAction SilentlyContinue
    }
}
finally {
    if ($null -eq $previousEnvironment) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
    else { $env:HERMES_IMAGE_REFERENCE = $previousEnvironment }
    if ($null -eq $previousWorkspaceEnvironment) { Remove-Item Env:\HERMES_HOST_WORKSPACE_PATH -ErrorAction SilentlyContinue }
    else { $env:HERMES_HOST_WORKSPACE_PATH = $previousWorkspaceEnvironment }
    Pop-Location
}
