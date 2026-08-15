[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA 'HermesPortable'),
    [switch]$SkipDesktopLauncher,
    [switch]$VerifyBundleOnly,
    [switch]$InstallPhotonCadAssets,
    [switch]$LoadPhotonCadImages
)

$ErrorActionPreference = 'Stop'
$approvedHermesImage = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'
$sourceRoot = $PSScriptRoot

function Test-PortableBundle {
    $manifestPath = Join-Path $sourceRoot 'bundle.manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $sourceRoot 'Build-Install-Zip.ps1') -PathType Leaf) {
            Write-Warning 'Running from the development installer folder; the generated portable-bundle manifest is not present.'
            return
        }
        throw 'The portable bundle manifest is missing. Extract a fresh Hermes-Remote-Install.zip and try again.'
    }

    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    if ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The portable bundle manifest is a reparse point. Extract a fresh installer bundle.'
    }
    try { $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json }
    catch { throw 'The portable bundle manifest is not valid JSON. Extract a fresh installer bundle.' }
    if ($manifest.protocolVersion -ne 1 -or [string]$manifest.algorithm -cne 'SHA-256') {
        throw 'The portable bundle manifest uses an unsupported format.'
    }

    $files = @($manifest.files)
    if ($files.Count -lt 10 -or $files.Count -gt 5000) {
        throw "The portable bundle manifest has an invalid file count: $($files.Count)."
    }
    $resolvedSource = (Resolve-Path -LiteralPath $sourceRoot).Path
    $sourcePrefix = $resolvedSource.TrimEnd('\') + '\'
    $seen = @{}

    foreach ($entry in $files) {
        $relative = [string]$entry.path
        $expectedHash = [string]$entry.sha256
        $segments = @($relative -split '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Length -gt 1024 -or
            $relative.Contains('\') -or [IO.Path]::IsPathRooted($relative) -or
            @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -gt 0) {
            throw "The portable bundle manifest contains an unsafe path: $relative"
        }
        if ($seen.ContainsKey($relative)) { throw "The portable bundle manifest repeats a path: $relative" }
        $seen[$relative] = $true
        if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') { throw "The portable bundle manifest has an invalid hash for: $relative" }

        $target = [IO.Path]::GetFullPath((Join-Path $resolvedSource $relative.Replace('/', '\')))
        if (-not $target.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The portable bundle manifest path escapes the extracted folder: $relative"
        }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "The portable bundle is missing: $relative" }
        $item = Get-Item -LiteralPath $target -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "The portable bundle contains a reparse-point file: $relative" }
        if ([long]$item.Length -ne [long]$entry.length) { throw "The portable bundle file size does not match its manifest: $relative" }
        $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($actualHash -cne $expectedHash.ToUpperInvariant()) { throw "The portable bundle file hash does not match its manifest: $relative" }
    }

    $required = @(
        'Install Hermes.cmd', 'Launch Hermes.cmd', 'Check Hermes.cmd', 'Shutdown Hermes.cmd', 'Update Hermes.cmd',
        'Install-Hermes.ps1', 'Install-NetCoreDbg.ps1', 'Install-RoslynLanguageServer.ps1', 'roslyn-language-server.lock.json', 'Install-ArduinoToolchain.ps1', 'arduino-toolchain.lock.json', 'Install-OpenSshToolchain.ps1', 'Install-PhotonModels.ps1', 'Install-PhotonCadRuntime.ps1', 'photon-cad-assets.lock.json', 'Launch-Hermes.ps1', 'Test-Hermes.ps1', 'Shutdown-Hermes.ps1', 'Update-Hermes.ps1', 'Photon-McpGateway.ps1',
        'mcp-profiles/profiles.lock.json', 'mcp-profiles/photon-engineering-discovery.yaml', 'mcp-profiles/photon-relentless-repair.yaml', 'mcp-profiles/photon-web-research.yaml',
        'Invoke-HermesFrontend.ps1', 'docker-compose.yml', 'launcher.settings.json', '.vscode/tasks.json',
        'src/package-lock.json', 'client/HermesDesktop.exe', 'tools/assistant-bus/assistant-bus.exe', 'licenses/netcoredbg-LICENSE.txt',
        'toolchains/netcoredbg-win64.zip', 'toolchains/microsoft.codeanalysis.languageserver.win-x64.5.0.0-1.25277.114.nupkg', 'toolchains/dotnet-sdk-10.0.302-win-x64.zip', 'toolchains/arduino-cli_1.5.1_Windows_64bit.zip', 'runtime/Runtime.Common.ps1', 'runtime/Runtime.Generation.ps1',
        'runtime/Build-HermesRuntime.ps1', 'runtime/Verify-HermesRuntime.ps1', 'runtime/Adopt-HermesRuntime.ps1',
        'runtime/photon-models.lock.json', 'runtime/memory-vector.lock.json'
    )
    if ($InstallPhotonCadAssets) {
        $required += @(
            'payloads/photon-cad/bundle-receipt.json', 'payloads/photon-cad/LOCAL-ENGINEERING-ONLY.md',
            'payloads/photon-cad/photon-cad-geometry.tar', 'payloads/photon-cad/photon-cad-assembly.tar',
            'payloads/photon-cad/runtime-policy.json'
        )
    }
    $missingRequired = @($required | Where-Object { -not $seen.ContainsKey($_) })
    if ($missingRequired.Count) { throw "The portable bundle manifest omits required files: $($missingRequired -join ', ')" }
    Write-Host "Verified portable bundle integrity: $($files.Count) files." -ForegroundColor Green
}

function Resolve-InstallDestination {
    param([string]$Path, [string]$ResolvedSource)

    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    $item = Get-Item -LiteralPath $candidate -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The install destination must be a regular local directory: $candidate"
    }
    $resolved = $item.FullName
    if ($resolved -eq $ResolvedSource) { return $resolved }
    $children = @(Get-ChildItem -LiteralPath $resolved -Force)
    if ($children.Count -eq 0) { return $resolved }

    $recognized = $false
    $markerPath = Join-Path $resolved '.hermes-workbench-install.json'
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        try {
            $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
            $recognized = $marker.protocolVersion -eq 1 -and [string]$marker.product -ceq 'HermesWorkbench'
        }
        catch { $recognized = $false }
    }
    if (-not $recognized) {
        $legacyFiles = @('Launch-Hermes.ps1', 'docker-compose.yml', 'launcher.settings.json')
        $recognized = @($legacyFiles | Where-Object { Test-Path -LiteralPath (Join-Path $resolved $_) -PathType Leaf }).Count -eq $legacyFiles.Count
    }
    if (-not $recognized) {
        throw "The install destination is not empty and is not an existing Hermes Workbench installation: $resolved"
    }
    return $resolved
}

