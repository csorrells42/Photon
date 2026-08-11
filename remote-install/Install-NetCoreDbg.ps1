[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$ArchivePath = (Join-Path $PSScriptRoot 'toolchains\netcoredbg-win64.zip')
)

$ErrorActionPreference = 'Stop'
$version = '3.1.3-1062'
$sourceCommit = '8b8b22200fecdb1aec5f47af63215462d8c79a4b'
$archiveName = 'netcoredbg-win64.zip'
$archiveLength = 3475639L
$archiveHash = 'C67AE052E0BCB9CE37000F261E2D397A0D5B6615CAFE30C868239A78598DFB37'
$licenseHash = '6CD03B0DE8299B0800F22B35AE842C931DED7684A2D1BA4F1D4188BAB9B09A11'
$expectedEntries = @(
    'netcoredbg/',
    'netcoredbg/dbgshim.dll',
    'netcoredbg/ManagedPart.dll',
    'netcoredbg/Microsoft.CodeAnalysis.CSharp.dll',
    'netcoredbg/Microsoft.CodeAnalysis.CSharp.Scripting.dll',
    'netcoredbg/Microsoft.CodeAnalysis.dll',
    'netcoredbg/Microsoft.CodeAnalysis.Scripting.dll',
    'netcoredbg/netcoredbg.exe'
)

function Assert-RegularPath {
    param([string]$Path, [string]$Label)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$Label must not be a reparse point: $Path"
    }
    return $item
}

$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
if (-not (Test-Path -LiteralPath $resolvedInstall -PathType Container)) {
    throw "The Hermes installation root does not exist: $resolvedInstall"
}
Assert-RegularPath -Path $resolvedInstall -Label 'The Hermes installation root' | Out-Null

$resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
    throw "The pinned NetCoreDbg archive is missing: $resolvedArchive"
}
$archiveItem = Assert-RegularPath -Path $resolvedArchive -Label 'The NetCoreDbg archive'
if ([long]$archiveItem.Length -ne $archiveLength) {
    throw 'The pinned NetCoreDbg archive has an unexpected byte length.'
}
if ((Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash -cne $archiveHash) {
    throw 'The pinned NetCoreDbg archive failed its SHA-256 integrity check.'
}

$licensePath = Join-Path $PSScriptRoot 'licenses\netcoredbg-LICENSE.txt'
$licenseIsValid = (Test-Path -LiteralPath $licensePath -PathType Leaf) -and
    (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash -ceq $licenseHash
if (-not $licenseIsValid) {
    throw 'The pinned NetCoreDbg license notice is missing or invalid.'
}
Assert-RegularPath -Path $licensePath -Label 'The NetCoreDbg license notice' | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if ($entryNames.Count -ne $expectedEntries.Count) {
        throw 'The pinned NetCoreDbg archive contains an unexpected number of entries.'
    }
    for ($index = 0; $index -lt $expectedEntries.Count; $index++) {
        if ($entryNames[$index] -cne $expectedEntries[$index]) {
            throw "The pinned NetCoreDbg archive inventory is unexpected at entry $index."
        }
    }
    foreach ($entry in $archive.Entries) {
        $normalized = $entry.FullName.Replace('\', '/')
        $segments = @($normalized -split '/' | Where-Object { $_ })
        $unsafeEntry = [IO.Path]::IsPathRooted($normalized) -or
            $normalized.Contains('\') -or
            @($segments | Where-Object { $_ -in @('.', '..') }).Count -gt 0
        if ($unsafeEntry) {
            throw "The pinned NetCoreDbg archive contains an unsafe path: $normalized"
        }
    }
}
finally {
    $archive.Dispose()
}

$temporaryRoot = Join-Path $resolvedInstall ('.netcoredbg-install-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
$installPrefix = $resolvedInstall.TrimEnd('\') + '\'
if (-not $resolvedTemporary.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The NetCoreDbg staging directory escaped the Hermes installation root.'
}

$targetParent = Join-Path $resolvedInstall 'debuggers\netcoredbg'
$target = Join-Path $targetParent $version
$backup = Join-Path $targetParent ($version + '.previous-' + [Guid]::NewGuid().ToString('N'))
$backupCreated = $false
try {
    New-Item -ItemType Directory -Path $resolvedTemporary | Out-Null
    $extractRoot = Join-Path $resolvedTemporary 'extract'
    [IO.Compression.ZipFile]::ExtractToDirectory($resolvedArchive, $extractRoot)
    Get-ChildItem -LiteralPath $extractRoot -Force -Recurse | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The extracted NetCoreDbg payload contains a reparse point: $($_.FullName)"
        }
    }

    $payload = Join-Path $resolvedTemporary 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $extractRoot 'netcoredbg') -Force | Copy-Item -Destination $payload -Recurse
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $payload 'LICENSE.txt')
    $executable = Join-Path $payload 'netcoredbg.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The verified NetCoreDbg payload does not contain netcoredbg.exe.'
    }
    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt = [ordered]@{
        SchemaVersion = 1
        Component = 'Samsung/netcoredbg'
        Version = $version
        SourceCommit = $sourceCommit
        ArchiveName = $archiveName
        ArchiveSha256 = $archiveHash.ToLowerInvariant()
        ExecutableSha256 = $executableHash
    }
    [IO.File]::WriteAllText(
        (Join-Path $payload 'hermes-provisioning.json'),
        ($receipt | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    Assert-RegularPath -Path (Join-Path $resolvedInstall 'debuggers') -Label 'The debugger directory' | Out-Null
    Assert-RegularPath -Path $targetParent -Label 'The NetCoreDbg directory' | Out-Null
    if (Test-Path -LiteralPath $target) {
        $targetItem = Assert-RegularPath -Path $target -Label 'The existing NetCoreDbg installation'
        if (-not $targetItem.PSIsContainer) { throw 'The existing NetCoreDbg target is not a directory.' }
        Move-Item -LiteralPath $target -Destination $backup
        $backupCreated = $true
    }
    Move-Item -LiteralPath $payload -Destination $target

    if ($backupCreated -and (Test-Path -LiteralPath $backup -PathType Container)) {
        Assert-RegularPath -Path $backup -Label 'The previous NetCoreDbg installation' | Out-Null
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
    if (Test-Path -LiteralPath $resolvedTemporary -PathType Container) {
        Assert-RegularPath -Path $resolvedTemporary -Label 'The NetCoreDbg staging directory' | Out-Null
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

Write-Host "Provisioned NetCoreDbg $version with verified release and executable hashes." -ForegroundColor Green
