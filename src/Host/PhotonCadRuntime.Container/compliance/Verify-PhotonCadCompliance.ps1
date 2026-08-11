[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ComplianceDirectory,

    [Parameter(Mandatory = $true)]
    [string] $RuntimeArchiveDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$canonicalPolicyPath = Join-Path $scriptRoot 'compliance-policy.v1.json'
$canonicalPythonMapPath = Join-Path $scriptRoot 'pinned\python-license-map.v1.json'
$canonicalUpstreamMapPath = Join-Path $scriptRoot 'pinned\upstream-legal-files.v1.json'
$canonicalSchemaPath = Join-Path $scriptRoot 'schemas\photon-cad-compliance-receipt-v1.schema.json'
$readLocks = New-Object 'System.Collections.Generic.List[System.IO.FileStream]'
trap {
    foreach ($stream in $readLocks) { $stream.Dispose() }
    throw $_
}

function Lock-ReadFile {
    param([System.IO.FileInfo] $File)
    $stream = [IO.File]::Open($File.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $script:readLocks.Add($stream)
}

function Get-RequiredProperty {
    param([object] $Value, [string] $Name, [string] $Context)
    if ($null -eq $Value) { throw "$Context is null." }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Context is missing '$Name'." }
    return $property.Value
}

function Assert-ExactSchema {
    param([object] $Value, [string] $Expected, [string] $Context)
    if ([string](Get-RequiredProperty $Value 'schema' $Context) -cne $Expected) { throw "$Context has an unexpected schema." }
}

function Assert-Sha256 {
    param([string] $Value, [string] $Context, [switch] $Prefixed)
    $pattern = if ($Prefixed) { '^sha256:[a-f0-9]{64}$' } else { '^[a-f0-9]{64}$' }
    if ($Value -cnotmatch $pattern) { throw "$Context is not a canonical lowercase SHA-256 value." }
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Resolve-SafeFile {
    param([string] $Root, [string] $RelativePath, [long] $MaximumBytes, [string] $Context)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) { throw "$Context must use a relative path." }
    $segments = @($RelativePath -split '[\\/]')
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) { throw "$Context contains an unsafe segment." }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Context root is unsafe." }
    $full = [IO.Path]::GetFullPath((Join-Path $rootFull ($segments -join '\')))
    if (-not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "$Context escapes its root." }
    $current = $rootItem
    foreach ($segment in $segments) {
        $current = Get-Item -LiteralPath (Join-Path $current.FullName $segment) -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Context traverses a reparse point." }
    }
    if ($current.PSIsContainer -or $current.Length -le 0 -or $current.Length -gt $MaximumBytes) { throw "$Context is not a bounded file." }
    Lock-ReadFile $current
    return $current
}

function Read-JsonFile {
    param([System.IO.FileInfo] $File, [string] $Context)
    try { return (Get-Content -LiteralPath $File.FullName -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop) }
    catch { throw "$Context is not valid JSON: $($_.Exception.Message)" }
}

function Assert-Artifact {
    param([object] $Record, [string] $Root, [long] $MaximumBytes, [string] $Context)
    $path = [string](Get-RequiredProperty $Record 'path' $Context)
    $sha256 = [string](Get-RequiredProperty $Record 'sha256' $Context)
    $byteLength = [long](Get-RequiredProperty $Record 'byteLength' $Context)
    Assert-Sha256 $sha256 "$Context hash"
    $file = Resolve-SafeFile $Root $path $MaximumBytes $Context
    if ($file.Length -ne $byteLength -or (Get-Sha256 $file.FullName) -cne $sha256) { throw "$Context hash or length mismatch." }
    return $file
}

function Get-PackageKey {
    param([string] $Ecosystem, [string] $Name, [string] $Version)
    if ([string]::IsNullOrWhiteSpace($Ecosystem) -or [string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Version)) { throw 'A package identity is incomplete.' }
    return ('{0}|{1}|{2}' -f $Ecosystem.ToLowerInvariant(), $Name.ToLowerInvariant(), $Version)
}

function Assert-EmptyUnresolved {
    param([object] $Document, [string] $Context)
    if (@(Get-RequiredProperty $Document 'unresolved' $Context).Count -ne 0) { throw "$Context contains unresolved entries." }
}

function Read-CanonicalJson {
    param([string] $Path, [string] $Context)
    $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -le 0 -or $file.Length -gt 16777216) { throw "$Context is unsafe." }
    Lock-ReadFile $file
    return [pscustomobject]@{ File = $file; Document = (Read-JsonFile $file $Context) }
}

$complianceRoot = [IO.Path]::GetFullPath($ComplianceDirectory)
$archiveRoot = [IO.Path]::GetFullPath($RuntimeArchiveDirectory)
$receiptFile = Resolve-SafeFile $complianceRoot 'compliance-receipt.v1.json' 16777216 'Compliance receipt'
$digestFile = Resolve-SafeFile $complianceRoot 'compliance-receipt.v1.json.sha256' 1024 'Detached receipt digest'
$digestText = (Get-Content -LiteralPath $digestFile.FullName -Raw -Encoding ASCII -ErrorAction Stop).Trim()
if ($digestText -cnotmatch '^([a-f0-9]{64}) \*compliance-receipt\.v1\.json$') { throw 'Detached receipt digest has an invalid format.' }
$receiptDigest = $Matches[1]
if ((Get-Sha256 $receiptFile.FullName) -cne $receiptDigest) { throw 'Detached receipt digest mismatch.' }
$receipt = Read-JsonFile $receiptFile 'Compliance receipt'
Assert-ExactSchema $receipt 'photon.cad.compliance-receipt/v1' 'Compliance receipt'
if ([string](Get-RequiredProperty $receipt 'status' 'Compliance receipt') -cne 'pass' -or @(Get-RequiredProperty $receipt 'blockers' 'Compliance receipt').Count -ne 0) { throw 'Compliance receipt is not a blocker-free pass.' }

$canonicalPolicy = Read-CanonicalJson $canonicalPolicyPath 'Canonical compliance policy'
$canonicalPythonMap = Read-CanonicalJson $canonicalPythonMapPath 'Canonical Python license map'
$canonicalUpstreamMap = Read-CanonicalJson $canonicalUpstreamMapPath 'Canonical upstream legal map'
$canonicalSchema = Read-CanonicalJson $canonicalSchemaPath 'Canonical receipt schema'
Assert-ExactSchema $canonicalPolicy.Document 'photon.cad.compliance-policy/v1' 'Canonical compliance policy'
Assert-ExactSchema $canonicalPythonMap.Document 'photon.cad.python-license-map/v1' 'Canonical Python license map'
Assert-ExactSchema $canonicalUpstreamMap.Document 'photon.cad.upstream-legal-files/v1' 'Canonical upstream legal map'

$inputBindings = @(
    @{ Name = 'policy'; Receipt = $receipt.policy; Canonical = $canonicalPolicy.File },
    @{ Name = 'Python license map'; Receipt = $receipt.maps.pythonLicenses; Canonical = $canonicalPythonMap.File },
    @{ Name = 'upstream legal map'; Receipt = $receipt.maps.upstreamLegalFiles; Canonical = $canonicalUpstreamMap.File },
    @{ Name = 'receipt schema'; Receipt = $receipt.maps.receiptSchema; Canonical = $canonicalSchema.File }
)
foreach ($binding in $inputBindings) {
    $boundFile = Assert-Artifact $binding.Receipt $complianceRoot 16777216 $binding.Name
    if ((Get-Sha256 $binding.Canonical.FullName) -cne (Get-Sha256 $boundFile.FullName) -or $binding.Canonical.Length -ne $boundFile.Length) {
        throw "$($binding.Name) differs from the verifier's canonical file."
    }
}
if ([bool]$canonicalPolicy.Document.failClosed -ne $true) { throw 'Canonical policy is not fail closed.' }
if ([string]$receipt.scanner.uri -cne [string]$canonicalPolicy.Document.scanner.uri -or
    [string]$receipt.scanner.digest -cne [string]$canonicalPolicy.Document.scanner.digest -or
    [string]$receipt.scanner.buildxVersion -cne [string]$canonicalPolicy.Document.buildxVersion) {
    throw 'Receipt buildx/scanner identity differs from canonical policy.'
}
$unresolvedPython = @($canonicalPythonMap.Document.entries | Where-Object { [string]$_.resolutionStatus -cne 'resolved' -or [string]::IsNullOrWhiteSpace([string]$_.licenseExpression) })
$unresolvedUpstream = @($canonicalUpstreamMap.Document.entries | Where-Object { [string]$_.resolutionStatus -cne 'resolved' -or [string]$_.sha256 -cnotmatch '^[a-f0-9]{64}$' })
if ($unresolvedPython.Count -ne 0 -or $unresolvedUpstream.Count -ne 0) { throw 'Canonical pinned maps still contain unresolved legal work.' }

$bundles = @(Get-RequiredProperty $receipt 'bundles' 'Compliance receipt')
if ($bundles.Count -ne 2) { throw 'Receipt must contain exactly two bundles.' }
$seenRoles = @{}
foreach ($bundle in $bundles) {
    $role = [string](Get-RequiredProperty $bundle 'role' 'Bundle receipt')
    if ($seenRoles.ContainsKey($role)) { throw "Duplicate bundle role: $role" }
    $seenRoles[$role] = $true
    $policyMatches = @($canonicalPolicy.Document.roles | Where-Object { [string]$_.role -ceq $role })
    if ($policyMatches.Count -ne 1) { throw "Unknown bundle role: $role" }
    $policyRole = $policyMatches[0]
    if ([string]$bundle.tag -cne [string]$policyRole.tag -or [string]$bundle.status -cne 'pass' -or @($bundle.blockers).Count -ne 0) { throw "$role bundle identity/status mismatch." }
    if ([string]$bundle.image.indexDigest -cne [string]$policyRole.imageIndexDigest -or
        [string]$bundle.image.platformManifestDigest -cne [string]$policyRole.platformManifestDigest -or
        [string]$bundle.image.attestationManifestDigest -cne [string]$policyRole.attestationManifestDigest) { throw "$role image digests differ from policy." }
    Assert-Sha256 ([string]$bundle.image.configDigest) "$role config digest" -Prefixed
    [void](Assert-Artifact $bundle.image.archive $archiveRoot ([long]$canonicalPolicy.Document.maximumRuntimeArchiveBytes) "$role runtime archive")

    $attestationFiles = @{}
    foreach ($name in @('imageIndex', 'imageManifest', 'attestationManifest')) {
        $attestationFiles[$name] = Assert-Artifact $bundle.attestations.$name $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role $name"
    }
    $sbomFile = Assert-Artifact $bundle.attestations.sbom.artifact $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role SBOM"
    $provenanceFile = Assert-Artifact $bundle.attestations.provenance.artifact $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role provenance"
    if ('sha256:' + (Get-Sha256 $attestationFiles.imageIndex.FullName) -cne [string]$policyRole.imageIndexDigest -or
        'sha256:' + (Get-Sha256 $attestationFiles.imageManifest.FullName) -cne [string]$policyRole.platformManifestDigest -or
        'sha256:' + (Get-Sha256 $attestationFiles.attestationManifest.FullName) -cne [string]$policyRole.attestationManifestDigest -or
        'sha256:' + (Get-Sha256 $sbomFile.FullName) -cne [string]$policyRole.sbom.digest -or
        'sha256:' + (Get-Sha256 $provenanceFile.FullName) -cne [string]$policyRole.provenance.digest) { throw "$role OCI/attestation content digest mismatch." }
    $imageManifest = Read-JsonFile $attestationFiles.imageManifest "$role image manifest"
    if ([string]$imageManifest.config.digest -cne [string]$bundle.image.configDigest) { throw "$role config digest is not bound by the image manifest." }
    $sbom = Read-JsonFile $sbomFile "$role SBOM"
    $provenance = Read-JsonFile $provenanceFile "$role provenance"
    foreach ($statement in @($sbom, $provenance)) {
        $subjectHex = ([string]$policyRole.platformManifestDigest).Substring(7)
        if (@($statement.subject | Where-Object { [string]$_.digest.sha256 -ceq $subjectHex }).Count -ne 1) { throw "$role statement subject mismatch." }
    }
    if ([string]$sbom.predicateType -cne [string]$policyRole.sbom.predicateType -or [string]$sbom.predicate.spdxVersion -cne 'SPDX-2.3' -or [string]$sbom.predicate.dataLicense -cne 'CC0-1.0') { throw "$role SPDX identity mismatch." }
    if (@($sbom.predicate.packages).Count -ne [int]$bundle.attestations.sbom.packageCount -or @($sbom.predicate.files).Count -ne [int]$bundle.attestations.sbom.fileCount -or @($sbom.predicate.relationships).Count -ne [int]$bundle.attestations.sbom.relationshipCount) { throw "$role SPDX counts mismatch." }

    $inventoryKeys = @{}
    foreach ($inventoryName in @('python', 'debian')) {
        $inventoryRecord = $bundle.inventories.$inventoryName
        $inventoryFile = Assert-Artifact $inventoryRecord.artifact $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role $inventoryName inventory"
        $inventory = Read-JsonFile $inventoryFile "$role $inventoryName inventory"
        Assert-ExactSchema $inventory 'photon.cad.package-inventory/v1' "$role $inventoryName inventory"
        Assert-EmptyUnresolved $inventory "$role $inventoryName inventory"
        if ([string]$inventory.role -cne $role) { throw "$role $inventoryName inventory role mismatch." }
        $expectedEcosystem = if ($inventoryName -eq 'python') { 'pypi' } else { 'deb' }
        $classes = if ($inventoryName -eq 'python') { @('direct', 'transitive', 'unlinked') } else { @('direct', 'transitive', 'base') }
        $counts = @{}; foreach ($class in $classes) { $counts[$class] = 0 }
        foreach ($package in @($inventory.packages)) {
            if ([string]$package.ecosystem -cne $expectedEcosystem -or $classes -cnotcontains [string]$package.classification) { throw "$role $inventoryName inventory contains invalid data." }
            $key = Get-PackageKey ([string]$package.ecosystem) ([string]$package.name) ([string]$package.version)
            if ($inventoryKeys.ContainsKey($key)) { throw "$role inventory duplicates $key." }
            $inventoryKeys[$key] = $true; $counts[[string]$package.classification]++
        }
        $residualClass = if ($inventoryName -eq 'python') { 'unlinked' } else { 'base' }
        if (@($inventory.packages).Count -ne [int]$inventoryRecord.packageCount -or $counts.direct -ne [int]$inventoryRecord.directCount -or $counts.transitive -ne [int]$inventoryRecord.transitiveCount -or $counts[$residualClass] -ne [int]$inventoryRecord.residualCount) { throw "$role $inventoryName receipt counts mismatch." }
    }

    $legalManifestFile = Assert-Artifact $bundle.legal.manifest $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role legal manifest"
    $sourceOfferFile = Assert-Artifact $bundle.legal.sourceOffer $complianceRoot ([long]$canonicalPolicy.Document.maximumJsonArtifactBytes) "$role source offer"
    [void](Assert-Artifact $bundle.legal.thirdPartyNotices $complianceRoot ([long]$canonicalPolicy.Document.maximumLegalArtifactBytes) "$role third-party notices")
    $legalManifest = Read-JsonFile $legalManifestFile "$role legal manifest"
    $sourceOffer = Read-JsonFile $sourceOfferFile "$role source offer"
    Assert-ExactSchema $legalManifest 'photon.cad.legal-manifest/v1' "$role legal manifest"
    Assert-ExactSchema $sourceOffer 'photon.cad.source-offer/v1' "$role source offer"
    Assert-EmptyUnresolved $legalManifest "$role legal manifest"; Assert-EmptyUnresolved $sourceOffer "$role source offer"
    if ([string]$legalManifest.role -cne $role -or [string]$sourceOffer.role -cne $role) { throw "$role legal/source-offer role mismatch." }
    $legalKeys = @{}; $requiredSources = @{}; $legalHashes = @{}; $legalArtifactsByPath = @{}; $referencedLegalPaths = @{}; $noticeCount = 0
    foreach ($artifact in @($bundle.legal.files)) {
        $file = Assert-Artifact $artifact $complianceRoot ([long]$canonicalPolicy.Document.maximumLegalArtifactBytes) "$role legal file"
        $relativePath = ([string]$artifact.path) -replace '\\', '/'
        if (-not $relativePath.StartsWith("$role/licenses/", [StringComparison]::Ordinal) -or $legalArtifactsByPath.ContainsKey($relativePath)) { throw "$role receipt contains an unsafe or duplicate legal-file path." }
        $hash = Get-Sha256 $file.FullName
        $legalHashes[$hash] = $true
        $legalArtifactsByPath[$relativePath] = [pscustomobject]@{ Sha256 = $hash; ByteLength = [long]$file.Length }
    }
    foreach ($package in @($legalManifest.packages)) {
        $key = Get-PackageKey ([string]$package.ecosystem) ([string]$package.name) ([string]$package.version)
        if (-not $inventoryKeys.ContainsKey($key) -or $legalKeys.ContainsKey($key) -or [string]$package.resolutionStatus -cne 'resolved' -or [string]::IsNullOrWhiteSpace([string]$package.licenseExpression) -or [string]$package.licenseExpression -ceq 'NOASSERTION') { throw "$role legal package is incomplete or unexpected: $key" }
        $legalKeys[$key] = $package
        if ([bool]$package.sourceOfferRequired) { $requiredSources[$key] = $true }
        foreach ($legalFile in @($package.legalFiles)) {
            $relativePath = ([string]$legalFile.path) -replace '\\', '/'
            $kind = [string]$legalFile.kind
            if (-not $relativePath.StartsWith("$role/licenses/", [StringComparison]::Ordinal) -or
                @($canonicalPolicy.Document.allowedLegalFileKinds) -cnotcontains $kind -or
                -not $legalArtifactsByPath.ContainsKey($relativePath) -or $referencedLegalPaths.ContainsKey($relativePath)) {
                throw "$role legal package references an unsafe, missing, or duplicate legal file."
            }
            $bound = $legalArtifactsByPath[$relativePath]
            if ([string]$legalFile.sha256 -cne [string]$bound.Sha256 -or [long]$legalFile.byteLength -ne [long]$bound.ByteLength) { throw "$role legal package file binding mismatch." }
            $referencedLegalPaths[$relativePath] = $true
            if ($kind -ceq 'notice') { $noticeCount++ }
        }
    }
    if ($legalKeys.Count -ne $inventoryKeys.Count -or $legalKeys.Count -ne [int]$bundle.legal.packageCount -or $noticeCount -ne [int]$bundle.legal.noticeCount -or $referencedLegalPaths.Count -ne $legalArtifactsByPath.Count) { throw "$role legal coverage/count mismatch." }
    $offers = @{}
    foreach ($offer in @($sourceOffer.entries)) {
        $key = [string]$offer.packageKey
        if (-not $requiredSources.ContainsKey($key) -or $offers.ContainsKey($key) -or [string]$offer.status -cne 'resolved' -or [string]$offer.sourceSha256 -cnotmatch '^[a-f0-9]{64}$') { throw "$role source-offer entry is incomplete or unexpected." }
        [Uri] $uri = $null
        if (-not [Uri]::TryCreate([string]$offer.sourceUri, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https') { throw "$role source-offer URI is not HTTPS." }
        $offers[$key] = $true
    }
    if ($offers.Count -ne $requiredSources.Count) { throw "$role source-offer coverage mismatch." }
    foreach ($notice in @($policyRole.requiredNotices)) {
        $key = Get-PackageKey ([string]$notice.ecosystem) ([string]$notice.name) ([string]$notice.version)
        if (-not $legalKeys.ContainsKey($key) -or @($legalKeys[$key].legalFiles | Where-Object { [string]$_.kind -ceq 'notice' -and [string]$_.sha256 -ceq [string]$notice.sha256 -and [long]$_.byteLength -eq [long]$notice.byteLength }).Count -ne 1) { throw "$role required NOTICE is absent: $key" }
    }
    if ($null -ne $policyRole.source.PSObject.Properties['runtimeAdaptation'] -and [bool]$policyRole.source.runtimeAdaptation.modificationNoticeRequired -and
        @($legalManifest.packages.legalFiles | ForEach-Object { @($_) } | Where-Object { [string]$_.kind -ceq 'modification-notice' }).Count -ne 1) {
        throw "$role requires exactly one modification notice."
    }
    foreach ($override in @($canonicalPythonMap.Document.entries | Where-Object { [string]$_.role -ceq $role })) {
        $key = Get-PackageKey 'pypi' ([string]$override.name) ([string]$override.version)
        if (-not $legalKeys.ContainsKey($key) -or [string]$legalKeys[$key].licenseExpression -cne [string]$override.licenseExpression) { throw "$role resolved Python override is not reflected in the legal manifest: $key" }
    }
    $sourceOfferHash = Get-Sha256 $sourceOfferFile.FullName
    foreach ($pinned in @($canonicalUpstreamMap.Document.entries | Where-Object { [string]$_.role -ceq $role })) {
        if (-not $legalHashes.ContainsKey([string]$pinned.sha256) -and [string]$pinned.sha256 -cne $sourceOfferHash) { throw "$role pinned upstream legal artifact is absent: $($pinned.id)" }
    }
}
if (-not $seenRoles.ContainsKey('geometry') -or -not $seenRoles.ContainsKey('assembly')) { throw 'Both CAD roles are required.' }

$result = [pscustomobject]@{
    Status = 'PASS'
    Receipt = $receiptFile.FullName
    ReceiptSha256 = $receiptDigest
    BundleCount = $bundles.Count
    Platform = [string]$receipt.platform
}
foreach ($stream in $readLocks) { $stream.Dispose() }
$readLocks.Clear()
$result
