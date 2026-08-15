[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$logsPath = Join-Path $bundleRoot 'logs'
. (Join-Path $bundleRoot 'Photon-McpGateway.ps1')

Test-PhotonMcpGatewaySecuritySmoke

$researchProfilePath = Join-Path $bundleRoot 'mcp-profiles\photon-web-research.yaml'
$researchProfileText = Get-Content -Raw -LiteralPath $researchProfilePath
$requiredResearchEvidence = @(
    'id: photon-web-research',
    'mcp/fetch@sha256:1a7a0996a565a0b8ca5c41b42830d4e5f334d33f851596bbd9debb2beedb22d3',
    'mcp/duckduckgo@sha256:5617c4c60f48f47556c0735de7a73f6abd731c132c65ec8c720b511819c9f88f',
    'mcp/markitdown@sha256:9cb5f26d30b609a1d5560a090371cb9dba84fc32e7df10e5496aa22aa7b52658',
    'name: convert_to_markdown'
)
foreach ($evidence in $requiredResearchEvidence) {
    if (-not $researchProfileText.Contains($evidence)) {
        throw "The web-research profile is missing pinned evidence: $evidence"
    }
}
if ($researchProfileText -notmatch '(?m)^ {6}tools:\r?\n {8}- search\r?$' -or
    $researchProfileText -match '(?m)^ {8}- fetch_content\r?$') {
    throw 'The web-research profile does not preserve the DuckDuckGo search-only allowlist.'
}
if ($researchProfileText -match '(?i)C:/Users/|desktop-commander\.paths') {
    throw 'The web-research profile contains a machine-specific or foreign workspace binding.'
}

$rootResearchHash = (Get-FileHash -LiteralPath $researchProfilePath -Algorithm SHA256).Hash
$remoteResearchPath = Join-Path $bundleRoot 'remote-install\mcp-profiles\photon-web-research.yaml'
$remoteResearchHash = (Get-FileHash -LiteralPath $remoteResearchPath -Algorithm SHA256).Hash
if ($rootResearchHash -cne $remoteResearchHash) {
    throw 'Root and portable web-research profiles are not byte-identical.'
}

$launcherText = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'Launch-Hermes.ps1')
$shutdownText = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'Shutdown-Hermes.ps1')
$lifecycleText = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'Photon-McpGateway.ps1')
if ($launcherText -notmatch 'Start-PhotonMcpGateway' -or $launcherText -notmatch 'Ensure-PhotonMcpHermesConfiguration') {
    throw 'The launcher does not mount the Photon Docker MCP lifecycle.'
}
if ($lifecycleText -notmatch 'MCP_GATEWAY_DOCKER_BIND_ALLOWED_PATHS' -or
    $lifecycleText -notmatch '-WorkingDirectory \$WorkspacePath') {
    throw 'The Photon MCP gateway does not enforce the exact read-only workspace mount root.'
}
if ($shutdownText -notmatch "photon-mcp\.pid" -or $shutdownText -notmatch "Port = 9131") {
    throw 'The shutdown workflow does not own the Photon Docker MCP listener.'
}

Write-Host 'Photon Docker MCP lifecycle smoke passed.' -ForegroundColor Green
