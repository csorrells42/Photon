[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$ServerArchivePath = (Join-Path $PSScriptRoot 'toolchains\microsoft.codeanalysis.languageserver.win-x64.5.0.0-1.25277.114.nupkg'),
    [string]$SdkArchivePath = (Join-Path $PSScriptRoot 'toolchains\dotnet-sdk-10.0.302-win-x64.zip')
)

$ErrorActionPreference = 'Stop'
$maximumLockBytes = 32KB
$maximumArchiveBytes = 512MB
$maximumExpandedBytes = 2GB
$maximumArchiveEntries = 16384

function Assert-ExactProperties {
    param([object]$Value, [string[]]$Expected, [string]$Label)
    if ($null -eq $Value) { throw "$Label is missing." }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) { throw "$Label has unexpected fields." }
    foreach ($name in $Expected) {
        if ($actual -cnotcontains $name) { throw "$Label is missing the exact '$name' field." }
    }
}

function Assert-Hex {
    param([string]$Value, [int]$Length, [string]$Label)
    if ($Value.Length -ne $Length -or $Value -cnotmatch "^[0-9a-f]{$Length}$") {
        throw "$Label is not the expected lowercase hexadecimal digest."
    }
}

function Assert-RegularExistingPath {
    param([string]$Path, [string]$Label, [switch]$Leaf, [switch]$Container)
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($Leaf -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Label is missing: $fullPath" }
    if ($Container -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) { throw "$Label is missing: $fullPath" }
    $current = Get-Item -LiteralPath $fullPath -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$Label crosses a reparse point: $($current.FullName)"
        }
        $parentPath = Split-Path -Parent $current.FullName
        if ([string]::IsNullOrEmpty($parentPath) -or $parentPath -ceq $current.FullName) { break }
        $current = Get-Item -LiteralPath $parentPath -Force
    }
    return Get-Item -LiteralPath $fullPath -Force
}

function Assert-NoReparseTree {
    param([string]$Path, [string]$Label)
    $root = Assert-RegularExistingPath -Path $Path -Label $Label -Container
    Get-ChildItem -LiteralPath $root.FullName -Force -Recurse | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$Label contains a reparse point: $($_.FullName)"
        }
    }
}

