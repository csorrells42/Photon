[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $OutputDirectory))
$leaf = Split-Path -Leaf $OutputDirectory
if ([string]::IsNullOrWhiteSpace($leaf) -or -not (Test-Path -LiteralPath $resolvedParent -PathType Container)) {
    throw 'OutputDirectory must name a new directory beneath an existing parent.'
}
$resolvedOutput = Join-Path $resolvedParent $leaf
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputDirectory already exists: $resolvedOutput"
}

$geometryTag = 'photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb'
$assemblyTag = 'photon-cad-assembly:0.1.1-partcad-0.7.158-b77c2c08-p1066a9ff'
$targets = @(
    @{
        Role = 'geometry'
        Tag = $geometryTag
        Dockerfile = (Join-Path $root 'geometry\Dockerfile')
        Context = (Join-Path $root 'geometry')
        Archive = 'photon-cad-geometry.tar'
        BaseName = 'python:3.12.11-slim-bookworm'
        BaseDigest = 'sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49'
    },
    @{
        Role = 'assembly'
        Tag = $assemblyTag
        Dockerfile = (Join-Path $root 'assembly\Dockerfile')
        Context = (Join-Path $root 'assembly')
        Archive = 'photon-cad-assembly.tar'
        BaseName = 'python:3.11.13-slim-bookworm'
        BaseDigest = 'sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1'
    }
)

New-Item -ItemType Directory -Path $resolvedOutput -ErrorAction Stop | Out-Null
$receipts = @()

foreach ($target in $targets) {
    & docker buildx build --platform linux/amd64 --load --provenance=true --sbom=true --file $target.Dockerfile --tag $target.Tag $target.Context
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed for $($target.Role)." }

    $inspectText = & docker image inspect $target.Tag
    if ($LASTEXITCODE -ne 0) { throw "Docker inspect failed for $($target.Role)." }
    $inspect = ($inspectText | ConvertFrom-Json)[0]
    if ($inspect.Os -ne 'linux' -or $inspect.Architecture -ne 'amd64' -or -not ($inspect.Id -match '^sha256:[a-f0-9]{64}$')) {
        throw "Unexpected image identity for $($target.Role)."
    }
    if ($inspect.Config.Labels.'org.opencontainers.image.base.name' -ne $target.BaseName -or
        $inspect.Config.Labels.'org.opencontainers.image.base.digest' -ne $target.BaseDigest) {
        throw "Base-image identity labels are missing or incorrect for $($target.Role)."
    }

    $archivePath = Join-Path $resolvedOutput $target.Archive
    & docker image save --output $archivePath $target.Tag
    if ($LASTEXITCODE -ne 0) { throw "Docker save failed for $($target.Role)." }
    $archive = Get-Item -LiteralPath $archivePath -ErrorAction Stop
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $receipts += [ordered]@{
        role = $target.Role
        tag = $target.Tag
        imageId = $inspect.Id
        platform = 'linux/amd64'
        archive = $archive.Name
        archiveSha256 = $archiveHash
        archiveByteLength = $archive.Length
        baseImage = "$($target.BaseName)@$($target.BaseDigest)"
    }
}

$policyPath = Join-Path $root 'runtime-policy.json'
$policyHash = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$assemblyPatchPath = Join-Path $root 'assembly\photon_partcad_offline_patch.py'
$assemblyPatchHash = (Get-FileHash -LiteralPath $assemblyPatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$receipt = [ordered]@{
    schema = 'photon.cad.bundle-receipt/v2'
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    policySha256 = $policyHash
    geometrySource = [ordered]@{
        repository = 'https://github.com/pzfreo/build123d-mcp'
        revision = '8fb7cefb28daa7f9f68b7e653244256f09b22b1f'
        archiveSha256 = 'd307c94e1fc94d2c634736a34ffbabc037b9186351915cf16aa60e7db8dad97f'
    }
    assemblySource = [ordered]@{
        repository = 'https://github.com/partcad/partcad'
        revision = 'b77c2c08f4a63b97070fcaf9e26bc09f34f95037'
        archiveSha256 = 'ec4be69fe9b5a864ee599e9939e91524398b4cc5af59cae081a253c5f59f5560'
        runtimeAdaptation = [ordered]@{
            kind = 'offline-runtime-patch'
            patch = 'assembly/photon_partcad_offline_patch.py'
            patchSha256 = $assemblyPatchHash
            target = 'partcad/src/partcad/runtime_python_none.py'
            patchedTargetSha256 = 'c6249705d5d43f6cc1bac13b3933fc0af27e6803a7d97f38a8db211588768e43'
        }
    }
    bundles = $receipts
}

$receiptPath = Join-Path $resolvedOutput 'bundle-receipt.json'
$receiptJson = $receipt | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($receiptPath, $receiptJson, [System.Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $receiptPath, (Join-Path $resolvedOutput 'photon-cad-geometry.tar'), (Join-Path $resolvedOutput 'photon-cad-assembly.tar')
