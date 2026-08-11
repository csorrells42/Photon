[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotonRuntimeAdoptionSmoke-' + [Guid]::NewGuid().ToString('N'))
$global:adoptionCalls = [Collections.Generic.List[string]]::new()
$global:adoptionCandidateId = 'sha256:' + ('2' * 64)
$global:adoptionBeforeId = 'sha256:' + ('9' * 64)
$global:adoptionMemoryVectorImage = 'qdrant/qdrant@sha256:affb67e1d6f2f93d7d20b90d238a7d4b974d36351c162e73bda794e4b2e03483'
$global:adoptionComposeMemoryVectorImage = $global:adoptionMemoryVectorImage
$global:adoptionMemoryVectorContainerId = '6' * 64
$global:adoptionActiveId = $global:adoptionBeforeId
$global:adoptionHealthMode = 'success'
$global:adoptionLock = $null
$global:adoptionDeploymentApplied = $false
$global:adoptionRequiredEnvironment = @(
    'HERMES_WORKBENCH=1',
    'HERMES_WORKBENCH_AUTHENTICATED_MEM0=1',
    'HERMES_MEM0_MODEL_URL=http://host.docker.internal:12434/engines/v1',
    'HERMES_MEM0_QDRANT_URL=http://memory-vector:6333'
)

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Require-Failure {
    param([scriptblock]$Action, [string]$Code)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -like "*$Code*") { return }
        throw "Expected '$Code', received '$($_.Exception.Message)'."
    }
    throw "Expected '$Code', but the operation succeeded."
}

function Write-Utf8 {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function New-AdoptionCase {
    param([Parameter(Mandatory = $true)][string]$Name)
    $root = Join-Path $tempRoot $Name
    $runtime = Join-Path $root 'runtime'
    $generationBase = Join-Path $root 'logs\runtime-generations'
    $generationId = ('1' * 16) + '-' + ('2' * 12)
    $generation = Join-Path $generationBase $generationId
    New-Item -ItemType Directory -Path $runtime, $generation -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'workspace') -Force | Out-Null
    Write-Utf8 (Join-Path $root 'launcher.settings.json') '{"WorkspacePath":"workspace"}'
    foreach ($file in @('Runtime.Common.ps1', 'Runtime.Generation.ps1', 'Adopt-HermesRuntime.ps1')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "runtime\$file") -Destination (Join-Path $runtime $file)
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'runtime\memory-vector.lock.json') -Destination (Join-Path $runtime 'memory-vector.lock.json')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docker-compose.yml') -Destination (Join-Path $root 'docker-compose.yml')
    $labels = [ordered]@{
        'org.opencontainers.image.revision' = ('3' * 40)
        'io.photon.workbench.source.digest' = ('1' * 64)
        'io.photon.workbench.dockerfile.sha256' = ('4' * 64)
        'io.photon.workbench.runtime.protocol' = '1'
        'io.photon.hermes-workbench.mode' = 'authenticated'
        'io.photon.hermes-workbench.mem0.capability' = 'mem0ai+qdrant'
        'io.photon.hermes-workbench.mem0.version' = '2.0.10'
        'io.photon.hermes-workbench.qdrant-client.version' = '1.18.0'
    }
    $lock = [ordered]@{
        schemaId = 'photon.runtime.image-lock/v1'
        protocolVersion = 1
        generationId = $generationId
        imageTag = 'hermes-workbench-runtime:dev-333333333333-1111111111111111'
        imageId = $global:adoptionCandidateId
        sourceCommit = ('3' * 40)
        sourceRemote = 'https://github.com/nousresearch/hermes-agent'
        sourceDigest = ('1' * 64)
        dockerfileSha256 = ('4' * 64)
        sourceManifestSha256 = ('5' * 64)
        labels = $labels
        probe = [ordered]@{}
    }
    $lockPath = Join-Path $generation 'runtime-image.lock.json'
    $envPath = Join-Path $generation 'runtime-image.env'
    Write-Utf8 $lockPath ($lock | ConvertTo-Json -Depth 8)
    Write-Utf8 $envPath "HERMES_IMAGE_REFERENCE=$($global:adoptionCandidateId)`n"
    (Get-Item -LiteralPath $lockPath).IsReadOnly = $true
    (Get-Item -LiteralPath $envPath).IsReadOnly = $true
    return [pscustomobject]@{
        Root = $root
        Runtime = $runtime
        GenerationId = $generationId
        Generation = $generation
        Lock = $lock
        LockPath = $lockPath
        EnvPath = $envPath
        AdoptScript = Join-Path $runtime 'Adopt-HermesRuntime.ps1'
        CommitPath = Join-Path $generationBase 'runtime-generation.current.json'
    }
}

