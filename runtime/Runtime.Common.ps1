$script:PhotonRuntimeProtocolVersion = 1
$script:PhotonRuntimeSourceManifestSchema = 'photon.runtime.source-manifest/v1'
$script:PhotonRuntimeSourceLockSchema = 'photon.runtime.source-lock/v1'
$script:PhotonRuntimeBuildRequestSchema = 'photon.runtime.build-request/v1'
$script:PhotonRuntimeImageLockSchema = 'photon.runtime.image-lock/v1'

function Get-PhotonSha256File {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PhotonSha256Text {
    param([Parameter(Mandatory = $true)][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally {
        $algorithm.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Write-PhotonJsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value,
        [switch]$CreateNew
    )
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json = $Value | ConvertTo-Json -Depth 12
    $encoding = [Text.UTF8Encoding]::new($false)
    if ($CreateNew) {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = $encoding.GetBytes($json)
            try { $stream.Write($bytes, 0, $bytes.Length) }
            finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        }
        finally { $stream.Dispose() }
    }
    else {
        [IO.File]::WriteAllText($Path, $json, $encoding)
    }
}

function Test-PhotonGitMetadataPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    return $RelativePath -eq '.git' -or $RelativePath.StartsWith('.git/', [StringComparison]::OrdinalIgnoreCase)
}

function Test-PhotonExcludedSourcePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized -split '/')
    $blockedSegments = @(
        'node_modules', 'dist', 'build', 'bin', 'obj', 'coverage',
        '.venv', 'venv', '__pycache__', '.pytest_cache', '.mypy_cache', '.ruff_cache',
        '.cache', '.npm', '.pnpm-store', '.uv-cache', 'data', 'logs', 'runtime',
        '.hermes', '.hermes-docker', 'vault', 'artifacts'
    )
    foreach ($segment in $segments) {
        if ($segment.ToLowerInvariant() -in $blockedSegments) { return $true }
    }
    $leaf = $segments[-1].ToLowerInvariant()
    return $false
}

function Test-PhotonSecretSourcePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $segments = @($RelativePath.Replace('\', '/') -split '/')
    $leaf = $segments[-1].ToLowerInvariant()
    if ($leaf -eq '.env' -or $leaf.StartsWith('.env.')) { return $leaf -ne '.env.example' }
    if ($leaf -in @('auth.json', 'credentials.json', 'secrets.json', '.pypirc', 'nuget.config')) { return $true }
    return [IO.Path]::GetExtension($leaf) -in @('.key', '.pem', '.pfx', '.p12', '.ppk')
}

function Test-PhotonSensitiveConfigContent {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$FullPath
    )
    if ([IO.Path]::GetFileName($RelativePath) -ine '.npmrc') { return $false }
    if ((Get-Item -LiteralPath $FullPath).Length -gt 262144) { return $true }
    $text = Get-Content -Raw -LiteralPath $FullPath
    return $text -match '(?im)^\s*(?:_auth|_authToken|username|password)\s*=' -or
        $text -match '(?i)https?://[^/\s:@]+:[^/\s@]+@'
}

function Get-PhotonSourceInventory {
    param([Parameter(Mandatory = $true)][string]$Root)
    $resolved = (Resolve-Path -LiteralPath $Root).Path
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'source_root_reparse_or_invalid'
    }
    $prefix = $resolved.TrimEnd('\') + '\'
    $entries = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $directories.Push($rootItem)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            $relative = $item.FullName.Substring($prefix.Length).Replace('\', '/')
            if (Test-PhotonGitMetadataPath $relative) { continue }
            if (Test-PhotonExcludedSourcePath $relative) { continue }
            if (Test-PhotonSecretSourcePath $relative) { throw "secret_source_path_rejected:$relative" }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "source_reparse_rejected:$relative"
            }
            if ($item.PSIsContainer) {
                $directories.Push($item)
                continue
            }
            if (Test-PhotonSensitiveConfigContent $relative $item.FullName) { throw "secret_source_content_rejected:$relative" }
            $entries.Add([pscustomobject][ordered]@{
                path = $relative
                length = [long]$item.Length
                sha256 = Get-PhotonSha256File $item.FullName
            })
        }
    }
    $ordered = @($entries | Sort-Object -Property path)
    if ($ordered.Count -eq 0) { throw 'source_context_empty' }
    return $ordered
}

