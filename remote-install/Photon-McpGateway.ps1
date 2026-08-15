[CmdletBinding()]
param()

$script:photonMcpPort = 9131
$script:photonMcpServerName = 'photon_docker_gateway'
$script:photonMcpDefaultProfile = 'photon-engineering-discovery'
$script:photonMcpRepairProfile = 'photon-relentless-repair'
$script:photonMcpResearchProfile = 'photon-web-research'
$script:photonMcpProfileRoot = Join-Path $bundleRoot 'mcp-profiles'
$script:photonMcpPidPath = Join-Path $logsPath 'photon-mcp.pid'
$script:photonMcpIdentityPath = Join-Path $logsPath 'photon-mcp.process.json'
$script:photonMcpTokenPath = Join-Path $logsPath 'photon-mcp.token'
$script:photonMcpConfigMarkerPath = Join-Path $logsPath 'photon-mcp.config.sha256'
$script:photonMcpStdoutPath = Join-Path $logsPath 'photon-mcp.out.log'
$script:photonMcpStderrPath = Join-Path $logsPath 'photon-mcp.err.log'

function Get-PhotonMcpSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-PhotonMcpTextSha256 {
    param([Parameter(Mandatory)][string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    try { return Get-PhotonMcpSha256 -Bytes $bytes }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Invoke-PhotonMcpDockerQuiet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker @Arguments *> $null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorAction }
}

function Assert-PhotonMcpProfileAssets {
    $lockPath = Join-Path $script:photonMcpProfileRoot 'profiles.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw 'Photon Docker MCP profile lock is missing. Reinstall Phos Agape Aphthartos.'
    }
    try { $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json }
    catch { throw 'Photon Docker MCP profile lock is invalid. Reinstall Phos Agape Aphthartos.' }

    $expectedIds = @($script:photonMcpDefaultProfile, $script:photonMcpRepairProfile, $script:photonMcpResearchProfile)
    $profiles = @($lock.profiles)
    if ([int]$lock.version -ne 1 -or $profiles.Count -ne $expectedIds.Count) {
        throw 'Photon Docker MCP profile lock has an unsupported shape.'
    }

    foreach ($id in $expectedIds) {
        $entries = @($profiles | Where-Object { [string]$_.id -ceq $id })
        if ($entries.Count -ne 1) { throw "Photon Docker MCP profile '$id' is not uniquely locked." }
        $entry = $entries[0]
        $fileName = [string]$entry.file
        $expectedHash = [string]$entry.sha256
        if ($fileName -cne "$id.yaml" -or $expectedHash -notmatch '^[A-F0-9]{64}$') {
            throw "Photon Docker MCP profile '$id' has an invalid lock entry."
        }
        $profilePath = Join-Path $script:photonMcpProfileRoot $fileName
        if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
            throw "Photon Docker MCP profile '$id' is missing. Reinstall Phos Agape Aphthartos."
        }
        $actualHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
        if ($actualHash -cne $expectedHash) {
            throw "Photon Docker MCP profile '$id' failed its SHA-256 receipt."
        }
    }
    return $profiles
}

function ConvertTo-PhotonMcpWorkspaceConfigArgument {
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [ValidateSet('desktop-commander.paths', 'markitdown.paths')]
        [string]$ConfigKey = 'desktop-commander.paths',
        [switch]$ContainerPath
    )

    $resolved = if ($ContainerPath) { $WorkspacePath } else { [IO.Path]::GetFullPath($WorkspacePath) }
    if ($resolved.Length -gt 1024 -or $resolved.IndexOfAny([char[]](0..31)) -ge 0 -or $resolved.Contains('"') -or
        ($ContainerPath -and $resolved -notmatch '^/[A-Za-z0-9._ /-]+$')) {
        throw 'The Photon MCP workspace path is invalid or too long.'
    }
    $portable = $resolved.Replace('\', '/')
    return $ConfigKey + '=[\"' + $portable + '\"]'
}

function ConvertTo-PhotonMcpContainerWorkspacePath {
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $resolved = [IO.Path]::GetFullPath($WorkspacePath)
    $root = [IO.Path]::GetPathRoot($resolved)
    if ($root -notmatch '^[A-Za-z]:\\$') {
        throw 'Photon MCP workspace mounts require a local Windows drive.'
    }
    $relative = $resolved.Substring($root.Length).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative)) {
        throw 'Photon MCP workspace mounts cannot expose an entire drive.'
    }
    return '/' + $relative
}

