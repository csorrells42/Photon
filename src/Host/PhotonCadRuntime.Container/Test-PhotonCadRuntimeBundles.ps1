[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$policy = Get-Content -LiteralPath (Join-Path $root 'runtime-policy.json') -Raw | ConvertFrom-Json
$geometryTag = [string]$policy.geometry.tag
$assemblyTag = [string]$policy.assembly.tag

function Invoke-DockerProbe {
    param(
        [Parameter(Mandatory = $true)][string] $Image,
        [Parameter(Mandatory = $true)][string[]] $Command
    )
    if ($Command.Count -lt 2) { throw 'A Docker probe requires an entrypoint and at least one argument.' }
    $arguments = @(
        'run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges:true', '--pids-limit', '64',
        '--memory', '2g', '--cpus', '2', '--user', '65532:65532',
        '--tmpfs', '/session:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532',
        '--tmpfs', '/tmp:rw,nosuid,nodev,noexec,size=256m,mode=0700,uid=65532,gid=65532',
        '--entrypoint', $Command[0], $Image
    ) + $Command[1..($Command.Count - 1)]
    $result = & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker probe failed for $Image." }
    return ($result -join "`n").Trim()
}

$expectedPolicy = @{
    network = 'none'
    readOnlyRoot = $true
    pidsLimit = 64
    runtimeUser = '65532:65532'
}
foreach ($entry in $expectedPolicy.GetEnumerator()) {
    $actual = $policy.PSObject.Properties[$entry.Key].Value
    if ($actual -ne $entry.Value) { throw "Runtime policy mismatch: $($entry.Key)." }
}
if (@($policy.dropCapabilities) -notcontains 'ALL' -or @($policy.securityOptions) -notcontains 'no-new-privileges:true') {
    throw 'Runtime capability/no-new-privileges policy is incomplete.'
}

$geometryVersion = Invoke-DockerProbe -Image $geometryTag -Command @('/opt/photon/venv/bin/build123d-mcp', '--version')
if ($geometryVersion -notmatch '^build123d-mcp 0\.3\.80') { throw "Unexpected geometry version: $geometryVersion" }
$geometryPython = Invoke-DockerProbe -Image $geometryTag -Command @('/opt/photon/venv/bin/python', '--version')
if ($geometryPython -ne 'Python 3.12.11') { throw "Unexpected geometry Python: $geometryPython" }

$assemblyVersion = Invoke-DockerProbe -Image $assemblyTag -Command @('/opt/photon/venv/bin/partcad-json-rpc', '--version')
if ($assemblyVersion -notmatch '^partcad-json-rpc 0\.7\.158') { throw "Unexpected assembly version: $assemblyVersion" }
$assemblyPython = Invoke-DockerProbe -Image $assemblyTag -Command @('/opt/photon/venv/bin/python', '--version')
if ($assemblyPython -ne 'Python 3.11.13') { throw "Unexpected assembly Python: $assemblyPython" }

# The CLI version path is intentionally lightweight and does not import PartCAD.
# Probe the installed runtime graph itself so an incomplete Poetry group cannot
# produce another bundle receipt that looks healthy but fails on its first RPC.
$assemblyImport = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-c', 'import partcad;print(partcad.__version__)'
)
if ($assemblyImport -ne '0.7.158') {
    throw "PartCAD runtime import probe failed: $assemblyImport"
}

$assemblyPackages = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-m', 'pip', 'list', '--format=freeze'
)
foreach ($requiredPackage in @(
    'partcad==0.7.158',
    'partcad-utils==0.7.158',
    'partcad-service-json-rpc==0.7.158',
    'sentry-sdk==2.66.0',
    'filelock==3.32.0'
)) {
    if (($assemblyPackages -split "`n") -notcontains $requiredPackage) {
        throw "PartCAD runtime package is missing or incorrectly pinned: $requiredPackage"
    }
}

$assemblyTelemetry = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-c',
    'import os;print(os.environ.get(bytes((80,67,95,84,69,76,69,77,69,84,82,89,95,84,89,80,69)).decode()))'
)
if ($assemblyTelemetry -ne 'none') {
    throw "PartCAD telemetry is not disabled: $assemblyTelemetry"
}

$assemblyPipCheck = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-m', 'pip', 'check'
)
if ($assemblyPipCheck -ne 'No broken requirements found.') {
    throw "PartCAD runtime dependency graph is inconsistent: $assemblyPipCheck"
}

$patchedRuntimeSource = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/usr/bin/sha256sum', '/opt/photon/venv/lib/python3.11/site-packages/partcad/runtime_python_none.py'
)
if (($patchedRuntimeSource -split ' ')[0] -ne 'c6249705d5d43f6cc1bac13b3933fc0af27e6803a7d97f38a8db211588768e43') {
    throw "PartCAD offline runtime adaptation is missing or changed: $patchedRuntimeSource"
}

