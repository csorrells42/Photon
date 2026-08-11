[CmdletBinding()]
param(
    [string]$Destination = '',
    [switch]$IncludeLocalEngineeringPhotonCadAssets
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$projectRoot = Split-Path -Parent $PSScriptRoot
$frontendSource = Join-Path $projectRoot 'src'
$testsSource = Join-Path $projectRoot 'tests'
$desktopClientSource = Join-Path $projectRoot 'artifacts\desktop\win-x64'
$bridgeClientSource = Join-Path $projectRoot 'artifacts\tools\hermes-bridge\win-x64'
$netCoreDbgArchiveSource = Join-Path $projectRoot 'artifacts\toolchain-cache\netcoredbg-win64.zip'
$roslynServerArchiveSource = Join-Path $projectRoot 'artifacts\toolchain-cache\microsoft.codeanalysis.languageserver.win-x64.5.0.0-1.25277.114.nupkg'
$roslynSdkArchiveSource = Join-Path $projectRoot 'artifacts\toolchain-cache\dotnet-sdk-10.0.302-win-x64.zip'
$Destination = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $projectRoot $(if ($IncludeLocalEngineeringPhotonCadAssets) {
        'Hermes-Remote-Install.LOCAL-ENGINEERING-ONLY.zip'
    } else {
        'Hermes-Remote-Install.zip'
    })
} else { $Destination }
$resolvedDestination = [IO.Path]::GetFullPath($Destination)
if ([IO.Path]::GetExtension($resolvedDestination) -ne '.zip') {
    throw "Installer destination must be a .zip file: $resolvedDestination"
}
if ($IncludeLocalEngineeringPhotonCadAssets -and
    -not [IO.Path]::GetFileName($resolvedDestination).EndsWith('.LOCAL-ENGINEERING-ONLY.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A bundle containing uncleared Photon CAD assets must use a .LOCAL-ENGINEERING-ONLY.zip filename.'
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesRemoteInstall-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $tempRoot 'Hermes-Remote-Install'
$tempArchive = Join-Path $tempRoot 'Hermes-Remote-Install.zip'

try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    $portableFiles = @(
        'Check Hermes.cmd', 'Install Hermes.cmd', 'Launch Hermes.cmd', 'Shutdown Hermes.cmd',
        'Update Hermes.cmd', 'Show Hermes Bridge.cmd', 'docker-compose.yml', 'launcher.settings.json',
        'Install-Hermes.ps1', 'Install-NetCoreDbg.ps1', 'Get-NetCoreDbg.ps1', 'Install-RoslynLanguageServer.ps1', 'roslyn-language-server.lock.json', 'Test-Hermes.ps1',
        'Install-PhotonModels.ps1', 'Install-PhotonCadRuntime.ps1', 'photon-cad-assets.lock.json',
        'Launch-Hermes.ps1', 'Shutdown-Hermes.ps1', 'Update-Hermes.ps1', 'Show-HermesBridge.ps1',
        'Invoke-HermesFrontend.ps1', 'README.md', 'THIRD-PARTY-NOTICES.md',
        'licenses\netcoredbg-LICENSE.txt', '.serena\project.yml'
    )
    foreach ($relative in $portableFiles) {
        $inputPath = Join-Path $source $relative
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required portable input is missing: $relative" }
        $inputItem = Get-Item -LiteralPath $inputPath -Force
        if ($inputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse-point portable input: $relative" }
        $outputPath = Join-Path $stage $relative
        $outputParent = Split-Path -Parent $outputPath
        if (-not (Test-Path -LiteralPath $outputParent)) { New-Item -ItemType Directory -Path $outputParent -Force | Out-Null }
        Copy-Item -LiteralPath $inputPath -Destination $outputPath -Force
    }

    $runtimeSource = Join-Path $projectRoot 'runtime'
    $runtimeStage = Join-Path $stage 'runtime'
    $runtimeFiles = @(
        'Runtime.Common.ps1', 'Runtime.Generation.ps1', 'Build-HermesRuntime.ps1',
        'Verify-HermesRuntime.ps1', 'Adopt-HermesRuntime.ps1',
        'source-lock.schema.json', 'runtime-image-lock.schema.json',
        'runtime-image.env.example', 'photon-models.lock.json', 'memory-vector.lock.json', 'README.md'
    )
    New-Item -ItemType Directory -Path $runtimeStage | Out-Null
    foreach ($relative in $runtimeFiles) {
        $inputPath = Join-Path $runtimeSource $relative
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required runtime recipe input is missing: $relative" }
        $item = Get-Item -LiteralPath $inputPath -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse-point runtime recipe input: $relative" }
        Copy-Item -LiteralPath $inputPath -Destination (Join-Path $runtimeStage $relative) -Force
    }

    if ($IncludeLocalEngineeringPhotonCadAssets) {
        # These images are not cleared for public redistribution. They enter a
        # bundle only through this explicit local-engineering switch and never
        # participate in the installer's broad application-tree copy.
        & (Join-Path $source 'Install-PhotonCadRuntime.ps1') -VerifyOnly
        $photonCadSource = Join-Path $projectRoot 'runtime-assets\photon-cad'
        $photonCadStage = Join-Path $stage 'payloads\photon-cad'
        $photonCadFiles = @(
            'bundle-receipt.json', 'LOCAL-ENGINEERING-ONLY.md',
            'photon-cad-geometry.tar', 'photon-cad-assembly.tar', 'runtime-policy.json'
        )
        New-Item -ItemType Directory -Path $photonCadStage -Force | Out-Null
        foreach ($relative in $photonCadFiles) {
            $inputPath = Join-Path $photonCadSource $relative
            if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required Photon CAD asset is missing: $relative" }
            $item = Get-Item -LiteralPath $inputPath -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse-point Photon CAD asset: $relative" }
            Copy-Item -LiteralPath $inputPath -Destination (Join-Path $photonCadStage $relative) -Force
        }
    }

    $editorTasks = Join-Path $projectRoot '.vscode\tasks.json'
    if (-not (Test-Path -LiteralPath $editorTasks -PathType Leaf)) {
        throw "Workbench editor tasks were not found at $editorTasks"
    }
    $editorStage = Join-Path $stage '.vscode'
    New-Item -ItemType Directory -Path $editorStage | Out-Null
    Copy-Item -LiteralPath $editorTasks -Destination (Join-Path $editorStage 'tasks.json') -Force

    $requiredSmokeScripts = @('Install-Hermes.Smoke.ps1', 'Shutdown-Hermes.Smoke.ps1', 'Update-Hermes.Smoke.ps1', 'NetCoreDbg-Provisioning.Smoke.ps1', 'RoslynLanguageServer-Provisioning.Fake.Smoke.ps1', 'PhotonCad-Offline-Assets.Smoke.ps1', 'Memory-Vector-Compose.Smoke.ps1')
    $missingSmokeScripts = @($requiredSmokeScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $testsSource $_) -PathType Leaf) })
    if ($missingSmokeScripts.Count -gt 0) {
        throw "Hermes Workbench installer smoke scripts were not found: $($missingSmokeScripts -join ', ')"
    }
    $testsStage = Join-Path $stage 'tests'
    New-Item -ItemType Directory -Path $testsStage | Out-Null
    foreach ($script in $requiredSmokeScripts) {
        Copy-Item -LiteralPath (Join-Path $testsSource $script) -Destination (Join-Path $testsStage $script) -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $frontendSource 'package-lock.json'))) {
        throw "Hermes Workbench source was not found at $frontendSource"
    }
    $frontendStage = Join-Path $stage 'src'
    New-Item -ItemType Directory -Path $frontendStage | Out-Null
    $frontendPrefix = $frontendSource.TrimEnd('\') + '\'
    $allowedFrontendRootFiles = @(
        'index.html', 'package.json', 'package-lock.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml',
        'tsconfig.app.json', 'tsconfig.json', 'tsconfig.node.json', 'vite.config.ts', 'vite.config.test.ts'
    )
    $allowedFrontendRoots = @('app', 'Host', 'Modules', 'Tools')
    $allowedFrontendExtensions = @('.cs', '.csproj', '.css', '.ico', '.json', '.lock', '.manifest', '.md', '.png', '.ps1', '.py', '.resx', '.svg', '.ts', '.tsx', '.xaml', '.xml')
    $excludedFrontendSegments = @('node_modules', 'dist', 'bin', 'obj', '.git', '.cache', '.npm', 'Smoke')
    $frontendInputs = @(
        $allowedFrontendRootFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $frontendSource $_) -Force }
        $allowedFrontendRoots | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $frontendSource $_) -Recurse -Force -File }
    ) | Where-Object {
        $candidateRelative = $_.FullName.Substring($frontendPrefix.Length)
        $candidateSegments = @($candidateRelative -split '[\\/]')
        @($candidateSegments | Where-Object { $_ -in $excludedFrontendSegments -or $_.EndsWith('.Smoke', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0
    }
    foreach ($item in $frontendInputs) {
        $relative = $item.FullName.Substring($frontendPrefix.Length)
        $segments = @($relative -split '[\\/]')
        $leaf = $segments[-1]
        $canonicalLeaf = $leaf.ToLowerInvariant()
        $blocked = @($segments | Where-Object { $_ -in @('node_modules', 'dist', 'bin', 'obj', '.git', 'data', 'logs', 'vault', '.cache', '.npm') }).Count -gt 0 -or
            $canonicalLeaf -in @('auth.json', 'credentials.json', 'secrets.json', '.npmrc', '.pypirc', 'nuget.config') -or
            $canonicalLeaf -eq '.env' -or $canonicalLeaf.StartsWith('.env.') -or
            [IO.Path]::GetExtension($canonicalLeaf) -in @('.key', '.pem', '.pfx', '.p12', '.ppk')
        if ($blocked -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing unsafe frontend package input: $relative"
        }
        if ($leaf -notin $allowedFrontendRootFiles -and
            -not $leaf.Equals('Dockerfile', [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetExtension($leaf).ToLowerInvariant() -notin $allowedFrontendExtensions) {
            throw "Frontend input is not allow-listed for portable packaging: $relative"
        }
        $target = Join-Path $frontendStage $relative
        $targetParent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetParent)) { New-Item -ItemType Directory -Path $targetParent -Force | Out-Null }
        Copy-Item -LiteralPath $item.FullName -Destination $target -Force
    }

    $desktopExecutable = Join-Path $desktopClientSource 'HermesDesktop.exe'
    if (-not (Test-Path -LiteralPath $desktopExecutable)) {
        throw "Published HermesDesktop client was not found at $desktopExecutable"
    }
    $clientStage = Join-Path $stage 'client'
    New-Item -ItemType Directory -Path $clientStage | Out-Null
    Copy-Item -LiteralPath $desktopExecutable -Destination (Join-Path $clientStage 'HermesDesktop.exe') -Force

    $bridgeExecutable = Join-Path $bridgeClientSource 'hermes-bridge.exe'
    if (-not (Test-Path -LiteralPath $bridgeExecutable -PathType Leaf)) {
        throw "Published Hermes bridge client was not found at $bridgeExecutable"
    }
    $bridgeStage = Join-Path $stage 'tools\hermes-bridge'
    New-Item -ItemType Directory -Path $bridgeStage -Force | Out-Null
    Copy-Item -LiteralPath $bridgeExecutable -Destination (Join-Path $bridgeStage 'hermes-bridge.exe') -Force

    & (Join-Path $source 'Get-NetCoreDbg.ps1') -DestinationPath $netCoreDbgArchiveSource
    $netCoreDbgArchiveValid = (Test-Path -LiteralPath $netCoreDbgArchiveSource -PathType Leaf) -and
        (Get-Item -LiteralPath $netCoreDbgArchiveSource).Length -eq 3475639L -and
        (Get-FileHash -LiteralPath $netCoreDbgArchiveSource -Algorithm SHA256).Hash -ceq 'C67AE052E0BCB9CE37000F261E2D397A0D5B6615CAFE30C868239A78598DFB37'
    if (-not $netCoreDbgArchiveValid) {
        throw 'The pinned NetCoreDbg archive is missing or failed its release integrity check.'
    }
    $toolchainStage = Join-Path $stage 'toolchains'
    New-Item -ItemType Directory -Path $toolchainStage -Force | Out-Null
    Copy-Item -LiteralPath $netCoreDbgArchiveSource -Destination (Join-Path $toolchainStage 'netcoredbg-win64.zip')

    $roslynServerArchiveValid = (Test-Path -LiteralPath $roslynServerArchiveSource -PathType Leaf) -and
        (Get-Item -LiteralPath $roslynServerArchiveSource).Length -eq 65767620L -and
        (Get-FileHash -LiteralPath $roslynServerArchiveSource -Algorithm SHA256).Hash -ceq '7C96C59532A81F710BE95A48E6DD25C4E4D17875A37F5A7171A90E82F8AB57A6'
    if (-not $roslynServerArchiveValid) {
        throw 'The pinned Roslyn language-server archive is missing or failed its release integrity check.'
    }
    $roslynSdkArchiveValid = (Test-Path -LiteralPath $roslynSdkArchiveSource -PathType Leaf) -and
        (Get-Item -LiteralPath $roslynSdkArchiveSource).Length -eq 297545270L -and
        (Get-FileHash -LiteralPath $roslynSdkArchiveSource -Algorithm SHA512).Hash -ceq '7D170ED75FA9AF34C00646621D92011DBD71943952E2787CD15DF9BE78E6452B55DADEF34D7EFF77B802E6AF4959E071A55855AC649AFEAC70901C3A2A258716'
    if (-not $roslynSdkArchiveValid) {
        throw 'The pinned .NET SDK archive for Roslyn is missing or failed its release integrity check.'
    }
    Copy-Item -LiteralPath $roslynServerArchiveSource -Destination (Join-Path $toolchainStage 'microsoft.codeanalysis.languageserver.win-x64.5.0.0-1.25277.114.nupkg')
    Copy-Item -LiteralPath $roslynSdkArchiveSource -Destination (Join-Path $toolchainStage 'dotnet-sdk-10.0.302-win-x64.zip')

    New-Item -ItemType Directory -Path (Join-Path $stage 'data'), (Join-Path $stage 'logs'), (Join-Path $stage 'workspace') | Out-Null

    $stagePrefix = $stage.TrimEnd('\') + '\'
    $manifestEntries = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to package a reparse-point file: $($_.FullName)"
        }
        [ordered]@{
            path = $_.FullName.Substring($stagePrefix.Length).Replace('\', '/')
            length = [long]$_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = [ordered]@{
        protocolVersion = 1
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
        algorithm = 'SHA-256'
        files = $manifestEntries
    }
    [IO.File]::WriteAllText(
        (Join-Path $stage 'bundle.manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false)
    )
    Compress-Archive -LiteralPath $stage -DestinationPath $tempArchive -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($tempArchive)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $requiredEntries = @(
            'Hermes-Remote-Install/Install Hermes.cmd',
            'Hermes-Remote-Install/Check Hermes.cmd',
            'Hermes-Remote-Install/Launch Hermes.cmd',
            'Hermes-Remote-Install/Shutdown Hermes.cmd',
            'Hermes-Remote-Install/Update Hermes.cmd',
            'Hermes-Remote-Install/Show Hermes Bridge.cmd',
            'Hermes-Remote-Install/Install-Hermes.ps1',
            'Hermes-Remote-Install/Install-NetCoreDbg.ps1',
            'Hermes-Remote-Install/Install-RoslynLanguageServer.ps1',
            'Hermes-Remote-Install/roslyn-language-server.lock.json',
            'Hermes-Remote-Install/Install-PhotonModels.ps1',
            'Hermes-Remote-Install/Install-PhotonCadRuntime.ps1',
            'Hermes-Remote-Install/photon-cad-assets.lock.json',
            'Hermes-Remote-Install/Get-NetCoreDbg.ps1',
            'Hermes-Remote-Install/Test-Hermes.ps1',
            'Hermes-Remote-Install/Launch-Hermes.ps1',
            'Hermes-Remote-Install/Shutdown-Hermes.ps1',
            'Hermes-Remote-Install/Update-Hermes.ps1',
            'Hermes-Remote-Install/Show-HermesBridge.ps1',
            'Hermes-Remote-Install/Invoke-HermesFrontend.ps1',
            'Hermes-Remote-Install/bundle.manifest.json',
            'Hermes-Remote-Install/docker-compose.yml',
            'Hermes-Remote-Install/launcher.settings.json',
            'Hermes-Remote-Install/.vscode/tasks.json',
            'Hermes-Remote-Install/.serena/project.yml',
            'Hermes-Remote-Install/tests/Install-Hermes.Smoke.ps1',
            'Hermes-Remote-Install/tests/Shutdown-Hermes.Smoke.ps1',
            'Hermes-Remote-Install/tests/Update-Hermes.Smoke.ps1',
            'Hermes-Remote-Install/tests/NetCoreDbg-Provisioning.Smoke.ps1',
            'Hermes-Remote-Install/tests/RoslynLanguageServer-Provisioning.Fake.Smoke.ps1',
            'Hermes-Remote-Install/tests/PhotonCad-Offline-Assets.Smoke.ps1',
            'Hermes-Remote-Install/tests/Memory-Vector-Compose.Smoke.ps1',
            'Hermes-Remote-Install/runtime/Runtime.Common.ps1',
            'Hermes-Remote-Install/runtime/Runtime.Generation.ps1',
            'Hermes-Remote-Install/runtime/Build-HermesRuntime.ps1',
            'Hermes-Remote-Install/runtime/Verify-HermesRuntime.ps1',
            'Hermes-Remote-Install/runtime/Adopt-HermesRuntime.ps1',
            'Hermes-Remote-Install/runtime/photon-models.lock.json',
            'Hermes-Remote-Install/runtime/memory-vector.lock.json',
            'Hermes-Remote-Install/licenses/netcoredbg-LICENSE.txt',
            'Hermes-Remote-Install/toolchains/netcoredbg-win64.zip',
            'Hermes-Remote-Install/toolchains/microsoft.codeanalysis.languageserver.win-x64.5.0.0-1.25277.114.nupkg',
            'Hermes-Remote-Install/toolchains/dotnet-sdk-10.0.302-win-x64.zip',
            'Hermes-Remote-Install/src/package-lock.json',
            'Hermes-Remote-Install/client/HermesDesktop.exe',
            'Hermes-Remote-Install/tools/hermes-bridge/hermes-bridge.exe'
        )
        if ($IncludeLocalEngineeringPhotonCadAssets) {
            $requiredEntries += @(
                'Hermes-Remote-Install/payloads/photon-cad/bundle-receipt.json',
                'Hermes-Remote-Install/payloads/photon-cad/LOCAL-ENGINEERING-ONLY.md',
                'Hermes-Remote-Install/payloads/photon-cad/photon-cad-geometry.tar',
                'Hermes-Remote-Install/payloads/photon-cad/photon-cad-assembly.tar',
                'Hermes-Remote-Install/payloads/photon-cad/runtime-policy.json'
            )
        }
        $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entryNames })
        if ($missingEntries.Count -gt 0) { throw "Installer ZIP is missing required entries: $($missingEntries -join ', ')" }

        $blockedEntries = @($archive.Entries | Where-Object {
            $normalized = $_.FullName.Replace('\', '/')
            $segments = @($normalized -split '/' | Where-Object { $_ })
            $leaf = if ($segments.Count) { $segments[-1] } else { '' }
            $canonicalSegments = @($segments | ForEach-Object { $_.ToLowerInvariant() })
            $canonicalLeaf = $leaf.ToLowerInvariant()
            $containsBuildOutput = @($canonicalSegments | Where-Object { $_ -in @('node_modules', 'dist', 'bin', 'obj', '.git', 'vault', '.cache', '.npm', 'packages') }).Count -gt 0
            $containsSecretEnvironment = $canonicalLeaf -eq '.env' -or $canonicalLeaf.StartsWith('.env.')
            $containsSecretFile = $canonicalLeaf -in @('auth.json', 'credentials.json', 'secrets.json', '.npmrc', '.pypirc', 'nuget.config') -or
                [IO.Path]::GetExtension($canonicalLeaf) -in @('.key', '.pem', '.pfx', '.p12', '.ppk')
            $containsRuntimeData = $_.Name -and ($normalized -like 'Hermes-Remote-Install/data/*' -or $normalized -like 'Hermes-Remote-Install/logs/*')
            $containsBuildOutput -or $containsSecretEnvironment -or $containsSecretFile -or $containsRuntimeData
        })
        if ($blockedEntries.Count -gt 0) {
            throw "Installer ZIP contains excluded content: $((@($blockedEntries | Select-Object -First 10 -ExpandProperty FullName)) -join ', ')"
        }

        $clientEntry = $archive.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -eq 'Hermes-Remote-Install/client/HermesDesktop.exe'
        } | Select-Object -First 1
        if (-not $clientEntry) { throw 'Packaged desktop host entry could not be opened for verification.' }
        $clientStream = $clientEntry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $embeddedClientHash = ([BitConverter]::ToString($sha.ComputeHash($clientStream))).Replace('-', '') }
            finally { $sha.Dispose() }
        }
        finally { $clientStream.Dispose() }
        $publishedClientHash = (Get-FileHash -LiteralPath $desktopExecutable -Algorithm SHA256).Hash
        if ($embeddedClientHash -ne $publishedClientHash) { throw 'Packaged desktop host does not match the published desktop host.' }

        $bridgeEntry = $archive.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -eq 'Hermes-Remote-Install/tools/hermes-bridge/hermes-bridge.exe'
        } | Select-Object -First 1
        if (-not $bridgeEntry) { throw 'Packaged Hermes bridge client entry could not be opened for verification.' }
        $bridgeStream = $bridgeEntry.Open()
        try {
            $bridgeSha = [Security.Cryptography.SHA256]::Create()
            try { $embeddedBridgeHash = ([BitConverter]::ToString($bridgeSha.ComputeHash($bridgeStream))).Replace('-', '') }
            finally { $bridgeSha.Dispose() }
        }
        finally { $bridgeStream.Dispose() }
        $publishedBridgeHash = (Get-FileHash -LiteralPath $bridgeExecutable -Algorithm SHA256).Hash
        if ($embeddedBridgeHash -ne $publishedBridgeHash) { throw 'Packaged Hermes bridge client does not match the published bridge client.' }
    }
    finally { $archive.Dispose() }

    $destinationParent = Split-Path -Parent $resolvedDestination
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $resolvedDestination) {
        $existing = Get-Item -LiteralPath $resolvedDestination -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to overwrite a non-regular installer destination: $resolvedDestination"
        }
    }
    Copy-Item -LiteralPath $tempArchive -Destination $resolvedDestination -Force
    $archiveHash = (Get-FileHash -LiteralPath $resolvedDestination -Algorithm SHA256).Hash
    $checksumPath = $resolvedDestination + '.sha256'
    if (Test-Path -LiteralPath $checksumPath) {
        $checksumItem = Get-Item -LiteralPath $checksumPath -Force
        if ($checksumItem.PSIsContainer -or ($checksumItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to overwrite a non-regular checksum destination: $checksumPath"
        }
    }
    [IO.File]::WriteAllText(
        $checksumPath,
        "$archiveHash  $([IO.Path]::GetFileName($resolvedDestination))`n",
        [Text.UTF8Encoding]::new($false)
    )
    Write-Host "Created $resolvedDestination" -ForegroundColor Green
    Write-Host "Created $checksumPath" -ForegroundColor Green
    Write-Host "Verified $($entryNames.Count) archive entries, $($manifestEntries.Count) manifested files, desktop host SHA-256 $publishedClientHash, and bridge client SHA-256 $publishedBridgeHash." -ForegroundColor Green
    Write-Host 'Credentials, sessions, logs, node_modules, builds, environment files, and local Hermes data were excluded.' -ForegroundColor Green
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemp.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp) -like 'HermesRemoteInstall-*' -and
        (Test-Path -LiteralPath $resolvedTemp -PathType Container)) {
        $tempItem = Get-Item -LiteralPath $resolvedTemp -Force
        if (-not ($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
        }
    }
}
