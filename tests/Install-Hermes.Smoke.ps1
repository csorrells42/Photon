[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$installerScript = @(
    (Join-Path $testRoot 'Install-Hermes.ps1'),
    (Join-Path $testRoot 'remote-install\Install-Hermes.ps1')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $installerScript) { throw 'Install-Hermes.ps1 was not found in the portable bundle or repository layout.' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('HermesInstallSmoke-' + [Guid]::NewGuid().ToString('N'))

function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Label)
    if ([string]$Actual -cne [string]$Expected) { throw "$Label expected '$Expected' but received '$Actual'." }
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $installerScript), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Installer has parse errors: $($errors.Message -join ' | ')" }
    foreach ($functionName in @('Resolve-InstallDestination', 'Resolve-CommandPath')) {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
        }, $true)
        if (-not $definition) { throw "Installer function was not found: $functionName" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }

    $source = (Resolve-Path -LiteralPath (Split-Path -Parent $installerScript)).Path
    $missing = Join-Path $tempRoot 'missing'
    Assert-Equal (Resolve-InstallDestination -Path $missing -ResolvedSource $source) ([IO.Path]::GetFullPath($missing)) 'missing destination'

    $empty = Join-Path $tempRoot 'empty'
    New-Item -ItemType Directory -Path $empty | Out-Null
    Assert-Equal (Resolve-InstallDestination -Path $empty -ResolvedSource $source) ([IO.Path]::GetFullPath($empty)) 'empty destination'

    $unrelated = Join-Path $tempRoot 'unrelated'
    New-Item -ItemType Directory -Path $unrelated | Out-Null
    [IO.File]::WriteAllText((Join-Path $unrelated 'customer-file.txt'), 'preserve me', [Text.UTF8Encoding]::new($false))
    $unrelatedRejected = $false
    try { Resolve-InstallDestination -Path $unrelated -ResolvedSource $source | Out-Null }
    catch { $unrelatedRejected = $_.Exception.Message -like '*not empty*not an existing Hermes Workbench*' }
    if (-not $unrelatedRejected) { throw 'An unrelated nonempty destination was not rejected.' }

    $legacy = Join-Path $tempRoot 'legacy'
    New-Item -ItemType Directory -Path $legacy | Out-Null
    foreach ($name in @('Launch-Hermes.ps1', 'docker-compose.yml', 'launcher.settings.json')) {
        [IO.File]::WriteAllText((Join-Path $legacy $name), 'fixture', [Text.UTF8Encoding]::new($false))
    }
    Assert-Equal (Resolve-InstallDestination -Path $legacy -ResolvedSource $source) ([IO.Path]::GetFullPath($legacy)) 'legacy destination'

    $marked = Join-Path $tempRoot 'marked'
    New-Item -ItemType Directory -Path $marked | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $marked '.hermes-workbench-install.json'),
        '{"protocolVersion":1,"product":"HermesWorkbench"}',
        [Text.UTF8Encoding]::new($false)
    )
    Assert-Equal (Resolve-InstallDestination -Path $marked -ResolvedSource $source) ([IO.Path]::GetFullPath($marked)) 'marked destination'

    $fallbackDirectory = Join-Path $tempRoot 'fallback'
    New-Item -ItemType Directory -Path $fallbackDirectory | Out-Null
    $fallback = Join-Path $fallbackDirectory 'fixture.exe'
    [IO.File]::WriteAllBytes($fallback, [byte[]](1, 2, 3))
    $resolvedFallback = Resolve-CommandPath -Name ('missing-command-' + [Guid]::NewGuid().ToString('N')) -FallbackPaths @($fallback)
    Assert-Equal $resolvedFallback ([IO.Path]::GetFullPath($fallback)) 'fallback executable'

    [pscustomobject]@{
        MissingDestinationAllowed = $true
        EmptyDestinationAllowed = $true
        UnrelatedDestinationRejected = $true
        LegacyInstallRecognized = $true
        VersionedMarkerRecognized = $true
        FreshPathFallbackResolved = $true
    } | Format-List
    Write-Host 'Hermes installer destination and command-discovery smoke tests passed.' -ForegroundColor Green
}
finally {
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'HermesInstallSmoke-*' -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        $item = Get-Item -LiteralPath $resolved -Force
        $reparse = @(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $reparse.Count -eq 0) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
