[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $projectRoot 'runtime\Build-HermesRuntime.ps1'
$verifyScript = Join-Path $projectRoot 'runtime\Verify-HermesRuntime.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotonRuntimeSmoke-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $tempRoot 'source'
$metadata = Join-Path $tempRoot 'metadata'
$generations = Join-Path $tempRoot 'generations'
$previousGlobalGitConfig = [Environment]::GetEnvironmentVariable('GIT_CONFIG_GLOBAL', 'Process')

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$verifyText = Get-Content -LiteralPath $verifyScript -Raw
Require ($verifyText.Contains('[Convert]::ToBase64String($sourceBytes)')) 'runtime probe must use bounded UTF-8/base64 argument transport'
Require ($verifyText.Contains("compile(base64.b64decode('`$encodedSource'),'<photon-runtime-probe>','exec')")) 'runtime probe must use the fixed one-line Python decoder'
Require ($verifyText.Contains('-c $probeBootstrap')) 'docker must receive the one-line bootstrap rather than multiline source'
Require (-not $verifyText.Contains('-c $probeCode')) 'multiline Python source must never be passed directly through the Windows Docker command line'

function Require-Failure {
    param([scriptblock]$Action, [string]$Code)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -like "*$Code*") { return }
        throw "Expected '$Code', received '$($_.Exception.Message)'."
    }
    throw "Expected '$Code', but the operation succeeded."
}