function Get-PhotonInventoryDigest {
    param([Parameter(Mandatory = $true)][object[]]$Entries)
    $builder = [Text.StringBuilder]::new()
    foreach ($entry in $Entries) {
        [void]$builder.Append([string]$entry.path).Append("`n")
        [void]$builder.Append([string]$entry.length).Append("`n")
        [void]$builder.Append(([string]$entry.sha256).ToLowerInvariant()).Append("`n")
    }
    return Get-PhotonSha256Text $builder.ToString()
}

function Assert-PhotonInventoryEqual {
    param(
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][object[]]$Actual,
        [Parameter(Mandatory = $true)][string]$FailureCode
    )
    if ($Expected.Count -ne $Actual.Count) { throw $FailureCode }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        $left = $Expected[$index]
        $right = $Actual[$index]
        if ([string]$left.path -cne [string]$right.path -or
            [long]$left.length -ne [long]$right.length -or
            [string]$left.sha256 -cne [string]$right.sha256) {
            throw $FailureCode
        }
    }
}

function Copy-PhotonSourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][object[]]$Inventory
    )
    $source = (Resolve-Path -LiteralPath $SourceRoot).Path
    New-Item -ItemType Directory -Path $StageRoot -Force | Out-Null
    $stage = (Resolve-Path -LiteralPath $StageRoot).Path
    $stagePrefix = $stage.TrimEnd('\') + '\'
    foreach ($entry in $Inventory) {
        $relative = ([string]$entry.path).Replace('/', '\')
        $inputPath = [IO.Path]::GetFullPath((Join-Path $source $relative))
        $outputPath = [IO.Path]::GetFullPath((Join-Path $stage $relative))
        if (-not $outputPath.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'stage_path_escape'
        }
        $parent = Split-Path -Parent $outputPath
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $inputPath -Destination $outputPath
    }
    $stageInventory = @(Get-PhotonSourceInventory $stage)
    Assert-PhotonInventoryEqual $Inventory $stageInventory 'staged_context_mismatch'
}

function Get-PhotonGitOutput {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $root = (Resolve-Path -LiteralPath $SourceRoot).Path
    $gitDirectory = Join-Path $root '.git'
    if (-not (Test-Path -LiteralPath $gitDirectory)) { throw 'source_git_metadata_missing' }
    $excludeFile = Join-Path $gitDirectory 'info\exclude'
    $output = @(& git -c "core.excludesFile=$excludeFile" "--git-dir=$gitDirectory" "--work-tree=$root" @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "git_command_failed:$($Arguments[0])" }
    return (($output -join "`n").Trim())
}

function ConvertTo-PhotonNormalizedRemote {
    param([Parameter(Mandatory = $true)][string]$Remote)
    $value = $Remote.Trim()
    if ($value -match '^git@github\.com:(.+)$') { $value = 'https://github.com/' + $Matches[1] }
    if ($value.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) { $value = $value.Substring(0, $value.Length - 4) }
    return $value.TrimEnd('/').ToLowerInvariant()
}

function Get-PhotonLockedPackageVersion {
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][string]$Package
    )
    $text = Get-Content -Raw -LiteralPath $LockPath
    $escaped = [Regex]::Escape($Package)
    $match = [Regex]::Match($text, "(?ms)\[\[package\]\]\s*name\s*=\s*`"$escaped`"\s*version\s*=\s*`"([^`"]+)`"")
    if (-not $match.Success) { throw "locked_package_missing:$Package" }
    return $match.Groups[1].Value
}