$assemblyOcp = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-I', '-c',
    'import OCP;from OCP.STEPControl import STEPControl_Reader,STEPControl_Writer;print(OCP.__name__)'
)
if ($assemblyOcp -ne 'OCP') {
    throw "PartCAD VTK OCP/STEPControl import failed: $assemblyOcp"
}

$offlineEnsureCode = @'
from partcad.runtime_python_none import NonePythonRuntime as Runtime

required = {
    'ensure',
    'ensure_onced',
    'ensure_onced_locked',
    'ensure_async',
    'ensure_async_onced',
    'ensure_async_onced_locked',
}
assert required.issubset(Runtime.__dict__)
runtime = object.__new__(Runtime)
runtime._verify_offline_requirement('cadquery-ocp==7.9.3.1.1')
try:
    runtime._verify_offline_requirement('requests>=1')
except RuntimeError:
    pass
else:
    raise AssertionError('non-allowlisted dependency was accepted')
print('offline-ensure-locked')
'@
$offlineEnsure = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-I', '-c', $offlineEnsureCode
)
if ($offlineEnsure -ne 'offline-ensure-locked') {
    throw "PartCAD offline ensure override probe failed: $offlineEnsure"
}

$offlinePolicyCode = @'
import sys

sys.path.insert(0, '/opt/photon/bin')
from partcad_service_json_rpc.core.session import Session
import photon_partcad_entrypoint as entrypoint

session = Session(settings={'forceUpdate': 'false', 'pythonSandbox': 'none', 'verbosity': 'error'})
entrypoint._lock_offline_policy(session)
config = session.partcad.user_config
print('%s:%s:%s' % (config.offline, config.force_update, config.python_sandbox))
'@
$offlinePolicy = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-I', '-c', $offlinePolicyCode
)
if ($offlinePolicy -ne 'True:False:none') {
    throw "PartCAD authoritative offline policy probe failed: $offlinePolicy"
}

$geometryUser = Invoke-DockerProbe -Image $geometryTag -Command @('/usr/local/bin/python', '-c', 'import os;print(os.getuid(),os.getgid(),sep=chr(58))')
$assemblyUser = Invoke-DockerProbe -Image $assemblyTag -Command @('/usr/local/bin/python', '-c', 'import os;print(os.getuid(),os.getgid(),sep=chr(58))')
if ($geometryUser -ne '65532:65532' -or $assemblyUser -ne '65532:65532') { throw 'A CAD image did not run as the fixed non-root identity.' }

$allowlist = Invoke-DockerProbe -Image $assemblyTag -Command @(
    '/opt/photon/venv/bin/python', '-c',
    'import photon_partcad_entrypoint as p;print(*sorted(p.ALLOWED_METHODS),sep=chr(44))'
)
$expectedAllowed = @(
    'context.create',
    'export.assembly',
    'export.part',
    'healthcheck',
    'info.object',
    'inspect.assembly',
    'inspect.object',
    'inspect.part',
    'list.objects',
    'list.packages',
    'version'
)
$actualAllowed = @($allowlist -split ',')
if ($actualAllowed.Count -ne $expectedAllowed.Count -or
    (Compare-Object -ReferenceObject $expectedAllowed -DifferenceObject $actualAllowed)) {
    throw "PartCAD method allowlist drifted: $allowlist"
}
foreach ($forbidden in @(
    'activate', 'add.assembly', 'add.object', 'add.part', 'ensure_loaded',
    'import.object', 'install', 'update', 'package.load', 'package.refresh',
    'render.objects', 'search.objects', 'daemon.set.telemetry', 'list.providers'
)) {
    if (($allowlist -split ',') -contains $forbidden) { throw "Forbidden PartCAD method exposed: $forbidden" }
}

[pscustomobject]@{
    Geometry = $geometryVersion
    GeometryPython = $geometryPython
    Assembly = $assemblyVersion
    AssemblyPython = $assemblyPython
    AssemblyImport = $assemblyImport
    AssemblyTelemetry = $assemblyTelemetry
    AssemblyPipCheck = $assemblyPipCheck
    AssemblyRuntimeAdaptation = ($patchedRuntimeSource -split ' ')[0]
    AssemblyOcp = $assemblyOcp
    AssemblyOfflineEnsure = $offlineEnsure
    AssemblyOfflinePolicy = $offlinePolicy
    RuntimeUser = $geometryUser
    Network = $policy.network
    ReadOnlyRoot = $policy.readOnlyRoot
    CapabilityDrop = (@($policy.dropCapabilities) -join ',')
    PartCadAllowedMethodCount = ($allowlist -split ',').Count
    Status = 'PASS'
}
