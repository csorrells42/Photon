[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$policyPath = Join-Path $scriptRoot 'compliance-policy.v1.json'
$pythonMapPath = Join-Path $scriptRoot 'pinned\python-license-map.v1.json'
$upstreamMapPath = Join-Path $scriptRoot 'pinned\upstream-legal-files.v1.json'
$schemaPath = Join-Path $scriptRoot 'schemas\photon-cad-compliance-receipt-v1.schema.json'
$utf8 = [System.Text.UTF8Encoding]::new($false)
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
    if ([string](Get-RequiredProperty $Value 'schema' $Context) -cne $Expected) {
        throw "$Context has an unexpected schema."
    }
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

function Assert-NoReparsePoint {
    param([string] $Root, [string] $FullPath)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidateFull = [System.IO.Path]::GetFullPath($FullPath)
    $prefix = $rootFull + '\'
    if (-not $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the evidence root: $candidateFull"
    }
    $current = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Evidence root is a reparse point: $rootFull"
    }
    $relative = $candidateFull.Substring($prefix.Length)
    foreach ($segment in ($relative -split '\\')) {
        $current = Get-Item -LiteralPath (Join-Path $current.FullName $segment) -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Evidence path traverses a reparse point: $($current.FullName)"
        }
    }
}

function Resolve-SafeFile {
    param([string] $Root, [string] $RelativePath, [long] $MaximumBytes, [string] $Context)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Context must use a nonempty relative path."
    }
    $segments = @($RelativePath -split '[\\/]')
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
        throw "$Context contains an unsafe path segment."
    }
    $rootFull = [IO.Path]::GetFullPath($Root)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $rootFull ($segments -join '\')))
    Assert-NoReparsePoint $rootFull $fullPath
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.PSIsContainer) { throw "$Context must identify a file." }
    if ($item.Length -le 0 -or $item.Length -gt $MaximumBytes) {
        throw "$Context has an invalid byte length: $($item.Length)."
    }
    Lock-ReadFile $item
    return $item
}

function Read-JsonFile {
    param([System.IO.FileInfo] $File, [string] $Context)
    try {
        return (Get-Content -LiteralPath $File.FullName -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        throw "$Context is not valid JSON: $($_.Exception.Message)"
    }
}

function Read-LocalJson {
    param([string] $Path, [string] $Context)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0 -or $item.Length -gt 16777216) {
        throw "$Context is not a safe bounded file."
    }
    Lock-ReadFile $item
    return [pscustomobject]@{ File = $item; Document = (Read-JsonFile $item $Context) }
}

function Get-PackageKey {
    param([string] $Ecosystem, [string] $Name, [string] $Version)
    if ([string]::IsNullOrWhiteSpace($Ecosystem) -or [string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Version)) {
        throw 'A package identity is incomplete.'
    }
    return ('{0}|{1}|{2}' -f $Ecosystem.ToLowerInvariant(), $Name.ToLowerInvariant(), $Version)
}

function Assert-EmptyUnresolved {
    param([object] $Document, [string] $Context)
    $unresolved = @(Get-RequiredProperty $Document 'unresolved' $Context)
    if ($unresolved.Count -ne 0) { throw "$Context contains unresolved entries." }
}