function Write-LegacySelfCommit {
    param([Parameter(Mandatory = $true)]$Case)
    $lockSha = (Get-FileHash -LiteralPath $Case.LockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $envSha = (Get-FileHash -LiteralPath $Case.EnvPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $commit = [ordered]@{
        schemaId = 'photon.runtime.generation-commit/v1'
        protocolVersion = 1
        committedAtUtc = '2026-08-10T21:39:26Z'
        currentGenerationId = $Case.GenerationId
        currentImageId = $global:adoptionCandidateId
        currentLockSha256 = $lockSha
        currentEnvSha256 = $envSha
        previous = [ordered]@{
            generationId = $Case.GenerationId
            imageId = $global:adoptionCandidateId
            imageReference = $global:adoptionCandidateId
        }
    }
    Write-Utf8 $Case.CommitPath ($commit | ConvertTo-Json -Depth 8)
    $identity = [ordered]@{
        protocolVersion = 1
        observedAtUtc = '2026-08-10T16:49:02Z'
        containerName = 'hermes'
        imageReference = 'nousresearch/hermes-agent@' + $global:adoptionBeforeId
        imageId = $global:adoptionBeforeId
        repoDigest = 'nousresearch/hermes-agent@' + $global:adoptionBeforeId
        revision = ('7' * 40)
    }
    Write-Utf8 (Join-Path $Case.Root 'logs\runtime-identity.json') ($identity | ConvertTo-Json -Depth 5)
}

function Write-PreQdrantDeploymentCommit {
    param([Parameter(Mandatory = $true)]$Case)

    $lockSha = (Get-FileHash -LiteralPath $Case.LockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $envSha = (Get-FileHash -LiteralPath $Case.EnvPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $composeSha = (Get-FileHash -LiteralPath (Join-Path $Case.Root 'docker-compose.yml') -Algorithm SHA256).Hash.ToLowerInvariant()
    $environment = [ordered]@{
        HERMES_WORKBENCH = '1'
        HERMES_WORKBENCH_AUTHENTICATED_MEM0 = '1'
        HERMES_MEM0_MODEL_URL = 'http://host.docker.internal:12434/engines/v1'
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('schemaId=photon.runtime.deployment/v1')
    $lines.Add("composeSha256=$composeSha")
    foreach ($key in $environment.Keys) { $lines.Add("$key=$($environment[$key])") }
    $fingerprintBytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($fingerprintBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $commit = [ordered]@{
        schemaId = 'photon.runtime.generation-commit/v2'
        protocolVersion = 1
        committedAtUtc = '2026-08-11T03:55:48Z'
        currentGenerationId = $Case.GenerationId
        currentImageId = $global:adoptionCandidateId
        currentLockSha256 = $lockSha
        currentEnvSha256 = $envSha
        deployment = [ordered]@{
            schemaId = 'photon.runtime.deployment/v1'
            composeSha256 = $composeSha
            fingerprintSha256 = $fingerprint
            requiredEnvironment = $environment
        }
        previous = [ordered]@{
            generationId = $null
            imageId = $global:adoptionBeforeId
            imageReference = 'nousresearch/hermes-agent@' + $global:adoptionBeforeId
        }
    }
    Write-Utf8 $Case.CommitPath ($commit | ConvertTo-Json -Depth 8)
}

function global:docker {
    $global:LASTEXITCODE = 0
    $command = ($args | ForEach-Object { [string]$_ }) -join '|'
    $global:adoptionCalls.Add($command)
    if ($args.Count -ge 3 -and $args[0] -ceq 'image' -and $args[1] -ceq 'inspect') {
        $id = [string]$args[2]
        if ($id -ceq $global:adoptionCandidateId) {
            return (@([ordered]@{ Id = $id; RepoDigests = @(); Config = [ordered]@{ Labels = $global:adoptionLock.labels } }) | ConvertTo-Json -Depth 8 -Compress)
        }
        if ($id -ceq $global:adoptionBeforeId) {
            return (@([ordered]@{
                Id = $id
                RepoDigests = @('nousresearch/hermes-agent@' + $global:adoptionBeforeId)
                Config = [ordered]@{ Labels = [ordered]@{} }
            }) | ConvertTo-Json -Depth 5 -Compress)
        }
        $global:LASTEXITCODE = 1
        return 'missing image'
    }
    if ($command -ceq ('inspect|--type|container|' + $global:adoptionMemoryVectorContainerId)) {
        return (@([ordered]@{
            Config = [ordered]@{ Image = $global:adoptionMemoryVectorImage }
            State = [ordered]@{ Running = $true; Health = [ordered]@{ Status = 'healthy' } }
        }) | ConvertTo-Json -Depth 6 -Compress)
    }
    if ($args.Count -ge 1 -and $args[0] -ceq 'inspect') {
        return (@([ordered]@{
            Image = $global:adoptionActiveId
            Config = [ordered]@{
                Image = if ($global:adoptionActiveId -ceq $global:adoptionCandidateId) { $global:adoptionCandidateId } else { 'nousresearch/hermes-agent@' + $global:adoptionBeforeId }
                Env = if ($global:adoptionDeploymentApplied) { $global:adoptionRequiredEnvironment } else { @('HERMES_WORKBENCH=1') }
            }
        }) | ConvertTo-Json -Depth 5 -Compress)
    }
    if ($command -ceq 'compose|config|--images') {
        if ([string]::IsNullOrWhiteSpace([string]$env:HERMES_HOST_WORKSPACE_PATH) -or
            -not (Test-Path -LiteralPath ([string]$env:HERMES_HOST_WORKSPACE_PATH) -PathType Container)) {
            $global:LASTEXITCODE = 91
            return 'workspace authority missing'
        }
        $global:adoptionCalls.Add('compose-workspace|' + [string]$env:HERMES_HOST_WORKSPACE_PATH)
        return ([string]$env:HERMES_IMAGE_REFERENCE + [Environment]::NewLine + $global:adoptionComposeMemoryVectorImage)
    }
    if ($command -ceq 'compose|up|-d|--no-deps|--force-recreate|gateway') {
        $global:adoptionCalls.Add('compose-image|' + [string]$env:HERMES_IMAGE_REFERENCE)
        $global:adoptionActiveId = [string]$env:HERMES_IMAGE_REFERENCE
        $global:adoptionDeploymentApplied = $global:adoptionActiveId -ceq $global:adoptionCandidateId
        return
    }
    if ($command -ceq 'compose|up|-d|--no-deps|memory-vector') { return }
    if ($command -ceq 'compose|ps|-q|memory-vector') { return $global:adoptionMemoryVectorContainerId }
    if ($args.Count -ge 1 -and $args[0] -ceq 'exec') {
        if ($global:adoptionActiveId -ceq $global:adoptionCandidateId -and $global:adoptionHealthMode -ceq 'failure') {
            $global:LASTEXITCODE = 1
            return 'candidate unhealthy'
        }
        return '1.0.0'
    }
    $global:LASTEXITCODE = 92
    return "unexpected:$command"
}

try {
    $launchText = Get-Content -LiteralPath (Join-Path $projectRoot 'Launch-Hermes.ps1') -Raw
    $remoteLaunchText = Get-Content -LiteralPath (Join-Path $projectRoot 'remote-install\Launch-Hermes.ps1') -Raw
    Require ($launchText -ceq $remoteLaunchText) 'launcher mirrors must be byte-identical'
    Require ($launchText.Contains('Get-PhotonCurrentRuntimeGeneration')) 'launcher must resolve the committed verified generation'
    Require ($launchText.Contains('Assert-PhotonRuntimeDockerImage')) 'launcher must revalidate exact image labels before Compose'
    $adoptText = Get-Content -LiteralPath (Join-Path $projectRoot 'runtime\Adopt-HermesRuntime.ps1') -Raw
    Require ($adoptText.Contains('$env:HERMES_HOST_WORKSPACE_PATH = Get-ConfiguredWorkspacePath')) 'runtime adoption must resolve the same validated workspace before Compose'
    $composeText = Get-Content -LiteralPath (Join-Path $projectRoot 'docker-compose.yml') -Raw
    Require ($composeText -ceq (Get-Content -LiteralPath (Join-Path $projectRoot 'remote-install\docker-compose.yml') -Raw)) 'Compose mirrors must be byte-identical'
    Require ($composeText.Contains('${HERMES_IMAGE_REFERENCE:?')) 'Compose must reject an unselected image'
    Require ($composeText.Contains('${HERMES_HOST_WORKSPACE_PATH:?launcher must set HERMES_HOST_WORKSPACE_PATH}')) 'Compose must reject an unvalidated workspace bind'
    Require ($composeText.Contains('create_host_path: false')) 'Compose must not implicitly create a workspace bind source'
    Require (-not $composeText.Contains(':-nousresearch/hermes-agent')) 'Compose must not silently fall back to the official image'
    $updateText = Get-Content -LiteralPath (Join-Path $projectRoot 'Update-Hermes.ps1') -Raw
    Require ($updateText -ceq (Get-Content -LiteralPath (Join-Path $projectRoot 'remote-install\Update-Hermes.ps1') -Raw)) 'updater mirrors must be byte-identical'
    Require ($updateText.Contains('A verified runtime generation is active')) 'legacy updater must refuse to replace an adopted generation'
    $shutdownText = Get-Content -LiteralPath (Join-Path $projectRoot 'Shutdown-Hermes.ps1') -Raw
    Require ($shutdownText -ceq (Get-Content -LiteralPath (Join-Path $projectRoot 'remote-install\Shutdown-Hermes.ps1') -Raw)) 'shutdown mirrors must be byte-identical'
    Require ($shutdownText.Contains("docker inspect --type container hermes")) 'shutdown must supply Compose the exact running image identity'
    $builderText = Get-Content -LiteralPath (Join-Path $projectRoot 'remote-install\Build-Install-Zip.ps1') -Raw
    foreach ($requiredRuntimeFile in @('Runtime.Generation.ps1', 'Adopt-HermesRuntime.ps1', 'photon-models.lock.json', 'memory-vector.lock.json', 'Install-PhotonModels.ps1')) {
        Require ($builderText.Contains("'$requiredRuntimeFile'")) "portable builder must explicitly allow-list $requiredRuntimeFile"
    }
    Require ($builderText.Contains("'Hermes-Remote-Install/runtime/Adopt-HermesRuntime.ps1'")) 'ZIP readback must require the adoption script'

    $preQdrant = New-AdoptionCase -Name 'pre-qdrant-deployment-upgrade'
    $global:adoptionLock = $preQdrant.Lock
    Write-PreQdrantDeploymentCommit -Case $preQdrant
    $global:adoptionActiveId = $global:adoptionCandidateId
    $global:adoptionDeploymentApplied = $false
    $global:adoptionCalls.Clear()
    $upgradePreflight = @(& $preQdrant.AdoptScript -GenerationId $preQdrant.GenerationId -PreflightOnly)
    Require ([string]$upgradePreflight[-1].Status -ceq 'preflight-passed') 'pre-Qdrant v2 deployment must be readable only for explicit adoption upgrade'
    Require ([bool]$upgradePreflight[-1].DeploymentRecreateRequired) 'pre-Qdrant deployment must require recreation'
    Require ([string]$upgradePreflight[-1].PreviousImageId -ceq $global:adoptionBeforeId) 'pre-Qdrant upgrade must preserve the exact prior rollback image'

    $success = New-AdoptionCase -Name 'success'
    $global:adoptionLock = $success.Lock
    $global:adoptionActiveId = $global:adoptionBeforeId
    $global:adoptionHealthMode = 'success'
    $global:adoptionDeploymentApplied = $false
    $global:adoptionCalls.Clear()
    $preflight = @(& $success.AdoptScript -GenerationId $success.GenerationId -PreflightOnly)
    Require ([string]$preflight[-1].Status -ceq 'preflight-passed') 'preflight must pass without adoption'
    Require (-not (Test-Path -LiteralPath $success.CommitPath)) 'preflight must not write the current-generation commit'
    Require (@($global:adoptionCalls | Where-Object { $_ -like 'compose|up|*' }).Count -eq 0) 'preflight must not recreate the container'

    $global:adoptionComposeMemoryVectorImage = 'qdrant/qdrant@sha256:' + ('8' * 64)
    Require-Failure { & $success.AdoptScript -GenerationId $success.GenerationId -PreflightOnly } 'runtime_adoption_compose_image_mismatch'
    $global:adoptionComposeMemoryVectorImage = $global:adoptionMemoryVectorImage

    $global:adoptionCalls.Clear()
    $adopted = @(& $success.AdoptScript -GenerationId $success.GenerationId -Yes -HealthTimeoutSeconds 10)
    Require ([string]$adopted[-1].Status -ceq 'adopted') 'healthy candidate must be adopted'
    Require ([string]$adopted[-1].MemoryVectorContainerId -ceq $global:adoptionMemoryVectorContainerId) 'adoption must bind the exact healthy memory-vector container'
    Require ($global:adoptionActiveId -ceq $global:adoptionCandidateId) 'healthy candidate must become active'
    $commit = Get-Content -LiteralPath $success.CommitPath -Raw | ConvertFrom-Json
    Require ([string]$commit.currentGenerationId -ceq $success.GenerationId) 'commit must name the exact generation'
    Require ([string]$commit.currentImageId -ceq $global:adoptionCandidateId) 'commit must bind the exact candidate image ID'
    Require ([string]$commit.previous.imageId -ceq $global:adoptionBeforeId) 'commit must retain the exact prior image ID'
    Require ([string]$commit.deployment.schemaId -ceq 'photon.runtime.deployment/v1') 'commit must bind deployment schema'
    Require ([string]$commit.deployment.composeSha256 -ceq (Get-FileHash -LiteralPath (Join-Path $success.Root 'docker-compose.yml') -Algorithm SHA256).Hash.ToLowerInvariant()) 'commit must bind exact Compose bytes'
    Require ([string]$commit.deployment.requiredEnvironment.HERMES_WORKBENCH_AUTHENTICATED_MEM0 -ceq '1') 'commit must bind strict authenticated Mem0 mode'
    Require ([string]$commit.deployment.requiredEnvironment.HERMES_MEM0_QDRANT_URL -ceq 'http://memory-vector:6333') 'commit must bind the internal memory-vector endpoint'

    . (Join-Path $success.Runtime 'Runtime.Generation.ps1')
    $resolved = Get-PhotonCurrentRuntimeGeneration -BundleRoot $success.Root
    Require ([string]$resolved.ImageId -ceq $global:adoptionCandidateId) 'resolver must select the committed exact image ID'
    $originalCompose = [IO.File]::ReadAllText((Join-Path $success.Root 'docker-compose.yml'))
    Write-Utf8 (Join-Path $success.Root 'docker-compose.yml') ($originalCompose + "`n# deployment tamper`n")
    Require-Failure { Get-PhotonCurrentRuntimeGeneration -BundleRoot $success.Root } 'runtime_generation_commit_deployment_mismatch'
    Write-Utf8 (Join-Path $success.Root 'docker-compose.yml') $originalCompose
    (Get-Item -LiteralPath $success.EnvPath).IsReadOnly = $false
    Add-Content -LiteralPath $success.EnvPath -Value 'UNAPPROVED=1'
    (Get-Item -LiteralPath $success.EnvPath).IsReadOnly = $true
    Require-Failure { Get-PhotonCurrentRuntimeGeneration -BundleRoot $success.Root } 'runtime_generation_env_hash_mismatch'

    $rollback = New-AdoptionCase -Name 'rollback'
    $global:adoptionLock = $rollback.Lock
    $global:adoptionActiveId = $global:adoptionBeforeId
    $global:adoptionHealthMode = 'failure'
    $global:adoptionDeploymentApplied = $false
    $global:adoptionCalls.Clear()
    Require-Failure { & $rollback.AdoptScript -GenerationId $rollback.GenerationId -Yes -HealthTimeoutSeconds 10 } 'runtime_adoption_health_failed_exact_rollback_restored'
    Require ($global:adoptionActiveId -ceq $global:adoptionBeforeId) 'failed candidate must restore the exact prior image ID'
    Require (-not (Test-Path -LiteralPath $rollback.CommitPath)) 'failed candidate must not commit a generation pointer'
    Require ($global:adoptionCalls -contains ('compose-image|' + $global:adoptionCandidateId)) 'candidate compose must use exact candidate ID'
    Require ($global:adoptionCalls -contains ('compose-image|' + $global:adoptionBeforeId)) 'rollback compose must use exact prior ID'
    Require (@($global:adoptionCalls | Where-Object { $_ -like 'compose-workspace|*' }).Count -ge 1) 'adoption must supply a validated workspace to Compose'

    $commitFailure = New-AdoptionCase -Name 'commit-failure'
    $global:adoptionLock = $commitFailure.Lock
    $global:adoptionActiveId = $global:adoptionBeforeId
    $global:adoptionHealthMode = 'success'
    $global:adoptionDeploymentApplied = $false
    $global:adoptionCalls.Clear()
    New-Item -ItemType Directory -Path $commitFailure.CommitPath | Out-Null
    Require-Failure { & $commitFailure.AdoptScript -GenerationId $commitFailure.GenerationId -Yes -HealthTimeoutSeconds 10 } 'runtime_adoption_commit_failed_exact_rollback_restored'
    Require ($global:adoptionActiveId -ceq $global:adoptionBeforeId) 'commit failure must restore the exact prior image ID'
    Require ($global:adoptionCalls -contains ('compose-image|' + $global:adoptionBeforeId)) 'commit-failure rollback must use the exact prior ID'

    $sameImage = New-AdoptionCase -Name 'same-image-deployment-upgrade'
    $global:adoptionLock = $sameImage.Lock
    Write-LegacySelfCommit -Case $sameImage
    $global:adoptionActiveId = $global:adoptionCandidateId
    $global:adoptionHealthMode = 'success'
    $global:adoptionDeploymentApplied = $true
    $global:adoptionCalls.Clear()
    Require-Failure {
        & $sameImage.AdoptScript -GenerationId $sameImage.GenerationId -PreflightOnly
    } 'runtime_adoption_previous_self_reference_requires_validated_evidence'
    $preflightRepair = @(& $sameImage.AdoptScript -GenerationId $sameImage.GenerationId -PreflightOnly -RepairPreviousFromRuntimeIdentity)
    Require ([bool]$preflightRepair[-1].DeploymentRecreateRequired) 'legacy deployment commit must require recreate even when image and running env match'
    Require ([string]$preflightRepair[-1].PreviousImageId -ceq $global:adoptionBeforeId) 'validated evidence must recover the exact non-self previous image'
    $global:adoptionCalls.Clear()
    & $sameImage.AdoptScript -GenerationId $sameImage.GenerationId -Yes -RepairPreviousFromRuntimeIdentity -HealthTimeoutSeconds 10 | Out-Null
    Require (@($global:adoptionCalls | Where-Object { $_ -ceq ('compose-image|' + $global:adoptionCandidateId) }).Count -eq 1) 'same image with changed deployment fingerprint must force exactly one recreate'
    $repaired = Get-Content -LiteralPath $sameImage.CommitPath -Raw | ConvertFrom-Json
    Require ([string]$repaired.schemaId -ceq 'photon.runtime.generation-commit/v2') 'same-image repair must upgrade the commit schema'
    Require ([string]$repaired.previous.imageId -ceq $global:adoptionBeforeId) 'same-image repair must restore validated prior rollback provenance'
    Require ([string]$repaired.previous.generationId -ceq '') 'external prior evidence must not invent a generation ID'
    $global:adoptionDeploymentApplied = $false
    $missingEnvironment = @(& $sameImage.AdoptScript -GenerationId $sameImage.GenerationId -PreflightOnly)
    Require ([bool]$missingEnvironment[-1].DeploymentRecreateRequired) 'same image with a missing required running environment value must require recreate'
    $global:adoptionDeploymentApplied = $true
    $global:adoptionCalls.Clear()
    & $sameImage.AdoptScript -GenerationId $sameImage.GenerationId -Yes -HealthTimeoutSeconds 10 | Out-Null
    Require (@($global:adoptionCalls | Where-Object { $_ -like 'compose-image|*' }).Count -eq 0) 'same image with matching committed and running deployment must be idempotent'
    $preserved = Get-Content -LiteralPath $sameImage.CommitPath -Raw | ConvertFrom-Json
    Require ([string]$preserved.previous.imageId -ceq $global:adoptionBeforeId) 'idempotent same-generation adoption must preserve the different rollback image'
}
finally {
    Remove-Item Function:\global:docker -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        Get-ChildItem -LiteralPath $tempRoot -Recurse -File -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Runtime generation adoption smoke passed.' -ForegroundColor Green
