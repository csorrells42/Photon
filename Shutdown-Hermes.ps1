[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundleRoot = $PSScriptRoot
$logsPath = Join-Path $bundleRoot 'logs'
$settingsPath = Join-Path $bundleRoot 'launcher.settings.json'
$approvedHermesImage = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'

function Stop-TrackedProcess {
    param(
        [string]$PidFile,
        [string]$IdentityFile,
        [string]$Label,
        [int]$OwnedPort = 0
    )

    if (-not (Test-Path -LiteralPath $PidFile)) { return }
    $text = (Get-Content -Raw -LiteralPath $PidFile).Trim()
    $processId = 0
    if (-not [int]::TryParse($text, [ref]$processId) -or $processId -le 0) {
        Write-Warning "Ignoring invalid $Label PID file: $PidFile"
        Remove-Item -LiteralPath $PidFile -Force
        return
    }

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item -LiteralPath $PidFile -Force
        return
    }
    if (-not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) {
        Write-Warning "$Label has no exact process identity record; PID $processId was not stopped."
        Remove-Item -LiteralPath $PidFile -Force
        return
    }
    try { $identity = Get-Content -Raw -LiteralPath $IdentityFile | ConvertFrom-Json }
    catch {
        Write-Warning "$Label process identity is invalid; PID $processId was not stopped."
        Remove-Item -LiteralPath $PidFile -Force
        Remove-Item -LiteralPath $IdentityFile -Force -ErrorAction SilentlyContinue
        return
    }
    $commandBytes = [Text.Encoding]::UTF8.GetBytes([string]$process.CommandLine)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $commandHash = ([BitConverter]::ToString($sha.ComputeHash($commandBytes))).Replace('-', '') }
    finally { $sha.Dispose(); [Array]::Clear($commandBytes, 0, $commandBytes.Length) }
    $identityMatches = [int]$identity.protocolVersion -eq 1 -and
        [int]$identity.pid -eq $processId -and
        [string]$identity.creationDate -ceq [string]$process.CreationDate -and
        [string]$identity.commandLineSha256 -ceq $commandHash
    try { $identityMatches = $identityMatches -and [IO.Path]::GetFullPath([string]$identity.executablePath) -eq [IO.Path]::GetFullPath([string]$process.ExecutablePath) }
    catch { $identityMatches = $false }
    if ($OwnedPort -gt 0) {
        $listener = Get-NetTCPConnection -LocalPort $OwnedPort -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $processId | Select-Object -First 1
        $identityMatches = $identityMatches -and [int]$identity.port -eq $OwnedPort -and $null -ne $listener
    }
    if (-not $identityMatches) {
        Write-Warning "PID $processId no longer matches the exact $Label launch identity; it was not stopped."
        Remove-Item -LiteralPath $PidFile -Force
        Remove-Item -LiteralPath $IdentityFile -Force -ErrorAction SilentlyContinue
        return
    }
    Stop-Process -Id $processId
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $remaining = Get-Process -Id $processId -ErrorAction SilentlyContinue
    } until (-not $remaining -or [DateTime]::UtcNow -ge $deadline)
    if ($remaining) { throw "$Label did not exit within 10 seconds." }
    Remove-Item -LiteralPath $PidFile -Force
    Remove-Item -LiteralPath $IdentityFile -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped $Label." -ForegroundColor Green
}

Push-Location $bundleRoot
try {
    $failures = [Collections.Generic.List[string]]::new()
    $ownedProcesses = @(
        @{ PidFile = (Join-Path $logsPath 'client.pid'); IdentityFile = (Join-Path $logsPath 'client.process.json'); Label = 'Hermes desktop client'; Port = 0 },
        @{ PidFile = (Join-Path $logsPath 'maui.pid'); IdentityFile = (Join-Path $logsPath 'maui.process.json'); Label = 'legacy MAUI client'; Port = 0 },
        @{ PidFile = (Join-Path $logsPath 'assistant-bus.pid'); IdentityFile = (Join-Path $logsPath 'assistant-bus.process.json'); Label = 'Assistant Conversation Bus'; Port = 9072 },
        @{ PidFile = (Join-Path $logsPath 'workbench.pid'); IdentityFile = (Join-Path $logsPath 'workbench.process.json'); Label = 'Hermes Workbench web client'; Port = 4173 },
        @{ PidFile = (Join-Path $logsPath 'serena.pid'); IdentityFile = (Join-Path $logsPath 'serena.process.json'); Label = 'Serena'; Port = 9121 }
    )
    foreach ($owned in $ownedProcesses) {
        try { Stop-TrackedProcess -PidFile $owned.PidFile -IdentityFile $owned.IdentityFile -Label $owned.Label -OwnedPort $owned.Port }
        catch { $failures.Add("$($owned.Label): $($_.Exception.Message)") }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $logsPath 'workbench.pid'))) {
        Remove-Item -LiteralPath (Join-Path $logsPath 'workbench.nonce') -Force -ErrorAction SilentlyContinue
    }

    if (Get-Command docker -ErrorAction SilentlyContinue) {
        & docker info *> $null
        if ($LASTEXITCODE -eq 0) {
            $previousImageReference = [Environment]::GetEnvironmentVariable('HERMES_IMAGE_REFERENCE', 'Process')
            try {
                $containerJson = (& docker inspect --type container hermes 2>$null) -join [Environment]::NewLine
                $selectedImage = $approvedHermesImage
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($containerJson)) {
                    try {
                        $runningContainer = @($containerJson | ConvertFrom-Json -ErrorAction Stop)[0]
                        if ([string]$runningContainer.Image -match '^sha256:[a-f0-9]{64}$') { $selectedImage = [string]$runningContainer.Image }
                    }
                    catch { }
                }
                $env:HERMES_IMAGE_REFERENCE = $selectedImage
                & docker compose down
                if ($LASTEXITCODE -ne 0) { $failures.Add('Docker Compose could not stop Hermes cleanly.') }
            }
            finally {
                if ($null -eq $previousImageReference) { Remove-Item Env:\HERMES_IMAGE_REFERENCE -ErrorAction SilentlyContinue }
                else { $env:HERMES_IMAGE_REFERENCE = $previousImageReference }
            }
        } else {
            Write-Warning 'Docker Desktop is not available; local Workbench processes were still cleaned up.'
        }
    }

    if ($failures.Count -gt 0) { throw "Hermes shutdown completed with attention needed: $($failures -join ' | ')" }
    Write-Host 'Hermes is shut down. Docker Desktop was left running.' -ForegroundColor Green
}
finally {
    Pop-Location
}
