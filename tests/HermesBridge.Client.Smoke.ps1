[CmdletBinding()]
param([int]$TimeoutSec = 5)

$ErrorActionPreference = 'Stop'
$endpoint = 'http://127.0.0.1:8972'
$settingsPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'HermesWorkbench\conversation-bridge.json'
$passed = 0
$skipped = 0

function Add-Pass([string]$Name) {
    $script:passed++
    Write-Output "PASS $Name"
}

function Invoke-SafeWebRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [hashtable]$Headers
    )
    try {
        $parameters = @{
            Uri = $Uri
            Method = 'Get'
            TimeoutSec = $TimeoutSec
            UseBasicParsing = $true
            ErrorAction = 'Stop'
        }
        if ($null -ne $Headers) { $parameters.Headers = $Headers }
        $response = Invoke-WebRequest @parameters
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = [string]$response.Content }
    }
    catch {
        $statusCode = 0
        if ($null -ne $_.Exception.Response -and $null -ne $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        return [pscustomobject]@{ StatusCode = $statusCode; Content = $null }
    }
}

$health = Invoke-SafeWebRequest -Uri "$endpoint/health"
if ($health.StatusCode -ne 200) {
    throw 'Hermes bridge health did not return HTTP 200 on 127.0.0.1:8972.'
}
try { $healthJson = $health.Content | ConvertFrom-Json }
catch { throw 'Hermes bridge health did not return valid JSON.' }
if ($healthJson.service -ne 'Hermes Conversation Bridge' -or $healthJson.state -ne 'running' -or [int]$healthJson.port -ne 8972) {
    throw 'Hermes bridge health returned an unexpected service identity.'
}
Add-Pass 'health is the expected loopback service'

$unauthenticated = Invoke-SafeWebRequest -Uri "$endpoint/v1/session"
if ($unauthenticated.StatusCode -ne 401) {
    throw 'Hermes bridge session did not reject an unauthenticated request with HTTP 401.'
}
Add-Pass 'unauthenticated session returns 401'

if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    $skipped++
    Write-Output 'SKIP authenticated session because local bridge settings do not exist'
}
else {
    $settingsFile = Get-Item -LiteralPath $settingsPath
    if ($settingsFile.Length -le 0 -or $settingsFile.Length -gt 16384) {
        throw 'Hermes bridge settings are empty or exceed the smoke-test size limit.'
    }
    try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
    catch { throw 'Hermes bridge settings are not valid JSON.' }
    $token = [string]$settings.authenticationToken
    if ([int]$settings.port -ne 8972 -or $token -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Hermes bridge settings are incomplete or invalid.'
    }
    $headers = @{ Authorization = "Bearer $token" }
    try {
        $authenticated = Invoke-SafeWebRequest -Uri "$endpoint/v1/session" -Headers $headers
        if ($authenticated.StatusCode -ne 200) {
            throw 'Hermes bridge authenticated session did not return HTTP 200.'
        }
        Add-Pass 'authenticated session succeeds when settings exist'
    }
    finally {
        $headers.Clear()
        $headers = $null
        $token = $null
        $settings = $null
    }
}

Write-Output "RESULT $passed passed, 0 failed, $skipped skipped"
