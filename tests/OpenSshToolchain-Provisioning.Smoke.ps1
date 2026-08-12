[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$portable = Test-Path -LiteralPath (Join-Path $projectRoot 'Install-OpenSshToolchain.ps1') -PathType Leaf
$installer = if ($portable) { Join-Path $projectRoot 'Install-OpenSshToolchain.ps1' } else { Join-Path $projectRoot 'remote-install\Install-OpenSshToolchain.ps1' }
$sourceRoot = "$env:SystemRoot\System32\OpenSSH"
foreach ($required in @($installer, (Join-Path $sourceRoot 'ssh.exe'), (Join-Path $sourceRoot 'scp.exe'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "OpenSSH provisioning smoke input is missing: $required" }
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesOpenSshProvisioning-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    & $installer -InstallRoot $smokeRoot -SourceRoot $sourceRoot
    $toolchain = Join-Path $smokeRoot 'toolchains\openssh'
    $receiptPath = Join-Path $toolchain 'hermes-toolchain-receipt.json'
    $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json
    if ($receipt.ReceiptVersion -ne 1 -or [string]$receipt.ToolchainId -cne 'windows-openssh-client' -or
        @($receipt.Executables).Count -ne 2 -or @($receipt.Files).Count -ne 4) {
        throw 'The OpenSSH receipt has the wrong identity or shape.'
    }
    foreach ($entry in @($receipt.Files)) {
        $file = Join-Path $toolchain ([string]$entry.RelativePath)
        if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.Length -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.Sha256) {
            throw 'A provisioned OpenSSH file does not match its receipt.'
        }
    }
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $versionText = (& (Join-Path $toolchain 'ssh.exe') -V 2>&1 | Out-String) }
    finally { $ErrorActionPreference = $previousPreference }
    if ($LASTEXITCODE -ne 0 -or $versionText -notmatch 'OpenSSH_for_Windows_') {
        throw 'The staged OpenSSH client could not report its Windows version.'
    }

    [IO.File]::WriteAllText((Join-Path $toolchain 'rogue.txt'), 'must be removed')
    & $installer -InstallRoot $smokeRoot -SourceRoot $sourceRoot
    if (Test-Path -LiteralPath (Join-Path $toolchain 'rogue.txt')) { throw 'Repeat OpenSSH provisioning retained an unpinned file.' }
    if (@(Get-ChildItem -LiteralPath $smokeRoot -Directory -Force | Where-Object Name -like '.openssh-*').Count -ne 0) {
        throw 'OpenSSH provisioning left an installation or backup directory.'
    }
    Write-Host 'OpenSSH toolchain provisioning smoke passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $smokeRoot -PathType Container) {
        $resolved = (Resolve-Path -LiteralPath $smokeRoot).Path
        $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\HermesOpenSshProvisioning-'
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unexpected smoke cleanup path.' }
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing reparse smoke cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
