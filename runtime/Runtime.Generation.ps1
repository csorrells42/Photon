. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')

$script:PhotonRuntimeGenerationCommitSchema = 'photon.runtime.generation-commit/v2'
$script:PhotonRuntimeGenerationCommitLegacySchema = 'photon.runtime.generation-commit/v1'
$script:PhotonRuntimeDeploymentSchema = 'photon.runtime.deployment/v1'
$script:PhotonRuntimeRequiredEnvironment = [ordered]@{
    HERMES_WORKBENCH = '1'
    HERMES_WORKBENCH_AUTHENTICATED_MEM0 = '1'
    HERMES_MEM0_MODEL_URL = 'http://host.docker.internal:12434/engines/v1'
    HERMES_MEM0_QDRANT_URL = 'http://memory-vector:6333'
}

function Assert-PhotonRuntimePlainItem {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('File', 'Directory')][string]$Kind,
        [switch]$RequireReadOnly
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($Kind -eq 'File' -and -not ($item -is [IO.FileInfo])) -or
        ($Kind -eq 'Directory' -and -not ($item -is [IO.DirectoryInfo]))) {
        throw "runtime_generation_item_invalid:$Path"
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "runtime_generation_reparse_rejected:$Path"
    }
    if ($RequireReadOnly -and -not $item.IsReadOnly) {
        throw "runtime_generation_file_not_readonly:$Path"
    }
    return $item
}

function Set-PhotonRuntimeComposeSelection {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)][string]$ImageReference
    )

    $root = [IO.Path]::GetFullPath($BundleRoot)
    $composePath = [IO.Path]::GetFullPath((Join-Path $root 'docker-compose.yml'))
    Assert-PhotonRuntimePlainItem -Path $composePath -Kind File | Out-Null
    # Never inherit a caller-controlled Compose file. CPU-only hosts use the
    # canonical deployment unchanged; the GPU overlay is selected only after
    # Docker proves that it can inject the NVIDIA driver into this exact image.
    $env:COMPOSE_FILE = $composePath

    $probeSource = "import ctypes; ctypes.CDLL('libcuda.so.1')"
    $probe = Invoke-PhotonRuntimeDocker -Arguments @(
        'run', '--rm', '--gpus', 'all', '--network', 'none',
        '--entrypoint', 'python', $ImageReference, '-c', $probeSource
    ) -AllowFailure
    if ($probe.ExitCode -ne 0) { return $false }

    $generatedRoot = Join-Path $root 'logs\runtime-compose'
    if (Test-Path -LiteralPath $generatedRoot) {
        Assert-PhotonRuntimePlainItem -Path $generatedRoot -Kind Directory | Out-Null
    }
    else {
        New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
        Assert-PhotonRuntimePlainItem -Path $generatedRoot -Kind Directory | Out-Null
    }
    $gpuOverridePath = Join-Path $generatedRoot 'docker-compose.gpu.yml'
    $gpuOverride = @'
services:
  gateway:
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
'@
    $gpuOverride = $gpuOverride.Replace("`r`n", "`n") + "`n"
    if (Test-Path -LiteralPath $gpuOverridePath) {
        Assert-PhotonRuntimePlainItem -Path $gpuOverridePath -Kind File | Out-Null
        if ([IO.File]::ReadAllText($gpuOverridePath).Replace("`r`n", "`n") -cne $gpuOverride) {
            throw 'runtime_gpu_compose_override_mismatch'
        }
    }
    else {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($gpuOverride)
        $stream = [IO.File]::Open($gpuOverridePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
    }

    $env:COMPOSE_FILE = $composePath + [IO.Path]::PathSeparator + $gpuOverridePath
    return $true
}

function Read-PhotonRuntimeJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$FailureCode)
    Assert-PhotonRuntimePlainItem -Path $Path -Kind File | Out-Null
    try { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw $FailureCode }
}

