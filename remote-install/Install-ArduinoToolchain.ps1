[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$ArchivePath = (Join-Path $PSScriptRoot 'toolchains\arduino-cli_1.5.1_Windows_64bit.zip'),
    [switch]$SkipBoardProfile
)

$ErrorActionPreference = 'Stop'
$maximumLockBytes = 32KB
$maximumArchiveBytes = 128MB
$maximumExpandedBytes = 128MB
$maximumEntries = 16

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
    $full = [IO.Path]::GetFullPath($Path)
    if ($Leaf -and -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label is missing: $full" }
    if ($Container -and -not (Test-Path -LiteralPath $full -PathType Container)) { throw "$Label is missing: $full" }
    $current = Get-Item -LiteralPath $full -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$Label crosses a reparse point: $($current.FullName)"
        }
        $parent = Split-Path -Parent $current.FullName
        if ([string]::IsNullOrEmpty($parent) -or $parent -ceq $current.FullName) { break }
        $current = Get-Item -LiteralPath $parent -Force
    }
    return Get-Item -LiteralPath $full -Force
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

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-JsonNoBom {
    param([string]$Path, [object]$Value, [int]$Depth = 8)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth $Depth), [Text.UTF8Encoding]::new($false))
}

function Assert-ZipEntry {
    param([IO.Compression.ZipArchiveEntry]$Entry, [string]$Label)
    $name = $Entry.FullName.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name.StartsWith('/')) {
        throw "$Label contains an unsafe path."
    }
    $segments = @($name -split '/')
    if ($segments.Count -ne 1 -or $segments[0] -in @('.', '..') -or $segments[0].Contains(':')) {
        throw "$Label contains an unexpected nested or relative path: $name"
    }
    $unixType = (($Entry.ExternalAttributes -shr 16) -band 0xF000)
    if ($unixType -eq 0xA000 -or ($Entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint)) {
        throw "$Label contains a link or reparse entry: $name"
    }
    if ($Entry.Length -le 0 -or $Entry.Length -gt $maximumExpandedBytes) {
        throw "$Label contains an invalid entry length: $name"
    }
    return $name
}

