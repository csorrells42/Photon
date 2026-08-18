[CmdletBinding()]
param([switch]$Json)

$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $env:LOCALAPPDATA 'HermesWorkbench\conversation-bridge.json'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw 'Hermes bridge settings do not exist yet. Launch Hermes Workbench once, then run this script again.'
}
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if (-not ($settings.port -is [int]) -or [string]::IsNullOrWhiteSpace([string]$settings.authenticationToken)) {
    throw 'Hermes bridge settings are incomplete. Restart Hermes Workbench to regenerate them.'
}
$result = [ordered]@{ endpoint = "http://127.0.0.1:$($settings.port)"; bridgeCode = [string]$settings.authenticationToken; settingsPath = $settingsPath }
if ($Json) { $result | ConvertTo-Json; exit 0 }
Write-Host 'Hermes conversation bridge' -ForegroundColor Cyan
Write-Host "Endpoint:    $($result.endpoint)"
Write-Host "Bridge code: $($result.bridgeCode)"
Write-Host ''
Write-Host 'Treat the bridge code like a password. The bridge accepts it only on this computer.' -ForegroundColor Yellow