function Get-PhotonRuntimeGenerationBase {
    param([Parameter(Mandatory = $true)][string]$BundleRoot)
    return [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'logs\runtime-generations'))
}

function New-PhotonRuntimeDeploymentFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$ComposeSha256,
        [Parameter(Mandatory = $true)]$RequiredEnvironment
    )

    if ($ComposeSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'runtime_deployment_compose_hash_invalid' }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("schemaId=$script:PhotonRuntimeDeploymentSchema")
    $lines.Add("composeSha256=$ComposeSha256")
    foreach ($key in $script:PhotonRuntimeRequiredEnvironment.Keys) {
        $property = $RequiredEnvironment.PSObject.Properties[$key]
        $value = if ($null -ne $property) { [string]$property.Value } elseif ($RequiredEnvironment -is [Collections.IDictionary]) { [string]$RequiredEnvironment[$key] } else { '' }
        if ($value -cne [string]$script:PhotonRuntimeRequiredEnvironment[$key]) {
            throw "runtime_deployment_environment_invalid:$key"
        }
        $lines.Add("$key=$value")
    }
    return Get-PhotonSha256Text (($lines -join "`n") + "`n")
}

function Get-PhotonRuntimeDeploymentSpec {
    param([Parameter(Mandatory = $true)][string]$BundleRoot)

    $composePath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'docker-compose.yml'))
    Assert-PhotonRuntimePlainItem -Path $composePath -Kind File | Out-Null
    $composeLines = @([IO.File]::ReadAllLines($composePath))
    $environment = [ordered]@{}
    foreach ($key in $script:PhotonRuntimeRequiredEnvironment.Keys) {
        $value = [string]$script:PhotonRuntimeRequiredEnvironment[$key]
        $expected = "- $key=$value"
        $matches = @($composeLines | Where-Object { $_.Trim() -ceq $expected })
        if ($matches.Count -ne 1) { throw "runtime_deployment_compose_environment_mismatch:$key" }
        $environment[$key] = $value
    }
    $composeSha256 = Get-PhotonSha256File $composePath
    $fingerprintSha256 = New-PhotonRuntimeDeploymentFingerprint -ComposeSha256 $composeSha256 -RequiredEnvironment $environment
    return [pscustomobject]@{
        SchemaId = $script:PhotonRuntimeDeploymentSchema
        ComposePath = $composePath
        ComposeSha256 = $composeSha256
        RequiredEnvironment = [pscustomobject]$environment
        FingerprintSha256 = $fingerprintSha256
    }
}

function Test-PhotonRuntimeEnvironmentMatches {
    param(
        [AllowNull()][object[]]$EnvironmentEntries,
        [Parameter(Mandatory = $true)]$Deployment
    )

    $entries = @($EnvironmentEntries | ForEach-Object { [string]$_ })
    foreach ($key in $script:PhotonRuntimeRequiredEnvironment.Keys) {
        $expected = "$key=$([string]$Deployment.RequiredEnvironment.$key)"
        if (@($entries | Where-Object { $_ -ceq $expected }).Count -ne 1) { return $false }
        if (@($entries | Where-Object { $_ -clike "$key=*" }).Count -ne 1) { return $false }
    }
    return $true
}

