[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$image = 'photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb'
$dockerArguments = @(
    'run', '-i', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL',
    '--security-opt', 'no-new-privileges:true', '--pids-limit', '64',
    '--memory', '2g', '--cpus', '2', '--user', '65532:65532',
    '--tmpfs', '/workspace:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532',
    '--tmpfs', '/session:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532',
    '--tmpfs', '/tmp:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532',
    $image
)

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'docker.exe'
$startInfo.Arguments = ($dockerArguments -join ' ')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $false

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$started = $false
$phase = 'startup'

function Send-Request {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][int] $Id,
        [Parameter(Mandatory = $true)][string] $Method,
        [Parameter(Mandatory = $true)] $Params,
        [int] $TimeoutMilliseconds = 120000
    )
    $frame = [ordered]@{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Params }
    $Process.StandardInput.WriteLine(($frame | ConvertTo-Json -Depth 12 -Compress))
    $Process.StandardInput.Flush()
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        $readTask = $Process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait($remaining)) { throw "Timed out waiting for MCP response $Id." }
        $line = $readTask.Result
        if ($null -eq $line) { throw "MCP stdout closed while waiting for response $Id." }
        try { $message = $line | ConvertFrom-Json } catch { continue }
        $idProperty = $message.PSObject.Properties['id']
        if ($null -ne $idProperty -and $idProperty.Value -eq $Id) {
            $errorProperty = $message.PSObject.Properties['error']
            if ($null -ne $errorProperty) { throw "MCP request $Id failed: $($errorProperty.Value | ConvertTo-Json -Compress)" }
            return $message.result
        }
    }
    throw "Timed out waiting for MCP response $Id."
}

function Get-ToolText {
    param([Parameter(Mandatory = $true)] $Result)
    $texts = @($Result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { [string]$_.text })
    return ($texts -join "`n")
}

function Test-ToolError {
    param([Parameter(Mandatory = $true)] $Result)
    $property = $Result.PSObject.Properties['isError']
    return $null -ne $property -and $property.Value -eq $true
}

try {
    if (-not $process.Start()) { throw 'Failed to start the geometry container.' }
    $started = $true

    $phase = 'initialize'
    $initialize = Send-Request -Process $process -Id 1 -Method 'initialize' -Params ([ordered]@{
        protocolVersion = '2025-06-18'
        capabilities = [ordered]@{}
        clientInfo = [ordered]@{ name = 'photon-runtime-smoke'; version = '0.1.0' }
    }) -TimeoutMilliseconds 30000
    if ($initialize.protocolVersion -ne '2025-06-18') { throw 'Unexpected MCP protocol version.' }

    $notification = [ordered]@{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = [ordered]@{} }
    $process.StandardInput.WriteLine(($notification | ConvertTo-Json -Depth 5 -Compress))
    $process.StandardInput.Flush()

    $phase = 'tools-list'
    $tools = Send-Request -Process $process -Id 2 -Method 'tools/list' -Params ([ordered]@{}) -TimeoutMilliseconds 30000
    $toolNames = @($tools.tools | ForEach-Object { [string]$_.name })
    foreach ($required in @('execute', 'measure', 'validate', 'export')) {
        if ($toolNames -notcontains $required) { throw "Pinned geometry server is missing tool: $required" }
    }

    $phase = 'create'
    $execute = Send-Request -Process $process -Id 3 -Method 'tools/call' -Params ([ordered]@{
        name = 'execute'
        arguments = [ordered]@{ code = "from build123d import *`nsmoke_box = Box(10, 20, 30)`nshow(smoke_box, 'smoke_box')" }
    })
    $executeText = Get-ToolText $execute
    if ((Test-ToolError $execute) -or $executeText -notmatch 'smoke_box') { throw "Geometry creation failed: $executeText" }

    $phase = 'measure'
    $measure = Send-Request -Process $process -Id 4 -Method 'tools/call' -Params ([ordered]@{
        name = 'measure'
        arguments = [ordered]@{ object_name = 'smoke_box' }
    })
    $measureText = Get-ToolText $measure
    if ((Test-ToolError $measure) -or $measureText -notmatch '6000') { throw "Geometry measurement failed: $measureText" }

    $phase = 'validate'
    $validate = Send-Request -Process $process -Id 5 -Method 'tools/call' -Params ([ordered]@{
        name = 'validate'
        arguments = [ordered]@{ object_name = 'smoke_box' }
    })
    $validateText = Get-ToolText $validate
    if ((Test-ToolError $validate) -or $validateText -notmatch 'PASS') { throw "Geometry validation failed: $validateText" }

    $phase = 'step-export'
    $export = Send-Request -Process $process -Id 6 -Method 'tools/call' -Params ([ordered]@{
        name = 'export'
        arguments = [ordered]@{ filename = '/workspace/smoke-box.step'; format = 'step'; object_name = 'smoke_box' }
    })
    $exportText = Get-ToolText $export
    if ((Test-ToolError $export) -or $exportText -notmatch 'smoke-box\.step') { throw "STEP export failed: $exportText" }

    [pscustomobject]@{
        Protocol = $initialize.protocolVersion
        ToolCount = $toolNames.Count
        Create = 'PASS'
        Measure = 'PASS (6000 mm^3)'
        Validate = 'PASS'
        StepExport = 'PASS'
        Network = 'none'
        Status = 'PASS'
    }
}
catch {
    throw "Geometry protocol smoke failed during ${phase}: $($_.Exception.Message)"
}
finally {
    if ($started -and -not $process.HasExited) {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    $process.Dispose()
}
