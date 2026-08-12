[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$SourceRoot = "$env:SystemRoot\System32\OpenSSH"
)

$ErrorActionPreference = 'Stop'
$requiredFiles = @('ssh.exe', 'scp.exe', 'LICENSE.txt', 'NOTICE.txt')
$microsoftSubject = 'CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'

function Assert-Directory {
    param([string]$Path, [string]$Label)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "$Label is missing: $full" }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Label is a reparse point." }
    return $full
}

function Assert-RegularFile {
    param([string]$Path, [string]$Label)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label is missing: $full" }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Label is a reparse point." }
    if ($item.Length -le 0 -or $item.Length -gt 16MB) { throw "$Label has an invalid length." }
    return $item
}

function Assert-MicrosoftSignature {
    param([IO.FileInfo]$File)
    $signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $microsoftSubject) {
        throw "The OpenSSH source file is not signed by Microsoft Windows: $($File.Name)"
    }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-JsonNoBom([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

$resolvedInstall = Assert-Directory -Path $InstallRoot -Label 'The Hermes installation root'
$resolvedSource = Assert-Directory -Path $SourceRoot -Label 'The Windows OpenSSH source root'
$installPrefix = $resolvedInstall.TrimEnd('\') + '\'
$target = [IO.Path]::GetFullPath((Join-Path $resolvedInstall 'toolchains\openssh'))
$temporary = [IO.Path]::GetFullPath((Join-Path $resolvedInstall ('.openssh-install-' + [Guid]::NewGuid().ToString('N'))))
if (-not $target.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $temporary.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The OpenSSH installation path escaped the Hermes installation root.'
}

$targetParent = Split-Path -Parent $target
$backup = Join-Path $targetParent ('.openssh-previous-' + [Guid]::NewGuid().ToString('N'))
$backupCreated = $false
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $manifest = @()
    foreach ($name in $requiredFiles) {
        $source = Assert-RegularFile -Path (Join-Path $resolvedSource $name) -Label 'The Windows OpenSSH source file'
        Assert-MicrosoftSignature -File $source
        $destination = Join-Path $temporary $name
        Copy-Item -LiteralPath $source.FullName -Destination $destination
        $copy = Assert-RegularFile -Path $destination -Label 'The staged OpenSSH file'
        if ($copy.Length -ne $source.Length -or (Get-Sha256 $copy.FullName) -cne (Get-Sha256 $source.FullName)) {
            throw "The staged OpenSSH file failed its exact copy check: $name"
        }
        $manifest += [ordered]@{
            RelativePath = $name
            Sha256 = Get-Sha256 $copy.FullName
            Length = [long]$copy.Length
        }
    }

    $sshHash = [string]($manifest | Where-Object RelativePath -CEQ 'ssh.exe').Sha256
    $scpHash = [string]($manifest | Where-Object RelativePath -CEQ 'scp.exe').Sha256
    $receipt = [ordered]@{
        ReceiptVersion = 1
        ToolchainId = 'windows-openssh-client'
        Executables = @(
            [ordered]@{ LogicalName = 'ssh'; RelativePath = 'ssh.exe'; Sha256 = $sshHash },
            [ordered]@{ LogicalName = 'scp'; RelativePath = 'scp.exe'; Sha256 = $scpHash }
        )
        Files = $manifest
    }
    Write-JsonNoBom -Path (Join-Path $temporary 'hermes-toolchain-receipt.json') -Value $receipt

    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    if (Test-Path -LiteralPath $target) {
        $existing = Get-Item -LiteralPath $target -Force
        if ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The existing OpenSSH toolchain is a reparse point.' }
        Move-Item -LiteralPath $target -Destination $backup
        $backupCreated = $true
    }
    Move-Item -LiteralPath $temporary -Destination $target
    if ($backupCreated) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backupCreated = $false
    }
}
catch {
    if ($backupCreated -and -not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $backup -PathType Container)) {
        Move-Item -LiteralPath $backup -Destination $target
        $backupCreated = $false
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Container) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}

Write-Host 'Provisioned the receipt-bound Microsoft Windows OpenSSH client.' -ForegroundColor Green