function Assert-PhotonRuntimeCommitDeployment {
    param(
        [Parameter(Mandatory = $true)]$Deployment,
        [Parameter(Mandatory = $true)]$Expected
    )

    if ([string]$Deployment.schemaId -cne $script:PhotonRuntimeDeploymentSchema -or
        [string]$Deployment.composeSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [string]$Deployment.fingerprintSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        $null -eq $Deployment.requiredEnvironment) {
        throw 'runtime_generation_commit_deployment_invalid'
    }
    $properties = @($Deployment.requiredEnvironment.PSObject.Properties)
    if ($properties.Count -ne $script:PhotonRuntimeRequiredEnvironment.Count) {
        throw 'runtime_generation_commit_deployment_environment_invalid'
    }
    $derived = New-PhotonRuntimeDeploymentFingerprint -ComposeSha256 ([string]$Deployment.composeSha256) `
        -RequiredEnvironment $Deployment.requiredEnvironment
    if ($derived -cne [string]$Deployment.fingerprintSha256) {
        throw 'runtime_generation_commit_deployment_fingerprint_invalid'
    }
    return ([string]$Deployment.composeSha256 -ceq [string]$Expected.ComposeSha256 -and
        [string]$Deployment.fingerprintSha256 -ceq [string]$Expected.FingerprintSha256)
}

function Assert-PhotonRuntimePreQdrantDeploymentForUpgrade {
    param([Parameter(Mandatory = $true)]$Deployment)

    $legacyEnvironment = [ordered]@{
        HERMES_WORKBENCH = '1'
        HERMES_WORKBENCH_AUTHENTICATED_MEM0 = '1'
        HERMES_MEM0_MODEL_URL = 'http://host.docker.internal:12434/engines/v1'
    }
    if ([string]$Deployment.schemaId -cne $script:PhotonRuntimeDeploymentSchema -or
        [string]$Deployment.composeSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [string]$Deployment.fingerprintSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        $null -eq $Deployment.requiredEnvironment -or
        @($Deployment.requiredEnvironment.PSObject.Properties).Count -ne $legacyEnvironment.Count) {
        throw 'runtime_generation_commit_deployment_environment_invalid'
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("schemaId=$script:PhotonRuntimeDeploymentSchema")
    $lines.Add("composeSha256=$([string]$Deployment.composeSha256)")
    foreach ($key in $legacyEnvironment.Keys) {
        $property = $Deployment.requiredEnvironment.PSObject.Properties[$key]
        if ($null -eq $property -or [string]$property.Value -cne [string]$legacyEnvironment[$key]) {
            throw 'runtime_generation_commit_deployment_environment_invalid'
        }
        $lines.Add("$key=$([string]$property.Value)")
    }
    $derived = Get-PhotonSha256Text (($lines -join "`n") + "`n")
    if ($derived -cne [string]$Deployment.fingerprintSha256) {
        throw 'runtime_generation_commit_deployment_fingerprint_invalid'
    }
}

function Get-PhotonRuntimeGeneration {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)][string]$GenerationId,
        [string]$ExpectedLockSha256 = '',
        [string]$ExpectedEnvSha256 = ''
    )

    if ($GenerationId -cnotmatch '^[a-f0-9]{16}-[a-f0-9]{12}$') { throw 'runtime_generation_id_invalid' }
    $base = Get-PhotonRuntimeGenerationBase -BundleRoot $BundleRoot
    Assert-PhotonRuntimePlainItem -Path $base -Kind Directory | Out-Null
    $path = [IO.Path]::GetFullPath((Join-Path $base $GenerationId))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'runtime_generation_path_escape'
    }
    Assert-PhotonRuntimePlainItem -Path $path -Kind Directory | Out-Null
    $lockPath = Join-Path $path 'runtime-image.lock.json'
    $envPath = Join-Path $path 'runtime-image.env'
    Assert-PhotonRuntimePlainItem -Path $lockPath -Kind File -RequireReadOnly | Out-Null
    Assert-PhotonRuntimePlainItem -Path $envPath -Kind File -RequireReadOnly | Out-Null

    $lockSha256 = Get-PhotonSha256File $lockPath
    $envSha256 = Get-PhotonSha256File $envPath
    if (-not [string]::IsNullOrWhiteSpace($ExpectedLockSha256) -and $lockSha256 -cne $ExpectedLockSha256) {
        throw 'runtime_generation_lock_hash_mismatch'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedEnvSha256) -and $envSha256 -cne $ExpectedEnvSha256) {
        throw 'runtime_generation_env_hash_mismatch'
    }

    $lock = Read-PhotonRuntimeJson -Path $lockPath -FailureCode 'runtime_generation_lock_invalid'
    if ([string]$lock.schemaId -cne $script:PhotonRuntimeImageLockSchema -or
        [int]$lock.protocolVersion -ne $script:PhotonRuntimeProtocolVersion -or
        [string]$lock.generationId -cne $GenerationId -or
        [string]$lock.imageId -cnotmatch '^sha256:[a-f0-9]{64}$' -or
        [string]$lock.sourceDigest -cnotmatch '^[a-f0-9]{64}$') {
        throw 'runtime_generation_lock_binding_mismatch'
    }
    $derivedGenerationId = ([string]$lock.sourceDigest).Substring(0, 16) + '-' + ([string]$lock.imageId).Substring(7, 12)
    if ($derivedGenerationId -cne $GenerationId) { throw 'runtime_generation_derived_id_mismatch' }

    $envText = [IO.File]::ReadAllText($envPath)
    if ($envText -cnotmatch '^HERMES_IMAGE_REFERENCE=(sha256:[a-f0-9]{64})\r?\n?$' -or
        [string]$Matches[1] -cne [string]$lock.imageId) {
        throw 'runtime_generation_env_binding_mismatch'
    }

    $requiredLabels = @(
        'org.opencontainers.image.revision',
        'io.photon.workbench.source.digest',
        'io.photon.workbench.dockerfile.sha256',
        'io.photon.workbench.runtime.protocol',
        'io.photon.hermes-workbench.mode',
        'io.photon.hermes-workbench.mem0.capability',
        'io.photon.hermes-workbench.mem0.version',
        'io.photon.hermes-workbench.qdrant-client.version'
    )
    foreach ($key in $requiredLabels) {
        if ($null -eq $lock.labels.PSObject.Properties[$key]) { throw "runtime_generation_label_missing:$key" }
    }
    if ([string]$lock.labels.'org.opencontainers.image.revision' -cne [string]$lock.sourceCommit -or
        [string]$lock.labels.'io.photon.workbench.source.digest' -cne [string]$lock.sourceDigest -or
        [string]$lock.labels.'io.photon.workbench.dockerfile.sha256' -cne [string]$lock.dockerfileSha256 -or
        [string]$lock.labels.'io.photon.workbench.runtime.protocol' -cne [string]$script:PhotonRuntimeProtocolVersion -or
        [string]$lock.labels.'io.photon.hermes-workbench.mode' -cne 'authenticated' -or
        [string]$lock.labels.'io.photon.hermes-workbench.mem0.capability' -cne 'mem0ai+qdrant') {
        throw 'runtime_generation_label_binding_mismatch'
    }

    return [pscustomobject]@{
        GenerationId = $GenerationId
        Path = $path
        LockPath = $lockPath
        EnvPath = $envPath
        LockSha256 = $lockSha256
        EnvSha256 = $envSha256
        ImageId = [string]$lock.imageId
        Lock = $lock
    }
}

function Get-PhotonCurrentRuntimeGeneration {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [switch]$AllowLegacyDeployment,
        [switch]$AllowDeploymentMismatch,
        [switch]$AllowRequiredEnvironmentUpgrade
    )

    $base = Get-PhotonRuntimeGenerationBase -BundleRoot $BundleRoot
    $commitPath = Join-Path $base 'runtime-generation.current.json'
    if (-not (Test-Path -LiteralPath $commitPath -PathType Leaf)) { return $null }
    $commit = Read-PhotonRuntimeJson -Path $commitPath -FailureCode 'runtime_generation_commit_invalid'
    $isLegacy = [string]$commit.schemaId -ceq $script:PhotonRuntimeGenerationCommitLegacySchema
    if ($isLegacy -and -not $AllowLegacyDeployment) { throw 'runtime_generation_commit_deployment_upgrade_required' }
    if (([string]$commit.schemaId -cne $script:PhotonRuntimeGenerationCommitSchema -and -not $isLegacy) -or
        [int]$commit.protocolVersion -ne $script:PhotonRuntimeProtocolVersion -or
        [string]$commit.currentGenerationId -cnotmatch '^[a-f0-9]{16}-[a-f0-9]{12}$' -or
        [string]$commit.currentLockSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [string]$commit.currentEnvSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [string]$commit.currentImageId -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw 'runtime_generation_commit_binding_mismatch'
    }
    if ($null -eq $commit.previous -or
        [string]$commit.previous.imageId -cnotmatch '^sha256:[a-f0-9]{64}$' -or
        (-not [string]::IsNullOrWhiteSpace([string]$commit.previous.generationId) -and
            [string]$commit.previous.generationId -cnotmatch '^[a-f0-9]{16}-[a-f0-9]{12}$')) {
        throw 'runtime_generation_commit_previous_invalid'
    }
    $generation = Get-PhotonRuntimeGeneration -BundleRoot $BundleRoot -GenerationId ([string]$commit.currentGenerationId) `
        -ExpectedLockSha256 ([string]$commit.currentLockSha256) -ExpectedEnvSha256 ([string]$commit.currentEnvSha256)
    if ([string]$generation.ImageId -cne [string]$commit.currentImageId) {
        throw 'runtime_generation_commit_image_mismatch'
    }
    $deployment = Get-PhotonRuntimeDeploymentSpec -BundleRoot $BundleRoot
    $deploymentMatchesCurrent = $false
    if ($isLegacy) {
        # Adoption may read a v1 commit only to migrate it to the deployment-bound v2 schema.
    }
    else {
        $propertyCount = if ($null -eq $commit.deployment.requiredEnvironment) { 0 } else { @($commit.deployment.requiredEnvironment.PSObject.Properties).Count }
        if ($AllowRequiredEnvironmentUpgrade -and $propertyCount -eq 3) {
            Assert-PhotonRuntimePreQdrantDeploymentForUpgrade -Deployment $commit.deployment
            $deploymentMatchesCurrent = $false
        }
        else {
            $deploymentMatchesCurrent = Assert-PhotonRuntimeCommitDeployment -Deployment $commit.deployment -Expected $deployment
        }
        if (-not $deploymentMatchesCurrent -and -not $AllowDeploymentMismatch) {
            throw 'runtime_generation_commit_deployment_mismatch'
        }
    }
    return [pscustomobject]@{
        CommitPath = $commitPath
        Commit = $commit
        Generation = $generation
        GenerationId = $generation.GenerationId
        ImageId = $generation.ImageId
        Deployment = if ($isLegacy) { $null } else { $commit.deployment }
        DeploymentMatchesCurrent = $deploymentMatchesCurrent
    }
}

function Invoke-PhotonRuntimeDocker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$AllowFailure)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& docker @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $output = (($lines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0 -and -not $AllowFailure) { throw "runtime_docker_command_failed:$($Arguments[0]):$exitCode" }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Assert-PhotonRuntimeDockerImage {
    param([Parameter(Mandatory = $true)]$Generation)
    $result = Invoke-PhotonRuntimeDocker -Arguments @('image', 'inspect', [string]$Generation.ImageId)
    try { $image = @($result.Output | ConvertFrom-Json -ErrorAction Stop)[0] }
    catch { throw 'runtime_generation_image_inspect_invalid' }
    if ([string]$image.Id -cne [string]$Generation.ImageId) { throw 'runtime_generation_image_id_mismatch' }
    if ($null -eq $image.Config.Labels) { throw 'runtime_generation_image_labels_missing' }
    foreach ($property in $Generation.Lock.labels.PSObject.Properties) {
        $actual = $image.Config.Labels.PSObject.Properties[$property.Name]
        if ($null -eq $actual -or [string]$actual.Value -cne [string]$property.Value) {
            throw "runtime_generation_image_label_mismatch:$($property.Name)"
        }
    }
}
