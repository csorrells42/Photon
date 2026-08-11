[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testsRoot = $PSScriptRoot
$projectRoot = Split-Path -Parent $testsRoot
$portableLayout = Test-Path -LiteralPath (Join-Path $projectRoot 'Install-ArduinoToolchain.ps1') -PathType Leaf
$installer = if ($portableLayout) {
    Join-Path $projectRoot 'Install-ArduinoToolchain.ps1'
} else {
    Join-Path $projectRoot 'remote-install\Install-ArduinoToolchain.ps1'
}
$lock = if ($portableLayout) {
    Join-Path $projectRoot 'arduino-toolchain.lock.json'
} else {
    Join-Path $projectRoot 'remote-install\arduino-toolchain.lock.json'
}
$archive = if ($portableLayout) {
    Join-Path $projectRoot 'toolchains\arduino-cli_1.5.1_Windows_64bit.zip'
} else {
    Join-Path $projectRoot 'artifacts\toolchain-cache\arduino-cli_1.5.1_Windows_64bit.zip'
}
foreach ($required in @($installer, $lock, $archive)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Arduino provisioning smoke input is missing: $required" }
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesArduinoProvisioning-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    & $installer -InstallRoot $smokeRoot -ArchivePath $archive -SkipBoardProfile

    $toolchainRoot = Join-Path $smokeRoot 'toolchains\arduino'
    $executable = Join-Path $toolchainRoot 'arduino-cli.exe'
    $configuration = Join-Path $toolchainRoot 'arduino-config.json'
    $receiptPath = Join-Path $toolchainRoot 'hermes-toolchain-receipt.json'
    foreach ($required in @($executable, $configuration, $receiptPath, (Join-Path $toolchainRoot 'LICENSE.txt'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Provisioned Arduino payload is incomplete: $required" }
    }
    $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json
    if ($receipt.ReceiptVersion -ne 1 -or [string]$receipt.ToolchainId -cne 'arduino-cli' -or
        @($receipt.Executables).Count -ne 1 -or [string]$receipt.Executables[0].LogicalName -cne 'arduino-cli') {
        throw 'The Arduino receipt did not bind the expected toolchain identity.'
    }
    $firstHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $dump = (& $executable --config-file $configuration --json config dump | Out-String) | ConvertFrom-Json
    $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot)
    foreach ($path in @($dump.config.directories.data, $dump.config.directories.downloads, $dump.config.directories.user)) {
        if (-not [IO.Path]::GetFullPath([string]$path).StartsWith($resolvedSmoke.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The provisioned Arduino configuration escaped the installation root.'
        }
    }

    [IO.File]::WriteAllText((Join-Path $toolchainRoot 'rogue.txt'), 'must-be-removed')
    & $installer -InstallRoot $smokeRoot -ArchivePath $archive -SkipBoardProfile
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $firstHash -or
        (Test-Path -LiteralPath (Join-Path $toolchainRoot 'rogue.txt'))) {
        throw 'Repeat Arduino provisioning did not replace the payload deterministically.'
    }

    $corrupt = Join-Path $smokeRoot 'corrupt-arduino.zip'
    Copy-Item -LiteralPath $archive -Destination $corrupt
    $stream = [IO.File]::Open($corrupt, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $stream.Position = 128
        $value = $stream.ReadByte()
        $stream.Position = 128
        $stream.WriteByte($value -bxor 0x5A)
    }
    finally { $stream.Dispose() }
    $rejected = $false
    try { & $installer -InstallRoot $smokeRoot -ArchivePath $corrupt -SkipBoardProfile }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'The Arduino installer accepted a corrupted archive.' }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $firstHash) {
        throw 'A rejected Arduino archive changed the installed payload.'
    }
    if (@(Get-ChildItem -LiteralPath $smokeRoot -Directory -Force | Where-Object Name -like '.arduino-install-*').Count -ne 0) {
        throw 'Arduino provisioning left a temporary installation directory.'
    }
    Write-Host 'Arduino toolchain provisioning smoke passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $smokeRoot -PathType Container) {
        $resolved = (Resolve-Path -LiteralPath $smokeRoot).Path
        $expected = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\HermesArduinoProvisioning-'
        if (-not $resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected smoke directory: $resolved"
        }
        $item = Get-Item -LiteralPath $resolved -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to remove a reparse-point smoke directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
