[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExpectedSchemaId = 'photon.model-runner.lock/v1'
$script:ExpectedRuntime = 'docker-desktop-model-runner'
$script:ExpectedContainerEndpoint = 'http://host.docker.internal:12434'
$script:ExpectedOpenAiBasePath = '/engines/v1'
$script:ExpectedGatewayContainer = 'hermes'
$script:ExpectedModels = @(
    [ordered]@{
        role = 'chat'
        pullReference = 'ai/qwen3:4B-UD-Q4_K_XL'
        registryIdentity = 'docker.io/ai/qwen3:4B-UD-Q4_K_XL'
        inventoryModelId = 'docker.io/ai/qwen3:4B-UD-Q4_K_XL'
        inspectTags = @('ai/qwen3:4B-UD-Q4_K_XL', 'docker.io/ai/qwen3:4B-UD-Q4_K_XL')
        apiModelId = 'ai/qwen3:4B-UD-Q4_K_XL'
    },
    [ordered]@{
        role = 'embedding'
        pullReference = 'ai/nomic-embed-text-v1.5'
        registryIdentity = 'docker.io/ai/nomic-embed-text-v1.5:latest'
        inventoryModelId = 'docker.io/ai/nomic-embed-text-v1.5:latest'
        inspectTags = @(
            'ai/nomic-embed-text-v1.5',
            'ai/nomic-embed-text-v1.5:latest',
            'docker.io/ai/nomic-embed-text-v1.5:latest'
        )
        apiModelId = 'ai/nomic-embed-text-v1.5'
    }
)

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-RegularFile {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (-not ($item -is [System.IO.FileInfo])) {
        throw "expected_regular_file:$LiteralPath"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "reparse_path_rejected:$LiteralPath"
    }
}

function Assert-PlainDirectory {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (-not ($item -is [System.IO.DirectoryInfo])) {
        throw "expected_directory:$LiteralPath"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "reparse_path_rejected:$LiteralPath"
    }
}

function Invoke-DockerCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell wraps native stderr as ErrorRecord objects. Keep it
        # captured for the explicit exit-code decision below rather than letting
        # a normal "model not found" inspect abort the install before its pull.
        $ErrorActionPreference = 'Continue'
        $outputLines = @(& docker @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $output = (($outputLines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    $result = [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $safeSummary = if ([string]::IsNullOrWhiteSpace($output)) { 'no output' } else { $output.Substring(0, [Math]::Min(400, $output.Length)) }
        throw "docker_command_failed:$($Arguments -join ','):${exitCode}:$safeSummary"
    }
    return $result
}

function Test-RunnerReadyValue {
    param([AllowNull()]$Value)

    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return [bool]$Value }
    if ($Value -is [string]) {
        return @('running', 'ready', 'healthy') -contains ([string]$Value).Trim().ToLowerInvariant()
    }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        foreach ($entry in $Value) {
            if (Test-RunnerReadyValue -Value $entry) { return $true }
        }
        return $false
    }
    foreach ($property in $Value.PSObject.Properties) {
        $key = $property.Name.ToLowerInvariant()
        if (@('running', 'ready', 'healthy', 'status', 'state') -contains $key) {
            if (Test-RunnerReadyValue -Value $property.Value) { return $true }
        }
        elseif ($null -ne $property.Value -and -not ($property.Value -is [ValueType]) -and -not ($property.Value -is [string])) {
            if (Test-RunnerReadyValue -Value $property.Value) { return $true }
        }
    }
    return $false
}

function Read-ExactLock {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    Assert-RegularFile -LiteralPath $LiteralPath
    $lock = Get-Content -LiteralPath $LiteralPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ([string]$lock.schemaId -cne $script:ExpectedSchemaId -or
        [string]$lock.runtime -cne $script:ExpectedRuntime -or
        [string]$lock.containerEndpoint -cne $script:ExpectedContainerEndpoint -or
        [string]$lock.openAiBasePath -cne $script:ExpectedOpenAiBasePath -or
        [string]$lock.gatewayContainer -cne $script:ExpectedGatewayContainer) {
        throw 'model_lock_header_mismatch'
    }

    $models = @($lock.models)
    if ($models.Count -ne $script:ExpectedModels.Count) { throw 'model_lock_count_mismatch' }
    for ($index = 0; $index -lt $script:ExpectedModels.Count; $index++) {
        $actual = $models[$index]
        $expected = $script:ExpectedModels[$index]
        foreach ($field in @('role', 'pullReference', 'registryIdentity', 'inventoryModelId', 'apiModelId')) {
            if ([string]$actual.$field -cne [string]$expected[$field]) {
                throw "model_lock_binding_mismatch:$field"
            }
        }
        $actualTags = @($actual.inspectTags)
        $expectedTags = @($expected.inspectTags)
        if ($actualTags.Count -ne $expectedTags.Count) { throw 'model_lock_tag_count_mismatch' }
        for ($tagIndex = 0; $tagIndex -lt $expectedTags.Count; $tagIndex++) {
            if ([string]$actualTags[$tagIndex] -cne [string]$expectedTags[$tagIndex]) {
                throw 'model_lock_tag_mismatch'
            }
        }
    }
    return $lock
}

function Get-ModelIdentity {
    param(
        [Parameter(Mandatory = $true)]$Model,
        [switch]$AllowMissing,
        [switch]$Remote
    )

    $inspectArguments = if ($Remote) {
        @('model', 'inspect', '--remote', [string]$Model.pullReference)
    }
    else {
        @('model', 'inspect', [string]$Model.pullReference)
    }
    $inspect = Invoke-DockerCommand -Arguments $inspectArguments -AllowFailure
    if ($inspect.ExitCode -ne 0) {
        if ($AllowMissing) { return $null }
        throw "model_inspect_failed:$($Model.pullReference)"
    }
    try {
        $details = $inspect.Output | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "model_inspect_invalid_json:$($Model.pullReference)"
    }
    $digest = [string]$details.id
    if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw "model_digest_invalid:$($Model.pullReference)"
    }
    [string[]]$observedTags = if ($details.PSObject.Properties.Name -ccontains 'tags') {
        @($details.tags | ForEach-Object { [string]$_ })
    }
    else { @() }
    $allowedTags = if ($Remote) {
        @([string]$Model.registryIdentity)
    }
    else {
        @($Model.inspectTags | ForEach-Object { [string]$_ })
    }
    # Docker Model Runner v1.2.6 omits tags from `model inspect --remote`.
    # The remote command still receives only the exact lock-owned reference,
    # and its returned digest is checked against the approved lock below.
    $hasObservedTag = $false
    foreach ($tag in @($observedTags)) { $hasObservedTag = $true; break }
    $matchedTag = if ($Remote -and -not $hasObservedTag) { [string]$Model.registryIdentity } else { $null }
    if ($null -eq $matchedTag) {
        foreach ($tag in $observedTags) {
            if ($allowedTags -ccontains $tag) { $matchedTag = $tag; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($matchedTag)) {
        throw "model_registry_identity_mismatch:$($Model.pullReference)"
    }
    return [pscustomobject]@{
        role = [string]$Model.role
        pullReference = [string]$Model.pullReference
        registryIdentity = [string]$Model.registryIdentity
        observedTag = $matchedTag
        inventoryModelId = [string]$Model.inventoryModelId
        apiModelId = [string]$Model.apiModelId
        localDigest = $digest
    }
}

function Read-ExistingReceipt {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    Assert-RegularFile -LiteralPath $LiteralPath
    try {
        return Get-Content -LiteralPath $LiteralPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw 'model_identity_receipt_invalid'
    }
}

function Assert-ReceiptMatches {
    param(
        [AllowNull()]$Receipt,
        [Parameter(Mandatory = $true)][string]$LockSha256,
        [Parameter(Mandatory = $true)][object[]]$Identities
    )

    if ($null -eq $Receipt) { return }
    if ([string]$Receipt.schemaId -cne 'photon.model-runner.identity/v1' -or
        [string]$Receipt.lockSha256 -cne $LockSha256 -or
        [string]$Receipt.containerEndpoint -cne $script:ExpectedContainerEndpoint) {
        throw 'model_identity_receipt_binding_mismatch'
    }
    $prior = @($Receipt.models)
    if ($prior.Count -ne $Identities.Count) { throw 'model_identity_receipt_count_mismatch' }
    foreach ($identity in $Identities) {
        $match = @($prior | Where-Object { [string]$_.role -ceq [string]$identity.role })
        if ($match.Count -ne 1 -or
            [string]$match[0].pullReference -cne [string]$identity.pullReference -or
            [string]$match[0].inventoryModelId -cne [string]$identity.inventoryModelId -or
            [string]$match[0].apiModelId -cne [string]$identity.apiModelId -or
            [string]$match[0].localDigest -cne [string]$identity.localDigest -or
            [string]$match[0].registryDigest -cne [string]$identity.registryDigest) {
            throw "model_identity_changed:$($identity.role)"
        }
    }
}

function Invoke-ContainerApiProbe {
    param([Parameter(Mandatory = $true)]$Lock)

    $running = Invoke-DockerCommand -Arguments @(
        'inspect', '--type', 'container', '--format', '{{.State.Running}}', $script:ExpectedGatewayContainer
    )
    if ($running.Output.Trim() -cne 'true') { throw 'gateway_container_not_running' }

    # Docker Desktop exposes Model Runner on host loopback TCP. Containers reach
    # that same loopback-only service through host.docker.internal; this script
    # never binds Model Runner to a public interface.
    $expectedModelUrl = $script:ExpectedContainerEndpoint + $script:ExpectedOpenAiBasePath
    # Preserve the Go-template string literal through Windows argv parsing.
    $endpointTemplate = '{{range .Config.Env}}{{if eq . \"HERMES_MEM0_MODEL_URL=' + $expectedModelUrl + '\"}}verified{{end}}{{end}}'
    $environment = Invoke-DockerCommand -Arguments @(
        'inspect', '--type', 'container', '--format', $endpointTemplate, $script:ExpectedGatewayContainer
    )
    if ($environment.Output.Trim() -cne 'verified') {
        throw 'gateway_model_runner_endpoint_mismatch'
    }

    $chatId = [string](@($Lock.models | Where-Object { [string]$_.role -ceq 'chat' })[0].apiModelId)
    $embeddingId = [string](@($Lock.models | Where-Object { [string]$_.role -ceq 'embedding' })[0].apiModelId)
    $chatInventoryId = [string](@($Lock.models | Where-Object { [string]$_.role -ceq 'chat' })[0].inventoryModelId)
    $embeddingInventoryId = [string](@($Lock.models | Where-Object { [string]$_.role -ceq 'embedding' })[0].inventoryModelId)
    $probeSource = @"
import json
import urllib.request

base = "$($script:ExpectedContainerEndpoint)$($script:ExpectedOpenAiBasePath)"
chat_id = "$chatId"
embedding_id = "$embeddingId"
chat_inventory_id = "$chatInventoryId"
embedding_inventory_id = "$embeddingInventoryId"

def request(path, payload=None, timeout=240):
    data = None if payload is None else json.dumps(payload, separators=(",", ":")).encode("utf-8")
    req = urllib.request.Request(base + path, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as response:
        if response.status < 200 or response.status >= 300:
            raise RuntimeError("model_runner_http_status")
        return json.loads(response.read().decode("utf-8"))

listed = request("/models")
ids = {entry.get("id") for entry in listed.get("data", []) if isinstance(entry, dict)}
if chat_inventory_id not in ids or embedding_inventory_id not in ids:
    raise RuntimeError("model_runner_api_identity_mismatch")

embedding = request("/embeddings", {"model": embedding_id, "input": "Photon compatibility probe"})
vector = embedding.get("data", [{}])[0].get("embedding", [])
if not isinstance(vector, list) or len(vector) != 768 or not all(isinstance(value, (int, float)) for value in vector):
    raise RuntimeError("model_runner_embedding_contract_mismatch")

chat = request("/chat/completions", {
    "model": chat_id,
    "messages": [{"role": "user", "content": "Return OK."}],
    "max_tokens": 2,
    "temperature": 0
})
if not isinstance(chat.get("choices"), list) or not chat["choices"]:
    raise RuntimeError("model_runner_chat_contract_mismatch")

print(json.dumps({
    "schemaId": "photon.model-runner.probe/v1",
    "chatModelId": chat_id,
    "embeddingModelId": embedding_id,
    "chatInventoryModelId": chat_inventory_id,
    "embeddingInventoryModelId": embedding_inventory_id,
    "embeddingDimensions": len(vector),
    "chatCompatible": True,
    "embeddingCompatible": True
}, separators=(",", ":")))
"@
    $probeBytes = [System.Text.Encoding]::UTF8.GetBytes($probeSource)
    $encodedProbe = [Convert]::ToBase64String($probeBytes)
    $bootstrap = "import base64;exec(base64.b64decode('$encodedProbe'))"
    $probeResult = Invoke-DockerCommand -Arguments @(
        'exec', $script:ExpectedGatewayContainer, 'python', '-c', $bootstrap
    )
    try { $probe = $probeResult.Output | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'model_runner_container_probe_invalid_json' }
    if ([string]$probe.schemaId -cne 'photon.model-runner.probe/v1' -or
        [string]$probe.chatModelId -cne $chatId -or
        [string]$probe.embeddingModelId -cne $embeddingId -or
        [string]$probe.chatInventoryModelId -cne $chatInventoryId -or
        [string]$probe.embeddingInventoryModelId -cne $embeddingInventoryId -or
        $probe.chatCompatible -ne $true -or
        $probe.embeddingCompatible -ne $true -or
        [int]$probe.embeddingDimensions -ne 768) {
        throw 'model_runner_container_probe_mismatch'
    }
    return $probe
}

$candidateLockPaths = @(
    (Join-Path $PSScriptRoot 'runtime\photon-models.lock.json'),
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime\photon-models.lock.json')
)
$lockPath = @($candidateLockPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
if ($lockPath.Count -ne 1) { throw 'photon_model_lock_not_found' }
$lockPath = [System.IO.Path]::GetFullPath([string]$lockPath[0])
$lock = Read-ExactLock -LiteralPath $lockPath
$lockSha256 = Get-Sha256Hex -LiteralPath $lockPath

$dockerVersion = Invoke-DockerCommand -Arguments @('version', '--format', '{{.Server.Version}}')
if ([string]::IsNullOrWhiteSpace($dockerVersion.Output)) { throw 'docker_engine_not_running' }
$modelRunnerVersion = Invoke-DockerCommand -Arguments @('model', 'version')
if ([string]::IsNullOrWhiteSpace($modelRunnerVersion.Output)) { throw 'docker_model_plugin_missing' }
$runnerStatus = Invoke-DockerCommand -Arguments @('model', 'status', '--json')
try { $runnerStatusObject = $runnerStatus.Output | ConvertFrom-Json -ErrorAction Stop }
catch { throw 'docker_model_status_invalid_json' }
if (-not (Test-RunnerReadyValue -Value $runnerStatusObject)) { throw 'docker_model_runner_not_ready' }

$stateRoot = Join-Path $PSScriptRoot 'logs'
if (-not (Test-Path -LiteralPath $stateRoot)) {
    New-Item -ItemType Directory -Path $stateRoot -ErrorAction Stop | Out-Null
}
Assert-PlainDirectory -LiteralPath $stateRoot
$stateRoot = [System.IO.Path]::GetFullPath($stateRoot)
$receiptPath = [System.IO.Path]::GetFullPath((Join-Path $stateRoot 'model-runner.identity.json'))
if (-not $receiptPath.StartsWith($stateRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'model_identity_receipt_path_escape'
}
$existingReceipt = Read-ExistingReceipt -LiteralPath $receiptPath

$identities = @()
foreach ($model in @($lock.models)) {
    $registryIdentity = Get-ModelIdentity -Model $model -Remote
    $identity = Get-ModelIdentity -Model $model -AllowMissing
    if ($null -eq $existingReceipt -or $null -eq $identity) {
        Invoke-DockerCommand -Arguments @('model', 'pull', [string]$model.pullReference) | Out-Null
        $identity = Get-ModelIdentity -Model $model
    }
    $identity | Add-Member -NotePropertyName registryDigest -NotePropertyValue ([string]$registryIdentity.localDigest)
    $identities += $identity
}
Assert-ReceiptMatches -Receipt $existingReceipt -LockSha256 $lockSha256 -Identities $identities
$probe = Invoke-ContainerApiProbe -Lock $lock

$receipt = [ordered]@{
    schemaId = 'photon.model-runner.identity/v1'
    verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
    lockSha256 = $lockSha256
    runtime = $script:ExpectedRuntime
    dockerServerVersion = $dockerVersion.Output.Trim()
    modelRunnerVersion = $modelRunnerVersion.Output.Trim()
    containerEndpoint = $script:ExpectedContainerEndpoint
    openAiBasePath = $script:ExpectedOpenAiBasePath
    gatewayContainer = $script:ExpectedGatewayContainer
    models = $identities
    apiProbe = [ordered]@{
        chatModelId = [string]$probe.chatModelId
        embeddingModelId = [string]$probe.embeddingModelId
        chatInventoryModelId = [string]$probe.chatInventoryModelId
        embeddingInventoryModelId = [string]$probe.embeddingInventoryModelId
        embeddingDimensions = [int]$probe.embeddingDimensions
        chatCompatible = [bool]$probe.chatCompatible
        embeddingCompatible = [bool]$probe.embeddingCompatible
    }
}
$temporaryReceipt = Join-Path $stateRoot ('.model-runner.identity.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryReceipt -Encoding UTF8 -NoNewline
    Assert-RegularFile -LiteralPath $temporaryReceipt
    Move-Item -LiteralPath $temporaryReceipt -Destination $receiptPath -Force -ErrorAction Stop
}
finally {
    if (Test-Path -LiteralPath $temporaryReceipt -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryReceipt -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Photon models are installed and verified through Docker Desktop Model Runner.' -ForegroundColor Green
Write-Host 'Docker Desktop Model Runner loopback TCP was verified from the Hermes container; no public listener is configured here.'
Write-Host "Identity receipt: $receiptPath"
