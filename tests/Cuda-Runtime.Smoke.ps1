[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeScript = Join-Path $projectRoot 'runtime\Runtime.Generation.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotonCudaRuntimeSmoke-' + [Guid]::NewGuid().ToString('N'))
$previousComposeFile = [Environment]::GetEnvironmentVariable('COMPOSE_FILE', 'Process')

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $tempRoot 'docker-compose.yml'),
        "services:`n  gateway:`n    image: fixture`n",
        [Text.UTF8Encoding]::new($false)
    )
    . $runtimeScript

    $script:gpuProbeArguments = $null
    function Invoke-PhotonRuntimeDocker {
        param([string[]]$Arguments, [switch]$AllowFailure)
        $script:gpuProbeArguments = @($Arguments)
        return [pscustomobject]@{ ExitCode = 0; Output = '' }
    }

    $env:COMPOSE_FILE = 'C:\untrusted\compose.yml'
    $enabled = Set-PhotonRuntimeComposeSelection -BundleRoot $tempRoot -ImageReference ('sha256:' + ('a' * 64))
    Require $enabled 'NVIDIA-capable Docker probe did not enable the GPU overlay.'
    Require ($script:gpuProbeArguments -contains '--gpus') 'GPU probe did not request NVIDIA device injection.'
    Require ($script:gpuProbeArguments -contains '--network') 'GPU probe must disable networking.'
    $composeFiles = @($env:COMPOSE_FILE -split [Regex]::Escape([string][IO.Path]::PathSeparator))
    Require ($composeFiles.Count -eq 2) 'GPU selection did not produce canonical base + generated override files.'
    Require ($composeFiles[0] -ceq [IO.Path]::GetFullPath((Join-Path $tempRoot 'docker-compose.yml'))) 'Inherited COMPOSE_FILE was not replaced.'
    Require (Test-Path -LiteralPath $composeFiles[1] -PathType Leaf) 'Generated GPU override is missing.'
    $override = [IO.File]::ReadAllText($composeFiles[1])
    Require ($override.Contains('driver: nvidia')) 'GPU override does not reserve the NVIDIA driver.'
    Require ($override.Contains('capabilities: [gpu]')) 'GPU override does not declare the required GPU capability.'

    function Invoke-PhotonRuntimeDocker {
        param([string[]]$Arguments, [switch]$AllowFailure)
        return [pscustomobject]@{ ExitCode = 1; Output = 'no NVIDIA runtime' }
    }
    $env:COMPOSE_FILE = 'C:\untrusted\compose.yml'
    $enabled = Set-PhotonRuntimeComposeSelection -BundleRoot $tempRoot -ImageReference 'fixture:cpu'
    Require (-not $enabled) 'CPU-only Docker probe unexpectedly enabled the GPU overlay.'
    Require ($env:COMPOSE_FILE -ceq [IO.Path]::GetFullPath((Join-Path $tempRoot 'docker-compose.yml'))) 'CPU fallback did not retain only the canonical Compose file.'

    $launchText = [IO.File]::ReadAllText((Join-Path $projectRoot 'Launch-Hermes.ps1'))
    $remoteLaunchText = [IO.File]::ReadAllText((Join-Path $projectRoot 'remote-install\Launch-Hermes.ps1'))
    Require ($launchText.Contains('Set-PhotonRuntimeComposeSelection')) 'Primary launcher does not select the GPU overlay.'
    Require ($remoteLaunchText.Contains('Set-PhotonRuntimeComposeSelection')) 'Portable launcher does not select the GPU overlay.'

    Write-Host 'Photon CUDA runtime smoke passed: NVIDIA selection, generated overlay, and CPU fallback.' -ForegroundColor Green
}
finally {
    if ($null -eq $previousComposeFile) { Remove-Item Env:\COMPOSE_FILE -ErrorAction SilentlyContinue }
    else { $env:COMPOSE_FILE = $previousComposeFile }
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($tempRoot)
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolved.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved) -like 'PhotonCudaRuntimeSmoke-*') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