function Read-Inventory {
    param([System.IO.FileInfo] $File, [string] $Role, [string] $Ecosystem, [string[]] $AllowedClassifications)
    $context = "$Role $Ecosystem inventory"
    $document = Read-JsonFile $File $context
    Assert-ExactSchema $document 'photon.cad.package-inventory/v1' $context
    if ([string](Get-RequiredProperty $document 'role' $context) -cne $Role) { throw "$context role mismatch." }
    Assert-EmptyUnresolved $document $context
    $packages = @(Get-RequiredProperty $document 'packages' $context)
    if ($packages.Count -eq 0) { throw "$context is empty." }
    $keys = @{}
    $counts = @{}
    foreach ($classification in $AllowedClassifications) { $counts[$classification] = 0 }
    foreach ($package in $packages) {
        $packageEcosystem = [string](Get-RequiredProperty $package 'ecosystem' $context)
        $name = [string](Get-RequiredProperty $package 'name' $context)
        $version = [string](Get-RequiredProperty $package 'version' $context)
        $classification = [string](Get-RequiredProperty $package 'classification' $context)
        if ($packageEcosystem -cne $Ecosystem -or $AllowedClassifications -cnotcontains $classification) {
            throw "$context contains an invalid ecosystem or classification."
        }
        $key = Get-PackageKey $packageEcosystem $name $version
        if ($keys.ContainsKey($key)) { throw "$context contains duplicate package $key." }
        $keys[$key] = $package
        $counts[$classification] = [int]$counts[$classification] + 1
    }
    return [pscustomobject]@{ Document = $document; Packages = $packages; Keys = $keys; Counts = $counts }
}

function Assert-StatementSubject {
    param([object] $Statement, [string] $ManifestDigest, [string] $Context)
    $hex = $ManifestDigest.Substring(7)
    $matches = @((Get-RequiredProperty $Statement 'subject' $Context) | Where-Object {
        $digest = Get-RequiredProperty $_ 'digest' "$Context subject"
        [string](Get-RequiredProperty $digest 'sha256' "$Context subject digest") -ceq $hex
    })
    if ($matches.Count -ne 1) { throw "$Context is not bound to the exact platform manifest." }
}

function Get-ArtifactRecord {
    param([string] $RelativePath, [System.IO.FileInfo] $File)
    return [ordered]@{
        path = ($RelativePath -replace '\\', '/')
        sha256 = Get-Sha256 $File.FullName
        byteLength = [long]$File.Length
    }
}

function Assert-DigestFile {
    param([System.IO.FileInfo] $File, [string] $ExpectedDigest, [long] $ExpectedLength, [string] $Context)
    Assert-Sha256 $ExpectedDigest $Context -Prefixed
    $actualDigest = 'sha256:' + (Get-Sha256 $File.FullName)
    if ($actualDigest -cne $ExpectedDigest -or ($ExpectedLength -gt 0 -and $File.Length -ne $ExpectedLength)) {
        throw "$Context digest or byte length mismatch."
    }
}

function Write-NewBytes {
    param([string] $Path, [byte[]] $Bytes)
    if (Test-Path -LiteralPath $Path) { throw "Refusing to overwrite $Path" }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -ErrorAction Stop | Out-Null
    }
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Copy-NewFile {
    param([System.IO.FileInfo] $Source, [string] $Destination)
    Write-NewBytes $Destination ([IO.File]::ReadAllBytes($Source.FullName))
}

$policySource = Read-LocalJson $policyPath 'Compliance policy'
$pythonMapSource = Read-LocalJson $pythonMapPath 'Python license map'
$upstreamMapSource = Read-LocalJson $upstreamMapPath 'Upstream legal-file map'
$schemaSource = Read-LocalJson $schemaPath 'Receipt schema'
$policy = $policySource.Document
$pythonMap = $pythonMapSource.Document
$upstreamMap = $upstreamMapSource.Document

Assert-ExactSchema $policy 'photon.cad.compliance-policy/v1' 'Compliance policy'
if ((Get-RequiredProperty $policy 'failClosed' 'Compliance policy') -ne $true) { throw 'Compliance policy must fail closed.' }
Assert-ExactSchema $pythonMap 'photon.cad.python-license-map/v1' 'Python license map'
Assert-ExactSchema $upstreamMap 'photon.cad.upstream-legal-files/v1' 'Upstream legal-file map'

