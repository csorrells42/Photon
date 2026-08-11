[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$shutdownScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Shutdown-Hermes.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesShutdownSmoke-' + [Guid]::NewGuid().ToString('N'))
$spawned = $null
$marker = 'OwnedStopSmoke_' + [Guid]::NewGuid().ToString('N')
$encodedCommand = [Convert]::ToBase64String(
    [Text.Encoding]::Unicode.GetBytes("`$null = '$marker'; Start-Sleep -Seconds 60")
)

function Write-TestIdentity {
    param([int]$ProcessId, [string]$Path, [int]$Port = 0)
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    $commandBytes = [Text.Encoding]::UTF8.GetBytes([string]$process.CommandLine)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $commandHash = ([BitConverter]::ToString($sha.ComputeHash($commandBytes))).Replace('-', '') }
    finally { $sha.Dispose(); [Array]::Clear($commandBytes, 0, $commandBytes.Length) }
    $identity = [ordered]@{
        protocolVersion = 1
        pid = $ProcessId
        creationDate = [string]$process.CreationDate
        executablePath = [string]$process.ExecutablePath
        commandLineSha256 = $commandHash
        port = $Port
    }
    [IO.File]::WriteAllText($Path, ($identity | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $shutdownScript), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Shutdown script has parse errors: $($errors.Message -join ' | ')" }
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-TrackedProcess'
    }, $true)
    if (-not $definition) { throw 'Stop-TrackedProcess was not found in Shutdown-Hermes.ps1.' }
    . ([scriptblock]::Create($definition.Extent.Text))

    $invalidPid = Join-Path $tempRoot 'invalid.pid'
    $invalidIdentity = Join-Path $tempRoot 'invalid.process.json'
    [IO.File]::WriteAllText($invalidPid, 'not-a-process-id', [Text.UTF8Encoding]::new($false))
    Stop-TrackedProcess -PidFile $invalidPid -IdentityFile $invalidIdentity -Label 'invalid record fixture'
    if (Test-Path -LiteralPath $invalidPid) { throw 'Invalid PID record was not cleared.' }

    $mismatchPid = Join-Path $tempRoot 'mismatch.pid'
    $mismatchIdentity = Join-Path $tempRoot 'mismatch.process.json'
    [IO.File]::WriteAllText($mismatchPid, [string]$PID, [Text.UTF8Encoding]::new($false))
    Write-TestIdentity -ProcessId $PID -Path $mismatchIdentity
    $tampered = Get-Content -Raw -LiteralPath $mismatchIdentity | ConvertFrom-Json
    $tampered.commandLineSha256 = '0' * 64
    [IO.File]::WriteAllText($mismatchIdentity, ($tampered | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Stop-TrackedProcess -PidFile $mismatchPid -IdentityFile $mismatchIdentity -Label 'mismatch fixture'
    if (Test-Path -LiteralPath $mismatchPid) { throw 'Mismatched PID record was not cleared.' }
    if (-not (Get-Process -Id $PID -ErrorAction SilentlyContinue)) { throw 'Mismatch protection stopped the smoke-test host process.' }

    $childArguments = @('-NoProfile', '-EncodedCommand', $encodedCommand)
    $spawned = Start-Process -FilePath 'powershell.exe' -ArgumentList $childArguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $childInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($spawned.Id)" -ErrorAction SilentlyContinue
    } until (($childInfo -and [string]$childInfo.CommandLine -match $ownershipPattern) -or [DateTime]::UtcNow -ge $deadline)
    if (-not $childInfo -or [string]$childInfo.CommandLine -notmatch $ownershipPattern) {
        throw 'Owned shutdown fixture did not start with the expected marker.'
    }

    $ownedPid = Join-Path $tempRoot 'owned.pid'
    $ownedIdentity = Join-Path $tempRoot 'owned.process.json'
    [IO.File]::WriteAllText($ownedPid, [string]$spawned.Id, [Text.UTF8Encoding]::new($false))
    Write-TestIdentity -ProcessId $spawned.Id -Path $ownedIdentity
    Stop-TrackedProcess -PidFile $ownedPid -IdentityFile $ownedIdentity -Label 'owned fixture'
    if (Test-Path -LiteralPath $ownedPid) { throw 'Owned PID record was not cleared after shutdown.' }
    if (Test-Path -LiteralPath $ownedIdentity) { throw 'Owned identity record was not cleared after shutdown.' }
    if (Get-Process -Id $spawned.Id -ErrorAction SilentlyContinue) { throw 'Owned fixture remained alive after shutdown.' }

    [pscustomobject]@{
        InvalidRecordCleared = $true
        MismatchedProcessPreserved = $true
        OwnedProcessStopped = $true
        ExitWaitVerified = $true
    } | Format-List
    Write-Host 'Hermes shutdown ownership smoke tests passed.' -ForegroundColor Green
}
finally {
    if ($spawned) {
        $remaining = Get-CimInstance Win32_Process -Filter "ProcessId = $($spawned.Id)" -ErrorAction SilentlyContinue
        if ($remaining) {
            Stop-Process -Id $spawned.Id -Force -ErrorAction SilentlyContinue
        }
    }
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'HermesShutdownSmoke-*' -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        $item = Get-Item -LiteralPath $resolved -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
