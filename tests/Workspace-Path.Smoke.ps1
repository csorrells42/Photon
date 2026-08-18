[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$launcherPath = Join-Path $projectRoot 'Launch-Hermes.ps1'
$remoteLauncherPath = Join-Path $projectRoot 'remote-install\Launch-Hermes.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('PhotonWorkspacePathSmoke-' + [Guid]::NewGuid().ToString('N'))

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Require-Rejected {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action | Out-Null }
    catch { return }
    throw $Message
}

try {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($launcherPath, [ref]$tokens, [ref]$errors)
    Require ($errors.Count -eq 0) 'The root launcher does not parse.'
    $resolver = @($ast.FindAll({
        $args[0] -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $args[0].Name -ceq 'Resolve-HermesWorkspacePath'
    }, $true))
    Require ($resolver.Count -eq 1) 'The launcher must define exactly one workspace resolver.'
    $adoptionTokens = $null
    $adoptionErrors = $null
    $adoptionAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $projectRoot 'runtime\Adopt-HermesRuntime.ps1'),
        [ref]$adoptionTokens,
        [ref]$adoptionErrors)
    Require ($adoptionErrors.Count -eq 0) 'The runtime adoption script does not parse.'
    $adoptionResolver = @($adoptionAst.FindAll({
        $args[0] -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $args[0].Name -ceq 'Resolve-HermesWorkspacePath'
    }, $true))
    Require ($adoptionResolver.Count -eq 1) 'Runtime adoption must define exactly one workspace resolver.'
    Require ($resolver[0].Extent.Text -ceq $adoptionResolver[0].Extent.Text) 'Launcher and runtime adoption workspace policies differ.'
    . ([scriptblock]::Create($resolver[0].Extent.Text))

    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $script:bundleRoot = Join-Path $tempRoot 'bundle'
    $portableWorkspace = Join-Path $script:bundleRoot 'workspace'
    $externalWorkspace = Join-Path $tempRoot 'approved-external-workspace'
    New-Item -ItemType Directory -Path $portableWorkspace, $externalWorkspace -Force | Out-Null

    Require ((Resolve-HermesWorkspacePath 'workspace') -ceq [IO.Path]::GetFullPath($portableWorkspace)) 'Portable relative workspace resolution failed.'
    Require ((Resolve-HermesWorkspacePath $externalWorkspace) -ceq [IO.Path]::GetFullPath($externalWorkspace)) 'Existing absolute workspace resolution failed.'
    Require ((Resolve-HermesWorkspacePath $script:bundleRoot) -ceq [IO.Path]::GetFullPath($script:bundleRoot)) 'The install root was not accepted as a workspace.'
    Require-Rejected { Resolve-HermesWorkspacePath ([IO.Path]::GetPathRoot($script:bundleRoot)) } 'A drive root was accepted as a workspace.'
    Require ((Resolve-HermesWorkspacePath '..') -ceq [IO.Path]::GetFullPath($tempRoot)) 'A relative parent workspace was not accepted.'
    Require-Rejected { Resolve-HermesWorkspacePath (Join-Path $tempRoot 'missing') } 'A missing workspace was accepted.'
    $normalFile = Join-Path $tempRoot 'file.txt'
    [IO.File]::WriteAllText($normalFile, 'not a directory')
    Require-Rejected { Resolve-HermesWorkspacePath $normalFile } 'A normal file was accepted as a workspace.'
    Require-Rejected { Resolve-HermesWorkspacePath ($externalWorkspace + ':stream') } 'An alternate data stream was accepted as a workspace.'
    Require ((Resolve-HermesWorkspacePath ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile))) -ceq [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) 'The user profile root was not accepted as a workspace.'
    Require ((Resolve-HermesWorkspacePath ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory))) -ceq [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) 'The Desktop root was not accepted as a workspace.'
    if (-not [string]::IsNullOrWhiteSpace([string]$env:OneDrive)) {
        Require ((Resolve-HermesWorkspacePath ([string]$env:OneDrive)) -ceq [IO.Path]::GetFullPath([string]$env:OneDrive).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) 'The OneDrive root was not accepted as a workspace.'
    }

    $junctionTarget = Join-Path $tempRoot 'junction-target'
    $junctionPath = Join-Path $script:bundleRoot 'junction-workspace'
    New-Item -ItemType Directory -Path $junctionTarget -Force | Out-Null
    New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget | Out-Null
    Require ((Resolve-HermesWorkspacePath $junctionPath) -ceq [IO.Path]::GetFullPath($junctionPath)) 'A junction workspace was not accepted.'

    $launcherText = [IO.File]::ReadAllText($launcherPath)
    $remoteLauncherText = [IO.File]::ReadAllText($remoteLauncherPath)
    Require ($remoteLauncherText.Contains('Launcher broad workspace acceptance smoke failed.')) 'Portable launcher still carries the broad-workspace rejection policy.'
    Require ($launcherText.Contains('$env:HERMES_HOST_WORKSPACE_PATH = $script:workspacePath')) 'Launcher does not export the validated Docker bind authority.'
    Require ($launcherText.Contains("'--project', `$script:workspacePath")) 'Serena is not bound to the validated workspace.'
    Require ($launcherText.Contains("terminal.cwd /workspace")) 'Photon terminal cwd is not aligned with /workspace.'

    $composeText = [IO.File]::ReadAllText((Join-Path $projectRoot 'docker-compose.yml'))
    Require ($composeText -ceq [IO.File]::ReadAllText((Join-Path $projectRoot 'remote-install\docker-compose.yml'))) 'Compose mirrors are not byte-identical.'
    Require ($composeText.Contains('${HERMES_HOST_WORKSPACE_PATH:?launcher must set HERMES_HOST_WORKSPACE_PATH}')) 'Compose does not require the validated workspace authority.'
    Require ($composeText.Contains('create_host_path: false')) 'Compose may implicitly create an unintended host workspace.'
    Require (-not $composeText.Contains('HERMES_WRITE_SAFE_ROOT')) 'A Photon-only file-write permission boundary remains in Compose.'
    Require (-not $composeText.Contains('./workspace:/workspace')) 'The legacy fixed workspace bind is still present.'

    $localSettings = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'launcher.settings.json') | ConvertFrom-Json
    $portableSettings = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'remote-install\launcher.settings.json') | ConvertFrom-Json
    Require ([string]$localSettings.WorkspacePath -ceq 'C:\Users\clsor\Documents\Codex') 'Local settings do not name the shared Codex workspace.'
    Require ([string]$portableSettings.WorkspacePath -ceq 'workspace') 'Portable settings do not use the install-local workspace default.'

    Write-Host 'Unrestricted workspace selection and Docker bind smoke passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop
    }
}
