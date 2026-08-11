Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Invoke-Installer {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$FakeBin,
        [Parameter(Mandatory = $true)][string]$FakeRoot,
        [switch]$ExpectFailure
    )

    $oldPath = $env:PATH
    $oldErrorActionPreference = $ErrorActionPreference
    $env:PATH = "$FakeBin;$oldPath"
    $env:PHOTON_FAKE_ROOT = $FakeRoot
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
        $env:PATH = $oldPath
        Remove-Item Env:PHOTON_FAKE_ROOT -ErrorAction SilentlyContinue
    }
    if ($ExpectFailure) {
        Assert-True ($exitCode -ne 0) 'installer should fail closed'
    }
    else {
        if ($exitCode -ne 0) { throw (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceInstaller = Join-Path $repoRoot 'remote-install\Install-PhotonModels.ps1'
$sourceLock = Join-Path $repoRoot 'runtime\photon-models.lock.json'

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($sourceInstaller, [ref]$tokens, [ref]$parseErrors) | Out-Null
Assert-True ($parseErrors.Count -eq 0) 'installer must parse in Windows PowerShell'

$installerText = Get-Content -LiteralPath $sourceInstaller -Raw
foreach ($forbidden in @('model purge', 'model rm', 'localhost:12434', '--openaiurl', 'ollama/ollama', 'LM Studio')) {
    Assert-True (-not $installerText.Contains($forbidden)) "installer must not contain forbidden runtime or destructive command: $forbidden"
}
Assert-True (-not $installerText.Contains('model-runner.docker.internal')) 'installer must not retain the non-working no-port Docker Desktop hostname'
$lock = Get-Content -LiteralPath $sourceLock -Raw | ConvertFrom-Json
Assert-True ([string]$lock.runtime -ceq 'docker-desktop-model-runner') 'lock must select Docker Desktop Model Runner'
Assert-True ([string]$lock.containerEndpoint -ceq 'http://host.docker.internal:12434') 'lock must use Docker Desktop loopback TCP through the container host alias'
Assert-True (@($lock.models).Count -eq 2) 'lock must contain exactly two models'
Assert-True ([string]$lock.models[0].pullReference -ceq 'ai/qwen3:4B-UD-Q4_K_XL') 'chat model must be exact'
Assert-True ([string]$lock.models[1].pullReference -ceq 'ai/nomic-embed-text-v1.5') 'embedding model must be exact'
Assert-True ([string]$lock.models[0].runtimeMode -ceq 'completion' -and [int]$lock.models[0].contextSize -eq 16384 -and [string]$lock.models[0].keepAlive -ceq '-1') 'chat runtime policy must be exact'
Assert-True ((@($lock.models[0].runtimeFlags) -join '|') -ceq '--n-gpu-layers|0') 'chat must remain CPU-first'
Assert-True ([string]$lock.models[1].runtimeMode -ceq 'embedding' -and [int]$lock.models[1].contextSize -eq 2048 -and [string]$lock.models[1].keepAlive -ceq '-1') 'embedding runtime policy must be exact'
Assert-True ((@($lock.models[1].runtimeFlags) -join '|') -ceq '--n-gpu-layers|0|--batch-size|2048|--ubatch-size|2048') 'embedding physical batch policy must be exact'

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('photon-model-runner-smoke-' + [Guid]::NewGuid().ToString('N'))
try {
    $bundleRoot = Join-Path $temporaryRoot 'bundle'
    $remoteRoot = Join-Path $bundleRoot 'remote-install'
    $runtimeRoot = Join-Path $bundleRoot 'runtime'
    $fakeBin = Join-Path $temporaryRoot 'fake-bin'
    $fakeRoot = Join-Path $temporaryRoot 'fake-state'
    New-Item -ItemType Directory -Path $remoteRoot, $runtimeRoot, $fakeBin, $fakeRoot -Force | Out-Null
    Copy-Item -LiteralPath $sourceInstaller -Destination (Join-Path $remoteRoot 'Install-PhotonModels.ps1')
    Copy-Item -LiteralPath $sourceLock -Destination (Join-Path $runtimeRoot 'photon-models.lock.json')
    '' | Set-Content -LiteralPath (Join-Path $fakeRoot 'models.txt') -Encoding UTF8
    '' | Set-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') -Encoding UTF8

    $fakeDockerCmd = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-docker.ps1" %*
exit /b %errorlevel%
'@
    Set-Content -LiteralPath (Join-Path $fakeBin 'docker.cmd') -Value $fakeDockerCmd -Encoding ASCII

    $fakeDocker = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $env:PHOTON_FAKE_ROOT
if ([string]::IsNullOrWhiteSpace($root)) { exit 90 }
$commandLine = ($args | ForEach-Object { [string]$_ }) -join '|'
Add-Content -LiteralPath (Join-Path $root 'calls.log') -Value $commandLine -Encoding UTF8
$modelsPath = Join-Path $root 'models.txt'
$models = @(Get-Content -LiteralPath $modelsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$qwen = 'ai/qwen3:4B-UD-Q4_K_XL'
$embed = 'ai/nomic-embed-text-v1.5'
$digests = @{
    $qwen = 'sha256:' + ('a' * 64)
    $embed = 'sha256:' + ('b' * 64)
}

if ($args.Count -ge 1 -and $args[0] -ceq 'version') { '28.5.1'; exit 0 }
if ($args.Count -ge 2 -and $args[0] -ceq 'model' -and $args[1] -ceq 'version') { 'Docker Model Runner v1.2.3'; exit 0 }
if ($args.Count -ge 3 -and $args[0] -ceq 'model' -and $args[1] -ceq 'status') {
    if ($env:PHOTON_FAKE_RUNNER_STATUS -ceq 'stopped') { '{"status":"stopped","engine":"llama.cpp"}' }
    else { '{"status":"running","engine":"llama.cpp"}' }
    exit 0
}
if ($args.Count -ge 3 -and $args[0] -ceq 'model' -and $args[1] -ceq 'configure') {
    if ($args[2] -ceq 'show' -and $args.Count -eq 4) {
        $reference = [string]$args[3]
        if ($reference -ceq $qwen) {
            @([ordered]@{
                Backend = 'llama.cpp'
                Model = 'docker.io/ai/qwen3:4B-UD-Q4_K_XL'
                Mode = 'completion'
                Config = [ordered]@{
                    'context-size' = 16384
                    'runtime-flags' = @('--n-gpu-layers', '0')
                    keep_alive = '-1'
                }
            }) | ConvertTo-Json -Depth 6 -Compress
            exit 0
        }
        if ($reference -ceq $embed) {
            @([ordered]@{
                Backend = 'llama.cpp'
                Model = 'docker.io/ai/nomic-embed-text-v1.5:latest'
                Mode = 'embedding'
                Config = [ordered]@{
                    'context-size' = 2048
                    'runtime-flags' = @('--n-gpu-layers', '0', '--batch-size', '2048', '--ubatch-size', '2048')
                    keep_alive = '-1'
                }
            }) | ConvertTo-Json -Depth 6 -Compress
            exit 0
        }
        exit 93
    }
    $chatConfigure = 'model|configure|--mode|completion|--context-size|16384|--keep-alive|-1|ai/qwen3:4B-UD-Q4_K_XL|--|--n-gpu-layers|0'
    $embedConfigure = 'model|configure|--mode|embedding|--context-size|2048|--keep-alive|-1|ai/nomic-embed-text-v1.5|--|--n-gpu-layers|0|--batch-size|2048|--ubatch-size|2048'
    if ($commandLine -ceq $chatConfigure -or $commandLine -ceq $embedConfigure) { exit 0 }
    exit 94
}
if ($args.Count -ge 3 -and $args[0] -ceq 'model' -and $args[1] -ceq 'inspect') {
    $isRemote = $args.Count -eq 4 -and $args[2] -ceq '--remote'
    $reference = if ($isRemote) { [string]$args[3] } else { [string]$args[2] }
    if (-not $isRemote -and -not ($models -ccontains $reference)) { 'model not found'; exit 1 }
    $tag = if ($reference -ceq $qwen) { 'docker.io/ai/qwen3:4B-UD-Q4_K_XL' } else { 'docker.io/ai/nomic-embed-text-v1.5:latest' }
    if ($env:PHOTON_FAKE_BAD_TAG -ceq $reference) { $tag = 'docker.io/attacker/replaced:latest' }
    $digest = $digests[$reference]
    if (-not $isRemote -and $env:PHOTON_FAKE_CHANGED_DIGEST -ceq $reference) { $digest = 'sha256:' + ('c' * 64) }
    if ($isRemote -and $env:PHOTON_FAKE_REMOTE_CHANGED_DIGEST -ceq $reference) { $digest = 'sha256:' + ('d' * 64) }
    [ordered]@{ id = $digest; tags = @($tag); config = [ordered]@{ format = 'gguf' } } | ConvertTo-Json -Compress
    exit 0
}
if ($args.Count -eq 3 -and $args[0] -ceq 'model' -and $args[1] -ceq 'pull') {
    $reference = [string]$args[2]
    if (@($qwen, $embed) -cnotcontains $reference) { exit 91 }
    if ($models -cnotcontains $reference) {
        Add-Content -LiteralPath $modelsPath -Value $reference -Encoding UTF8
    }
    "pulled $reference"
    exit 0
}
if ($args.Count -ge 1 -and $args[0] -ceq 'inspect') {
    if ($args -contains '{{.State.Running}}') { 'true'; exit 0 }
    if (@($args | Where-Object { $_ -like '{{range .Config.Env}}*' }).Count -eq 1) { 'verified'; exit 0 }
}
if ($args.Count -ge 1 -and $args[0] -ceq 'exec') {
    if ($env:PHOTON_FAKE_API_MISMATCH -ceq '1') { 'api mismatch'; exit 1 }
    [ordered]@{
        schemaId = 'photon.model-runner.probe/v1'
        chatModelId = $qwen
        embeddingModelId = $embed
        chatInventoryModelId = 'docker.io/ai/qwen3:4B-UD-Q4_K_XL'
        embeddingInventoryModelId = 'docker.io/ai/nomic-embed-text-v1.5:latest'
        embeddingDimensions = 768
        chatCompatible = $true
        embeddingCompatible = $true
    } | ConvertTo-Json -Compress
    exit 0
}
'unsupported fake docker invocation'
exit 92
'@
    Set-Content -LiteralPath (Join-Path $fakeBin 'fake-docker.ps1') -Value $fakeDocker -Encoding UTF8

    $installer = Join-Path $remoteRoot 'Install-PhotonModels.ps1'
    Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot | Out-Null
    $calls = @(Get-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $pullCalls = @($calls | Where-Object { $_ -like 'model|pull|*' })
    Assert-True ($pullCalls.Count -eq 2) 'first install must pull exactly the two absent locked models'
    Assert-True ($pullCalls[0] -ceq 'model|pull|ai/qwen3:4B-UD-Q4_K_XL') 'chat pull must be exact'
    Assert-True ($pullCalls[1] -ceq 'model|pull|ai/nomic-embed-text-v1.5') 'embedding pull must be exact'
    Assert-True (@($calls | Where-Object { $_ -like 'model|inspect|--remote|*' }).Count -eq 2) 'each model must bind to its exact remote registry artifact'
    Assert-True (@($calls | Where-Object { $_ -like 'model|configure|--mode|*' }).Count -eq 2) 'installer must apply exactly two locked per-mode runtime configurations'
    Assert-True (@($calls | Where-Object { $_ -like 'model|configure|show|*' }).Count -eq 2) 'installer must read back both runtime configurations'
    Assert-True (@($calls | Where-Object { $_ -like 'exec|hermes|python|-c|*' }).Count -eq 1) 'API probe must run inside the gateway container'
    Assert-True (@($calls | Where-Object { $_ -like 'inspect|--type|container|--format|*HERMES_MEM0_MODEL_URL=http://host.docker.internal:12434/engines/v1*|hermes' }).Count -eq 1) 'gateway environment check must bind the exact composed OpenAI base URL'

    $receiptPath = Join-Path $remoteRoot 'logs\model-runner.identity.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    Assert-True ([string]$receipt.models[0].localDigest -ceq ('sha256:' + ('a' * 64))) 'chat digest must be bound'
    Assert-True ([string]$receipt.models[1].localDigest -ceq ('sha256:' + ('b' * 64))) 'embedding digest must be bound'
    Assert-True ([string]$receipt.models[1].inventoryModelId -ceq 'docker.io/ai/nomic-embed-text-v1.5:latest') 'embedding inventory must bind the full registry ID'
    Assert-True ([string]$receipt.models[0].registryDigest -ceq [string]$receipt.models[0].localDigest) 'chat local and registry digests must match'
    Assert-True ([string]$receipt.models[1].registryDigest -ceq [string]$receipt.models[1].localDigest) 'embedding local and registry digests must match'
    Assert-True ([int]$receipt.models[0].contextSize -eq 16384 -and [string]$receipt.models[0].keepAlive -ceq '-1') 'chat receipt must bind its retained CPU-first context policy'
    Assert-True ((@($receipt.models[1].runtimeFlags) -join '|') -ceq '--n-gpu-layers|0|--batch-size|2048|--ubatch-size|2048') 'embedding receipt must bind the physical batch policy'
    Assert-True ([int]$receipt.apiProbe.embeddingDimensions -eq 768) 'embedding API contract must be recorded'
    Assert-True ([string]$receipt.apiProbe.chatModelId -ceq 'ai/qwen3:4B-UD-Q4_K_XL') 'chat request must use the short API request ID'
    Assert-True ([string]$receipt.apiProbe.chatInventoryModelId -ceq 'docker.io/ai/qwen3:4B-UD-Q4_K_XL') 'chat inventory must bind the full registry ID'
    Assert-True ($installerText.Contains('len(vector) != 768')) 'container probe must require the measured 768-dimensional embedding contract'
    Assert-True (-not $installerText.Contains('chat.get("model")')) 'chat compatibility must not equate the internal response model path with the request ID'
    Assert-True ($installerText.Contains('["memory"] * 900')) 'installer must prove an embedding input beyond the broken 512-token default'

    '' | Set-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') -Encoding UTF8
    Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot | Out-Null
    $secondCalls = @(Get-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Assert-True (@($secondCalls | Where-Object { $_ -like 'model|pull|*' }).Count -eq 0) 'repeat install must not repull verified models'
    Assert-True (@($secondCalls | Where-Object { $_ -like 'model|configure|--mode|*' }).Count -eq 2) 'repeat install must reassert both runtime configurations after Docker restart or eviction'

    $env:PHOTON_FAKE_RUNNER_STATUS = 'stopped'
    try { Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null }
    finally { Remove-Item Env:PHOTON_FAKE_RUNNER_STATUS -ErrorAction SilentlyContinue }

    $env:PHOTON_FAKE_CHANGED_DIGEST = 'ai/qwen3:4B-UD-Q4_K_XL'
    try { Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null }
    finally { Remove-Item Env:PHOTON_FAKE_CHANGED_DIGEST -ErrorAction SilentlyContinue }

    $env:PHOTON_FAKE_BAD_TAG = 'ai/nomic-embed-text-v1.5'
    try { Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null }
    finally { Remove-Item Env:PHOTON_FAKE_BAD_TAG -ErrorAction SilentlyContinue }

    $env:PHOTON_FAKE_REMOTE_CHANGED_DIGEST = 'ai/nomic-embed-text-v1.5'
    try { Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null }
    finally { Remove-Item Env:PHOTON_FAKE_REMOTE_CHANGED_DIGEST -ErrorAction SilentlyContinue }

    $env:PHOTON_FAKE_API_MISMATCH = '1'
    try { Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null }
    finally { Remove-Item Env:PHOTON_FAKE_API_MISMATCH -ErrorAction SilentlyContinue }

    $tamperedLock = Get-Content -LiteralPath (Join-Path $runtimeRoot 'photon-models.lock.json') -Raw | ConvertFrom-Json
    $tamperedLock.models[0].pullReference = 'ai/unapproved'
    $tamperedLock | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'photon-models.lock.json') -Encoding UTF8
    '' | Set-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') -Encoding UTF8
    Invoke-Installer -ScriptPath $installer -FakeBin $fakeBin -FakeRoot $fakeRoot -ExpectFailure | Out-Null
    $tamperCalls = @(Get-Content -LiteralPath (Join-Path $fakeRoot 'calls.log') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Assert-True ($tamperCalls.Count -eq 0) 'tampered lock must fail before Docker is invoked'
}
finally {
    if ($env:PHOTON_KEEP_MODEL_SMOKE -ne '1' -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Model Runner installer smoke passed.' -ForegroundColor Green