function Write-Utf8 {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

try {
    $emptyGitConfig = Join-Path $tempRoot 'empty.gitconfig'
    Write-Utf8 $emptyGitConfig ''
    $env:GIT_CONFIG_GLOBAL = $emptyGitConfig
    New-Item -ItemType Directory -Path $source | Out-Null
    Write-Utf8 (Join-Path $source 'Dockerfile') "FROM scratch`nCOPY . /opt/hermes`nARG HERMES_GIT_SHA`n"
    Write-Utf8 (Join-Path $source '.dockerignore') ".git`n"
    Write-Utf8 (Join-Path $source 'app.py') "print('fixture')`n"
    Write-Utf8 (Join-Path $source '.pytest_cache\cache.txt') "excluded`n"
    Write-Utf8 (Join-Path $source 'uv.lock') @'
[[package]]
name = "mem0ai"
version = "2.0.10"

[[package]]
name = "qdrant-client"
version = "1.18.0"
'@
    foreach ($relative in @(
        'agent\workbench_identity.py',
        'hermes_cli\mcp_editor.py',
        'hermes_cli\memory_archive.py',
        'hermes_cli\dashboard_auth\live_principals.py'
    )) { Write-Utf8 (Join-Path $source $relative) "# fixture`n" }

    & git -C $source init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git repository initialization failed.' }
    $fixtureExclude = Join-Path $source '.git\info\exclude'
    & git -C $source config user.email 'runtime-smoke@example.invalid'
    & git -C $source config user.name 'Runtime Smoke'
    & git -C $source remote add origin 'https://github.com/example/photon-runtime-fixture.git'
    & git -c "core.excludesFile=$fixtureExclude" -C $source add --all
    & git -C $source commit --quiet -m 'fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git commit failed.' }
    $commit = (& git -C $source rev-parse HEAD).Trim()

    $prepared = @(& $buildScript -Mode Development -SourceRoot $source -MetadataRoot $metadata -PrepareOnly)
    $prepared = $prepared[-1]
    Require ($prepared.PreparedOnly -eq $true) 'Development preparation did not remain Docker-free.'
    Require ([string]$prepared.Tag -match '^hermes-workbench-runtime:dev-[a-f0-9]{12}-[a-f0-9]{16}$') 'Development tag was not content-derived.'
    Require (Test-Path -LiteralPath $prepared.ManifestPath -PathType Leaf) 'Source manifest was not retained.'
    $preparedManifest = Get-Content -Raw -LiteralPath $prepared.ManifestPath | ConvertFrom-Json
    Require (-not (@($preparedManifest.files.path) -contains '.pytest_cache/cache.txt')) 'Cache content entered the source manifest.'

    Require-Failure {
        & $buildScript -Mode Development -SourceRoot $source -MetadataRoot (Join-Path $tempRoot 'tamper') -PrepareOnly `
            -BeforeSourceRevalidation { param($root) [IO.File]::AppendAllText((Join-Path $root 'app.py'), "# changed`n") }
    } 'staged_context_mismatch'
    & git -c "core.excludesFile=$fixtureExclude" -C $source checkout -- app.py

    [IO.File]::AppendAllText((Join-Path $source 'app.py'), "# dirty`n")
    $releaseLock = Join-Path $tempRoot 'release.lock.json'
    Require-Failure {
        & $buildScript -Mode Release -SourceRoot $source -MetadataRoot (Join-Path $tempRoot 'dirty-release') -PrepareOnly `
            -ExpectedSourceCommit $commit -ExpectedUpstreamRemote 'https://github.com/example/photon-runtime-fixture' `
            -ReleaseLockPath $releaseLock
    } 'release_source_dirty'
    Require (-not (Test-Path -LiteralPath $releaseLock)) 'Dirty release wrote a release lock.'
    & git -c "core.excludesFile=$fixtureExclude" -C $source checkout -- app.py

    $junctionTarget = Join-Path $tempRoot 'junction-target'
    $junction = Join-Path $source 'unsafe-link'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    Write-Utf8 (Join-Path $junctionTarget 'outside.txt') "outside`n"
    New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    Require-Failure {
        & $buildScript -Mode Development -SourceRoot $source -MetadataRoot (Join-Path $tempRoot 'reparse') -PrepareOnly
    } 'source_reparse_rejected'
    [IO.Directory]::Delete($junction)

    $request = Get-Content -Raw -LiteralPath $prepared.BuildRequestPath | ConvertFrom-Json
    $imageId = 'sha256:' + ('a' * 64)
    $labels = [ordered]@{}
    foreach ($property in $request.labels.PSObject.Properties) { $labels[$property.Name] = [string]$property.Value }
    $labels['io.photon.workbench.source.digest'] = 'b' * 64
    $inspectionPath = Join-Path $tempRoot 'inspection.json'
    Write-Utf8 $inspectionPath ([ordered]@{
        tag = [ordered]@{ Id = $imageId }
        image = [ordered]@{ Id = $imageId; Config = [ordered]@{ Labels = $labels } }
    } | ConvertTo-Json -Depth 8)
    $requiredFiles = [ordered]@{
        '/opt/hermes/agent/workbench_identity.py' = $true
        '/opt/hermes/hermes_cli/mcp_editor.py' = $true
        '/opt/hermes/hermes_cli/memory_archive.py' = $true
        '/opt/hermes/hermes_cli/dashboard_auth/live_principals.py' = $true
    }
    $probePath = Join-Path $tempRoot 'probe.json'
    Write-Utf8 $probePath ([ordered]@{
        mem0Version = '2.0.10'
        qdrantClientVersion = '1.18.0'
        workbenchMode = $true
        identityMarker = '[photos-agape-aphthartos:workbench-identity:v1]'
        requiredFiles = $requiredFiles
    } | ConvertTo-Json -Depth 6)
    Require-Failure {
        & $verifyScript -BuildRequestPath $prepared.BuildRequestPath -GenerationRoot $generations -TestOnly `
            -InspectionFixturePath $inspectionPath -ProbeFixturePath $probePath
    } 'runtime_label_mismatch'

    $correctLabels = [ordered]@{}
    foreach ($property in $request.labels.PSObject.Properties) { $correctLabels[$property.Name] = [string]$property.Value }
    Write-Utf8 $inspectionPath ([ordered]@{
        tag = [ordered]@{ Id = $imageId }
        image = [ordered]@{ Id = $imageId; Config = [ordered]@{ Labels = $correctLabels } }
    } | ConvertTo-Json -Depth 8)
    $verified = @(& $verifyScript -BuildRequestPath $prepared.BuildRequestPath -GenerationRoot $generations -TestOnly `
        -InspectionFixturePath $inspectionPath -ProbeFixturePath $probePath)
    $verified = $verified[-1]
    $lockPath = Join-Path $verified.GenerationPath 'runtime-image.lock.json'
    $envPath = Join-Path $verified.GenerationPath 'runtime-image.env'
    Require (Test-Path -LiteralPath $lockPath -PathType Leaf) 'Verified generation lock is missing.'
    Require ((Get-Content -Raw -LiteralPath $envPath).Trim() -ceq "HERMES_IMAGE_REFERENCE=$imageId") 'Generation env did not bind the exact image ID.'
    Require ((Get-Item -LiteralPath $lockPath -Force).IsReadOnly) 'Generation lock is not read-only.'

    Write-Host 'Photon full-source runtime smoke passed: preparation, tamper, dirty release, reparse, labels, probe, and generation binding.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($tempRoot)
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolved.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved) -like 'PhotonRuntimeSmoke-*') {
            Get-ChildItem -LiteralPath $resolved -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { $_.IsReadOnly = $false }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    if ($null -eq $previousGlobalGitConfig) { Remove-Item Env:\GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue }
    else { $env:GIT_CONFIG_GLOBAL = $previousGlobalGitConfig }
}
