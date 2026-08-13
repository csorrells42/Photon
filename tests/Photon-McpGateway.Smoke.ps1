[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$logsPath = Join-Path $bundleRoot 'logs'
. (Join-Path $bundleRoot 'Photon-McpGateway.ps1')

Test-PhotonMcpGatewaySecuritySmoke

$launcherText = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'Launch-Hermes.ps1')
$shutdownText = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'Shutdown-Hermes.ps1')
if ($launcherText -notmatch 'Start-PhotonMcpGateway' -or $launcherText -notmatch 'Ensure-PhotonMcpHermesConfiguration') {
    throw 'The launcher does not mount the Photon Docker MCP lifecycle.'
}
if ($shutdownText -notmatch "photon-mcp\.pid" -or $shutdownText -notmatch "Port = 9131") {
    throw 'The shutdown workflow does not own the Photon Docker MCP listener.'
}

Write-Host 'Photon Docker MCP lifecycle smoke passed.' -ForegroundColor Green