function Resolve-CommandPath {
    param([string]$Name, [string[]]$FallbackPaths = @())

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($path in $FallbackPaths) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $directory = Split-Path -Parent ([IO.Path]::GetFullPath($path))
        $pathEntries = @($env:PATH -split ';')
        if ($directory -notin $pathEntries) { $env:PATH = $directory + ';' + $env:PATH }
        return [IO.Path]::GetFullPath($path)
    }
    return $null
}

function Require-Command {
    param([string]$Name, [string]$WingetId, [string[]]$FallbackPaths = @())
    if (Resolve-CommandPath -Name $Name -FallbackPaths $FallbackPaths) { return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$Name is required and winget is unavailable. Install $Name, then rerun this script."
    }
    & winget install --id $WingetId --exact --accept-package-agreements --accept-source-agreements
    $wingetExitCode = $LASTEXITCODE
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (Resolve-CommandPath -Name $Name -FallbackPaths $FallbackPaths) { return }
    if ($wingetExitCode -ne 0) { throw "winget failed to install $Name ($WingetId), exit code $wingetExitCode." }
    throw "$Name was installed but its executable could not be located. Restart Windows, then rerun the installer."
}

function Test-WebView2Runtime {
    $registrations = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\*','HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\*','HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\*' -ErrorAction SilentlyContinue
    return [bool]($registrations | Where-Object { $_.name -like '*WebView2*' -and $_.pv })
}

