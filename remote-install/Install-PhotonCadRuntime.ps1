[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'HermesPortable'),
    [switch]$VerifyOnly,
    [switch]$LoadImages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExpectedLockSha256 = '3803287144fd059c422a277cd522c08d24c9d55e7c2eb6909cd197af68147a6a'
$script:ExpectedReceiptSha256 = '73774bd9e932624775f281c46377267945f0934b6ae6c9d6d83e13d0702b3687'
$script:ExpectedPolicySha256 = '1e09f5a3d43eea127ffa8072620a308b91c8028c44be735907d88be22e2465a2'
$script:ExpectedFiles = @(
    [ordered]@{ name = 'bundle-receipt.json'; length = 3047L; sha256 = $script:ExpectedReceiptSha256 },
    [ordered]@{ name = 'LOCAL-ENGINEERING-ONLY.md'; length = 577L; sha256 = 'dcb4b32f4cec203835e2ea16c82788235a8f1505e05e2c22e193680703fa0939' },
    [ordered]@{ name = 'photon-cad-geometry.tar'; length = 466348032L; sha256 = 'c09e75b12ec594a0bade4d9b4821c1f528d7e537b040c3a3f1a7562872801efa' },
    [ordered]@{ name = 'photon-cad-assembly.tar'; length = 526439936L; sha256 = 'ce0fadf9783c58c1d6f34fa02c6e09a215681b85d7e47990abbb8ad1078c0bce' },
    [ordered]@{ name = 'runtime-policy.json'; length = 1146L; sha256 = $script:ExpectedPolicySha256 }
)
$script:ExpectedImages = @(
    [ordered]@{
        role = 'geometry'
        tag = 'photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb'
        imageId = 'sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703'
        archive = 'photon-cad-geometry.tar'
        archiveSha256 = 'c09e75b12ec594a0bade4d9b4821c1f528d7e537b040c3a3f1a7562872801efa'
        archiveByteLength = 466348032L
    },
    [ordered]@{
        role = 'assembly'
        tag = 'photon-cad-assembly:0.1.1-partcad-0.7.158-b77c2c08-p1066a9ff'
        imageId = 'sha256:8b7fe2eb30f044aaa4e6251f46029f5a1fb221c49b0f79c95d4df70249c0c58b'
        archive = 'photon-cad-assembly.tar'
        archiveSha256 = 'ce0fadf9783c58c1d6f34fa02c6e09a215681b85d7e47990abbb8ad1078c0bce'
        archiveByteLength = 526439936L
    }
)

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-RegularFile {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (-not ($item -is [System.IO.FileInfo])) { throw "expected_regular_file:$LiteralPath" }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "reparse_path_rejected:$LiteralPath"
    }
}

function Assert-PlainDirectory {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (-not ($item -is [System.IO.DirectoryInfo])) { throw "expected_directory:$LiteralPath" }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "reparse_path_rejected:$LiteralPath"
    }
}

function Assert-ExactLock {
    param([Parameter(Mandatory = $true)]$Lock)

    if ([string]$Lock.schemaId -cne 'photon.cad.offline-assets.lock/v1' -or
        [string]$Lock.distribution -cne 'local-engineering-only' -or
        [string]$Lock.receiptSha256 -cne $script:ExpectedReceiptSha256 -or
        [string]$Lock.policySha256 -cne $script:ExpectedPolicySha256) {
        throw 'photon_cad_lock_header_mismatch'
    }

    $files = @($Lock.files)
    if ($files.Count -ne $script:ExpectedFiles.Count) { throw 'photon_cad_lock_file_count_mismatch' }
    for ($index = 0; $index -lt $script:ExpectedFiles.Count; $index++) {
        $actual = $files[$index]
        $expected = $script:ExpectedFiles[$index]
        if ([string]$actual.name -cne [string]$expected.name -or
            [long]$actual.length -ne [long]$expected.length -or
            [string]$actual.sha256 -cne [string]$expected.sha256) {
            throw "photon_cad_lock_file_mismatch:$index"
        }
    }

    $images = @($Lock.images)
    if ($images.Count -ne $script:ExpectedImages.Count) { throw 'photon_cad_lock_image_count_mismatch' }
    for ($index = 0; $index -lt $script:ExpectedImages.Count; $index++) {
        $actual = $images[$index]
        $expected = $script:ExpectedImages[$index]
        foreach ($field in @('role', 'tag', 'imageId', 'archive', 'archiveSha256')) {
            if ([string]$actual.$field -cne [string]$expected[$field]) {
                throw "photon_cad_lock_image_mismatch:$($index):$field"
            }
        }
        if ([long]$actual.archiveByteLength -ne [long]$expected.archiveByteLength) {
            throw "photon_cad_lock_image_length_mismatch:$index"
        }
    }
}