$unresolvedPython = @((Get-RequiredProperty $pythonMap 'entries' 'Python license map') | Where-Object {
    [string](Get-RequiredProperty $_ 'resolutionStatus' 'Python license-map entry') -cne 'resolved' -or
    [string]::IsNullOrWhiteSpace([string](Get-RequiredProperty $_ 'licenseExpression' 'Python license-map entry'))
})
if ($unresolvedPython.Count -ne 0) {
    $identities = @($unresolvedPython | ForEach-Object { "$($_.role):$($_.name)==$($_.version)[$($_.resolutionStatus)]" }) -join ', '
    throw "Pinned Python license map is unresolved: $identities"
}
$unresolvedUpstream = @((Get-RequiredProperty $upstreamMap 'entries' 'Upstream legal-file map') | Where-Object {
    [string](Get-RequiredProperty $_ 'resolutionStatus' 'Upstream legal-file entry') -cne 'resolved' -or
    [string](Get-RequiredProperty $_ 'sha256' 'Upstream legal-file entry') -cnotmatch '^[a-f0-9]{64}$'
})
if ($unresolvedUpstream.Count -ne 0) {
    $identities = @($unresolvedUpstream | ForEach-Object { "$($_.id)[$($_.resolutionStatus)]" }) -join ', '
    throw "Pinned upstream legal-file map is unresolved: $identities"
}

