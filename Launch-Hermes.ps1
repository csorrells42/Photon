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
$script:nativeHermesRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'hermes')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

function Test-PhotonPathInsideNativeHermes {
    param([AllowNull()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
        if (-not [IO.Path]::IsPathRooted($expanded)) { return $false }
        $resolved = [IO.Path]::GetFullPath($expanded).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        return [string]::Equals($resolved, $script:nativeHermesRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.StartsWith($script:nativeHermesRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-PhotonIsolatedPathValue {
    param([AllowNull()][string]$PathValue)

    return (@($PathValue -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and -not (Test-PhotonPathInsideNativeHermes -Path $_)
    }) -join ';')
}

function Assert-PhotonPathIsolated {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Purpose
    )

    if (Test-PhotonPathInsideNativeHermes -Path $Path) {
        throw "Photon refused $Purpose from the native Hermes installation: $Path"
    }
}

function Initialize-PhotonNativeHermesIsolation {
    Assert-PhotonPathIsolated -Path $bundleRoot -Purpose 'its install bundle'
    $env:PATH = Get-PhotonIsolatedPathValue -PathValue $env:PATH
    foreach ($name in @('HERMES_HOME', 'HERMES_DESKTOP_HERMES_ROOT', 'HERMES_DESKTOP_HERMES')) {
        Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
    }
}

Initialize-PhotonNativeHermesIsolation
$runtimeGenerationCandidates = @(
    (Join-Path $bundleRoot 'runtime\Runtime.Generation.ps1'),
    (Join-Path (Split-Path -Parent $bundleRoot) 'runtime\Runtime.Generation.ps1')
)
$runtimeGenerationScript = @($runtimeGenerationCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
if ($runtimeGenerationScript.Count -ne 1) {
    throw 'Verified runtime generation support is missing. Reinstall Phos Agape Aphthartos.'
}
. ([string]$runtimeGenerationScript[0])
$previousComposeFile = [Environment]::GetEnvironmentVariable('COMPOSE_FILE', 'Process')

New-Item -ItemType Directory -Force -Path $logsPath | Out-Null
$photonMcpLifecycleScript = Join-Path $bundleRoot 'Photon-McpGateway.ps1'
if (-not (Test-Path -LiteralPath $photonMcpLifecycleScript -PathType Leaf)) {
    throw 'Photon Docker MCP lifecycle support is missing. Reinstall Phos Agape Aphthartos.'
}
. $photonMcpLifecycleScript

if (-not ('HermesLauncherWindowProbe' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class HermesLauncherWindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public static bool HasVisibleTopLevelWindow(int expectedProcessId)
    {
        var found = false;
        EnumWindows((hWnd, _) =>
        {
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == (uint)expectedProcessId && IsWindow(hWnd) && IsWindowVisible(hWnd))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}

function Test-HermesDesktopWindow {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    $Process.Refresh()
    if ($Process.HasExited -or -not $Process.Responding) { return $false }
    # MainWindowHandle is the process-owned WPF top-level window that an operator
    # can actually see and use. A transient or auxiliary visible window is not
    # sufficient evidence that the desktop survived startup.
    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) { return $false }
    return [HermesLauncherWindowProbe]::HasVisibleTopLevelWindow($Process.Id)
}

function Resolve-HermesWorkspacePath {
    param([AllowNull()][string]$ConfiguredPath)

    $configured = if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) { 'workspace' } else { $ConfiguredPath.Trim() }
    if ($configured.Length -gt 1024 -or $configured.IndexOfAny([char[]](0..31)) -ge 0) {
        throw 'WorkspacePath contains invalid characters or is too long.'
    }
    if ($configured -match '^(\\\\[?.]\\|\\\\|//|\\\?\\)') {
        throw 'WorkspacePath must be a local drive path or a bundle-relative path.'
    }

    $resolved = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($configured)) { $configured } else { Join-Path $bundleRoot $configured }))
    $pathRoot = [IO.Path]::GetPathRoot($resolved)
    if ([string]::Equals($resolved.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $pathRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkspacePath cannot be a drive root.'
    }
    $resolvedTrimmed = $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $resolvedTrimmed -PathType Container)) {
        throw "WorkspacePath must already exist as a directory: $resolvedTrimmed"
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
        $dockerReady = $false
        try {
            & docker info *> $null
            $dockerReady = $LASTEXITCODE -eq 0
        }
        catch {
            # Windows PowerShell promotes Docker's unavailable-engine stderr to
            # a terminating NativeCommandError while Desktop is still starting.
        }
        if ($dockerReady) { return }
        Start-Sleep -Seconds 2
    }
    throw "Docker Desktop did not become ready within $TimeoutSeconds seconds."
}

function Start-DockerDesktop {
    $dockerReady = $false
    try {
        & docker info *> $null
        $dockerReady = $LASTEXITCODE -eq 0
    }
    catch {
        # An unavailable engine is the condition this function is responsible
        # for recovering; it is not itself a launcher failure.
    }
    if ($dockerReady) { return }

    $dockerDesktopCandidates = @(
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\DockerDesktop\Docker Desktop.exe'),
        'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    )
    $dockerDesktop = @($dockerDesktopCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    if ($dockerDesktop.Count -ne 1) {
        throw 'Docker Desktop is not installed. Run Install-Hermes.ps1 first.'
    }
    Start-Process -FilePath ([string]$dockerDesktop[0]) -WindowStyle Hidden
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
        $process = if ($listener) { Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue } else { $null }
        $owned = $listener -and $process -and
            [string]$process.CommandLine -match '(^|\s|\")serve(\s|\"|$)'
        try { $owned = $owned -and [IO.Path]::GetFullPath([string]$process.ExecutablePath) -eq $executable }
        catch { $owned = $false }
        if (-not $owned -or -not (Test-AssistantBusHealth)) {
            throw "Port $assistantBusPort is not owned by the verified Assistant Conversation Bus."
        }
        [IO.File]::WriteAllText($pidPath, [string]$listener.OwningProcess)
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
            if (Test-Path -LiteralPath $configured -PathType Leaf) {
                $resolved = [IO.Path]::GetFullPath($configured)
                Assert-PhotonPathIsolated -Path $resolved -Purpose 'its Serena executable'
                return $resolved
            }
        }
    }

    $serena = Get-Command serena -ErrorAction SilentlyContinue
    if ($serena) {
        Assert-PhotonPathIsolated -Path $serena.Source -Purpose 'its Serena executable'
        return $serena.Source
    }

    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if ($uv) {
        Assert-PhotonPathIsolated -Path $uv.Source -Purpose 'its uv executable'
        $toolBin = @(& $uv.Source tool dir --bin 2>$null) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1
        if ($LASTEXITCODE -eq 0 -and $toolBin) {
            foreach ($name in @('serena.exe', 'serena.cmd', 'serena')) {
                $candidate = Join-Path ([string]$toolBin).Trim() $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $resolved = [IO.Path]::GetFullPath($candidate)
                    Assert-PhotonPathIsolated -Path $resolved -Purpose 'its Serena executable'
                    return $resolved
                }
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
    Assert-PhotonPathIsolated -Path $nodeExecutable -Purpose 'its Node executable'
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
    Assert-PhotonPathIsolated -Path $executable -Purpose 'its desktop client'
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
            if ($tracked -and [IO.Path]::GetFullPath($tracked.Path) -eq [IO.Path]::GetFullPath($executable)) {
                if (Test-HermesDesktopWindow -Process $tracked) {
                    # A transient startup HWND is not proof that the desktop is
                    # still usable. Recheck after the WebView has had time to
                    # initialize; a windowless process must be replaced.
                    Start-Sleep -Seconds 3
                    if (Test-HermesDesktopWindow -Process $tracked) { return $true }
                }
                Stop-Process -Id $tracked.Id -ErrorAction Stop
                $tracked.WaitForExit()
            }
        }
    }
    $env:HERMES_WORKSPACE_PATH = $script:workspacePath
    $env:HERMES_INSTALL_ROOT = $bundleRoot
    $env:HERMES_WORKBENCH_URL = $script:workbenchUrl
    $env:HERMES_WORKBENCH_NONCE = $script:workbenchNonce
    $process = Start-Process -FilePath $executable -WorkingDirectory $workingDirectory -PassThru
    [IO.File]::WriteAllText($clientPidPath, [string]$process.Id)
    Write-ProcessIdentity -ProcessId $process.Id -Name 'client'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $usableSince = $null
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw 'The desktop client exited before its window opened.' }
        if (Test-HermesDesktopWindow -Process $process) {
            if ($null -eq $usableSince) {
                $usableSince = [DateTime]::UtcNow
            }
            elseif (([DateTime]::UtcNow - $usableSince).TotalSeconds -ge 3) {
                return $true
            }
        }
        else {
            $usableSince = $null
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'The desktop client did not open a usable window.'
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
    & docker exec photon hermes config set --force mcp_servers.serena.url 'http://host.docker.internal:9121/mcp' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Serena MCP endpoint in Hermes.' }
    & docker exec photon hermes config set --force mcp_servers.serena.headers.Host 'localhost:9121' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure Serena MCP host-header protection in Hermes.' }
    return $true
}

function Get-HermesConfigValue {
    param([Parameter(Mandatory)][string]$Key)

    $value = (& docker exec photon hermes config get $Key 2>$null) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { return '' }
    return $value.Trim()
}

function Ensure-HermesVisionConfiguration {
    $visionProvider = Get-HermesConfigValue -Key 'auxiliary.vision.provider'
    if (-not [string]::IsNullOrWhiteSpace($visionProvider) -and $visionProvider -ne 'auto') { return $false }

    $mainProvider = Get-HermesConfigValue -Key 'model.provider'
    if ($mainProvider -eq 'gemini') {
        Write-Host 'Hermes image analysis is using the configured Gemini provider through automatic vision routing.' -ForegroundColor DarkGray
        return $false
    }
    if ($mainProvider -ne 'openrouter') {
        Write-Warning "Hermes image analysis still needs a vision provider. Current main provider '$mainProvider' is not configured automatically; choose one with 'hermes tools configure'."
        return $false
    }

    Write-Host 'Enabling Hermes image analysis through the configured OpenRouter account...' -ForegroundColor Cyan
    & docker exec photon hermes config set --force auxiliary.vision.provider openrouter | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Hermes auxiliary vision provider.' }
    return $true
}

function Ensure-HermesWorkspaceConfiguration {
    if ((Get-HermesConfigValue -Key 'terminal.cwd') -eq '/workspace') { return $false }
    Write-Host 'Aligning Photon terminal tools with the dedicated workspace...' -ForegroundColor Cyan
    & docker exec photon hermes config set --force terminal.cwd /workspace | Out-Null
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
        $containerJson = (& docker inspect photon 2>$null) -join [Environment]::NewLine
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
            containerName = 'photon'
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
    if ((Resolve-HermesWorkspacePath -ConfiguredPath $bundleRoot) -cne [IO.Path]::GetFullPath($bundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
        throw 'Launcher broad workspace acceptance smoke failed.'
    }
    $isolatedTestPath = Get-PhotonIsolatedPathValue -PathValue "$script:nativeHermesRoot\bin;$bundleRoot"
    if (($isolatedTestPath -split ';' | Where-Object { Test-PhotonPathInsideNativeHermes -Path $_ }).Count -ne 0) {
        throw 'Launcher native Hermes PATH isolation smoke failed.'
    }
    $nativeHermesExecutableRejected = $false
    try { Assert-PhotonPathIsolated -Path "$script:nativeHermesRoot\bin\uvx.exe" -Purpose 'a smoke-test executable' }
    catch { $nativeHermesExecutableRejected = $true }
    if (-not $nativeHermesExecutableRejected) { throw 'Launcher native Hermes executable rejection smoke failed.' }
    Test-PhotonMcpGatewaySecuritySmoke
    Write-Host 'Launcher endpoint, process ownership, native Hermes isolation, and unrestricted workspace smoke passed.' -ForegroundColor Green
    return
}

Push-Location $bundleRoot
try {
    $script:workspacePath = Get-ConfiguredWorkspacePath
    $env:HERMES_HOST_WORKSPACE_PATH = $script:workspacePath
    $env:HERMES_WORKSPACE_PATH = $script:workspacePath
    $script:workbenchUrl = Get-ConfiguredWorkbenchUrl
    Start-DockerDesktop
    $photonMcpGatewayStarted = Start-PhotonMcpGateway -WorkspacePath $script:workspacePath
    Start-Serena
    $env:HERMES_IMAGE_REFERENCE = Get-HermesLaunchImageReference
    $gpuEnabled = Set-PhotonRuntimeComposeSelection -BundleRoot $bundleRoot -ImageReference $env:HERMES_IMAGE_REFERENCE
    if ($gpuEnabled) { Write-Host 'NVIDIA acceleration enabled for Photon speech recognition.' -ForegroundColor Green }
    else { Write-Host 'NVIDIA acceleration unavailable; Photon speech recognition will use CPU fallback.' -ForegroundColor DarkGray }
    & docker compose up -d --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed to start Hermes.' }

    $serenaConfigurationChanged = Ensure-SerenaMcpConfiguration
    $photonMcpConfigurationChanged = Ensure-PhotonMcpHermesConfiguration
    $visionConfigurationChanged = Ensure-HermesVisionConfiguration
    $workspaceConfigurationChanged = Ensure-HermesWorkspaceConfiguration
    if ($serenaConfigurationChanged -or $photonMcpConfigurationChanged -or $photonMcpGatewayStarted -or $visionConfigurationChanged -or $workspaceConfigurationChanged -or $script:serenaProcessStarted) {
        Write-Host 'Refreshing Hermes with the current Serena, Docker MCP, and vision configuration...' -ForegroundColor Cyan
        & docker restart photon | Out-Null
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
    Write-Host 'Photon, Docker MCP tools, Serena, the Assistant Conversation Bus, and Phos Agape Aphthartos are running.' -ForegroundColor Green
}
finally {
    if ($null -eq $previousComposeFile) { Remove-Item Env:\COMPOSE_FILE -ErrorAction SilentlyContinue }
    else { $env:COMPOSE_FILE = $previousComposeFile }
    Pop-Location
}
