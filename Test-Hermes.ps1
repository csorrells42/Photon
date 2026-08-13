[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundleRoot = $PSScriptRoot
$logsPath = Join-Path $bundleRoot 'logs'
$settingsPath = Join-Path $bundleRoot 'launcher.settings.json'
$identityPath = Join-Path $bundleRoot 'logs\runtime-identity.json'
$checks = [Collections.Generic.List[object]]::new()

function Add-Check {
    param([string]$Component, [ValidateSet('OK', 'WARN', 'ERROR')][string]$State, [string]$Detail)
    $checks.Add([pscustomobject]@{ Component = $Component; State = $State; Detail = $Detail })
}

function Test-OwnedPort {
    param([string]$Component, [int]$Port, [string]$ExpectedCommandPattern)
    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) {
        Add-Check $Component 'ERROR' "Nothing is listening on 127.0.0.1:$Port."
        return
    }
    $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue
    if (-not $processInfo) {
        Add-Check $Component 'WARN' "Port $Port is listening, but process ownership could not be read."
    } elseif ([string]$processInfo.CommandLine -match $ExpectedCommandPattern) {
        Add-Check $Component 'OK' "Listening on 127.0.0.1:$Port (PID $($listener.OwningProcess))."
    } else {
        Add-Check $Component 'ERROR' "Port $Port belongs to an unexpected process (PID $($listener.OwningProcess))."
    }
}

$requiredFiles = @(
    'docker-compose.yml', 'launcher.settings.json', 'Launch-Hermes.ps1', 'Shutdown-Hermes.ps1',
    'Update-Hermes.ps1', 'Test-Hermes.ps1', 'Show-HermesBridge.ps1', 'Photon-McpGateway.ps1',
    'mcp-profiles\profiles.lock.json', 'mcp-profiles\photon-engineering-discovery.yaml',
    'mcp-profiles\photon-relentless-repair.yaml', 'src\package-lock.json',
    'tests\Install-Hermes.Smoke.ps1', 'tests\Shutdown-Hermes.Smoke.ps1', 'tests\Update-Hermes.Smoke.ps1'
)
$missingFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $bundleRoot $_) -PathType Leaf) })
if ($missingFiles.Count) { Add-Check 'Bundle files' 'ERROR' ("Missing: " + ($missingFiles -join ', ')) }
else { Add-Check 'Bundle files' 'OK' "$($requiredFiles.Count) required files are present." }

try {
    . (Join-Path $bundleRoot 'Photon-McpGateway.ps1')
    Assert-PhotonMcpProfileAssets | Out-Null
    Add-Check 'Photon MCP profiles' 'OK' 'Both Docker MCP profiles match their SHA-256 receipts.'
}
catch { Add-Check 'Photon MCP profiles' 'ERROR' $_.Exception.Message }

$settings = $null
try {
    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    Add-Check 'Launcher settings' 'OK' 'launcher.settings.json is valid JSON.'
}
catch { Add-Check 'Launcher settings' 'ERROR' 'launcher.settings.json could not be read.' }

$clientPath = if ($settings) { [string]$settings.ClientExecutable } else { '' }
if ([string]::IsNullOrWhiteSpace($clientPath) -and $settings) { $clientPath = [string]$settings.MauiExecutable }
if (-not [string]::IsNullOrWhiteSpace($clientPath) -and -not [IO.Path]::IsPathRooted($clientPath)) { $clientPath = Join-Path $bundleRoot $clientPath }
if ([string]::IsNullOrWhiteSpace($clientPath)) { Add-Check 'Desktop host' 'WARN' 'No native client is configured; browser fallback remains available.' }
elseif (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) { Add-Check 'Desktop host' 'ERROR' 'The configured HermesDesktop executable is missing.' }
else {
    $clientHash = (Get-FileHash -LiteralPath $clientPath -Algorithm SHA256).Hash
    Add-Check 'Desktop host' 'OK' ("HermesDesktop is present; SHA-256 " + $clientHash.Substring(0, 12) + '...')
}

