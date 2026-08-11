[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$testsRoot = $PSScriptRoot
$projectRoot = Split-Path -Parent $testsRoot
$installer = Join-Path $projectRoot 'remote-install\Install-RoslynLanguageServer.ps1'
$productionLock = Join-Path $projectRoot 'remote-install\roslyn-language-server.lock.json'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or -not (Test-Path -LiteralPath $productionLock -PathType Leaf)) {
    throw 'The Roslyn provisioning lane files were not found.'
}

function Get-Digest {
    param([string]$Path, [string]$Algorithm)
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Get-Inventory {
    param([string]$Root)
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse)
    $rows = [Collections.Generic.List[string]]::new()
    [long]$bytes = 0
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $rows.Add($relative + "`t" + [string]$file.Length + "`t" + (Get-Digest -Path $file.FullName -Algorithm SHA256))
        $bytes += $file.Length
    }
    $ordered = [string[]]$rows.ToArray()
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    $canonical = ($ordered -join "`n") + "`n"
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($hasher.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    return [pscustomobject]@{ FileCount = $files.Count; Bytes = $bytes; Sha256 = $hash }
}

function Get-ZipEntryCount {
    param([string]$Path)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return $archive.Entries.Count } finally { $archive.Dispose() }
}

function New-NormalizedZip {
    param([string]$SourceRoot, [string]$Destination)
    $sourcePrefix = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\') + '\'
    $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceRoot -File -Force -Recurse) {
                $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead($file.FullName)
                try {
                    $entryStream = $entry.Open()
                    try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose() }
                }
                finally { $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $output.Dispose() }
}

