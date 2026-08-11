[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$composePath = Join-Path $projectRoot 'docker-compose.yml'
$remoteComposePath = Join-Path $projectRoot 'remote-install\docker-compose.yml'
$lockPath = Join-Path $projectRoot 'runtime\memory-vector.lock.json'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesMemoryVectorSmoke-' + [Guid]::NewGuid().ToString('N'))
$expectedImage = 'qdrant/qdrant@sha256:affb67e1d6f2f93d7d20b90d238a7d4b974d36351c162e73bda794e4b2e03483'
$expectedEndpoint = 'http://memory-vector:6333'

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Require-ExactProperties {
    param([object]$Value, [string[]]$Expected, [string]$Label)
    $actual = @($Value.PSObject.Properties.Name)
    Require ($actual.Count -eq $Expected.Count) "$Label has unexpected fields."
    foreach ($name in $Expected) { Require ($actual -ccontains $name) "$Label is missing '$name'." }
}

$priorImage = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
$priorWorkspace = [Environment]::GetEnvironmentVariable('HERMES_HOST_WORKSPACE_PATH', 'Process')
try {
    Require (Test-Path -LiteralPath $composePath -PathType Leaf) 'Root Compose file is missing.'
    Require (Test-Path -LiteralPath $remoteComposePath -PathType Leaf) 'Remote Compose file is missing.'
    Require ((Get-FileHash -LiteralPath $composePath -Algorithm SHA256).Hash -ceq
        (Get-FileHash -LiteralPath $remoteComposePath -Algorithm SHA256).Hash) 'Root and remote Compose files differ.'

    $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
    Require-ExactProperties $lock @('schemaId','serviceName','version','image','indexDigest','platforms','endpoint','readyPath','storagePath','publishedPorts') 'Memory-vector lock'
    Require-ExactProperties $lock.platforms @('linux/amd64','linux/arm64') 'Memory-vector platforms'
    Require ([string]$lock.schemaId -ceq 'photon-memory-vector-lock/v1') 'Memory-vector lock schema is wrong.'
    Require ([string]$lock.serviceName -ceq 'memory-vector') 'Memory-vector service name is wrong.'
    Require ([string]$lock.version -ceq '1.18.3') 'Memory-vector version is wrong.'
    Require ([string]$lock.image -ceq $expectedImage) 'Memory-vector image is not the exact approved digest.'
    Require ([string]$lock.indexDigest -ceq 'sha256:affb67e1d6f2f93d7d20b90d238a7d4b974d36351c162e73bda794e4b2e03483') 'Memory-vector index digest is wrong.'
    Require ([string]$lock.platforms.'linux/amd64' -ceq 'sha256:0676e6666a339b96e70a533089e25270dfca648195d905110f19d2239ed1878c') 'Memory-vector amd64 digest is wrong.'
    Require ([string]$lock.platforms.'linux/arm64' -ceq 'sha256:5ebe1c46e8176ce8b6ccf74b8b500e6a984ca0593ca589d9bd7496c60b9e8a68') 'Memory-vector arm64 digest is wrong.'
    Require ([string]$lock.endpoint -ceq $expectedEndpoint) 'Memory-vector endpoint is wrong.'
    Require ([string]$lock.readyPath -ceq '/readyz') 'Memory-vector readiness path is wrong.'
    Require ([string]$lock.storagePath -ceq '/qdrant/storage') 'Memory-vector storage path is wrong.'
    Require (@($lock.publishedPorts).Count -eq 0) 'Memory-vector must not publish a host port.'

    Require ($null -ne (Get-Command docker -ErrorAction SilentlyContinue)) 'Docker is required for the Compose normalization smoke.'
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $env:HERMES_IMAGE_REFERENCE = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'
    $env:HERMES_HOST_WORKSPACE_PATH = $tempRoot
    $normalizedText = (& docker compose -f $composePath config --format json 2>&1) -join [Environment]::NewLine
    Require ($LASTEXITCODE -eq 0) "Compose normalization failed: $normalizedText"
    $normalized = $normalizedText | ConvertFrom-Json
    $gateway = $normalized.services.gateway
    $memoryVector = $normalized.services.'memory-vector'
    Require ($null -ne $gateway -and $null -ne $memoryVector) 'Compose does not contain both required services.'
    Require ([string]$memoryVector.image -ceq $expectedImage) 'Normalized memory-vector image drifted.'
    Require ([bool]$memoryVector.read_only) 'Memory-vector root filesystem must be read-only.'
    Require (@($memoryVector.cap_drop) -ccontains 'ALL') 'Memory-vector must drop all Linux capabilities.'
    Require (@($memoryVector.security_opt) -ccontains 'no-new-privileges:true') 'Memory-vector must deny privilege escalation.'
    Require ([long]$memoryVector.pids_limit -eq 256) 'Memory-vector PID limit drifted.'
    Require ([long][string]$memoryVector.mem_limit -eq 1GB) 'Memory-vector memory limit drifted.'
    Require ([decimal]$memoryVector.cpus -eq 2) 'Memory-vector CPU limit drifted.'
    Require ($null -eq $memoryVector.ports -or @($memoryVector.ports).Count -eq 0) 'Memory-vector unexpectedly publishes a host port.'
    $storage = @($memoryVector.volumes | Where-Object { $_.target -ceq '/qdrant/storage' })
    Require ($storage.Count -eq 1 -and [string]$storage[0].type -ceq 'volume') 'Memory-vector storage is not one named volume.'
    Require (@($memoryVector.tmpfs) -contains '/tmp:rw,nosuid,nodev,noexec,size=64m,uid=1000,gid=1000') 'Memory-vector bounded tmpfs is missing.'
    Require ([string]$memoryVector.environment.QDRANT__TELEMETRY_DISABLED -ceq 'true') 'Memory-vector telemetry is not disabled.'
    Require ([string]$memoryVector.environment.QDRANT_INIT_FILE_PATH -ceq '/qdrant/storage/.qdrant-initialized') 'Memory-vector init marker is outside owned storage.'
    Require ([string]$memoryVector.environment.QDRANT__STORAGE__SNAPSHOTS_PATH -ceq '/qdrant/storage/snapshots') 'Memory-vector snapshots are outside owned storage.'
    Require ((@($memoryVector.healthcheck.test) -join ' ').IndexOf('/readyz', [StringComparison]::Ordinal) -ge 0) 'Memory-vector healthcheck does not probe /readyz.'
    Require ([string]$gateway.depends_on.'memory-vector'.condition -ceq 'service_healthy') 'Gateway is not gated on memory-vector health.'
    Require ([string]$gateway.environment.HERMES_MEM0_QDRANT_URL -ceq $expectedEndpoint) 'Gateway does not receive the exact internal Qdrant endpoint.'

    Write-Host 'Memory-vector immutable internal Compose contract smoke passed.' -ForegroundColor Green
}
finally {
    if ($null -eq $priorImage) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
    else { $env:HERMES_IMAGE_REFERENCE = $priorImage }
    if ($null -eq $priorWorkspace) { Remove-Item Env:\HERMES_HOST_WORKSPACE_PATH -ErrorAction SilentlyContinue }
    else { $env:HERMES_HOST_WORKSPACE_PATH = $priorWorkspace }

    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ((Test-Path -LiteralPath $resolved -PathType Container) -and
        $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'HermesMemoryVectorSmoke-*') {
        $item = Get-Item -LiteralPath $resolved -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