function Ensure-WebView2Runtime {
    if (Test-WebView2Runtime) { return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'Microsoft Edge WebView2 Runtime is required and winget is unavailable.'
    }
    Write-Host 'Installing the Microsoft Edge WebView2 Runtime for HermesDesktop...' -ForegroundColor Cyan
    & winget install --id 'Microsoft.EdgeWebView2Runtime' --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0 -or -not (Test-WebView2Runtime)) {
        throw 'Microsoft Edge WebView2 Runtime installation did not complete.'
    }
}

Test-PortableBundle
if ($LoadPhotonCadImages -and -not $InstallPhotonCadAssets) {
    throw 'LoadPhotonCadImages requires the explicit InstallPhotonCadAssets switch.'
}
if ($VerifyBundleOnly) {
    Write-Host 'Portable bundle verification completed. No prerequisites were installed and no application files were copied.' -ForegroundColor Green
    return
}
Require-Command -Name docker -WingetId 'Docker.DockerDesktop' -FallbackPaths @('C:\Program Files\Docker\Docker\resources\bin\docker.exe')
Require-Command -Name uv -WingetId 'astral-sh.uv' -FallbackPaths @((Join-Path $env:USERPROFILE '.local\bin\uv.exe'), (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe'))
Require-Command -Name node -WingetId 'OpenJS.NodeJS.LTS' -FallbackPaths @('C:\Program Files\nodejs\node.exe')
$nodeExecutable = Resolve-CommandPath -Name node -FallbackPaths @('C:\Program Files\nodejs\node.exe')
if (-not $nodeExecutable) { throw 'Node.js was installed, but its executable could not be located.' }
Ensure-WebView2Runtime

if (-not (Get-Command serena -ErrorAction SilentlyContinue)) {
    & uv tool install 'serena-agent==1.6.1'
    if ($LASTEXITCODE -ne 0) { throw 'Serena installation failed.' }
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
}

$serenaCommand = Get-Command serena -ErrorAction SilentlyContinue
$serenaExecutable = if ($serenaCommand) { $serenaCommand.Source } else { $null }
if (-not $serenaExecutable) {
    $uv = Get-Command uv -ErrorAction Stop
    $toolBin = @(& $uv.Source tool dir --bin) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or -not $toolBin) { throw 'uv could not report its installed-tool directory.' }
    foreach ($name in @('serena.exe', 'serena.cmd', 'serena')) {
        $candidate = Join-Path ([string]$toolBin).Trim() $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $serenaExecutable = [IO.Path]::GetFullPath($candidate)
            break
        }
    }
}
if (-not $serenaExecutable) { throw 'Serena was installed, but its executable could not be located.' }

$resolvedSource = (Resolve-Path -LiteralPath $sourceRoot).Path
$validatedInstall = Resolve-InstallDestination -Path $InstallPath -ResolvedSource $resolvedSource
$resolvedInstallParent = [IO.Path]::GetFullPath((Split-Path -Parent $validatedInstall))
New-Item -ItemType Directory -Force -Path $resolvedInstallParent | Out-Null

if (-not (Test-Path -LiteralPath $validatedInstall)) {
    New-Item -ItemType Directory -Path $validatedInstall | Out-Null
}
$resolvedInstall = (Resolve-Path -LiteralPath $validatedInstall).Path

if ($resolvedInstall -ne $resolvedSource) {
    Get-ChildItem -Force -LiteralPath $sourceRoot | Where-Object {
        $_.Name -notin @('data', 'logs', 'payloads')
    } | Copy-Item -Destination $resolvedInstall -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $resolvedInstall 'data'), (Join-Path $resolvedInstall 'logs'), (Join-Path $resolvedInstall 'workspace') | Out-Null

