[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [switch]$SecuritySmoke
)

$ErrorActionPreference = 'Stop'
$approvedHermesImage = 'nousresearch/hermes-agent@sha256:4f42492918adefaec23b17637182ba2f38992ef6e5512465d88cb8c9d0edf418'
$bundleRoot = $PSScriptRoot
$logsPath = Join-Path $bundleRoot 'logs'
$settingsPath = Join-Path $bundleRoot 'launcher.settings.json'
$serenaPort = 9121
$workbenchPort = 4173
$assistantBusPort = 9072
$script:serenaProcessStarted = $false
$runtimeGenerationCandidates = @(
    (Join-Path $bundleRoot 'runtime\Runtime.Generation.ps1'),
    (Join-Path (Split-Path -Parent $bundleRoot) 'runtime\Runtime.Generation.ps1')
)
$runtimeGenerationScript = @($runtimeGenerationCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
if ($runtimeGenerationScript.Count -ne 1) {
    throw 'Verified runtime generation support is missing. Reinstall Phos Agape Aphthartos.'
}
. ([string]$runtimeGenerationScript[0])

New-Item -ItemType Directory -Force -Path $logsPath | Out-Null

function Resolve-HermesWorkspacePath {
    param([AllowNull()][string]$ConfiguredPath)

    $configured = if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) { 'workspace' } else { $ConfiguredPath.Trim() }
    if ($configured.Length -gt 1024 -or $configured.IndexOfAny([char[]](0..31)) -ge 0) {
        throw 'WorkspacePath contains invalid characters or is too long.'
    }
    if ($configured -match '^(\\\\[?.]\\|\\\\|//|\\\?\\)') {
        throw 'WorkspacePath must be a local drive path or a bundle-relative path.'
    }

    $isRooted = [IO.Path]::IsPathRooted($configured)
    $resolved = [IO.Path]::GetFullPath($(if ($isRooted) { $configured } else { Join-Path $bundleRoot $configured }))
    $bundleFull = [IO.Path]::GetFullPath($bundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedTrimmed = $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [IO.Path]::GetPathRoot($resolvedTrimmed)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        [string]::Equals($resolvedTrimmed, $pathRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkspacePath cannot be a drive root.'
    }
    if ([string]::Equals($resolvedTrimmed, $bundleFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkspacePath cannot be the install bundle root.'
    }
    if (-not $isRooted -and
        -not $resolvedTrimmed.StartsWith($bundleFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A relative WorkspacePath must remain inside the install bundle.'
    }

    $relativeToRoot = $resolvedTrimmed.Substring($pathRoot.Length)
    if ($relativeToRoot.Contains(':')) {
        throw 'WorkspacePath cannot contain an alternate data stream.'
    }
    if (-not (Test-Path -LiteralPath $resolvedTrimmed -PathType Container)) {
        throw "WorkspacePath must already exist as a directory: $resolvedTrimmed"
    }

    $blockedRoots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [string]$env:OneDrive
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    foreach ($blocked in $blockedRoots) {
        if ([string]::Equals($resolvedTrimmed, $blocked, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'WorkspacePath cannot be a profile, Desktop, or OneDrive root.'
        }
    }

    $cursor = $resolvedTrimmed
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "WorkspacePath cannot traverse a reparse point: $cursor"
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = $parent
    }
    return $resolvedTrimmed
}

function Get-ConfiguredWorkspacePath {
    $configured = $null
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $configured = [string](Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).WorkspacePath
    }
    return Resolve-HermesWorkspacePath -ConfiguredPath $configured
}

function Wait-DockerEngine {
    param([int]$TimeoutSeconds = 180)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker info *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw "Docker Desktop did not become ready within $TimeoutSeconds seconds."
}

function Start-DockerDesktop {
    & docker info *> $null
    if ($LASTEXITCODE -eq 0) { return }

    $dockerDesktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path -LiteralPath $dockerDesktop)) {
        throw 'Docker Desktop is not installed. Run Install-Hermes.ps1 first.'
    }
    Start-Process -FilePath $dockerDesktop -WindowStyle Hidden
    Wait-DockerEngine
}