$inputRoot = [IO.Path]::GetFullPath($InputDirectory)
$inputRootItem = Get-Item -LiteralPath $inputRoot -Force -ErrorAction Stop
if (-not $inputRootItem.PSIsContainer -or ($inputRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'InputDirectory must be a non-reparse directory.'
}
$inputManifestFile = Resolve-SafeFile $inputRoot 'compliance-input.v1.json' ([long]$policy.maximumJsonArtifactBytes) 'Compliance input manifest'
$inputManifest = Read-JsonFile $inputManifestFile 'Compliance input manifest'
Assert-ExactSchema $inputManifest 'photon.cad.compliance-input/v1' 'Compliance input manifest'
if ([string](Get-RequiredProperty $inputManifest 'buildxVersion' 'Compliance input manifest') -cne [string]$policy.buildxVersion) {
    throw 'Buildx version differs from the pinned compliance policy.'
}

$policyRoles = @(Get-RequiredProperty $policy 'roles' 'Compliance policy')
$inputRoles = @(Get-RequiredProperty $inputManifest 'roles' 'Compliance input manifest')
if ($policyRoles.Count -ne 2 -or $inputRoles.Count -ne 2) { throw 'Exactly two CAD roles are required.' }
$contexts = @()

foreach ($policyRole in $policyRoles) {
    $role = [string](Get-RequiredProperty $policyRole 'role' 'Policy role')
    $inputRoleMatches = @($inputRoles | Where-Object { [string]$_.role -ceq $role })
    if ($inputRoleMatches.Count -ne 1) { throw "Input must contain exactly one $role role." }
    $inputRole = $inputRoleMatches[0]
    if ([string](Get-RequiredProperty $inputRole 'tag' "$role input") -cne [string]$policyRole.tag) { throw "$role tag mismatch." }
    $artifactDescriptors = Get-RequiredProperty $inputRole 'artifacts' "$role input"
    $files = @{}
    foreach ($artifactKey in @(Get-RequiredProperty $policy 'requiredArtifactKeys' 'Compliance policy')) {
        $descriptor = Get-RequiredProperty $artifactDescriptors ([string]$artifactKey) "$role artifacts"
        $relativePath = [string](Get-RequiredProperty $descriptor 'path' "$role $artifactKey")
        $maximum = if ($artifactKey -eq 'archive') { [long]$policy.maximumRuntimeArchiveBytes } else { [long]$policy.maximumJsonArtifactBytes }
        $files[[string]$artifactKey] = Resolve-SafeFile $inputRoot $relativePath $maximum "$role $artifactKey"
    }

    Assert-DigestFile $files.imageIndex ([string]$policyRole.imageIndexDigest) 0 "$role image index"
    Assert-DigestFile $files.imageManifest ([string]$policyRole.platformManifestDigest) 0 "$role image manifest"
    Assert-DigestFile $files.attestationManifest ([string]$policyRole.attestationManifestDigest) 0 "$role attestation manifest"
    Assert-DigestFile $files.sbom ([string]$policyRole.sbom.digest) ([long]$policyRole.sbom.byteLength) "$role SBOM"
    Assert-DigestFile $files.provenance ([string]$policyRole.provenance.digest) ([long]$policyRole.provenance.byteLength) "$role provenance"

    $imageIndex = Read-JsonFile $files.imageIndex "$role image index"
    $imageManifest = Read-JsonFile $files.imageManifest "$role image manifest"
    $attestationManifest = Read-JsonFile $files.attestationManifest "$role attestation manifest"
    $sbom = Read-JsonFile $files.sbom "$role SBOM"
    $provenance = Read-JsonFile $files.provenance "$role provenance"
    if (@($imageIndex.manifests | Where-Object { [string]$_.digest -ceq [string]$policyRole.platformManifestDigest }).Count -ne 1 -or
        @($imageIndex.manifests | Where-Object { [string]$_.digest -ceq [string]$policyRole.attestationManifestDigest }).Count -ne 1) {
        throw "$role image index does not bind both manifests."
    }
    if (@($attestationManifest.layers | Where-Object { [string]$_.digest -ceq [string]$policyRole.sbom.digest }).Count -ne 1 -or
        @($attestationManifest.layers | Where-Object { [string]$_.digest -ceq [string]$policyRole.provenance.digest }).Count -ne 1) {
        throw "$role attestation manifest does not bind both predicates."
    }
    $configDigest = [string](Get-RequiredProperty (Get-RequiredProperty $imageManifest 'config' "$role image manifest") 'digest' "$role image config")
    Assert-Sha256 $configDigest "$role config digest" -Prefixed
    if ([string](Get-RequiredProperty $sbom 'predicateType' "$role SBOM") -cne [string]$policyRole.sbom.predicateType) { throw "$role SBOM predicate mismatch." }
    if ([string](Get-RequiredProperty $provenance 'predicateType' "$role provenance") -cne [string]$policyRole.provenance.predicateType) { throw "$role provenance predicate mismatch." }
    Assert-StatementSubject $sbom ([string]$policyRole.platformManifestDigest) "$role SBOM"
    Assert-StatementSubject $provenance ([string]$policyRole.platformManifestDigest) "$role provenance"
    $spdx = Get-RequiredProperty $sbom 'predicate' "$role SBOM"
    if ([string](Get-RequiredProperty $spdx 'spdxVersion' "$role SPDX") -cne [string]$policyRole.sbom.spdxVersion -or
        [string](Get-RequiredProperty $spdx 'dataLicense' "$role SPDX") -cne [string]$policyRole.sbom.dataLicense) {
        throw "$role SPDX identity mismatch."
    }

    $pythonInventory = Read-Inventory $files.pythonInventory $role 'pypi' @('direct', 'transitive', 'unlinked')
    $debianInventory = Read-Inventory $files.debianInventory $role 'deb' @('direct', 'transitive', 'base')
    $inventoryKeys = @{}
    foreach ($key in @($pythonInventory.Keys.Keys) + @($debianInventory.Keys.Keys)) {
        if ($inventoryKeys.ContainsKey($key)) { throw "$role package collision: $key" }
        $inventoryKeys[$key] = $true
    }

    $legalDocument = Read-JsonFile $files.legalManifest "$role legal manifest"
    Assert-ExactSchema $legalDocument 'photon.cad.legal-manifest/v1' "$role legal manifest"
    if ([string](Get-RequiredProperty $legalDocument 'role' "$role legal manifest") -cne $role) { throw "$role legal-manifest role mismatch." }
    Assert-EmptyUnresolved $legalDocument "$role legal manifest"
    $legalPackages = @(Get-RequiredProperty $legalDocument 'packages' "$role legal manifest")
    $legalByKey = @{}
    $legalFiles = @{}
    $sourceRequired = @{}
    foreach ($package in $legalPackages) {
        $ecosystem = [string](Get-RequiredProperty $package 'ecosystem' "$role legal package")
        $name = [string](Get-RequiredProperty $package 'name' "$role legal package")
        $version = [string](Get-RequiredProperty $package 'version' "$role legal package")
        $key = Get-PackageKey $ecosystem $name $version
        if (-not $inventoryKeys.ContainsKey($key) -or $legalByKey.ContainsKey($key)) { throw "$role legal coverage is not one-to-one for $key." }
        if ([string](Get-RequiredProperty $package 'resolutionStatus' "$role legal package") -cne 'resolved') { throw "$role legal package is unresolved: $key" }
        $licenseExpression = [string](Get-RequiredProperty $package 'licenseExpression' "$role legal package")
        if ([string]::IsNullOrWhiteSpace($licenseExpression) -or $licenseExpression -ceq 'NOASSERTION') { throw "$role legal package lacks a resolved SPDX expression: $key" }
        $packageFiles = @(Get-RequiredProperty $package 'legalFiles' "$role legal package")
        if ($packageFiles.Count -eq 0) { throw "$role legal package has no legal files: $key" }
        foreach ($legalFile in $packageFiles) {
            $relativePath = ([string](Get-RequiredProperty $legalFile 'path' "$role legal file")) -replace '\\', '/'
            if (-not $relativePath.StartsWith("$role/licenses/", [StringComparison]::Ordinal)) { throw "$role legal file is outside its role license directory." }
            $kind = [string](Get-RequiredProperty $legalFile 'kind' "$role legal file")
            if (@($policy.allowedLegalFileKinds) -cnotcontains $kind) { throw "$role legal file kind is not allowed: $kind" }
            $expectedHash = [string](Get-RequiredProperty $legalFile 'sha256' "$role legal file")
            $expectedLength = [long](Get-RequiredProperty $legalFile 'byteLength' "$role legal file")
            Assert-Sha256 $expectedHash "$role legal file hash"
            $file = Resolve-SafeFile $inputRoot $relativePath ([long]$policy.maximumLegalArtifactBytes) "$role legal file"
            if ((Get-Sha256 $file.FullName) -cne $expectedHash -or $file.Length -ne $expectedLength) { throw "$role legal file hash/length mismatch: $relativePath" }
            if ($legalFiles.ContainsKey($relativePath)) { throw "$role legal file path is duplicated: $relativePath" }
            $legalFiles[$relativePath] = [pscustomobject]@{ File = $file; Kind = $kind; PackageKey = $key; Sha256 = $expectedHash; ByteLength = $expectedLength }
        }
        $legalByKey[$key] = $package
        if ([bool](Get-RequiredProperty $package 'sourceOfferRequired' "$role legal package")) { $sourceRequired[$key] = $true }
    }
    if ($legalByKey.Count -ne $inventoryKeys.Count) { throw "$role legal manifest does not cover every inventory package exactly once." }

    foreach ($notice in @($policyRole.requiredNotices)) {
        $noticeKey = Get-PackageKey ([string]$notice.ecosystem) ([string]$notice.name) ([string]$notice.version)
        if (-not $legalByKey.ContainsKey($noticeKey)) { throw "$role required NOTICE package is absent: $noticeKey" }
        $noticeMatches = @($legalFiles.Values | Where-Object { $_.PackageKey -ceq $noticeKey -and $_.Kind -ceq 'notice' -and $_.Sha256 -ceq [string]$notice.sha256 -and $_.ByteLength -eq [long]$notice.byteLength })
        if ($noticeMatches.Count -ne 1) { throw "$role required NOTICE is absent or ambiguous: $noticeKey" }
    }
    if ($null -ne $policyRole.source.PSObject.Properties['runtimeAdaptation'] -and [bool]$policyRole.source.runtimeAdaptation.modificationNoticeRequired) {
        if (@($legalFiles.Values | Where-Object { $_.Kind -ceq 'modification-notice' }).Count -ne 1) { throw "$role requires exactly one PartCAD modification notice." }
    }

    $sourceOfferDocument = Read-JsonFile $files.sourceOffer "$role source offer"
    Assert-ExactSchema $sourceOfferDocument 'photon.cad.source-offer/v1' "$role source offer"
    if ([string](Get-RequiredProperty $sourceOfferDocument 'role' "$role source offer") -cne $role) { throw "$role source-offer role mismatch." }
    Assert-EmptyUnresolved $sourceOfferDocument "$role source offer"
    $offerByKey = @{}
    foreach ($offer in @(Get-RequiredProperty $sourceOfferDocument 'entries' "$role source offer")) {
        $packageKey = [string](Get-RequiredProperty $offer 'packageKey' "$role source-offer entry")
        if (-not $sourceRequired.ContainsKey($packageKey) -or $offerByKey.ContainsKey($packageKey)) { throw "$role source offer has an unexpected or duplicate package: $packageKey" }
        if ([string](Get-RequiredProperty $offer 'status' "$role source-offer entry") -cne 'resolved') { throw "$role source-offer entry is unresolved: $packageKey" }
        $sourceSha = [string](Get-RequiredProperty $offer 'sourceSha256' "$role source-offer entry")
        Assert-Sha256 $sourceSha "$role source-offer source hash"
        $uriText = [string](Get-RequiredProperty $offer 'sourceUri' "$role source-offer entry")
        [Uri] $uri = $null
        if (-not [Uri]::TryCreate($uriText, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https') { throw "$role source offer must use an absolute HTTPS URI." }
        $offerByKey[$packageKey] = $offer
    }
    if ($offerByKey.Count -ne $sourceRequired.Count) { throw "$role source offer does not cover every required package exactly once." }

    foreach ($override in @($pythonMap.entries | Where-Object { [string]$_.role -ceq $role })) {
        $key = Get-PackageKey 'pypi' ([string]$override.name) ([string]$override.version)
        if (-not $legalByKey.ContainsKey($key) -or [string]$legalByKey[$key].licenseExpression -cne [string]$override.licenseExpression) {
            throw "$role legal manifest does not match resolved Python override $key."
        }
    }
    $availableLegalHashes = @{}
    foreach ($entry in $legalFiles.Values) { $availableLegalHashes[$entry.Sha256] = $true }
    $sourceOfferHash = Get-Sha256 $files.sourceOffer.FullName
    foreach ($pinned in @($upstreamMap.entries | Where-Object { [string]$_.role -ceq $role })) {
        $hash = [string]$pinned.sha256
        if (-not $availableLegalHashes.ContainsKey($hash) -and $hash -cne $sourceOfferHash) {
            throw "$role pinned upstream legal artifact is not present: $($pinned.id)"
        }
    }

    $contexts += [pscustomobject]@{
        Role = $role; Policy = $policyRole; Input = $inputRole; Files = $files
        ImageManifest = $imageManifest; ConfigDigest = $configDigest; Sbom = $sbom
        Python = $pythonInventory; Debian = $debianInventory; LegalDocument = $legalDocument
        LegalPackages = $legalPackages; LegalFiles = $legalFiles; SourceOfferDocument = $sourceOfferDocument
    }
}

$resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $OutputDirectory))
$leaf = Split-Path -Leaf $OutputDirectory
if ([string]::IsNullOrWhiteSpace($leaf) -or -not (Test-Path -LiteralPath $resolvedParent -PathType Container)) { throw 'OutputDirectory must be new beneath an existing parent.' }
$resolvedOutput = Join-Path $resolvedParent $leaf
if (Test-Path -LiteralPath $resolvedOutput) { throw "OutputDirectory already exists: $resolvedOutput" }
New-Item -ItemType Directory -Path $resolvedOutput -ErrorAction Stop | Out-Null

$inputCopies = @(
    @{ Name = 'compliance-policy.v1.json'; Source = $policySource.File },
    @{ Name = 'python-license-map.v1.json'; Source = $pythonMapSource.File },
    @{ Name = 'upstream-legal-files.v1.json'; Source = $upstreamMapSource.File },
    @{ Name = 'photon-cad-compliance-receipt-v1.schema.json'; Source = $schemaSource.File }
)
$inputRecords = @{}
foreach ($copy in $inputCopies) {
    $relative = "inputs/$($copy.Name)"
    Copy-NewFile $copy.Source (Join-Path $resolvedOutput ($relative -replace '/', '\'))
    $inputRecords[$copy.Name] = Get-ArtifactRecord $relative $copy.Source
}

$bundleReceipts = @()
foreach ($context in $contexts) {
    $role = $context.Role
    $canonicalNames = [ordered]@{
        imageIndex = 'image-index.oci.json'; imageManifest = 'image-manifest.oci.json'
        attestationManifest = 'attestation-manifest.oci.json'; sbom = 'sbom.spdx.intoto.json'
        provenance = 'provenance.intoto.json'; pythonInventory = 'python-packages.json'
        debianInventory = 'debian-packages.json'; sourceOffer = 'debian-source-offer.json'
        legalManifest = 'legal-files.manifest.json'
    }
    $artifactRecords = @{}
    foreach ($key in $canonicalNames.Keys) {
        $relative = "$role/$($canonicalNames[$key])"
        Copy-NewFile $context.Files[$key] (Join-Path $resolvedOutput ($relative -replace '/', '\'))
        $artifactRecords[$key] = Get-ArtifactRecord $relative $context.Files[$key]
    }
    $legalRecords = @()
    foreach ($relative in @($context.LegalFiles.Keys | Sort-Object)) {
        $entry = $context.LegalFiles[$relative]
        Copy-NewFile $entry.File (Join-Path $resolvedOutput ($relative -replace '/', '\'))
        $legalRecords += Get-ArtifactRecord $relative $entry.File
    }
    $noticeLines = @(
        'Photon CAD third-party legal artifact index',
        "Role: $role",
        'The referenced legal files are copied verbatim beside this index.',
        ''
    )
    foreach ($package in @($context.LegalPackages | Sort-Object ecosystem, name, version)) {
        $key = Get-PackageKey ([string]$package.ecosystem) ([string]$package.name) ([string]$package.version)
        $paths = @($context.LegalFiles.GetEnumerator() | Where-Object { $_.Value.PackageKey -ceq $key } | ForEach-Object { $_.Key } | Sort-Object)
        $noticeLines += ('{0} {1} {2} | {3} | {4}' -f $package.ecosystem, $package.name, $package.version, $package.licenseExpression, ($paths -join ', '))
    }
    $noticeText = ($noticeLines -join "`n") + "`n"
    $noticeBytes = $utf8.GetBytes($noticeText)
    $noticeRelative = "$role/THIRD_PARTY_NOTICES.txt"
    $noticePath = Join-Path $resolvedOutput ($noticeRelative -replace '/', '\')
    Write-NewBytes $noticePath $noticeBytes
    $noticeItem = Get-Item -LiteralPath $noticePath -ErrorAction Stop

    $spdx = $context.Sbom.predicate
    $archiveRelative = ([string](Get-RequiredProperty $context.Input.artifacts.archive 'path' "$role archive")) -replace '\\', '/'
    $bundleReceipts += [ordered]@{
        role = $role
        tag = [string]$context.Policy.tag
        image = [ordered]@{
            indexDigest = [string]$context.Policy.imageIndexDigest
            platformManifestDigest = [string]$context.Policy.platformManifestDigest
            configDigest = $context.ConfigDigest
            attestationManifestDigest = [string]$context.Policy.attestationManifestDigest
            archive = Get-ArtifactRecord $archiveRelative $context.Files.archive
        }
        source = $context.Policy.source
        baseImage = $context.Policy.baseImage
        attestations = [ordered]@{
            imageIndex = $artifactRecords.imageIndex
            imageManifest = $artifactRecords.imageManifest
            attestationManifest = $artifactRecords.attestationManifest
            sbom = [ordered]@{
                artifact = $artifactRecords.sbom; predicateType = [string]$context.Policy.sbom.predicateType
                layerDigest = [string]$context.Policy.sbom.digest; subjectDigest = [string]$context.Policy.platformManifestDigest
                spdxVersion = [string]$spdx.spdxVersion; dataLicense = [string]$spdx.dataLicense
                packageCount = @($spdx.packages).Count; fileCount = @($spdx.files).Count; relationshipCount = @($spdx.relationships).Count
            }
            provenance = [ordered]@{
                artifact = $artifactRecords.provenance; predicateType = [string]$context.Policy.provenance.predicateType
                layerDigest = [string]$context.Policy.provenance.digest; subjectDigest = [string]$context.Policy.platformManifestDigest
            }
        }
        inventories = [ordered]@{
            python = [ordered]@{
                artifact = $artifactRecords.pythonInventory; packageCount = $context.Python.Packages.Count
                directCount = [int]$context.Python.Counts.direct; transitiveCount = [int]$context.Python.Counts.transitive
                residualCount = [int]$context.Python.Counts.unlinked
            }
            debian = [ordered]@{
                artifact = $artifactRecords.debianInventory; packageCount = $context.Debian.Packages.Count
                directCount = [int]$context.Debian.Counts.direct; transitiveCount = [int]$context.Debian.Counts.transitive
                residualCount = [int]$context.Debian.Counts.base
            }
        }
        legal = [ordered]@{
            manifest = $artifactRecords.legalManifest; sourceOffer = $artifactRecords.sourceOffer
            thirdPartyNotices = Get-ArtifactRecord $noticeRelative $noticeItem
            packageCount = $context.LegalPackages.Count
            noticeCount = @($context.LegalFiles.Values | Where-Object { $_.Kind -ceq 'notice' }).Count
            files = $legalRecords
        }
        status = 'pass'
        blockers = @()
    }
}

$receipt = [ordered]@{
    schema = [string]$policy.receiptSchema
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    platform = [string]$policy.platform
    scanner = [ordered]@{
        uri = [string]$policy.scanner.uri
        digest = [string]$policy.scanner.digest
        buildxVersion = [string]$policy.buildxVersion
    }
    policy = $inputRecords['compliance-policy.v1.json']
    maps = [ordered]@{
        pythonLicenses = $inputRecords['python-license-map.v1.json']
        upstreamLegalFiles = $inputRecords['upstream-legal-files.v1.json']
        receiptSchema = $inputRecords['photon-cad-compliance-receipt-v1.schema.json']
    }
    bundles = $bundleReceipts
    status = 'pass'
    blockers = @()
}
$receiptPath = Join-Path $resolvedOutput 'compliance-receipt.v1.json'
$receiptBytes = $utf8.GetBytes(($receipt | ConvertTo-Json -Depth 24) + "`n")
Write-NewBytes $receiptPath $receiptBytes
$receiptHash = Get-Sha256 $receiptPath
$digestPath = Join-Path $resolvedOutput 'compliance-receipt.v1.json.sha256'
Write-NewBytes $digestPath ($utf8.GetBytes("$receiptHash *compliance-receipt.v1.json`n"))

$result = [pscustomobject]@{
    Status = 'PASS'
    Receipt = $receiptPath
    ReceiptSha256 = $receiptHash
    BundleCount = $bundleReceipts.Count
    ComplianceDirectory = $resolvedOutput
}
foreach ($stream in $readLocks) { $stream.Dispose() }
$readLocks.Clear()

$result