if ($resolvedInstall -ne $resolvedSource) {
    $installMarkerPath = Join-Path $resolvedInstall '.hermes-workbench-install.json'
    $installedAtUtc = [DateTime]::UtcNow.ToString('o')
    if (Test-Path -LiteralPath $installMarkerPath -PathType Leaf) {
        try {
            $existingMarker = Get-Content -Raw -LiteralPath $installMarkerPath | ConvertFrom-Json
            if ([string]$existingMarker.installedAtUtc) { $installedAtUtc = [string]$existingMarker.installedAtUtc }
        }
        catch { }
    }
    $installMarker = [ordered]@{
        protocolVersion = 1
        product = 'HermesWorkbench'
        installedAtUtc = $installedAtUtc
        lastInstalledAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    [IO.File]::WriteAllText($installMarkerPath, ($installMarker | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

$launcherSettingsPath = Join-Path $resolvedInstall 'launcher.settings.json'
if (-not (Test-Path -LiteralPath $launcherSettingsPath)) { throw "Launcher settings missing: $launcherSettingsPath" }
$launcherSettings = Get-Content -Raw -LiteralPath $launcherSettingsPath | ConvertFrom-Json
$launcherSettings | Add-Member -NotePropertyName SerenaExecutable -NotePropertyValue ([IO.Path]::GetFullPath($serenaExecutable)) -Force
$launcherSettings | Add-Member -NotePropertyName NodeExecutable -NotePropertyValue ([IO.Path]::GetFullPath($nodeExecutable)) -Force
[IO.File]::WriteAllText(
    $launcherSettingsPath,
    ($launcherSettings | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false)
)

$launchScript = Join-Path $resolvedInstall 'Launch-Hermes.ps1'
if (-not (Test-Path -LiteralPath $launchScript)) { throw "Launcher missing: $launchScript" }

$frontendRoot = Join-Path $resolvedInstall 'src'
$packageLock = Join-Path $frontendRoot 'package-lock.json'
if (-not (Test-Path -LiteralPath $packageLock)) { throw "Hermes Workbench package is missing: $packageLock" }
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) {
    $standardNpm = 'C:\Program Files\nodejs\npm.cmd'
    if (Test-Path -LiteralPath $standardNpm) { $npm = Get-Item -LiteralPath $standardNpm }
}
if (-not $npm) { throw 'npm was not found after installing Node.js. Close PowerShell, reopen it, and rerun the installer.' }

$debuggerInstaller = Join-Path $resolvedInstall 'Install-NetCoreDbg.ps1'
if (-not (Test-Path -LiteralPath $debuggerInstaller -PathType Leaf)) {
    throw "The .NET debugger installer is missing: $debuggerInstaller"
}
Write-Host 'Provisioning the verified .NET debugger...' -ForegroundColor Cyan
& $debuggerInstaller -InstallRoot $resolvedInstall

$roslynInstaller = Join-Path $resolvedInstall 'Install-RoslynLanguageServer.ps1'
if (-not (Test-Path -LiteralPath $roslynInstaller -PathType Leaf)) {
    throw "The Roslyn language-server installer is missing: $roslynInstaller"
}
Write-Host 'Provisioning the verified Roslyn language server...' -ForegroundColor Cyan
& $roslynInstaller -InstallRoot $resolvedInstall

$arduinoInstaller = Join-Path $resolvedInstall 'Install-ArduinoToolchain.ps1'
if (-not (Test-Path -LiteralPath $arduinoInstaller -PathType Leaf)) {
    throw "The Arduino toolchain installer is missing: $arduinoInstaller"
}
Write-Host 'Provisioning the verified Arduino toolchain...' -ForegroundColor Cyan
& $arduinoInstaller -InstallRoot $resolvedInstall

$openSshInstaller = Join-Path $resolvedInstall 'Install-OpenSshToolchain.ps1'
if (-not (Test-Path -LiteralPath $openSshInstaller -PathType Leaf)) {
    throw "The Windows OpenSSH toolchain installer is missing: $openSshInstaller"
}
Write-Host 'Provisioning the verified Microsoft Windows OpenSSH client...' -ForegroundColor Cyan
& $openSshInstaller -InstallRoot $resolvedInstall

Write-Host 'Installing the Hermes Workbench frontend...' -ForegroundColor Cyan
Push-Location $frontendRoot
try {
    & $npm.FullName ci
    if ($LASTEXITCODE -ne 0) { throw 'Hermes Workbench dependency installation failed.' }
}
finally {
    Pop-Location
}

$dockerDesktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    if (-not (Test-Path -LiteralPath $dockerDesktop)) { throw 'Docker Desktop was not found after installation.' }
    Write-Host 'Starting Docker Desktop. This can take a minute on first launch...' -ForegroundColor Cyan
    Start-Process -FilePath $dockerDesktop -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        & docker info *> $null
    } until ($LASTEXITCODE -eq 0 -or [DateTime]::UtcNow -ge $deadline)
    if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop did not become ready within three minutes.' }
}

if ($InstallPhotonCadAssets) {
    $photonCadInstaller = Join-Path $sourceRoot 'Install-PhotonCadRuntime.ps1'
    if (-not (Test-Path -LiteralPath $photonCadInstaller -PathType Leaf)) {
        throw "The Photon CAD offline asset installer is missing: $photonCadInstaller"
    }
    Write-Host 'Installing exact local-engineering-only Photon CAD runtime assets...' -ForegroundColor Cyan
    & $photonCadInstaller -InstallRoot $resolvedInstall -LoadImages:$LoadPhotonCadImages
}

Push-Location $resolvedInstall
try {
    $env:HERMES_IMAGE_REFERENCE = $approvedHermesImage
    & docker compose pull
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the Hermes Docker image.' }

    $configPath = Join-Path $resolvedInstall 'data\config.yaml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        Write-Host ''
        Write-Host 'STEP 1 OF 2: Hermes model setup' -ForegroundColor Cyan
        Write-Host 'Follow the official prompts to select and authenticate your model provider.'
        & docker run -it --rm -v "${resolvedInstall}\data:/opt/data" nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418 setup
        if ($LASTEXITCODE -ne 0) { throw 'Hermes provider setup did not complete.' }
    }

    $hasDashboardAuth = $false
    if (Test-Path -LiteralPath $configPath) {
        $hasDashboardAuth = [bool](Select-String -Path $configPath -Pattern '^\s+password_hash:\s+.+$' -Quiet)
    }
    if (-not $hasDashboardAuth) {
        Write-Host ''
        Write-Host 'STEP 2 OF 2: Dashboard password' -ForegroundColor Cyan
        Write-Host 'Choose option 1 and create a dashboard username/password.'
        Write-Host 'When the dashboard reports that it is running, press Ctrl+C once to return here.' -ForegroundColor Yellow
        & docker run -it --rm -v "${resolvedInstall}\data:/opt/data" nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418 dashboard --host 0.0.0.0 --port 9119 --no-open
    }
}
finally {
    Pop-Location
}