function Get-HermesLaunchImageReference {
    $current = Get-PhotonCurrentRuntimeGeneration -BundleRoot $bundleRoot
    if ($null -eq $current) { return $approvedHermesImage }
    Assert-PhotonRuntimeDockerImage -Generation $current.Generation
    Write-Host "Using verified Hermes runtime generation $($current.GenerationId)." -ForegroundColor DarkGray
    return [string]$current.ImageId
}

function Test-LocalPort {
    param([int]$Port)
    try {
        $client = [Net.Sockets.TcpClient]::new()
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if (-not $task.Wait(1000)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { if ($client) { $client.Dispose() } }
}

function Write-ProcessIdentity {
    param([int]$ProcessId, [string]$Name, [int]$Port = 0)
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    $commandBytes = [Text.Encoding]::UTF8.GetBytes([string]$process.CommandLine)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $commandHash = ([BitConverter]::ToString($sha.ComputeHash($commandBytes))).Replace('-', '') }
    finally { $sha.Dispose(); [Array]::Clear($commandBytes, 0, $commandBytes.Length) }
    $identity = [ordered]@{
        protocolVersion = 1
        pid = $ProcessId
        executablePath = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        creationDate = [string]$process.CreationDate
        commandLineSha256 = $commandHash
        port = $Port
    }
    [IO.File]::WriteAllText((Join-Path $logsPath "$Name.process.json"), ($identity | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}

function Resolve-AssistantBusExecutable {
    $candidates = @(
        (Join-Path $bundleRoot 'tools\assistant-bus\assistant-bus.exe'),
        (Join-Path $bundleRoot 'artifacts\tools\assistant-bus\win-x64\assistant-bus.exe')
    )
    $resolved = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    if ($resolved.Count -ne 1) {
        throw 'The Assistant Conversation Bus executable is missing. Reinstall Phos Agape Aphthartos.'
    }
    return [IO.Path]::GetFullPath([string]$resolved[0])
}

function Test-AssistantBusHealth {
    try {
        $health = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$assistantBusPort/health" -TimeoutSec 2
        return [string]$health.status -ceq 'ready' -and [int]$health.protocolVersion -eq 1
    }
    catch { return $false }
}

function Start-AssistantConversationBus {
    $executable = Resolve-AssistantBusExecutable
    $pidPath = Join-Path $logsPath 'assistant-bus.pid'
    if (Test-LocalPort -Port $assistantBusPort) {
        $listener = Get-NetTCPConnection -LocalPort $assistantBusPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        $trackedPid = 0
        $tracked = (Test-Path -LiteralPath $pidPath -PathType Leaf) -and
            [int]::TryParse(([IO.File]::ReadAllText($pidPath).Trim()), [ref]$trackedPid)
        $process = if ($listener) { Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue } else { $null }
        $owned = $tracked -and $listener -and $trackedPid -eq $listener.OwningProcess -and $process -and
            [string]$process.CommandLine -match '(^|\s|\")serve(\s|\"|$)'
        try { $owned = $owned -and [IO.Path]::GetFullPath([string]$process.ExecutablePath) -eq $executable }
        catch { $owned = $false }
        if (-not $owned -or -not (Test-AssistantBusHealth)) {
            throw "Port $assistantBusPort is not owned by the verified Assistant Conversation Bus."
        }
        Write-ProcessIdentity -ProcessId $listener.OwningProcess -Name 'assistant-bus' -Port $assistantBusPort
        return
    }

    $stdout = Join-Path $logsPath 'assistant-bus.out.log'
    $stderr = Join-Path $logsPath 'assistant-bus.err.log'
    $process = Start-Process -FilePath $executable -ArgumentList @('serve') -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    [IO.File]::WriteAllText($pidPath, [string]$process.Id)
    Write-ProcessIdentity -ProcessId $process.Id -Name 'assistant-bus' -Port $assistantBusPort
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "The Assistant Conversation Bus exited during startup. Check $stderr." }
        if (Test-AssistantBusHealth) { return }
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    throw "The Assistant Conversation Bus did not become ready on port $assistantBusPort. Check $stderr."
}

function Find-SerenaExecutable {
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        $configured = [string]$settings.SerenaExecutable
        if (-not [string]::IsNullOrWhiteSpace($configured)) {
            if (-not [IO.Path]::IsPathRooted($configured)) { $configured = Join-Path $bundleRoot $configured }
            if (Test-Path -LiteralPath $configured -PathType Leaf) { return [IO.Path]::GetFullPath($configured) }
        }
    }

    $serena = Get-Command serena -ErrorAction SilentlyContinue
    if ($serena) { return $serena.Source }

    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if ($uv) {
        $toolBin = @(& $uv.Source tool dir --bin 2>$null) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1
        if ($LASTEXITCODE -eq 0 -and $toolBin) {
            foreach ($name in @('serena.exe', 'serena.cmd', 'serena')) {
                $candidate = Join-Path ([string]$toolBin).Trim() $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
            }
        }
    }

    throw 'Serena is not installed or its executable could not be found. Run Install-Hermes.ps1 first.'
}

function Start-Serena {
    if (Test-LocalPort -Port $serenaPort) {
        $listener = Get-NetTCPConnection -LocalPort $serenaPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $listener) { throw "Port $serenaPort is occupied, but its process could not be identified." }
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue
        if ([string]$processInfo.CommandLine -notmatch 'serena|start-mcp-server') {
            throw "Port $serenaPort is already used by another application (PID $($listener.OwningProcess))."
        }
        $commandLine = [string]$processInfo.CommandLine
        $projectMatches = $commandLine.IndexOf('--project', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $commandLine.IndexOf($script:workspacePath, [StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($projectMatches) {
            [IO.File]::WriteAllText((Join-Path $logsPath 'serena.pid'), [string]$listener.OwningProcess)
            Write-ProcessIdentity -ProcessId $listener.OwningProcess -Name 'serena' -Port $serenaPort
            return
        }

        $pidPath = Join-Path $logsPath 'serena.pid'
        $trackedPid = 0
        $ownsListener = (Test-Path -LiteralPath $pidPath -PathType Leaf) -and
            [int]::TryParse(([IO.File]::ReadAllText($pidPath).Trim()), [ref]$trackedPid) -and
            $trackedPid -eq [int]$listener.OwningProcess
        if (-not $ownsListener) {
            throw "Serena is already running on port $serenaPort for another project (PID $($listener.OwningProcess)). Stop that instance or choose a dedicated port."
        }

        Write-Host 'Restarting the owned Serena service against this Workbench project...' -ForegroundColor Cyan
        Stop-Process -Id $trackedPid
        $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 100
        } until (-not (Get-Process -Id $trackedPid -ErrorAction SilentlyContinue) -or [DateTime]::UtcNow -ge $stopDeadline)
        if (Get-Process -Id $trackedPid -ErrorAction SilentlyContinue) {
            throw "The existing Serena service (PID $trackedPid) did not stop cleanly."
        }
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    }

    $serenaExecutable = Find-SerenaExecutable
    $nodeExecutable = Find-NodeExecutable
    $nodeDirectory = Split-Path -Parent $nodeExecutable

    $stdout = Join-Path $logsPath 'serena.out.log'
    $stderr = Join-Path $logsPath 'serena.err.log'
    $arguments = @(
        'start-mcp-server',
        '--transport', 'streamable-http',
        '--host', '127.0.0.1',
        '--port', "$serenaPort",
        '--project', $script:workspacePath,
        '--context', 'ide',
        '--enable-web-dashboard', 'true',
        '--open-web-dashboard', 'false',
        '--enable-gui-log-window', 'false'
    )
    $previousPath = $env:PATH
    try {
        if ($nodeDirectory -notin @($env:PATH -split ';')) {
            $env:PATH = $nodeDirectory + ';' + $env:PATH
        }
        Start-Process -FilePath $serenaExecutable -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
    }
    finally {
        $env:PATH = $previousPath
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-LocalPort -Port $serenaPort) {
            $listener = Get-NetTCPConnection -LocalPort $serenaPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($listener) {
                [IO.File]::WriteAllText((Join-Path $logsPath 'serena.pid'), [string]$listener.OwningProcess)
                Write-ProcessIdentity -ProcessId $listener.OwningProcess -Name 'serena' -Port $serenaPort
                $script:serenaProcessStarted = $true
            }
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "Serena did not start on port $serenaPort. Check $stderr."
}

function Start-ConfiguredClient {
    if (-not (Test-Path -LiteralPath $settingsPath)) { return $false }
    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    $executable = [string]$settings.ClientExecutable
    if ([string]::IsNullOrWhiteSpace($executable)) { $executable = [string]$settings.MauiExecutable }
    if ([string]::IsNullOrWhiteSpace($executable)) { return $false }
    if (-not [IO.Path]::IsPathRooted($executable)) {
        $executable = Join-Path $bundleRoot $executable
    }
    if (-not (Test-Path -LiteralPath $executable)) {
        Write-Warning "Configured desktop client was not found: $executable"
        return $false
    }
    $workingDirectory = [string]$settings.ClientWorkingDirectory
    if ([string]::IsNullOrWhiteSpace($workingDirectory)) { $workingDirectory = [string]$settings.MauiWorkingDirectory }
    if ([string]::IsNullOrWhiteSpace($workingDirectory)) {
        $workingDirectory = Split-Path -Parent $executable
    } elseif (-not [IO.Path]::IsPathRooted($workingDirectory)) {
        $workingDirectory = Join-Path $bundleRoot $workingDirectory
    }
    $clientPidPath = Join-Path $logsPath 'client.pid'
    if (Test-Path -LiteralPath $clientPidPath) {
        $trackedPid = 0
        if ([int]::TryParse(([IO.File]::ReadAllText($clientPidPath).Trim()), [ref]$trackedPid)) {
            $tracked = Get-Process -Id $trackedPid -ErrorAction SilentlyContinue
            if ($tracked -and [IO.Path]::GetFullPath($tracked.Path) -eq [IO.Path]::GetFullPath($executable)) { return $true }
        }
    }
    $env:HERMES_WORKSPACE_PATH = $script:workspacePath
    $env:HERMES_INSTALL_ROOT = $bundleRoot
    $env:HERMES_WORKBENCH_URL = $script:workbenchUrl
    $env:HERMES_WORKBENCH_NONCE = $script:workbenchNonce
    $process = Start-Process -FilePath $executable -WorkingDirectory $workingDirectory -PassThru
    [IO.File]::WriteAllText($clientPidPath, [string]$process.Id)
    Write-ProcessIdentity -ProcessId $process.Id -Name 'client'
    return $true
}

function Resolve-ConfiguredWorkbenchUrl {
    param([AllowNull()][string]$Configured)
    $expected = "http://127.0.0.1:$workbenchPort"
    if ([string]::IsNullOrWhiteSpace($Configured)) { return $expected }
    if (-not [string]::Equals($Configured.TrimEnd('/'), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "WorkbenchUrl must be the launcher-owned endpoint $expected."
    }
    return $expected
}

function Get-ConfiguredWorkbenchUrl {
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { return Resolve-ConfiguredWorkbenchUrl $null }
    return Resolve-ConfiguredWorkbenchUrl ([string](Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).WorkbenchUrl)
}

function Test-WorkbenchProcessOwnership {
    param(
        [AllowNull()][object]$ProcessInfo,
        [string]$NodeExecutable,
        [string[]]$RequiredArguments
    )
    if (-not $ProcessInfo -or [string]::IsNullOrWhiteSpace([string]$ProcessInfo.ExecutablePath)) { return $false }
    try {
        if ([IO.Path]::GetFullPath([string]$ProcessInfo.ExecutablePath) -ne [IO.Path]::GetFullPath($NodeExecutable)) { return $false }
    }
    catch { return $false }
    $commandLine = [string]$ProcessInfo.CommandLine
    foreach ($argument in $RequiredArguments) {
        if ($commandLine.IndexOf($argument, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $false }
    }
    return $true
}

function Test-WorkbenchNonce {
    param([AllowNull()][string]$Nonce)
    return -not [string]::IsNullOrWhiteSpace($Nonce) -and $Nonce -match '^[a-fA-F0-9]{64}$'
}

function New-WorkbenchNonce {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Find-NodeExecutable {
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $configured = [string](Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).NodeExecutable
            if (-not [string]::IsNullOrWhiteSpace($configured)) {
                if (-not [IO.Path]::IsPathRooted($configured)) { $configured = Join-Path $bundleRoot $configured }
                if (Test-Path -LiteralPath $configured -PathType Leaf) { return [IO.Path]::GetFullPath($configured) }
            }
        }
        catch { }
    }

    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) { return $node.Source }

    $standardNode = 'C:\Program Files\nodejs\node.exe'
    if (Test-Path -LiteralPath $standardNode) { return $standardNode }
    throw 'Node.js is not installed or is missing from PATH. Run Install-Hermes.ps1 first.'
}

function Start-WorkbenchWeb {
    $frontendRoot = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'src'))
    $viteScript = [IO.Path]::GetFullPath((Join-Path $frontendRoot 'node_modules\vite\bin\vite.js'))
    $viteConfig = [IO.Path]::GetFullPath((Join-Path $frontendRoot 'vite.config.ts'))
    if (-not (Test-Path -LiteralPath $viteScript -PathType Leaf) -or -not (Test-Path -LiteralPath $viteConfig -PathType Leaf)) {
        throw 'Phos Agape Aphthartos dependencies are missing. Run Install-Hermes.ps1 first.'
    }
    $nodeExecutable = [IO.Path]::GetFullPath((Find-NodeExecutable))

    if (Test-LocalPort -Port $workbenchPort) {
        $listener = Get-NetTCPConnection -LocalPort $workbenchPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $listener) { throw "Port $workbenchPort is occupied, but its process could not be identified." }
        $pidPath = Join-Path $logsPath 'workbench.pid'
        $trackedPid = 0
        $trackedPidValid = (Test-Path -LiteralPath $pidPath -PathType Leaf) -and
            [int]::TryParse(([IO.File]::ReadAllText($pidPath).Trim()), [ref]$trackedPid)
        if (-not $trackedPidValid -or $trackedPid -ne $listener.OwningProcess) {
            throw "Port $workbenchPort is already used by an unverified process (PID $($listener.OwningProcess))."
        }
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue
        $requiredArguments = @($viteScript, $frontendRoot, '--config', $viteConfig, '--host', '127.0.0.1', '--port', "$workbenchPort", '--strictPort')
        if (-not (Test-WorkbenchProcessOwnership $processInfo $nodeExecutable $requiredArguments)) {
            throw "Port $workbenchPort is already used by a process that is not the launcher-owned Workbench (PID $($listener.OwningProcess))."
        }
        $noncePath = Join-Path $logsPath 'workbench.nonce'
        $existingNonce = if (Test-Path -LiteralPath $noncePath -PathType Leaf) { [IO.File]::ReadAllText($noncePath).Trim() } else { $null }
        if (-not (Test-WorkbenchNonce $existingNonce)) {
            throw 'The launcher-owned Workbench is missing its identity nonce. Shut it down and launch it again.'
        }
        Start-Sleep -Milliseconds 750
        $stableListener = Get-NetTCPConnection -LocalPort $workbenchPort -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $listener.OwningProcess | Select-Object -First 1
        if ($stableListener) {
            $script:workbenchNonce = $existingNonce.ToLowerInvariant()
            [IO.File]::WriteAllText((Join-Path $logsPath 'workbench.pid'), [string]$listener.OwningProcess)
            Write-ProcessIdentity -ProcessId $listener.OwningProcess -Name 'workbench' -Port $workbenchPort
            return
        }
    }

    $stdout = Join-Path $logsPath 'workbench.out.log'
    $stderr = Join-Path $logsPath 'workbench.err.log'
    $arguments = @($viteScript, $frontendRoot, '--config', $viteConfig, '--host', '127.0.0.1', '--port', "$workbenchPort", '--strictPort')
    $nonce = New-WorkbenchNonce
    $previousNonce = $env:HERMES_WORKBENCH_NONCE
    try {
        $env:HERMES_WORKBENCH_NONCE = $nonce
        $process = Start-Process -FilePath $nodeExecutable -ArgumentList $arguments -WorkingDirectory $frontendRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    }
    finally {
        if ($null -eq $previousNonce) { Remove-Item Env:\HERMES_WORKBENCH_NONCE -ErrorAction SilentlyContinue }
        else { $env:HERMES_WORKBENCH_NONCE = $previousNonce }
    }
    [IO.File]::WriteAllText((Join-Path $logsPath 'workbench.pid'), [string]$process.Id)
    Write-ProcessIdentity -ProcessId $process.Id -Name 'workbench' -Port $workbenchPort

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-LocalPort -Port $workbenchPort) {
            Start-Sleep -Milliseconds 750
            $stableListener = Get-NetTCPConnection -LocalPort $workbenchPort -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $process.Id | Select-Object -First 1
            if (-not $process.HasExited -and $stableListener) {
                $script:workbenchNonce = $nonce
                [IO.File]::WriteAllText((Join-Path $logsPath 'workbench.nonce'), $nonce)
                return
            }
        }
        if ($process.HasExited) { throw "Phos Agape Aphthartos exited during startup. Check $stderr." }
        Start-Sleep -Milliseconds 500
    }
    throw "Phos Agape Aphthartos did not start on port $workbenchPort. Check $stderr."
}