function Set-PhotonMcpWorkspaceProfileConfig {
    param(
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][ValidateSet('desktop-commander.paths', 'markitdown.paths')][string]$ConfigKey,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$CapabilityLabel
    )

    $configuredWorkspacePath = if ($ConfigKey -ceq 'markitdown.paths') {
        ConvertTo-PhotonMcpContainerWorkspacePath -WorkspacePath $WorkspacePath
    } else {
        [IO.Path]::GetFullPath($WorkspacePath).Replace('\', '/')
    }
    $configArgument = ConvertTo-PhotonMcpWorkspaceConfigArgument -WorkspacePath $configuredWorkspacePath `
        -ConfigKey $ConfigKey -ContainerPath:($ConfigKey -ceq 'markitdown.paths')
    if ((Invoke-PhotonMcpDockerQuiet -Arguments @('mcp', 'profile', 'config', $ProfileId, '--set', $configArgument)) -ne 0) {
        throw "Docker could not bind the $CapabilityLabel profile to the Hermes workspace."
    }

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $rawConfig = (& docker mcp profile config $ProfileId --get-all --format json 2>$null) -join [Environment]::NewLine
        $configExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorAction }
    if ($configExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($rawConfig)) {
        throw "Docker could not verify the $CapabilityLabel profile workspace binding."
    }
    try { $config = $rawConfig | ConvertFrom-Json }
    catch { throw "Docker returned malformed $CapabilityLabel profile configuration." }
    $property = $config.PSObject.Properties[$ConfigKey]
    $configuredPaths = @()
    if ($null -ne $property) {
        $configuredPaths = @($property.Value)
    }
    $expectedPath = $configuredWorkspacePath
    if ($configuredPaths.Count -ne 1 -or ([string]$configuredPaths[0]).Replace('\', '/') -cne $expectedPath) {
        throw "The $CapabilityLabel profile is not restricted to the configured Hermes workspace."
    }
}

function Install-PhotonMcpProfiles {
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $profiles = Assert-PhotonMcpProfileAssets
    if ((Invoke-PhotonMcpDockerQuiet -Arguments @('mcp', 'version')) -ne 0) {
        throw 'Docker Desktop MCP Toolkit is unavailable. Update Docker Desktop, then relaunch Phos Agape Aphthartos.'
    }

    foreach ($profile in $profiles) {
        $profilePath = Join-Path $script:photonMcpProfileRoot ([string]$profile.file)
        if ((Invoke-PhotonMcpDockerQuiet -Arguments @('mcp', 'profile', 'import', $profilePath)) -ne 0) {
            throw "Docker could not import Photon MCP profile '$($profile.id)'."
        }
    }

    Set-PhotonMcpWorkspaceProfileConfig -ProfileId $script:photonMcpRepairProfile `
        -ConfigKey 'desktop-commander.paths' -WorkspacePath $WorkspacePath -CapabilityLabel 'repair'
    Set-PhotonMcpWorkspaceProfileConfig -ProfileId $script:photonMcpResearchProfile `
        -ConfigKey 'markitdown.paths' -WorkspacePath $WorkspacePath -CapabilityLabel 'web research'
}

function Get-PhotonMcpGatewayTokenFromLog {
    param([Parameter(Mandatory)][string]$LogText)

    $match = [regex]::Match($LogText, '(?m)^> Use Bearer token: Authorization: Bearer (?<token>[A-Za-z0-9_-]{32,128})\s*$')
    if (-not $match.Success) { return $null }
    return $match.Groups['token'].Value
}

function Get-PhotonMcpGatewayToken {
    if (-not (Test-Path -LiteralPath $script:photonMcpTokenPath -PathType Leaf)) { return $null }
    $token = [IO.File]::ReadAllText($script:photonMcpTokenPath).Trim()
    if ($token -notmatch '^[A-Za-z0-9_-]{32,128}$') { return $null }
    return $token
}

function Test-PhotonMcpGatewayEndpoint {
    try {
        Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$script:photonMcpPort/mcp" `
            -ContentType 'application/json' -Headers @{ Accept = 'application/json, text/event-stream' } `
            -Body '{}' -TimeoutSec 3 | Out-Null
        return $false
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { return $true }
        return $false
    }
}

function Test-PhotonMcpTrackedProcess {
    if (-not (Test-Path -LiteralPath $script:photonMcpPidPath -PathType Leaf)) { return $false }
    $processId = 0
    if (-not [int]::TryParse(([IO.File]::ReadAllText($script:photonMcpPidPath).Trim()), [ref]$processId) -or $processId -le 0) {
        return $false
    }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    try { $isDocker = [IO.Path]::GetFileName([string]$process.ExecutablePath) -ieq 'docker-mcp.exe' }
    catch { $isDocker = $false }
    $command = [string]$process.CommandLine
    $portPattern = '(?i)--port(\s|")+{0}(\s|"|$)' -f $script:photonMcpPort
    return $isDocker -and
        $command -match '(?i)(^|\s|\")mcp(\s|\"|$)' -and
        $command -match '(?i)(^|\s|\")gateway(\s|\"|$)' -and
        $command -match '(?i)(^|\s|\")run(\s|\"|$)' -and
        $command -match ([regex]::Escape($script:photonMcpDefaultProfile)) -and
        $command -match $portPattern
}

function Start-PhotonMcpGateway {
    param([Parameter(Mandatory)][string]$WorkspacePath)

    if ((Test-PhotonMcpTrackedProcess) -and (Get-PhotonMcpGatewayToken) -and (Test-PhotonMcpGatewayEndpoint)) {
        return $false
    }
    if (Test-PhotonMcpGatewayEndpoint) {
        throw "Port $script:photonMcpPort is already serving an unowned MCP gateway. Stop it before launching Phos Agape Aphthartos."
    }

    Install-PhotonMcpProfiles -WorkspacePath $WorkspacePath
    foreach ($path in @($script:photonMcpPidPath, $script:photonMcpIdentityPath, $script:photonMcpTokenPath, $script:photonMcpConfigMarkerPath, $script:photonMcpStdoutPath, $script:photonMcpStderrPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }

    $dockerExecutable = (Get-Command docker -ErrorAction Stop).Source
    $arguments = @(
        'mcp', 'gateway', 'run',
        '--profile', $script:photonMcpDefaultProfile,
        '--transport', 'streaming', '--host', '127.0.0.1', '--port', [string]$script:photonMcpPort,
        '--cpus', '1', '--memory', '768Mb', '--block-secrets', '--verify-signatures'
    )
    $allowedBindPath = ConvertTo-PhotonMcpContainerWorkspacePath -WorkspacePath $WorkspacePath
    $previousAllowedBindPaths = $env:MCP_GATEWAY_DOCKER_BIND_ALLOWED_PATHS
    try {
        $env:MCP_GATEWAY_DOCKER_BIND_ALLOWED_PATHS = $allowedBindPath
        $process = Start-Process -FilePath $dockerExecutable -ArgumentList $arguments -WorkingDirectory $WorkspacePath `
            -WindowStyle Hidden -RedirectStandardOutput $script:photonMcpStdoutPath `
            -RedirectStandardError $script:photonMcpStderrPath -PassThru
    }
    finally {
        if ($null -eq $previousAllowedBindPaths) {
            Remove-Item Env:MCP_GATEWAY_DOCKER_BIND_ALLOWED_PATHS -ErrorAction SilentlyContinue
        } else {
            $env:MCP_GATEWAY_DOCKER_BIND_ALLOWED_PATHS = $previousAllowedBindPaths
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $token = $null
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if (Test-Path -LiteralPath $script:photonMcpStderrPath -PathType Leaf) {
            $logText = Get-Content -Raw -LiteralPath $script:photonMcpStderrPath -ErrorAction SilentlyContinue
            $token = Get-PhotonMcpGatewayTokenFromLog -LogText ([string]$logText)
        }
        $ready = $token -and (Test-PhotonMcpGatewayEndpoint)
    } until ($ready -or $process.HasExited -or [DateTime]::UtcNow -ge $deadline)

    if (-not $ready) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $script:photonMcpPidPath, $script:photonMcpIdentityPath -Force -ErrorAction SilentlyContinue
        $detail = if (Test-Path -LiteralPath $script:photonMcpStderrPath) {
            ((Get-Content -LiteralPath $script:photonMcpStderrPath -Tail 12) -join ' ') -replace 'Bearer [A-Za-z0-9_-]{32,128}', 'Bearer [redacted]'
        } else { 'No Docker MCP diagnostic was produced.' }
        throw "Photon Docker MCP gateway did not become ready. $detail"
    }

    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $script:photonMcpPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    $listenerProcess = if ($listener) { Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction SilentlyContinue } else { $null }
    $listenerOwned = $listenerProcess -and [int]$listenerProcess.ParentProcessId -eq $process.Id -and
        [IO.Path]::GetFileName([string]$listenerProcess.ExecutablePath) -ieq 'docker-mcp.exe' -and
        [string]$listenerProcess.CommandLine -match [regex]::Escape($script:photonMcpDefaultProfile)
    if (-not $listenerOwned) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'Photon Docker MCP endpoint was not owned by the expected Docker MCP child process.'
    }

    [IO.File]::WriteAllText($script:photonMcpPidPath, [string]$listener.OwningProcess)
    if (Get-Command Write-ProcessIdentity -ErrorAction SilentlyContinue) {
        Write-ProcessIdentity -ProcessId $listener.OwningProcess -Name 'photon-mcp' -Port $script:photonMcpPort
    }
    [IO.File]::WriteAllText($script:photonMcpTokenPath, $token, [Text.UTF8Encoding]::new($false))
    try { (Get-Item -LiteralPath $script:photonMcpTokenPath).Attributes = 'Hidden' }
    catch { }
    return $true
}

function Ensure-PhotonMcpHermesConfiguration {
    $token = Get-PhotonMcpGatewayToken
    if (-not $token) { throw 'Photon Docker MCP gateway authentication is unavailable.' }
    $endpoint = "http://host.docker.internal:$script:photonMcpPort/mcp"
    $marker = Get-PhotonMcpTextSha256 -Text "$endpoint`n$token"

    $url = (& docker exec hermes hermes config get "mcp_servers.$script:photonMcpServerName.url" 2>$null) -join [Environment]::NewLine
    $trust = (& docker exec hermes hermes config get "mcp_servers.$script:photonMcpServerName.trust" 2>$null) -join [Environment]::NewLine
    $sampling = (& docker exec hermes hermes config get "mcp_servers.$script:photonMcpServerName.sampling.enabled" 2>$null) -join [Environment]::NewLine
    $elicitation = (& docker exec hermes hermes config get "mcp_servers.$script:photonMcpServerName.elicitation.enabled" 2>$null) -join [Environment]::NewLine
    $recordedMarker = if (Test-Path -LiteralPath $script:photonMcpConfigMarkerPath -PathType Leaf) {
        [IO.File]::ReadAllText($script:photonMcpConfigMarkerPath).Trim()
    } else { '' }
    $changed = $url.Trim() -cne $endpoint -or $trust.Trim() -cne 'untrusted' -or
        $sampling.Trim() -cne 'false' -or $elicitation.Trim() -cne 'false' -or $recordedMarker -cne $marker

    $authorization = "Bearer $token"
    $settings = @(
        @("mcp_servers.$script:photonMcpServerName.url", $endpoint),
        @("mcp_servers.$script:photonMcpServerName.headers.Authorization", $authorization),
        @("mcp_servers.$script:photonMcpServerName.headers.Host", "localhost:$script:photonMcpPort"),
        @("mcp_servers.$script:photonMcpServerName.enabled", 'true'),
        @("mcp_servers.$script:photonMcpServerName.trust", 'untrusted'),
        @("mcp_servers.$script:photonMcpServerName.sampling.enabled", 'false'),
        @("mcp_servers.$script:photonMcpServerName.elicitation.enabled", 'false')
    )
    foreach ($setting in $settings) {
        $arguments = @('exec', 'hermes', 'hermes', 'config', 'set', '--force', [string]$setting[0], [string]$setting[1])
        if ((Invoke-PhotonMcpDockerQuiet -Arguments $arguments) -ne 0) {
            throw "Could not configure Photon Docker MCP setting '$($setting[0])'."
        }
    }
    [IO.File]::WriteAllText($script:photonMcpConfigMarkerPath, $marker, [Text.UTF8Encoding]::new($false))
    return $changed
}