function Write-JsonNoBom {
    param([string]$Path, [object]$Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function New-FakeLock {
    param([string]$ServerArchive, [object]$Inventory, [string]$ExecutableHash, [string]$SdkArchive)
    $version = '0.0.0-fake.1'
    $archiveName = 'microsoft.codeanalysis.languageserver.win-x64.' + $version + '.nupkg'
    return [ordered]@{
        schemaVersion = 2
        targetRelativePath = 'developer-services/roslyn'
        providerJsonFields = @('packageVersion', 'executableSha256')
        serverPackage = [ordered]@{
            component = 'Microsoft.CodeAnalysis.LanguageServer.win-x64'
            packageVersion = $version
            sourceCommit = ('1' * 40)
            sourceUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.languageserver.win-x64/' + $version + '/' + $archiveName
            archiveName = $archiveName
            archiveLength = (Get-Item -LiteralPath $ServerArchive).Length
            archiveSha256 = Get-Digest -Path $ServerArchive -Algorithm SHA256
            nugetContentSha512 = [Convert]::ToBase64String([byte[]]::new(64))
            archiveEntryCount = Get-ZipEntryCount -Path $ServerArchive
            payloadPrefix = 'content/LanguageServer/win-x64/'
            payloadFileCount = $Inventory.FileCount
            payloadBytes = $Inventory.Bytes
            payloadInventorySha256 = $Inventory.Sha256
            executableRelativePath = 'Microsoft.CodeAnalysis.LanguageServer.exe'
            executableSha256 = $ExecutableHash
            authorCertificateSha256 = ('a' * 64)
            repositoryCertificateSha256 = ('b' * 64)
        }
        sdkPackage = [ordered]@{
            component = 'Microsoft.NETCoreSdk'
            version = '10.0.302'
            runtimeVersion = '10.0.10'
            rid = 'win-x64'
            sourceUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-win-x64.zip'
            archiveName = 'dotnet-sdk-10.0.302-win-x64.zip'
            archiveLength = (Get-Item -LiteralPath $SdkArchive).Length
            archiveSha512 = Get-Digest -Path $SdkArchive -Algorithm SHA512
            releaseMetadataUrl = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
        }
    }
}

$locked = Get-Content -Raw -LiteralPath $productionLock | ConvertFrom-Json
if ([string]$locked.serverPackage.packageVersion -cne '5.0.0-1.25277.114' -or
    [string]$locked.serverPackage.archiveSha256 -cne '7c96c59532a81f710be95a48e6dd25c4e4d17875a37f5a7171a90e82f8ab57a6' -or
    [string]$locked.serverPackage.executableSha256 -cne 'a0880370c766535d3e53c3acf2d1a89ab1f46fa6c7aa84f05aeef51ae7e430ac' -or
    [int]$locked.schemaVersion -ne 2 -or
    [string]$locked.sdkPackage.version -cne '10.0.302' -or
    [string]$locked.sdkPackage.runtimeVersion -cne '10.0.10' -or
    [long]$locked.sdkPackage.archiveLength -ne 297545270L -or
    [string]$locked.sdkPackage.archiveSha512 -cne '7d170ed75fa9af34c00646621d92011dbd71943952e2787cd15df9be78e6452b55dadef34d7eff77b802e6af4959e071a55855ac649afeac70901c3a2a258716') {
    throw 'The production Roslyn provenance pins drifted.'
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesRoslynProvisioningFake-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    $portableRoot = Join-Path $smokeRoot 'portable'
    $installRoot = Join-Path $smokeRoot 'install'
    $serverSource = Join-Path $smokeRoot 'server-source'
    $payloadSource = Join-Path $serverSource 'content\LanguageServer\win-x64'
    $sdkSource = Join-Path $smokeRoot 'sdk-source'
    New-Item -ItemType Directory -Path $portableRoot, $installRoot, $payloadSource | Out-Null
    New-Item -ItemType Directory -Path `
        (Join-Path $sdkSource 'host\fxr\10.0.10'), `
        (Join-Path $sdkSource 'shared\Microsoft.NETCore.App\10.0.10'), `
        (Join-Path $sdkSource 'sdk\10.0.302'), `
        (Join-Path $sdkSource 'packs\Microsoft.NETCore.App.Ref\10.0.10\ref\net10.0') | Out-Null

    [IO.File]::WriteAllText((Join-Path $serverSource '.signature.p7s'), 'fake signed-package marker', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $serverSource 'Microsoft.CodeAnalysis.LanguageServer.win-x64.nuspec'), '<package>fake</package>', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $payloadSource 'Microsoft.CodeAnalysis.LanguageServer.exe'), 'fake roslyn executable bytes', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $payloadSource 'Microsoft.CodeAnalysis.LanguageServer.dll'), 'fake managed payload', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $payloadSource 'Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json'), '{"runtimeOptions":{"rollForward":"Major"}}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'dotnet.exe'), 'fake dotnet host', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'dnx.cmd'), 'fake dnx shim', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'host\fxr\10.0.10\hostfxr.dll'), 'fake hostfxr', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'shared\Microsoft.NETCore.App\10.0.10\System.Private.CoreLib.dll'), 'fake corelib', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'sdk\10.0.302\MSBuild.dll'), 'fake MSBuild', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'packs\Microsoft.NETCore.App.Ref\10.0.10\ref\net10.0\System.Runtime.dll'), 'fake reference assembly', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'LICENSE.txt'), 'fake SDK license', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sdkSource 'ThirdPartyNotices.txt'), 'fake SDK notices', [Text.UTF8Encoding]::new($false))

    $serverArchive = Join-Path $smokeRoot 'fake-server.nupkg'
    $sdkArchive = Join-Path $smokeRoot 'fake-sdk.zip'
    New-NormalizedZip -SourceRoot $serverSource -Destination $serverArchive
    New-NormalizedZip -SourceRoot $sdkSource -Destination $sdkArchive
    $inventory = Get-Inventory -Root $payloadSource
    $executableHash = Get-Digest -Path (Join-Path $payloadSource 'Microsoft.CodeAnalysis.LanguageServer.exe') -Algorithm SHA256

    $portableInstaller = Join-Path $portableRoot 'Install-RoslynLanguageServer.ps1'
    $portableLock = Join-Path $portableRoot 'roslyn-language-server.lock.json'
    Copy-Item -LiteralPath $installer -Destination $portableInstaller
    $baseLock = New-FakeLock -ServerArchive $serverArchive -Inventory $inventory -ExecutableHash $executableHash -SdkArchive $sdkArchive
    Write-JsonNoBom -Path $portableLock -Value $baseLock

    & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $serverArchive -SdkArchivePath $sdkArchive
    $target = Join-Path $installRoot 'developer-services\roslyn'
    $receiptPath = Join-Path $target 'provider.json'
    $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json
    $receiptFields = @($receipt.PSObject.Properties.Name)
    if ($receiptFields.Count -ne 2 -or $receiptFields[0] -cne 'packageVersion' -or $receiptFields[1] -cne 'executableSha256' -or
        [string]$receipt.packageVersion -cne '0.0.0-fake.1' -or [string]$receipt.executableSha256 -cne $executableHash) {
        throw 'The fake install did not write the exact host receipt contract.'
    }
    $expectedFiles = @(
        'Microsoft.CodeAnalysis.LanguageServer.dll',
        'Microsoft.CodeAnalysis.LanguageServer.exe',
        'Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json',
        'dotnet/dnx.cmd',
        'dotnet/dotnet.exe',
        'dotnet/host/fxr/10.0.10/hostfxr.dll',
        'dotnet/LICENSE.txt',
        'dotnet/packs/Microsoft.NETCore.App.Ref/10.0.10/ref/net10.0/System.Runtime.dll',
        'dotnet/sdk/10.0.302/MSBuild.dll',
        'dotnet/shared/Microsoft.NETCore.App/10.0.10/System.Private.CoreLib.dll',
        'dotnet/ThirdPartyNotices.txt',
        'provider.json')
    $targetPrefix = [IO.Path]::GetFullPath($target).TrimEnd('\') + '\'
    $actualFiles = [string[]]@(Get-ChildItem -LiteralPath $target -File -Force -Recurse | ForEach-Object {
        $_.FullName.Substring($targetPrefix.Length).Replace('\', '/')
    })
    [Array]::Sort($actualFiles, [StringComparer]::Ordinal)
    $orderedExpected = [string[]]$expectedFiles
    [Array]::Sort($orderedExpected, [StringComparer]::Ordinal)
    if (($actualFiles -join "`n") -cne ($orderedExpected -join "`n")) { throw 'The fake install contains missing or extra files.' }

    [IO.File]::WriteAllText((Join-Path $target 'rogue.txt'), 'must be removed', [Text.UTF8Encoding]::new($false))
    Write-JsonNoBom -Path $portableLock -Value $baseLock
    & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $serverArchive -SdkArchivePath $sdkArchive
    if (Test-Path -LiteralPath (Join-Path $target 'rogue.txt')) { throw 'Repeat provisioning preserved an extra target file.' }

    $installedExe = Join-Path $target 'Microsoft.CodeAnalysis.LanguageServer.exe'
    $installedHash = Get-Digest -Path $installedExe -Algorithm SHA256
    $corruptArchive = Join-Path $smokeRoot 'corrupt-server.nupkg'
    Copy-Item -LiteralPath $serverArchive -Destination $corruptArchive
    $stream = [IO.File]::Open($corruptArchive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $stream.Position = 16; $value = $stream.ReadByte(); $stream.Position = 16; $stream.WriteByte($value -bxor 0x5A) }
    finally { $stream.Dispose() }
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $corruptArchive -SdkArchivePath $sdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'A corrupt archive was accepted or changed the existing installation.'
    }

    $corruptSdkArchive = Join-Path $smokeRoot 'corrupt-sdk.zip'
    Copy-Item -LiteralPath $sdkArchive -Destination $corruptSdkArchive
    $stream = [IO.File]::Open($corruptSdkArchive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $stream.Position = 16; $value = $stream.ReadByte(); $stream.Position = 16; $stream.WriteByte($value -bxor 0x5A) }
    finally { $stream.Dispose() }
    Write-JsonNoBom -Path $portableLock -Value $baseLock
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $serverArchive -SdkArchivePath $corruptSdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'A corrupt SDK archive was accepted or changed the existing installation.'
    }

    $missingSdkSource = Join-Path $smokeRoot 'missing-sdk-source'
    Copy-Item -LiteralPath $sdkSource -Destination $missingSdkSource -Recurse
    Remove-Item -LiteralPath (Join-Path $missingSdkSource 'sdk\10.0.302\MSBuild.dll') -Force
    $missingSdkArchive = Join-Path $smokeRoot 'missing-sdk.zip'
    New-NormalizedZip -SourceRoot $missingSdkSource -Destination $missingSdkArchive
    $missingSdkLock = New-FakeLock -ServerArchive $serverArchive -Inventory $inventory -ExecutableHash $executableHash -SdkArchive $missingSdkArchive
    Write-JsonNoBom -Path $portableLock -Value $missingSdkLock
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $serverArchive -SdkArchivePath $missingSdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'An SDK archive without MSBuild was accepted or changed the existing installation.'
    }

    $extraSdkSource = Join-Path $smokeRoot 'extra-sdk-source'
    Copy-Item -LiteralPath $sdkSource -Destination $extraSdkSource -Recurse
    New-Item -ItemType Directory -Path (Join-Path $extraSdkSource 'unexpected-root') | Out-Null
    [IO.File]::WriteAllText((Join-Path $extraSdkSource 'unexpected-root\payload.bin'), 'extra', [Text.UTF8Encoding]::new($false))
    $extraSdkArchive = Join-Path $smokeRoot 'extra-sdk.zip'
    New-NormalizedZip -SourceRoot $extraSdkSource -Destination $extraSdkArchive
    $extraSdkLock = New-FakeLock -ServerArchive $serverArchive -Inventory $inventory -ExecutableHash $executableHash -SdkArchive $extraSdkArchive
    Write-JsonNoBom -Path $portableLock -Value $extraSdkLock
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $serverArchive -SdkArchivePath $extraSdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'An SDK archive with an unexpected top-level entry was accepted or changed the existing installation.'
    }

    $extraSource = Join-Path $smokeRoot 'extra-source'
    Copy-Item -LiteralPath $serverSource -Destination $extraSource -Recurse
    [IO.File]::WriteAllText((Join-Path $extraSource 'content\LanguageServer\win-x64\unexpected.dll'), 'extra', [Text.UTF8Encoding]::new($false))
    $extraArchive = Join-Path $smokeRoot 'extra-server.nupkg'
    New-NormalizedZip -SourceRoot $extraSource -Destination $extraArchive
    $extraLock = New-FakeLock -ServerArchive $extraArchive -Inventory $inventory -ExecutableHash $executableHash -SdkArchive $sdkArchive
    Write-JsonNoBom -Path $portableLock -Value $extraLock
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $extraArchive -SdkArchivePath $sdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'An archive with an extra payload file was accepted or changed the existing installation.'
    }

    $linkArchive = Join-Path $smokeRoot 'link-server.nupkg'
    Copy-Item -LiteralPath $serverArchive -Destination $linkArchive
    $zip = [IO.Compression.ZipFile]::Open($linkArchive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $linkEntry = $zip.GetEntry('content/LanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.dll')
        $linkEntry.ExternalAttributes = (0xA1FF -shl 16)
    }
    finally { $zip.Dispose() }
    $linkLock = New-FakeLock -ServerArchive $linkArchive -Inventory $inventory -ExecutableHash $executableHash -SdkArchive $sdkArchive
    Write-JsonNoBom -Path $portableLock -Value $linkLock
    $rejected = $false
    try { & $portableInstaller -InstallRoot $installRoot -ServerArchivePath $linkArchive -SdkArchivePath $sdkArchive }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Digest -Path $installedExe -Algorithm SHA256) -cne $installedHash) {
        throw 'A link-bearing archive was accepted or changed the existing installation.'
    }

    Write-Host 'Roslyn language-server fake provisioning smoke passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $smokeRoot -PathType Container) {
        $resolvedSmoke = (Resolve-Path -LiteralPath $smokeRoot).Path
        $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\HermesRoslynProvisioningFake-'
        if (-not $resolvedSmoke.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected smoke directory: $resolvedSmoke"
        }
        $nodes = @(Get-Item -LiteralPath $resolvedSmoke -Force) + @(Get-ChildItem -LiteralPath $resolvedSmoke -Force -Recurse)
        if (@($nodes | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
            throw "Refusing to remove a reparse-bearing smoke directory: $resolvedSmoke"
        }
        Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
    }
}