function Ensure-SerenaMcpConfiguration {
    $configPath = Join-Path $bundleRoot 'data\config.yaml'
    $hasUrl = (Test-Path -LiteralPath $configPath) -and [bool](Select-String -LiteralPath $configPath -Pattern '^\s*url:\s*http://host\.docker\.internal:9121/mcp\s*$' -Quiet)
    $hasHost = (Test-Path -LiteralPath $configPath) -and [bool](Select-String -LiteralPath $configPath -Pattern '^\s*Host:\s*localhost:9121\s*$' -Quiet)
    if ($hasUrl -and $hasHost) { return $false }

    Write-Host 'Connecting Hermes to the local Serena tools for the first time...' -ForegroundColor Cyan
    & docker exec hermes hermes config set --force mcp_servers.serena.url 'http://host.docker.internal:9121/mcp' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Serena MCP endpoint in Hermes.' }
    & docker exec hermes hermes config set --force mcp_servers.serena.headers.Host 'localhost:9121' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure Serena MCP host-header protection in Hermes.' }
    return $true
}

function Get-HermesConfigValue {
    param([Parameter(Mandatory)][string]$Key)

    $value = (& docker exec hermes hermes config get $Key 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { return '' }
    return $value.Trim()
}

function Ensure-HermesVisionConfiguration {
    $visionProvider = Get-HermesConfigValue -Key 'auxiliary.vision.provider'
    if (-not [string]::IsNullOrWhiteSpace($visionProvider) -and $visionProvider -ne 'auto') { return $false }

    $mainProvider = Get-HermesConfigValue -Key 'model.provider'
    if ($mainProvider -ne 'openrouter') {
        Write-Warning "Hermes image analysis still needs a vision provider. Current main provider '$mainProvider' is not configured automatically; choose one with 'hermes tools configure'."
        return $false
    }

    Write-Host 'Enabling Hermes image analysis through the configured OpenRouter account...' -ForegroundColor Cyan
    & docker exec hermes hermes config set --force auxiliary.vision.provider openrouter | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Hermes auxiliary vision provider.' }
    return $true
}

function Ensure-HermesWorkspaceConfiguration {
    if ((Get-HermesConfigValue -Key 'terminal.cwd') -eq '/workspace') { return $false }
    Write-Host 'Aligning Photon terminal tools with the dedicated workspace...' -ForegroundColor Cyan
    & docker exec hermes hermes config set --force terminal.cwd /workspace | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Hermes terminal workspace.' }
    return $true
}

function Wait-HermesGateway {
    param([int]$TimeoutSeconds = 120)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:9119/api/status' -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Seconds 2
    } until ([DateTime]::UtcNow -ge $deadline)
    throw "Hermes did not become ready on port 9119 within $TimeoutSeconds seconds. Run docker compose logs gateway for details."
}