function Test-PhotonMcpGatewaySecuritySmoke {
    Assert-PhotonMcpProfileAssets | Out-Null
    $sampleToken = 'a' * 48
    $sampleLog = "> Use Bearer token: Authorization: Bearer $sampleToken`r`n"
    if ((Get-PhotonMcpGatewayTokenFromLog -LogText $sampleLog) -cne $sampleToken) {
        throw 'Photon MCP token parser security smoke failed.'
    }
    if (Get-PhotonMcpGatewayTokenFromLog -LogText '> Use Bearer token: Authorization: Bearer too-short') {
        throw 'Photon MCP token parser accepted an invalid token.'
    }
    $argument = ConvertTo-PhotonMcpWorkspaceConfigArgument -WorkspacePath (Join-Path $bundleRoot 'workspace')
    if ($argument -notmatch '^desktop-commander\.paths=\[\\"[A-Za-z]:/.+\\"\]$') {
        throw 'Photon MCP workspace argument security smoke failed.'
    }
    $researchPath = ConvertTo-PhotonMcpContainerWorkspacePath -WorkspacePath (Join-Path $bundleRoot 'workspace')
    $researchArgument = ConvertTo-PhotonMcpWorkspaceConfigArgument -WorkspacePath $researchPath -ConfigKey 'markitdown.paths' -ContainerPath
    if ($researchArgument -notmatch '^markitdown\.paths=\[\\"/[A-Za-z0-9._ /-]+\\"\]$') {
        throw 'Photon MCP web-research workspace argument security smoke failed.'
    }
}