function Get-FileDigest {
    param([string]$Path, [string]$Algorithm)
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Assert-SafeZipEntry {
    param([IO.Compression.ZipArchiveEntry]$Entry, [string]$Label)
    $name = $Entry.FullName
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\')) { throw "$Label contains an invalid entry name." }
    $normalized = $name.Replace('\', '/')
    if ([IO.Path]::IsPathRooted($normalized) -or $normalized.StartsWith('/', [StringComparison]::Ordinal)) {
        throw "$Label contains a rooted entry: $normalized"
    }
    $segments = @($normalized.TrimEnd('/') -split '/')
    if ($segments.Count -eq 0 -or @($segments | Where-Object {
        [string]::IsNullOrEmpty($_) -or $_ -in @('.', '..') -or $_.Contains(':') -or $_.EndsWith('.') -or $_.EndsWith(' ')
    }).Count -gt 0) {
        throw "$Label contains an unsafe entry: $normalized"
    }
    $unixType = (($Entry.ExternalAttributes -shr 16) -band 0xF000)
    if ($unixType -eq 0xA000 -or ($Entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint)) {
        throw "$Label contains a link or reparse entry: $normalized"
    }
    if ($Entry.Length -lt 0 -or $Entry.Length -gt $maximumExpandedBytes) {
        throw "$Label contains an oversized entry: $normalized"
    }
    return $normalized
}

function Assert-ZipSafety {
    param([IO.Compression.ZipArchive]$Archive, [string]$Label)
    if ($Archive.Entries.Count -le 0 -or $Archive.Entries.Count -gt $maximumArchiveEntries) {
        throw "$Label has an unsafe entry count."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$expanded = 0
    foreach ($entry in $Archive.Entries) {
        $normalized = Assert-SafeZipEntry -Entry $entry -Label $Label
        if (-not $seen.Add($normalized.TrimEnd('/'))) { throw "$Label contains a duplicate entry: $normalized" }
        $expanded += $entry.Length
        if ($expanded -gt $maximumExpandedBytes) { throw "$Label expands beyond the safe boundary." }
    }
}

function Copy-ZipEntry {
    param([IO.Compression.ZipArchiveEntry]$Entry, [string]$RelativePath, [string]$DestinationRoot)
    $root = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\') + '\'
    $relativeWindows = $RelativePath.Replace('/', '\')
    $destination = [IO.Path]::GetFullPath((Join-Path $DestinationRoot $relativeWindows))
    if (-not $destination.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "An archive entry escaped its staging root: $RelativePath"
    }
    if ($RelativePath.EndsWith('/', [StringComparison]::Ordinal)) {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        return
    }
    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $input = $Entry.Open()
    try {
        $output = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) } finally { $output.Dispose() }
    }
    finally { $input.Dispose() }
}

function Get-PayloadInventory {
    param([string]$Root)
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Root -Force -File -Recurse)
    $rows = [Collections.Generic.List[string]]::new()
    [long]$bytes = 0
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "The Roslyn payload contains a reparse point: $($file.FullName)" }
        $relative = $file.FullName.Substring($resolvedRoot.Length).Replace('\', '/')
        $hash = Get-FileDigest -Path $file.FullName -Algorithm SHA256
        $rows.Add($relative + "`t" + [string]$file.Length + "`t" + $hash)
        $bytes += $file.Length
    }
    $ordered = [string[]]$rows.ToArray()
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    $canonical = ($ordered -join "`n") + "`n"
    $encoding = [Text.UTF8Encoding]::new($false)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $digest = ([BitConverter]::ToString($hasher.ComputeHash($encoding.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    return [pscustomobject]@{ FileCount = $files.Count; Bytes = $bytes; Sha256 = $digest }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$lockPath = Join-Path $PSScriptRoot 'roslyn-language-server.lock.json'
$lockItem = Assert-RegularExistingPath -Path $lockPath -Label 'The Roslyn provisioning lock' -Leaf
if ($lockItem.Length -le 0 -or $lockItem.Length -gt $maximumLockBytes) { throw 'The Roslyn provisioning lock has an unsafe size.' }
try { $lock = Get-Content -Raw -LiteralPath $lockItem.FullName | ConvertFrom-Json }
catch { throw 'The Roslyn provisioning lock is not valid JSON.' }

Assert-ExactProperties -Value $lock -Expected @('schemaVersion', 'targetRelativePath', 'providerJsonFields', 'serverPackage', 'sdkPackage') -Label 'The Roslyn provisioning lock'
Assert-ExactProperties -Value $lock.serverPackage -Expected @(
    'component', 'packageVersion', 'sourceCommit', 'sourceUrl', 'archiveName', 'archiveLength', 'archiveSha256',
    'nugetContentSha512', 'archiveEntryCount', 'payloadPrefix', 'payloadFileCount', 'payloadBytes',
    'payloadInventorySha256', 'executableRelativePath', 'executableSha256', 'authorCertificateSha256',
    'repositoryCertificateSha256') -Label 'The Roslyn server lock'
Assert-ExactProperties -Value $lock.sdkPackage -Expected @(
    'component', 'version', 'runtimeVersion', 'rid', 'sourceUrl', 'archiveName', 'archiveLength', 'archiveSha512',
    'releaseMetadataUrl') -Label 'The .NET SDK lock'

if ([int]$lock.schemaVersion -ne 2 -or [string]$lock.targetRelativePath -cne 'developer-services/roslyn') {
    throw 'The Roslyn provisioning lock targets an unsupported schema or location.'
}
if (@($lock.providerJsonFields).Count -ne 2 -or [string]$lock.providerJsonFields[0] -cne 'packageVersion' -or [string]$lock.providerJsonFields[1] -cne 'executableSha256') {
    throw 'The Roslyn provider receipt fields do not match the host contract.'
}

$server = $lock.serverPackage
$sdk = $lock.sdkPackage
if ([string]$server.component -cne 'Microsoft.CodeAnalysis.LanguageServer.win-x64' -or
    [string]$server.payloadPrefix -cne 'content/LanguageServer/win-x64/' -or
    [string]$server.executableRelativePath -cne 'Microsoft.CodeAnalysis.LanguageServer.exe') {
    throw 'The Roslyn server lock names an unsupported component or payload.'
}
if ([string]$server.packageVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-[0-9A-Za-z.-]+$' -or
    [string]$server.sourceCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The Roslyn package version or source commit is not immutable.'
}
$expectedServerName = ('microsoft.codeanalysis.languageserver.win-x64.' + [string]$server.packageVersion + '.nupkg').ToLowerInvariant()
$expectedServerUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.languageserver.win-x64/' +
    ([string]$server.packageVersion).ToLowerInvariant() + '/' + $expectedServerName
if ([string]$server.archiveName -cne $expectedServerName -or [string]$server.sourceUrl -cne $expectedServerUrl) {
    throw 'The Roslyn source is not the exact official NuGet archive.'
}
if ([string]$server.sourceUrl -match '(?i)latest' -or [string]$sdk.sourceUrl -match '(?i)latest') {
    throw 'Mutable latest sources are forbidden.'
}
Assert-Hex -Value ([string]$server.archiveSha256) -Length 64 -Label 'The Roslyn archive SHA-256'
Assert-Hex -Value ([string]$server.payloadInventorySha256) -Length 64 -Label 'The Roslyn payload inventory SHA-256'
Assert-Hex -Value ([string]$server.executableSha256) -Length 64 -Label 'The Roslyn executable SHA-256'
Assert-Hex -Value ([string]$server.authorCertificateSha256) -Length 64 -Label 'The Roslyn author certificate SHA-256'
Assert-Hex -Value ([string]$server.repositoryCertificateSha256) -Length 64 -Label 'The NuGet repository certificate SHA-256'
try { if ([Convert]::FromBase64String([string]$server.nugetContentSha512).Length -ne 64) { throw } }
catch { throw 'The NuGet content SHA-512 is invalid.' }
if ([long]$server.archiveLength -le 0 -or [long]$server.archiveLength -gt $maximumArchiveBytes -or
    [int]$server.archiveEntryCount -le 0 -or [int]$server.archiveEntryCount -gt $maximumArchiveEntries -or
    [int]$server.payloadFileCount -le 0 -or [int]$server.payloadFileCount -gt $maximumArchiveEntries -or
    [long]$server.payloadBytes -le 0 -or [long]$server.payloadBytes -gt $maximumExpandedBytes) {
    throw 'The Roslyn package bounds are invalid.'
}

if ([string]$sdk.component -cne 'Microsoft.NETCoreSdk' -or [string]$sdk.rid -cne 'win-x64' -or
    [string]$sdk.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    [string]$sdk.runtimeVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw 'The .NET SDK lock is invalid.'
}
$expectedSdkName = 'dotnet-sdk-' + [string]$sdk.version + '-win-x64.zip'
$expectedSdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/' + [string]$sdk.version + '/' + $expectedSdkName
if ([string]$sdk.archiveName -cne $expectedSdkName -or [string]$sdk.sourceUrl -cne $expectedSdkUrl -or
    [long]$sdk.archiveLength -le 0 -or [long]$sdk.archiveLength -gt $maximumArchiveBytes -or
    [string]$sdk.releaseMetadataUrl -cne 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json') {
    throw 'The .NET SDK source is not the exact official Microsoft archive.'
}
Assert-Hex -Value ([string]$sdk.archiveSha512) -Length 128 -Label 'The .NET SDK archive SHA-512'

$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
Assert-RegularExistingPath -Path $resolvedInstall -Label 'The Hermes installation root' -Container | Out-Null
$resolvedServerArchive = [IO.Path]::GetFullPath($ServerArchivePath)
$serverArchiveItem = Assert-RegularExistingPath -Path $resolvedServerArchive -Label 'The pinned Roslyn archive' -Leaf
$resolvedSdkArchive = [IO.Path]::GetFullPath($SdkArchivePath)
$sdkArchiveItem = Assert-RegularExistingPath -Path $resolvedSdkArchive -Label 'The pinned .NET SDK archive' -Leaf
if ($serverArchiveItem.Length -ne [long]$server.archiveLength -or $sdkArchiveItem.Length -ne [long]$sdk.archiveLength) {
    throw 'A pinned Roslyn provisioning archive has an unexpected byte length.'
}
if ((Get-FileDigest -Path $resolvedServerArchive -Algorithm SHA256) -cne [string]$server.archiveSha256) {
    throw 'The pinned Roslyn archive failed SHA-256 verification.'
}
if ((Get-FileDigest -Path $resolvedSdkArchive -Algorithm SHA512) -cne [string]$sdk.archiveSha512) {
    throw 'The pinned .NET SDK archive failed SHA-512 verification.'
}

$temporaryRoot = Join-Path $resolvedInstall ('.roslyn-install-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
$installPrefix = $resolvedInstall.TrimEnd('\') + '\'
if (-not $resolvedTemporary.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Roslyn staging directory escaped the Hermes installation root.'
}
$targetParent = Join-Path $resolvedInstall 'developer-services'
$target = Join-Path $targetParent 'roslyn'
$backup = Join-Path $targetParent ('.roslyn-previous-' + [Guid]::NewGuid().ToString('N'))
$backupCreated = $false

try {
    New-Item -ItemType Directory -Path $resolvedTemporary | Out-Null
    $payload = Join-Path $resolvedTemporary 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null

    $serverArchive = [IO.Compression.ZipFile]::OpenRead($resolvedServerArchive)
    try {
        Assert-ZipSafety -Archive $serverArchive -Label 'The Roslyn archive'
        if ($serverArchive.Entries.Count -ne [int]$server.archiveEntryCount) { throw 'The Roslyn archive contains unexpected entries.' }
        if (@($serverArchive.Entries | Where-Object { $_.FullName -ceq '.signature.p7s' }).Count -ne 1) {
            throw 'The Roslyn NuGet archive is missing its exact package signature entry.'
        }
        $payloadEntries = @($serverArchive.Entries | Where-Object {
            $_.FullName.StartsWith([string]$server.payloadPrefix, [StringComparison]::Ordinal) -and $_.Name.Length -gt 0
        })
        if ($payloadEntries.Count -ne [int]$server.payloadFileCount -or
            [long](($payloadEntries | Measure-Object Length -Sum).Sum) -ne [long]$server.payloadBytes) {
            throw 'The Roslyn payload inventory bounds do not match the lock.'
        }
        foreach ($entry in $payloadEntries) {
            $relative = $entry.FullName.Substring(([string]$server.payloadPrefix).Length)
            if ($relative -ceq 'provider.json' -or $relative.StartsWith('dotnet/', [StringComparison]::OrdinalIgnoreCase)) {
                throw "The Roslyn package overlaps an installer-owned path: $relative"
            }
            Copy-ZipEntry -Entry $entry -RelativePath $relative -DestinationRoot $payload
        }
    }
    finally { $serverArchive.Dispose() }

    Assert-NoReparseTree -Path $payload -Label 'The staged Roslyn payload'
    $inventory = Get-PayloadInventory -Root $payload
    if ($inventory.FileCount -ne [int]$server.payloadFileCount -or $inventory.Bytes -ne [long]$server.payloadBytes -or
        $inventory.Sha256 -cne [string]$server.payloadInventorySha256) {
        throw 'The extracted Roslyn payload contains missing, changed, or extra files.'
    }
    $executable = Join-Path $payload ([string]$server.executableRelativePath)
    Assert-RegularExistingPath -Path $executable -Label 'The Roslyn language-server executable' -Leaf | Out-Null
    if ((Get-FileDigest -Path $executable -Algorithm SHA256) -cne [string]$server.executableSha256) {
        throw 'The extracted Roslyn language-server executable failed integrity verification.'
    }

    $dotnetRoot = Join-Path $payload 'dotnet'
    New-Item -ItemType Directory -Path $dotnetRoot | Out-Null
    $sdkArchive = [IO.Compression.ZipFile]::OpenRead($resolvedSdkArchive)
    try {
        Assert-ZipSafety -Archive $sdkArchive -Label 'The .NET SDK archive'
        foreach ($entry in $sdkArchive.Entries) {
            $normalized = $entry.FullName.Replace('\', '/')
            $top = ($normalized.TrimEnd('/') -split '/')[0]
            if ($top -cnotin @(
                'dnx.cmd', 'dotnet.exe', 'host', 'packs', 'sdk', 'sdk-manifests', 'shared', 'templates',
                'LICENSE.txt', 'ThirdPartyNotices.txt')) {
                throw "The .NET SDK archive contains an unexpected top-level entry: $normalized"
            }
            Copy-ZipEntry -Entry $entry -RelativePath $normalized -DestinationRoot $dotnetRoot
        }
    }
    finally { $sdkArchive.Dispose() }

    Assert-NoReparseTree -Path $dotnetRoot -Label 'The staged .NET SDK'
    foreach ($requiredSdkFile in @(
        (Join-Path $dotnetRoot 'dotnet.exe'),
        (Join-Path $dotnetRoot ('host\fxr\' + [string]$sdk.runtimeVersion + '\hostfxr.dll')),
        (Join-Path $dotnetRoot ('shared\Microsoft.NETCore.App\' + [string]$sdk.runtimeVersion + '\System.Private.CoreLib.dll')),
        (Join-Path $dotnetRoot ('sdk\' + [string]$sdk.version + '\MSBuild.dll')),
        (Join-Path $dotnetRoot ('packs\Microsoft.NETCore.App.Ref\' + [string]$sdk.runtimeVersion + '\ref\net10.0\System.Runtime.dll')))) {
        Assert-RegularExistingPath -Path $requiredSdkFile -Label 'The pinned .NET SDK payload' -Leaf | Out-Null
    }

    $receipt = [ordered]@{
        packageVersion = [string]$server.packageVersion
        executableSha256 = [string]$server.executableSha256
    }
    [IO.File]::WriteAllText(
        (Join-Path $payload 'provider.json'),
        ($receipt | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    Assert-RegularExistingPath -Path $targetParent -Label 'The developer-services directory' -Container | Out-Null
    if (Test-Path -LiteralPath $target) {
        $targetItem = Assert-RegularExistingPath -Path $target -Label 'The existing Roslyn installation'
        if (-not $targetItem.PSIsContainer) { throw 'The existing Roslyn target is not a directory.' }
        Assert-NoReparseTree -Path $target -Label 'The existing Roslyn installation'
        Move-Item -LiteralPath $target -Destination $backup
        $backupCreated = $true
    }
    Move-Item -LiteralPath $payload -Destination $target

    if ($backupCreated) {
        Assert-NoReparseTree -Path $backup -Label 'The previous Roslyn installation'
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
        Assert-NoReparseTree -Path $resolvedTemporary -Label 'The Roslyn staging directory'
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

Write-Host "Provisioned Roslyn language server $($server.packageVersion) with .NET SDK $($sdk.version) from verified offline archives." -ForegroundColor Green
