[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test', 'build', 'preview')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
$frontendRoot = Join-Path $PSScriptRoot 'src'
$packagePath = Join-Path $frontendRoot 'package.json'
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Hermes Workbench frontend was not found at $frontendRoot"
}

function Resolve-NpmCommand {
    $command = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = [Collections.Generic.List[string]]::new()
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($nodeCommand) { $candidates.Add((Join-Path (Split-Path -Parent $nodeCommand.Source) 'npm.cmd')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'nodejs\npm.cmd')) }
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'nodejs\npm.cmd')) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\nodejs\npm.cmd')) }

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'npm was not found. Install the Node.js LTS prerequisite or rerun Install Hermes.cmd.'
}

$npm = Resolve-NpmCommand
$npmDirectory = Split-Path -Parent $npm
$pathEntries = @($env:PATH -split ';')
if ($npmDirectory -notin $pathEntries) { $env:PATH = $npmDirectory + ';' + $env:PATH }
Push-Location $frontendRoot
try {
    & $npm run $Action
    $npmExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($npmExitCode -ne 0) { throw "Hermes frontend '$Action' failed with exit code $npmExitCode." }
