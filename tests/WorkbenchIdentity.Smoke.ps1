[CmdletBinding()]
param([string]$NodeExecutable)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$frontendRoot = Join-Path $repoRoot 'src'
$viteScript = Join-Path $frontendRoot 'node_modules\vite\bin\vite.js'
$viteConfig = Join-Path $frontendRoot 'vite.config.ts'
$lookalikeScript = Join-Path $PSScriptRoot 'WorkbenchIdentity.Lookalike.mjs'
if ([string]::IsNullOrWhiteSpace($NodeExecutable)) {
    $node = Get-Command node -ErrorAction Stop
    $NodeExecutable = $node.Source
}

$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()
function New-RandomHex32 {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}
$nonce = New-RandomHex32
$challenge = New-RandomHex32
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = [IO.Path]::GetFullPath((Join-Path $tempRoot "HermesWorkbenchIdentity-$([Guid]::NewGuid().ToString('N'))"))
if ([IO.Path]::GetDirectoryName($scratch) -ne $tempRoot -or [IO.Path]::GetFileName($scratch) -notlike 'HermesWorkbenchIdentity-*') {
    throw 'Disposable identity-smoke directory resolution failed.'
}
New-Item -ItemType Directory -Path $scratch | Out-Null
$real = $null
$lookalike = $null

function Get-ExpectedProof([string]$Nonce, [string]$Challenge) {
    $keyBytes = [byte[]]::new(32)
    for ($index = 0; $index -lt 32; $index++) { $keyBytes[$index] = [Convert]::ToByte($Nonce.Substring($index * 2, 2), 16) }
    $hmac = [Security.Cryptography.HMACSHA256]::new($keyBytes)
    try {
        return ([BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("hermes-workbench-v1:$($Challenge.ToLowerInvariant())"))) -replace '-', '').ToLowerInvariant()
    }
    finally { $hmac.Dispose() }
}

function Wait-Identity([int]$Port, [string]$Challenge) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { return Invoke-RestMethod -Uri "http://127.0.0.1:$Port/workbench-api/host-identity?challenge=$Challenge" -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 150 }
    }
    throw "Identity endpoint did not become ready on port $Port."
}

try {
    $previousNonce = $env:HERMES_WORKBENCH_NONCE
    try {
        $env:HERMES_WORKBENCH_NONCE = $nonce
        $real = Start-Process -FilePath $NodeExecutable -ArgumentList @(
            $viteScript, $frontendRoot, '--config', $viteConfig,
            '--host', '127.0.0.1', '--port', "$port", '--strictPort'
        ) -WorkingDirectory $frontendRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'real.out.log') -RedirectStandardError (Join-Path $scratch 'real.err.log') -PassThru
    }
    finally {
        if ($null -eq $previousNonce) { Remove-Item Env:\HERMES_WORKBENCH_NONCE -ErrorAction SilentlyContinue }
        else { $env:HERMES_WORKBENCH_NONCE = $previousNonce }
    }

    $expected = Get-ExpectedProof $nonce $challenge
    $realIdentity = Wait-Identity $port $challenge
    if ($realIdentity.protocolVersion -ne 1 -or $realIdentity.challenge -ne $challenge -or $realIdentity.proof -ne $expected) {
        throw 'The real Workbench did not return the expected launch-bound proof.'
    }

    Stop-Process -Id $real.Id -ErrorAction Stop
    $real.WaitForExit()
    $real = $null
    $lookalike = Start-Process -FilePath $NodeExecutable -ArgumentList @($lookalikeScript, '--port', "$port") -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'lookalike.out.log') -RedirectStandardError (Join-Path $scratch 'lookalike.err.log') -PassThru
    $replacementIdentity = Wait-Identity $port $challenge
    if ($replacementIdentity.proof -eq $expected) {
        throw 'A replacement loopback service reproduced the launch-bound proof.'
    }
    Write-Host 'Workbench identity takeover smoke passed: the same-port replacement was rejected.' -ForegroundColor Green
}
finally {
    if ($real -and -not $real.HasExited) { Stop-Process -Id $real.Id -ErrorAction SilentlyContinue }
    if ($lookalike -and -not $lookalike.HasExited) { Stop-Process -Id $lookalike.Id -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $scratch -PathType Container) {
        $scratchItem = Get-Item -LiteralPath $scratch -Force
        $isReparsePoint = ($scratchItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        $hasExpectedParent = [IO.Path]::GetDirectoryName($scratchItem.FullName) -eq $tempRoot
        $hasExpectedName = $scratchItem.Name -like 'HermesWorkbenchIdentity-*'
        if ($isReparsePoint -or -not $hasExpectedParent -or -not $hasExpectedName) {
            throw 'Refusing to remove an unexpected identity-smoke directory.'
        }
        Remove-Item -LiteralPath $scratchItem.FullName -Recurse -Force
    }
}