function Write-HermesRuntimeIdentity {
    $identityPath = Join-Path $logsPath 'runtime-identity.json'
    try {
        $containerJson = (& docker inspect hermes 2>$null) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerJson)) { return }
        $container = @($containerJson | ConvertFrom-Json)[0]

        $imageId = [string]$container.Image
        $imageReference = [string]$container.Config.Image
        $revision = if ($container.Config.Labels) { [string]$container.Config.Labels.'org.opencontainers.image.revision' } else { $null }
        $repoDigest = $null
        if ($imageId) {
            $imageJson = (& docker image inspect $imageId 2>$null) -join [Environment]::NewLine
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($imageJson)) {
                $image = @($imageJson | ConvertFrom-Json)[0]
                $repoDigest = @($image.RepoDigests | Where-Object { $_ -like 'nousresearch/hermes-agent@*' } | Select-Object -First 1)[0]
            }
        }

        $identity = [ordered]@{
            protocolVersion = 1
            observedAtUtc = [DateTime]::UtcNow.ToString('o')
            containerName = 'hermes'
            imageReference = $imageReference
            imageId = $imageId
            repoDigest = if ($repoDigest) { [string]$repoDigest } else { $null }
            revision = if ($revision) { $revision } else { $null }
        }
        $utf8WithoutBom = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($identityPath, ($identity | ConvertTo-Json -Compress), $utf8WithoutBom)
    }
    catch {
        # Runtime identity is a compatibility aid and must not make an otherwise healthy launch fail.
    }
}