function Read-ExactLock {
    $lockPath = Join-Path $PSScriptRoot 'photon-cad-assets.lock.json'
    Assert-RegularFile -LiteralPath $lockPath
    if ((Get-Sha256Hex -LiteralPath $lockPath) -cne $script:ExpectedLockSha256) {
        throw 'photon_cad_lock_hash_mismatch'
    }
    try { $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'photon_cad_lock_invalid_json' }
    Assert-ExactLock -Lock $lock
    return $lock
}

function Resolve-AssetSource {
    $candidates = @(
        (Join-Path $PSScriptRoot 'payloads\photon-cad'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime-assets\photon-cad')
    )
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        Assert-PlainDirectory -LiteralPath $candidate
        return [System.IO.Path]::GetFullPath($candidate)
    }
    throw 'photon_cad_asset_source_not_found'
}

function Assert-ExactReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)]$Lock
    )

    try { $receipt = Get-Content -LiteralPath $LiteralPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'photon_cad_receipt_invalid_json' }
    if ([string]$receipt.schema -cne 'photon.cad.bundle-receipt/v2' -or
        [string]$receipt.policySha256 -cne $script:ExpectedPolicySha256) {
        throw 'photon_cad_receipt_header_mismatch'
    }
    $bundles = @($receipt.bundles)
    if ($bundles.Count -ne $script:ExpectedImages.Count) { throw 'photon_cad_receipt_bundle_count_mismatch' }
    for ($index = 0; $index -lt $script:ExpectedImages.Count; $index++) {
        $actual = $bundles[$index]
        $expected = $script:ExpectedImages[$index]
        if ([string]$actual.role -cne [string]$expected.role -or
            [string]$actual.tag -cne [string]$expected.tag -or
            [string]$actual.imageId -cne [string]$expected.imageId -or
            [string]$actual.platform -cne 'linux/amd64' -or
            [string]$actual.archive -cne [string]$expected.archive -or
            [string]$actual.archiveSha256 -cne [string]$expected.archiveSha256 -or
            [long]$actual.archiveByteLength -ne [long]$expected.archiveByteLength) {
            throw "photon_cad_receipt_bundle_mismatch:$index"
        }
    }
    if ([string]$Lock.receiptSha256 -cne $script:ExpectedReceiptSha256) {
        throw 'photon_cad_receipt_lock_binding_mismatch'
    }
}

function Assert-ExactPolicy {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    try { $policy = Get-Content -LiteralPath $LiteralPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'photon_cad_policy_invalid_json' }
    if ([string]$policy.schema -cne 'photon.cad.container-policy/v1' -or
        [string]$policy.platform -cne 'linux/amd64' -or
        [string]$policy.runtimeUser -cne '65532:65532' -or
        [string]$policy.network -cne 'none' -or
        $policy.readOnlyRoot -ne $true -or
        [string]$policy.geometry.tag -cne [string]$script:ExpectedImages[0].tag -or
        [string]$policy.assembly.tag -cne [string]$script:ExpectedImages[1].tag) {
        throw 'photon_cad_policy_binding_mismatch'
    }
}

function Assert-ExactAssetDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)]$Lock
    )

    Assert-PlainDirectory -LiteralPath $LiteralPath
    $children = @(Get-ChildItem -LiteralPath $LiteralPath -Force)
    if ($children.Count -ne $script:ExpectedFiles.Count) { throw 'photon_cad_asset_entry_count_mismatch' }
    $expectedNames = @($script:ExpectedFiles | ForEach-Object { [string]$_.name })
    foreach ($child in $children) {
        if ($expectedNames -cnotcontains $child.Name) { throw "photon_cad_unapproved_asset_entry:$($child.Name)" }
    }
    foreach ($expected in $script:ExpectedFiles) {
        $path = Join-Path $LiteralPath ([string]$expected.name)
        Assert-RegularFile -LiteralPath $path
        $item = Get-Item -LiteralPath $path -Force
        if ([long]$item.Length -ne [long]$expected.length) { throw "photon_cad_asset_length_mismatch:$($expected.name)" }
        if ((Get-Sha256Hex -LiteralPath $path) -cne [string]$expected.sha256) { throw "photon_cad_asset_hash_mismatch:$($expected.name)" }
    }
    Assert-ExactReceipt -LiteralPath (Join-Path $LiteralPath 'bundle-receipt.json') -Lock $Lock
    Assert-ExactPolicy -LiteralPath (Join-Path $LiteralPath 'runtime-policy.json')
}

