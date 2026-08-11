[CmdletBinding()]
param(
    [string]$DestinationPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\toolchain-cache\netcoredbg-win64.zip')
)

$ErrorActionPreference = 'Stop'
$sourceUri = 'https://github.com/Samsung/netcoredbg/releases/download/3.1.3-1062/netcoredbg-win64.zip'
$expectedLength = 3475639L
$expectedHash = 'C67AE052E0BCB9CE37000F261E2D397A0D5B6615CAFE30C868239A78598DFB37'
$destination = [IO.Path]::GetFullPath($DestinationPath)
$parent = Split-Path -Parent $destination

function Test-VerifiedArchive {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
    return [long]$item.Length -eq $expectedLength -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ceq $expectedHash
}

if (Test-VerifiedArchive -Path $destination) {
    Write-Host "Using verified NetCoreDbg release input: $destination" -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Path $parent -Force | Out-Null
$parentItem = Get-Item -LiteralPath $parent -Force
if ($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "The NetCoreDbg cache directory must not be a reparse point: $parent"
}
$temporary = Join-Path $parent ('.netcoredbg-download-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Write-Host "Downloading pinned NetCoreDbg 3.1.3-1062 from Samsung/netcoredbg..." -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $sourceUri -OutFile $temporary
    if (-not (Test-VerifiedArchive -Path $temporary)) {
        throw 'The downloaded NetCoreDbg archive did not match the pinned byte length and SHA-256.'
    }
    if (Test-Path -LiteralPath $destination) {
        $existing = Get-Item -LiteralPath $destination -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace a non-regular NetCoreDbg cache entry: $destination"
        }
    }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Write-Host "Acquired and verified $destination" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
}