if (-not $SkipDesktopLauncher) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $desktopLauncher = Join-Path $desktop 'Launch Hermes.cmd'
    $shutdownScript = Join-Path $resolvedInstall 'Shutdown-Hermes.ps1'
    $desktopShutdown = Join-Path $desktop 'Shutdown Hermes.cmd'
    $updateScript = Join-Path $resolvedInstall 'Update-Hermes.ps1'
    $desktopUpdate = Join-Path $desktop 'Update Hermes.cmd'
    $checkScript = Join-Path $resolvedInstall 'Test-Hermes.ps1'
    $desktopCheck = Join-Path $desktop 'Check Hermes.cmd'
    $cmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launchScript`"`r`nif errorlevel 1 pause`r`n"
    $shutdownCmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$shutdownScript`"`r`nif errorlevel 1 pause`r`n"
    $updateCmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$updateScript`"`r`nif errorlevel 1 pause`r`n"
    $checkCmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$checkScript`"`r`npause`r`n"
    [IO.File]::WriteAllText($desktopLauncher, $cmd, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($desktopShutdown, $shutdownCmd, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($desktopUpdate, $updateCmd, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($desktopCheck, $checkCmd, [Text.UTF8Encoding]::new($false))
}

& $launchScript -NoBrowser

Write-Host ''
Write-Host "Installed to: $resolvedInstall" -ForegroundColor Green
Write-Host 'Use Launch, Check, Shutdown, and Update Hermes commands in this folder or on the Desktop.' -ForegroundColor Green
Write-Host 'Optional: install Codex Desktop, the OpenAI VS Code extension, or Codex CLI to enable the Codex agent panel.' -ForegroundColor DarkGray