if ($SecuritySmoke) {
    $testNode = 'C:\Program Files\nodejs\node.exe'
    $testRoot = [IO.Path]::GetFullPath((Join-Path $bundleRoot 'src'))
    $testVite = [IO.Path]::GetFullPath((Join-Path $testRoot 'node_modules\vite\bin\vite.js'))
    $testConfig = [IO.Path]::GetFullPath((Join-Path $testRoot 'vite.config.ts'))
    $required = @($testVite, $testRoot, '--config', $testConfig, '--host', '127.0.0.1', '--port', '4173', '--strictPort')
    $owned = [pscustomobject]@{ ExecutablePath = $testNode; CommandLine = "`"$testNode`" `"$testVite`" `"$testRoot`" --config `"$testConfig`" --host 127.0.0.1 --port 4173 --strictPort" }
    $lookalike = [pscustomobject]@{ ExecutablePath = $testNode; CommandLine = "`"$testNode`" vite --port 4173" }
    $ownedAccepted = Test-WorkbenchProcessOwnership $owned $testNode $required
    $lookalikeAccepted = Test-WorkbenchProcessOwnership $lookalike $testNode $required
    if (-not $ownedAccepted -or $lookalikeAccepted) {
        throw 'Launcher process-ownership security smoke failed.'
    }
    $wrongPortRejected = $false
    try { Resolve-ConfiguredWorkbenchUrl 'http://127.0.0.1:8872' | Out-Null }
    catch { $wrongPortRejected = $true }
    if (-not $wrongPortRejected -or (Resolve-ConfiguredWorkbenchUrl 'http://127.0.0.1:4173/') -ne 'http://127.0.0.1:4173') {
        throw 'Launcher WorkbenchUrl security smoke failed.'
    }
    if (-not (Test-WorkbenchNonce ('ab' * 32)) -or (Test-WorkbenchNonce 'not-a-launch-nonce')) {
        throw 'Launcher Workbench nonce security smoke failed.'
    }
    $workspace = Resolve-HermesWorkspacePath -ConfiguredPath 'workspace'
    if ($workspace -ne [IO.Path]::GetFullPath((Join-Path $bundleRoot 'workspace'))) {
        throw 'Launcher relative workspace resolution smoke failed.'
    }
    Get-ConfiguredWorkspacePath | Out-Null
    $bundleRootRejected = $false
    try { Resolve-HermesWorkspacePath -ConfiguredPath $bundleRoot | Out-Null }
    catch { $bundleRootRejected = $true }
    if (-not $bundleRootRejected) { throw 'Launcher install-root workspace rejection smoke failed.' }
    foreach ($blockedWorkspace in @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [string]$env:OneDrive
    )) {
        if ([string]::IsNullOrWhiteSpace($blockedWorkspace)) { continue }
        $rejected = $false
        try { Resolve-HermesWorkspacePath -ConfiguredPath $blockedWorkspace | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Launcher broad workspace rejection smoke failed.' }
    }
    Write-Host 'Launcher endpoint and process-ownership security smoke passed.' -ForegroundColor Green
    return
}

Push-Location $bundleRoot
try {
    $script:workspacePath = Get-ConfiguredWorkspacePath
    $env:HERMES_HOST_WORKSPACE_PATH = $script:workspacePath
    $env:HERMES_WORKSPACE_PATH = $script:workspacePath
    $script:workbenchUrl = Get-ConfiguredWorkbenchUrl
    Start-DockerDesktop
    Start-Serena
    $env:HERMES_IMAGE_REFERENCE = Get-HermesLaunchImageReference
    & docker compose up -d --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed to start Hermes.' }

    $serenaConfigurationChanged = Ensure-SerenaMcpConfiguration
    $visionConfigurationChanged = Ensure-HermesVisionConfiguration
    $workspaceConfigurationChanged = Ensure-HermesWorkspaceConfiguration
    if ($serenaConfigurationChanged -or $visionConfigurationChanged -or $workspaceConfigurationChanged -or $script:serenaProcessStarted) {
        Write-Host 'Refreshing Hermes with the current Serena and vision configuration...' -ForegroundColor Cyan
        & docker restart hermes | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Hermes could not refresh its local integration configuration.' }
    }
    Wait-HermesGateway
    Write-HermesRuntimeIdentity
    Start-AssistantConversationBus
    Start-WorkbenchWeb
    $nativeClientStarted = Start-ConfiguredClient
    if (-not $nativeClientStarted) {
        if (-not $NoBrowser) {
            Start-Process $script:workbenchUrl
        }
    }
    Write-Host 'Photon, Serena, the Assistant Conversation Bus, and Phos Agape Aphthartos are running.' -ForegroundColor Green
}
finally {
    Pop-Location
}
