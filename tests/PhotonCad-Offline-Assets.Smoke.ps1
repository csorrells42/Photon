[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Invoke-HelperProcess {
    param(
        [Parameter(Mandatory = $true)][string]$PowerShellPath,
        [Parameter(Mandatory = $true)][string]$HelperPath,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [switch]$ExpectFailure
    )

    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $PowerShellPath -NoProfile -ExecutionPolicy Bypass -File $HelperPath -InstallRoot $InstallRoot -VerifyOnly 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    if ($ExpectFailure) {
        Assert-True ($exitCode -ne 0) 'tampered or reparse-point input must fail closed'
    }
    elseif ($exitCode -ne 0) {
        throw (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

$layoutRoot = Split-Path -Parent $PSScriptRoot
$helper = @(
    (Join-Path $layoutRoot 'Install-PhotonCadRuntime.ps1'),
    (Join-Path $layoutRoot 'remote-install\Install-PhotonCadRuntime.ps1')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $helper) { throw 'Install-PhotonCadRuntime.ps1 was not found.' }
$helper = [System.IO.Path]::GetFullPath([string]$helper)
$helperRoot = Split-Path -Parent $helper
$lockPath = Join-Path $helperRoot 'photon-cad-assets.lock.json'
$assetSource = @(
    (Join-Path $helperRoot 'payloads\photon-cad'),
    (Join-Path (Split-Path -Parent $helperRoot) 'runtime-assets\photon-cad')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
if (-not $assetSource) { throw 'Canonical Photon CAD smoke assets were not found.' }
$assetSource = [System.IO.Path]::GetFullPath([string]$assetSource)

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$parseErrors) | Out-Null
Assert-True ($parseErrors.Count -eq 0) 'Photon CAD installer must parse in Windows PowerShell'

$helperText = Get-Content -LiteralPath $helper -Raw
foreach ($required in @(
    "'load', '--input'",
    "'image', 'inspect', '--format', '{{.Id}}'",
    'photon_cad_loaded_image_id_mismatch',
    'LOCAL-ENGINEERING-ONLY.md',
    'payloads\photon-cad',
    'runtime-assets\photon-cad'
)) {
    Assert-True ($helperText.Contains($required)) "installer is missing required exact gate: $required"
}
foreach ($forbidden in @('docker pull', 'docker run', 'Invoke-Expression', 'iex ', 'Start-Process')) {
    Assert-True (-not $helperText.Contains($forbidden)) "installer contains a forbidden command surface: $forbidden"
}

$lockItem = Get-Item -LiteralPath $lockPath -Force
Assert-True (-not ($lockItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'asset lock must be a regular non-reparse file'
Assert-True ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash -ceq '3803287144FD059C422A277CD522C08D24C9D55E7C2EB6909CD197AF68147A6A') 'asset lock hash must be exact'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True ([string]$lock.distribution -ceq 'local-engineering-only') 'lock must preserve the compliance boundary'
Assert-True ([string]$lock.receiptSha256 -ceq '73774bd9e932624775f281c46377267945f0934b6ae6c9d6d83e13d0702b3687') 'receipt hash must be exact'
Assert-True ([string]$lock.policySha256 -ceq '1e09f5a3d43eea127ffa8072620a308b91c8028c44be735907d88be22e2465a2') 'policy hash must be exact'
Assert-True (@($lock.files).Count -eq 5) 'lock must allow exactly five files'
Assert-True (@($lock.images).Count -eq 2) 'lock must allow exactly two images'
Assert-True ([string]$lock.images[0].imageId -ceq 'sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703') 'geometry image ID must be exact'
Assert-True ([string]$lock.images[1].imageId -ceq 'sha256:8b7fe2eb30f044aaa4e6251f46029f5a1fb221c49b0f79c95d4df70249c0c58b') 'assembly image ID must be exact'

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('PhotonCadOfflineAssetsSmoke-' + [Guid]::NewGuid().ToString('N'))
$junctionPath = $null
$powershell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$oldPath = $env:PATH
$oldFakeLog = $env:PHOTON_CAD_FAKE_DOCKER_LOG
try {
    $fakeBin = Join-Path $temporaryRoot 'fake-bin'
    New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
    $fakeLog = Join-Path $temporaryRoot 'docker-calls.log'
    [IO.File]::WriteAllText($fakeLog, '', [Text.UTF8Encoding]::new($false))
    $fakeDocker = "@echo off`r`necho invoked>>`"%PHOTON_CAD_FAKE_DOCKER_LOG%`"`r`nexit /b 91`r`n"
    [IO.File]::WriteAllText((Join-Path $fakeBin 'docker.cmd'), $fakeDocker, [Text.Encoding]::ASCII)
    $env:PATH = $fakeBin + ';' + $oldPath
    $env:PHOTON_CAD_FAKE_DOCKER_LOG = $fakeLog

    $unusedInstallRoot = Join-Path $temporaryRoot 'must-not-be-created'
    Invoke-HelperProcess -PowerShellPath $powershell -HelperPath $helper -InstallRoot $unusedInstallRoot | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $unusedInstallRoot)) 'VerifyOnly must not create the install root'
    Assert-True ((Get-Item -LiteralPath $fakeLog).Length -eq 0) 'VerifyOnly must not invoke Docker'

    $tamperRoot = Join-Path $temporaryRoot 'tampered'
    New-Item -ItemType Directory -Path $tamperRoot -Force | Out-Null
    Copy-Item -LiteralPath $helper -Destination (Join-Path $tamperRoot 'Install-PhotonCadRuntime.ps1')
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $tamperRoot 'photon-cad-assets.lock.json')
    Add-Content -LiteralPath (Join-Path $tamperRoot 'photon-cad-assets.lock.json') -Value ' ' -Encoding ASCII
    Invoke-HelperProcess -PowerShellPath $powershell -HelperPath (Join-Path $tamperRoot 'Install-PhotonCadRuntime.ps1') -InstallRoot $unusedInstallRoot -ExpectFailure | Out-Null
    Assert-True ((Get-Item -LiteralPath $fakeLog).Length -eq 0) 'tampered lock must fail before Docker'

    $junctionRoot = Join-Path $temporaryRoot 'junction-case'
    $junctionHelperRoot = Join-Path $junctionRoot 'remote-install'
    $junctionPayloadRoot = Join-Path $junctionHelperRoot 'payloads'
    New-Item -ItemType Directory -Path $junctionHelperRoot, $junctionPayloadRoot -Force | Out-Null
    Copy-Item -LiteralPath $helper -Destination (Join-Path $junctionHelperRoot 'Install-PhotonCadRuntime.ps1')
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $junctionHelperRoot 'photon-cad-assets.lock.json')
    $junctionPath = Join-Path $junctionPayloadRoot 'photon-cad'
    New-Item -ItemType Junction -Path $junctionPath -Target $assetSource -ErrorAction Stop | Out-Null
    Invoke-HelperProcess -PowerShellPath $powershell -HelperPath (Join-Path $junctionHelperRoot 'Install-PhotonCadRuntime.ps1') -InstallRoot $unusedInstallRoot -ExpectFailure | Out-Null
    Assert-True ((Get-Item -LiteralPath $fakeLog).Length -eq 0) 'reparse-point source must fail before Docker'

    $installScript = Join-Path $helperRoot 'Install-Hermes.ps1'
    if (Test-Path -LiteralPath $installScript -PathType Leaf) {
        $installText = Get-Content -LiteralPath $installScript -Raw
        Assert-True ($installText.Contains('$_.Name -notin @(''data'', ''logs'', ''payloads'')')) 'main installer must exclude payloads from its broad copy'
        Assert-True ($installText.Contains('$LoadPhotonCadImages -and -not $InstallPhotonCadAssets')) 'image load must require explicit asset installation'
    }
    $buildScript = Join-Path $helperRoot 'Build-Install-Zip.ps1'
    if (Test-Path -LiteralPath $buildScript -PathType Leaf) {
        $buildText = Get-Content -LiteralPath $buildScript -Raw
        Assert-True ($buildText.Contains('[switch]$IncludeLocalEngineeringPhotonCadAssets')) 'CAD archives must require an explicit local-engineering build switch'
        Assert-True ($buildText.Contains('if ($IncludeLocalEngineeringPhotonCadAssets)')) 'normal portable builds must omit local-engineering CAD archives'
        Assert-True ($buildText.Contains('Hermes-Remote-Install.LOCAL-ENGINEERING-ONLY.zip')) 'local CAD bundles must have a distinct default output identity'
        Assert-True ($buildText.Contains("EndsWith('.LOCAL-ENGINEERING-ONLY.zip'")) 'local CAD bundles must reject a public-looking output filename'
        foreach ($entry in @(
            'Install-PhotonCadRuntime.ps1', 'photon-cad-assets.lock.json',
            'payloads/photon-cad/bundle-receipt.json',
            'payloads/photon-cad/LOCAL-ENGINEERING-ONLY.md',
            'payloads/photon-cad/photon-cad-geometry.tar',
            'payloads/photon-cad/photon-cad-assembly.tar',
            'payloads/photon-cad/runtime-policy.json'
        )) {
            Assert-True ($buildText.Contains($entry)) "portable build is missing exact CAD entry: $entry"
        }
    }
}
finally {
    $env:PATH = $oldPath
    if ($null -eq $oldFakeLog) { Remove-Item Env:PHOTON_CAD_FAKE_DOCKER_LOG -ErrorAction SilentlyContinue }
    else { $env:PHOTON_CAD_FAKE_DOCKER_LOG = $oldFakeLog }
    if ($junctionPath -and (Test-Path -LiteralPath $junctionPath)) {
        $junctionItem = Get-Item -LiteralPath $junctionPath -Force
        if ($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            [IO.Directory]::Delete($junctionPath, $false)
        }
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $resolved = [System.IO.Path]::GetFullPath($temporaryRoot)
        $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $item = Get-Item -LiteralPath $resolved -Force
        $reparse = @(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved) -like 'PhotonCadOfflineAssetsSmoke-*' -and
            -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $reparse.Count -eq 0) {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
        }
    }
}

Write-Host 'Photon CAD offline asset fake smoke passed; no install or Docker load was executed.' -ForegroundColor Green