$bridgeClientPath = @(
    (Join-Path $bundleRoot 'tools\hermes-bridge\hermes-bridge.exe'),
    (Join-Path $bundleRoot 'artifacts\tools\hermes-bridge\win-x64\hermes-bridge.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $bridgeClientPath) { Add-Check 'Bridge client' 'ERROR' 'The self-contained hermes-bridge executable is missing.' }
else {
    $bridgeClientOutput = @(& $bridgeClientPath health 2>$null)
    if ($LASTEXITCODE -eq 0 -and ($bridgeClientOutput -join "`n") -match 'Hermes Conversation Bridge') {
        $bridgeClientHash = (Get-FileHash -LiteralPath $bridgeClientPath -Algorithm SHA256).Hash
        Add-Check 'Bridge client' 'OK' ("Self-contained client reached loopback health; SHA-256 " + $bridgeClientHash.Substring(0, 12) + '...')
    } else { Add-Check 'Bridge client' 'ERROR' 'The self-contained bridge client could not reach loopback health.' }
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
$dockerReady = $false
$containerImageId = $null
$containerState = $null
if (-not $docker) { Add-Check 'Docker' 'ERROR' 'Docker is missing from PATH. Run Install-Hermes.ps1.' }
else {
    & $docker.Source info *> $null
    $dockerReady = $LASTEXITCODE -eq 0
    if (-not $dockerReady) { Add-Check 'Docker' 'ERROR' 'Docker Desktop is installed but its engine is unavailable.' }
    else {
        Add-Check 'Docker' 'OK' 'Docker engine is available.'
        $containerOutput = @(& $docker.Source inspect --format '{{.State.Status}}|{{.Image}}' hermes 2>$null)
        $inspectExitCode = $LASTEXITCODE
        $containerLine = @($containerOutput | Select-Object -First 1)[0]
        if ($inspectExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($containerLine)) {
            Add-Check 'Hermes container' 'ERROR' 'The supervised hermes container was not found.'
        } else {
            $containerParts = [string]$containerLine -split '\|', 2
            $containerImageId = if ($containerParts.Count -gt 1) { $containerParts[1] } else { $null }
            $containerState = $containerParts[0]
            Add-Check 'Hermes container' $(if ($containerState -eq 'running') { 'OK' } else { 'ERROR' }) "Container state: $containerState."
        }
    }
}

if ($dockerReady -and $containerState -eq 'running') {
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        $visionOutput = @(& $docker.Source exec hermes python -c "from tools.vision_tools import check_vision_requirements; print(check_vision_requirements())" 2>$null)
        $visionExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($visionExitCode -eq 0 -and @($visionOutput | Where-Object { ([string]$_).Trim() -eq 'True' }).Count -gt 0) {
        Add-Check 'Hermes vision' 'OK' 'An auxiliary or native vision provider is available for screenshots.'
    } else {
        Add-Check 'Hermes vision' 'WARN' 'Screenshot upload works, but no image-analysis provider is ready.'
    }
} else {
    Add-Check 'Hermes vision' 'ERROR' 'Vision readiness cannot be checked until the Hermes container is running.'
}

try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:9119/api/status' -TimeoutSec 5
    $status = $response.Content | ConvertFrom-Json
    if ($response.StatusCode -eq 200 -and [string]$status.version) {
        Add-Check 'Hermes gateway' 'OK' "Hermes $($status.version); gateway $($status.gateway_state)."
    } else { Add-Check 'Hermes gateway' 'ERROR' 'The status endpoint returned an incomplete response.' }
}
catch { Add-Check 'Hermes gateway' 'ERROR' 'The status endpoint is not ready on 127.0.0.1:9119.' }

Test-OwnedPort -Component 'Serena MCP' -Port 9121 -ExpectedCommandPattern 'serena|start-mcp-server'
Test-OwnedPort -Component 'Photon Docker MCP' -Port 9131 -ExpectedCommandPattern 'docker-mcp|mcp gateway run'
Test-OwnedPort -Component 'Workbench web' -Port 4173 -ExpectedCommandPattern 'vite|4173'

if ($dockerReady -and $containerState -eq 'running') {
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $mcpOutput = @(& $docker.Source exec hermes hermes mcp test photon_docker_gateway 2>$null)
        $mcpExitCode = $LASTEXITCODE
        $mcpText = $mcpOutput -join [Environment]::NewLine
        $trust = ((& $docker.Source exec hermes hermes config get mcp_servers.photon_docker_gateway.trust 2>$null) -join '').Trim()
    }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($mcpExitCode -eq 0 -and $mcpText -match 'Connected' -and $mcpText -match 'Tools discovered:\s*[1-9][0-9]*' -and $trust -ceq 'untrusted') {
        Add-Check 'Photon MCP in Hermes' 'OK' 'Hermes discovered authenticated Docker tools with approval gating enabled.'
    } else {
        Add-Check 'Photon MCP in Hermes' 'ERROR' 'Hermes could not verify the authenticated, approval-gated Docker MCP catalog.'
    }
} else {
    Add-Check 'Photon MCP in Hermes' 'ERROR' 'Docker MCP readiness cannot be checked until Hermes is running.'
}

try {
    $bridgeHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:8972/health' -TimeoutSec 5
    if ($bridgeHealth.service -eq 'Hermes Conversation Bridge' -and $bridgeHealth.state -eq 'running' -and $bridgeHealth.port -eq 8972) {
        Add-Check 'Hermes bridge' 'OK' 'Authenticated conversation bridge is listening on 127.0.0.1:8972.'
    } else { Add-Check 'Hermes bridge' 'ERROR' 'Port 8972 returned an unexpected service identity.' }
}
catch { Add-Check 'Hermes bridge' 'ERROR' 'The desktop conversation bridge is not ready on 127.0.0.1:8972.' }

if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
    Add-Check 'Runtime identity' 'WARN' 'Runtime identity is not recorded yet; run Launch Hermes.cmd.'
} else {
    try {
        $identity = Get-Content -Raw -LiteralPath $identityPath | ConvertFrom-Json
        $validIdentity = $identity.protocolVersion -eq 1 -and $identity.containerName -eq 'hermes' -and [string]$identity.imageId -match '^sha256:[a-f0-9]{64}$'
        if (-not $validIdentity) { Add-Check 'Runtime identity' 'ERROR' 'runtime-identity.json has an unsupported or invalid shape.' }
        elseif ($containerImageId -and [string]$identity.imageId -cne [string]$containerImageId) { Add-Check 'Runtime identity' 'WARN' 'Recorded identity differs from the running image; rerun Launch Hermes.cmd.' }
        else { Add-Check 'Runtime identity' 'OK' ("Recorded image " + ([string]$identity.imageId).Substring(7, 12) + '...') }
    }
    catch { Add-Check 'Runtime identity' 'ERROR' 'runtime-identity.json could not be read.' }
}

Write-Host ''
$checks | Format-Table Component, State, Detail -AutoSize -Wrap
$errorCount = @($checks | Where-Object State -eq 'ERROR').Count
$warningCount = @($checks | Where-Object State -eq 'WARN').Count
Write-Host "Checks: $($checks.Count) total, $errorCount errors, $warningCount warnings."
if ($errorCount -gt 0) {
    Write-Host 'Hermes needs attention. Run Launch Hermes.cmd, then check again.' -ForegroundColor Red
    exit 1
}
Write-Host 'Hermes Workbench is healthy.' -ForegroundColor Green
