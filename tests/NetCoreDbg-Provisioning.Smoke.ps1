[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testsRoot = $PSScriptRoot
$projectRoot = Split-Path -Parent $testsRoot
$portableLayout = Test-Path -LiteralPath (Join-Path $projectRoot 'Install-NetCoreDbg.ps1') -PathType Leaf
$installer = if ($portableLayout) {
    Join-Path $projectRoot 'Install-NetCoreDbg.ps1'
} else {
    Join-Path $projectRoot 'remote-install\Install-NetCoreDbg.ps1'
}
$archive = if ($portableLayout) {
    Join-Path $projectRoot 'toolchains\netcoredbg-win64.zip'
} else {
    Join-Path $projectRoot 'artifacts\toolchain-cache\netcoredbg-win64.zip'
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The NetCoreDbg installer was not found.' }
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'The pinned NetCoreDbg archive was not found.' }

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesNetCoreDbgSmoke-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    & $installer -InstallRoot $smokeRoot -ArchivePath $archive
    $componentRoot = Join-Path $smokeRoot 'debuggers\netcoredbg\3.1.3-1062'
    $executable = Join-Path $componentRoot 'netcoredbg.exe'
    $receiptPath = Join-Path $componentRoot 'hermes-provisioning.json'
    $licensePath = Join-Path $componentRoot 'LICENSE.txt'
    $payloadComplete = (Test-Path -LiteralPath $executable -PathType Leaf) -and
        (Test-Path -LiteralPath $receiptPath -PathType Leaf) -and
        (Test-Path -LiteralPath $licensePath -PathType Leaf)
    if (-not $payloadComplete) {
        throw 'The provisioned NetCoreDbg payload is incomplete.'
    }
    $firstExecutableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json
    $receiptMatches = $receipt.SchemaVersion -eq 1 -and
        [string]$receipt.Component -ceq 'Samsung/netcoredbg' -and
        [string]$receipt.Version -ceq '3.1.3-1062' -and
        [string]$receipt.SourceCommit -ceq '8b8b22200fecdb1aec5f47af63215462d8c79a4b' -and
        [string]$receipt.ExecutableSha256 -ceq $firstExecutableHash.ToLowerInvariant()
    if (-not $receiptMatches) {
        throw 'The NetCoreDbg provisioning receipt does not bind the installed executable.'
    }

    & $installer -InstallRoot $smokeRoot -ArchivePath $archive
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $firstExecutableHash) {
        throw 'Repeat provisioning changed the pinned NetCoreDbg executable.'
    }

    $corruptArchive = Join-Path $smokeRoot 'corrupt-netcoredbg.zip'
    Copy-Item -LiteralPath $archive -Destination $corruptArchive
    $stream = [IO.File]::Open($corruptArchive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $stream.Position = 128
        $value = $stream.ReadByte()
        $stream.Position = 128
        $stream.WriteByte($value -bxor 0x5A)
    }
    finally { $stream.Dispose() }
    $rejected = $false
    try { & $installer -InstallRoot $smokeRoot -ArchivePath $corruptArchive }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'The NetCoreDbg installer accepted a corrupted archive.' }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $firstExecutableHash) {
        throw 'A rejected NetCoreDbg update changed the installed executable.'
    }

    Write-Host 'NetCoreDbg provisioning smoke passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $smokeRoot -PathType Container) {
        $resolvedSmoke = (Resolve-Path -LiteralPath $smokeRoot).Path
        $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\HermesNetCoreDbgSmoke-'
        if (-not $resolvedSmoke.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected smoke directory: $resolvedSmoke"
        }
        $item = Get-Item -LiteralPath $resolvedSmoke -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to remove a reparse-point smoke directory: $resolvedSmoke"
        }
        Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
    }
}