function Copy-ZipFile {
    param([IO.Compression.ZipArchiveEntry]$Entry, [string]$Destination)
    $input = $Entry.Open()
    try {
        $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) } finally { $output.Dispose() }
    }
    finally { $input.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$lockPath = Join-Path $PSScriptRoot 'arduino-toolchain.lock.json'
$lockItem = Assert-RegularExistingPath -Path $lockPath -Label 'The Arduino provisioning lock' -Leaf
if ($lockItem.Length -le 0 -or $lockItem.Length -gt $maximumLockBytes) { throw 'The Arduino provisioning lock has an unsafe size.' }
try { $lock = Get-Content -Raw -LiteralPath $lockItem.FullName | ConvertFrom-Json }
catch { throw 'The Arduino provisioning lock is not valid JSON.' }

Assert-ExactProperties -Value $lock -Expected @(
    'schemaVersion', 'targetRelativePath', 'stateRelativePath', 'receiptFileName',
    'configurationFileName', 'cliPackage', 'boardProfile') -Label 'The Arduino provisioning lock'
Assert-ExactProperties -Value $lock.cliPackage -Expected @(
    'component', 'version', 'commit', 'sourceUrl', 'checksumsUrl', 'archiveName',
    'archiveLength', 'archiveSha256', 'archiveEntryCount', 'executableRelativePath',
    'executableLength', 'executableSha256', 'licenseRelativePath', 'licenseLength',
    'licenseSha256') -Label 'The Arduino CLI package lock'
Assert-ExactProperties -Value $lock.boardProfile -Expected @('package', 'version', 'fqbn') -Label 'The Arduino board profile lock'

if ([int]$lock.schemaVersion -ne 1 -or
    [string]$lock.targetRelativePath -cne 'toolchains/arduino' -or
    [string]$lock.stateRelativePath -cne 'developer-services/arduino-state' -or
    [string]$lock.receiptFileName -cne 'hermes-toolchain-receipt.json' -or
    [string]$lock.configurationFileName -cne 'arduino-config.json') {
    throw 'The Arduino provisioning lock targets an unsupported schema or location.'
}
$package = $lock.cliPackage
$profile = $lock.boardProfile
if ([string]$package.component -cne 'arduino-cli' -or
    [string]$package.version -cne '1.5.1' -or
    [string]$package.commit -cne '01f3d4f2b' -or
    [string]$package.archiveName -cne 'arduino-cli_1.5.1_Windows_64bit.zip' -or
    [string]$package.sourceUrl -cne 'https://github.com/arduino/arduino-cli/releases/download/v1.5.1/arduino-cli_1.5.1_Windows_64bit.zip' -or
    [string]$package.checksumsUrl -cne 'https://github.com/arduino/arduino-cli/releases/download/v1.5.1/1.5.1-checksums.txt' -or
    [string]$package.executableRelativePath -cne 'arduino-cli.exe' -or
    [string]$package.licenseRelativePath -cne 'LICENSE.txt') {
    throw 'The Arduino CLI lock is not the exact supported official package.'
}
if ([string]$profile.package -cne 'arduino:avr' -or
    [string]$profile.version -cne '1.8.8' -or
    [string]$profile.fqbn -cne 'arduino:avr:uno') {
    throw 'The Arduino board profile lock is not the exact supported Uno profile.'
}
Assert-Hex -Value ([string]$package.archiveSha256) -Length 64 -Label 'The Arduino archive SHA-256'
Assert-Hex -Value ([string]$package.executableSha256) -Length 64 -Label 'The Arduino executable SHA-256'
Assert-Hex -Value ([string]$package.licenseSha256) -Length 64 -Label 'The Arduino license SHA-256'
if ([long]$package.archiveLength -le 0 -or [long]$package.archiveLength -gt $maximumArchiveBytes -or
    [int]$package.archiveEntryCount -le 0 -or [int]$package.archiveEntryCount -gt $maximumEntries -or
    [long]$package.executableLength -le 0 -or [long]$package.executableLength -gt $maximumExpandedBytes -or
    [long]$package.licenseLength -le 0 -or [long]$package.licenseLength -gt 1MB) {
    throw 'The Arduino package bounds are invalid.'
}

$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
Assert-RegularExistingPath -Path $resolvedInstall -Label 'The Hermes installation root' -Container | Out-Null
$resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
$archiveItem = Assert-RegularExistingPath -Path $resolvedArchive -Label 'The pinned Arduino CLI archive' -Leaf
if ($archiveItem.Length -ne [long]$package.archiveLength -or (Get-Sha256 $resolvedArchive) -cne [string]$package.archiveSha256) {
    throw 'The pinned Arduino CLI archive failed its exact length or SHA-256 verification.'
}

$installPrefix = $resolvedInstall.TrimEnd('\') + '\'
$temporary = [IO.Path]::GetFullPath((Join-Path $resolvedInstall ('.arduino-install-' + [Guid]::NewGuid().ToString('N'))))
if (-not $temporary.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Arduino staging directory escaped the Hermes installation root.'
}
$payload = Join-Path $temporary 'payload'
$target = [IO.Path]::GetFullPath((Join-Path $resolvedInstall ([string]$lock.targetRelativePath)))
$state = [IO.Path]::GetFullPath((Join-Path $resolvedInstall ([string]$lock.stateRelativePath)))
if (-not $target.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $state.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Arduino target or state path escaped the Hermes installation root.'
}
$targetParent = Split-Path -Parent $target
$backup = Join-Path $targetParent ('.arduino-previous-' + [Guid]::NewGuid().ToString('N'))
$backupCreated = $false

try {
    New-Item -ItemType Directory -Path $payload | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
    try {
        if ($archive.Entries.Count -ne [int]$package.archiveEntryCount) { throw 'The Arduino CLI archive has an unexpected entry count.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [long]$expanded = 0
        foreach ($entry in $archive.Entries) {
            $name = Assert-ZipEntry -Entry $entry -Label 'The Arduino CLI archive'
            if (-not $seen.Add($name)) { throw "The Arduino CLI archive repeats an entry: $name" }
            $expanded += $entry.Length
            if ($expanded -gt $maximumExpandedBytes) { throw 'The Arduino CLI archive exceeds the expanded-size boundary.' }
            Copy-ZipFile -Entry $entry -Destination (Join-Path $payload $name)
        }
    }
    finally { $archive.Dispose() }

    Assert-NoReparseTree -Path $payload -Label 'The staged Arduino CLI payload'
    $executable = Join-Path $payload ([string]$package.executableRelativePath)
    $license = Join-Path $payload ([string]$package.licenseRelativePath)
    foreach ($lockedFile in @(
        @{ Path = $executable; Length = [long]$package.executableLength; Sha256 = [string]$package.executableSha256 },
        @{ Path = $license; Length = [long]$package.licenseLength; Sha256 = [string]$package.licenseSha256 })) {
        $item = Assert-RegularExistingPath -Path $lockedFile.Path -Label 'The staged Arduino package file' -Leaf
        if ($item.Length -ne $lockedFile.Length -or (Get-Sha256 $item.FullName) -cne $lockedFile.Sha256) {
            throw 'A staged Arduino package file failed integrity verification.'
        }
    }

    New-Item -ItemType Directory -Force -Path $state, (Join-Path $state 'data'), (Join-Path $state 'downloads'), (Join-Path $state 'user') | Out-Null
    Assert-NoReparseTree -Path $state -Label 'The Arduino state root'
    $configurationPath = Join-Path $payload ([string]$lock.configurationFileName)
    $configuration = [ordered]@{
        board_manager = [ordered]@{ additional_urls = @(); enable_unsafe_install = $false }
        directories = [ordered]@{
            data = (Join-Path $state 'data')
            downloads = (Join-Path $state 'downloads')
            user = (Join-Path $state 'user')
        }
        library = [ordered]@{ enable_unsafe_install = $false }
        network = [ordered]@{
            connection_timeout = '60s'
            cloud_api = [ordered]@{ skip_board_detection_calls = $true }
        }
    }
    Write-JsonNoBom -Path $configurationPath -Value $configuration

    & $executable --config-file $configurationPath --json config dump *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The staged Arduino CLI rejected the private configuration.' }
    if (-not $SkipBoardProfile) {
        & $executable --config-file $configurationPath --json core update-index *> $null
        if ($LASTEXITCODE -ne 0) { throw 'The Arduino board index update failed.' }
        $coreSpec = [string]$profile.package + '@' + [string]$profile.version
        & $executable --config-file $configurationPath --json core install $coreSpec *> $null
        if ($LASTEXITCODE -ne 0) { throw "The exact Arduino board core failed to install: $coreSpec" }
        $coreListText = (& $executable --config-file $configurationPath --json core list | Out-String)
        if ($LASTEXITCODE -ne 0) { throw 'The installed Arduino board core could not be inspected.' }
        try { $coreList = $coreListText | ConvertFrom-Json }
        catch { throw 'The installed Arduino board-core inventory is not valid JSON.' }
        $coreMatches = @($coreList.platforms | Where-Object {
            [string]$_.id -ceq [string]$profile.package -and
            [string]$_.installed_version -ceq [string]$profile.version
        })
        if ($coreMatches.Count -ne 1) { throw 'The exact pinned Arduino board core is not installed.' }
        & $executable --config-file $configurationPath --json board details --fqbn ([string]$profile.fqbn) *> $null
        if ($LASTEXITCODE -ne 0) { throw 'The exact Arduino Uno board profile could not be verified.' }
    }

    $manifestPaths = @($executable, $license, $configurationPath)
    $files = @($manifestPaths | ForEach-Object {
        $item = Get-Item -LiteralPath $_ -Force
        [ordered]@{
            RelativePath = $item.FullName.Substring($payload.TrimEnd('\').Length + 1)
            Sha256 = Get-Sha256 $item.FullName
            Length = [long]$item.Length
        }
    })
    $receipt = [ordered]@{
        ReceiptVersion = 1
        ToolchainId = 'arduino-cli'
        Executables = @([ordered]@{
            LogicalName = 'arduino-cli'
            RelativePath = 'arduino-cli.exe'
            Sha256 = [string]$package.executableSha256
        })
        Files = $files
    }
    Write-JsonNoBom -Path (Join-Path $payload ([string]$lock.receiptFileName)) -Value $receipt

    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    Assert-RegularExistingPath -Path $targetParent -Label 'The Arduino toolchain parent' -Container | Out-Null
    if (Test-Path -LiteralPath $target) {
        Assert-NoReparseTree -Path $target -Label 'The existing Arduino installation'
        Move-Item -LiteralPath $target -Destination $backup
        $backupCreated = $true
    }
    Move-Item -LiteralPath $payload -Destination $target
    if ($backupCreated) {
        Assert-NoReparseTree -Path $backup -Label 'The previous Arduino installation'
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
        Assert-NoReparseTree -Path $temporary -Label 'The Arduino staging directory'
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}

Write-Host "Provisioned Arduino CLI $($package.version) and board profile $($profile.fqbn)@$($profile.version)." -ForegroundColor Green