function Invoke-ExactDocker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $docker = Get-Command docker -CommandType Application -ErrorAction Stop
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $docker.Source @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $output = (($lines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0) { throw "photon_cad_docker_command_failed:$($Arguments[0]):$exitCode" }
    return $output
}

function Install-ExactAssets {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)]$Lock
    )

    $resolvedInstallRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
    Assert-PlainDirectory -LiteralPath $resolvedInstallRoot
    $runtimeAssetsRoot = Join-Path $resolvedInstallRoot 'runtime-assets'
    if (-not (Test-Path -LiteralPath $runtimeAssetsRoot)) {
        New-Item -ItemType Directory -Path $runtimeAssetsRoot -ErrorAction Stop | Out-Null
    }
    Assert-PlainDirectory -LiteralPath $runtimeAssetsRoot
    $runtimeAssetsRoot = [System.IO.Path]::GetFullPath($runtimeAssetsRoot)
    $destination = [System.IO.Path]::GetFullPath((Join-Path $runtimeAssetsRoot 'photon-cad'))
    if (-not $destination.StartsWith($runtimeAssetsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'photon_cad_install_path_escape'
    }
    if (Test-Path -LiteralPath $destination) {
        Assert-ExactAssetDirectory -LiteralPath $destination -Lock $Lock
        return $destination
    }

    $staging = Join-Path $runtimeAssetsRoot ('.photon-cad.stage-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
        Assert-PlainDirectory -LiteralPath $staging
        foreach ($file in $script:ExpectedFiles) {
            Copy-Item -LiteralPath (Join-Path $SourcePath ([string]$file.name)) -Destination (Join-Path $staging ([string]$file.name)) -ErrorAction Stop
        }
        Assert-ExactAssetDirectory -LiteralPath $staging -Lock $Lock
        Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
        Assert-ExactAssetDirectory -LiteralPath $destination -Lock $Lock
        return $destination
    }
    finally {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            $resolvedStaging = [System.IO.Path]::GetFullPath($staging)
            $prefix = $runtimeAssetsRoot.TrimEnd('\') + '\.photon-cad.stage-'
            $item = Get-Item -LiteralPath $resolvedStaging -Force
            if ($resolvedStaging.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
                -not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                Remove-Item -LiteralPath $resolvedStaging -Recurse -Force -ErrorAction Stop
            }
        }
    }
}

function Import-ExactImages {
    param(
        [Parameter(Mandatory = $true)][string]$AssetPath,
        [Parameter(Mandatory = $true)]$Lock
    )

    Assert-ExactAssetDirectory -LiteralPath $AssetPath -Lock $Lock
    foreach ($image in $script:ExpectedImages) {
        $archivePath = Join-Path $AssetPath ([string]$image.archive)
        Invoke-ExactDocker -Arguments @('load', '--input', $archivePath) | Out-Null
        $loadedId = (Invoke-ExactDocker -Arguments @('image', 'inspect', '--format', '{{.Id}}', [string]$image.tag)).Trim()
        if ($loadedId -cne [string]$image.imageId) { throw "photon_cad_loaded_image_id_mismatch:$($image.role)" }
    }
}

if ($VerifyOnly -and $LoadImages) { throw 'VerifyOnly cannot be combined with LoadImages.' }
$lock = Read-ExactLock
$assetSource = Resolve-AssetSource
Assert-ExactAssetDirectory -LiteralPath $assetSource -Lock $lock
if ($VerifyOnly) {
    Write-Host 'Verified exact local-engineering-only Photon CAD assets. No files were copied and Docker was not invoked.' -ForegroundColor Green
    return
}

$installedAssets = Install-ExactAssets -SourcePath $assetSource -DestinationRoot $InstallRoot -Lock $lock
Write-Host "Installed exact Photon CAD assets to $installedAssets" -ForegroundColor Green
if ($LoadImages) {
    Import-ExactImages -AssetPath $installedAssets -Lock $lock
    Write-Host 'Loaded and verified the two receipt-bound Photon CAD images.' -ForegroundColor Green
}
else {
    Write-Host 'Docker images were not loaded. Use -LoadImages for an explicit exact-image import.' -ForegroundColor DarkGray
}
Write-Host 'These assets remain local engineering only; public redistribution is not approved.' -ForegroundColor Yellow
