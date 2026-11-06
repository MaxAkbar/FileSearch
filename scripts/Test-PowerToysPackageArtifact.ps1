[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$RequireSigned
)

$ErrorActionPreference = 'Stop'
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$layout = Join-Path $artifactRoot "package\$RuntimeIdentifier"
$package = Join-Path $artifactRoot "FileSearch.PowerToys_${Version}_$RuntimeIdentifier.msix"
foreach ($relativePath in @(
    'AppxManifest.xml', 'FileSearch.PowerToys.exe', 'FileSearch.PowerToys.dll',
    'Microsoft.CommandPalette.Extensions.dll', 'Microsoft.CommandPalette.Extensions.Toolkit.dll',
    'THIRD-PARTY-NOTICES.txt', 'Assets\StoreLogo.png', 'Public\README.txt'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $layout $relativePath) -PathType Leaf)) {
        throw "Companion layout is missing $relativePath."
    }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Raw
if ($manifest.Package.Identity.Version -ne $Version -or
    $manifest.Package.Identity.ProcessorArchitecture -ne $RuntimeIdentifier.Substring(4)) {
    throw 'Companion manifest version or architecture does not match the release.'
}
$extension = $manifest.SelectSingleNode("//*[local-name()='AppExtension' and @Name='com.microsoft.commandpalette']")
if (-not $extension -or $extension.PublicFolder -ne 'Public') {
    throw 'Command Palette discovery is missing from the companion manifest.'
}
if ($manifest.SelectSingleNode("//*[local-name()='StartupTask']")) {
    throw 'The optional companion must not register a startup task.'
}
$class = $manifest.SelectSingleNode("//*[local-name()='ExeServer']/*[local-name()='Class']")
$activation = $extension.SelectSingleNode(".//*[local-name()='CreateInstance']")
if ($class.Id -ne '969211A8-FD4E-4DA0-A1B7-A855127DBC7A' -or $activation.ClassId -ne $class.Id) {
    throw 'Companion COM activation identifiers do not match.'
}
$fileVersion = (Get-Item -LiteralPath (Join-Path $layout 'FileSearch.PowerToys.exe')).VersionInfo.FileVersion
if ($fileVersion -ne $Version) {
    throw "Companion executable version is $fileVersion; expected $Version."
}
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw 'The companion MSIX is missing.'
}
if ($RequireSigned) {
    $signature = Get-AuthenticodeSignature -LiteralPath $package
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {
        throw 'The companion MSIX must have a valid timestamped signature.'
    }
}
else {
    $zipPath = Join-Path $artifactRoot "FileSearch.PowerToys_${Version}_$RuntimeIdentifier-development.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in @('AppxManifest.xml', 'FileSearch.PowerToys.exe', 'README-DEVELOPMENT.txt')) {
            if (-not $archive.GetEntry($entry)) {
                throw "The unsigned development archive is missing $entry."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
$checksumPath = Join-Path $artifactRoot "SHA256SUMS-PowerToys-$RuntimeIdentifier.txt"
$checkedNames = @()
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -notmatch '^(?<hash>[a-fA-F0-9]{64})\s+(?<name>[^/\\]+)$') {
        throw "Invalid companion checksum entry: $line"
    }
    $expected = $Matches.hash
    $name = $Matches.name
    $actual = (Get-FileHash -LiteralPath (Join-Path $artifactRoot $name) -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Checksum mismatch for $name." }
    $checkedNames += $name
}
if ($checkedNames -notcontains [IO.Path]::GetFileName($package)) {
    throw 'The companion MSIX is missing from the checksum file.'
}
if (-not $RequireSigned -and $checkedNames -notcontains [IO.Path]::GetFileName($zipPath)) {
    throw 'The unsigned development archive is missing from the checksum file.'
}
Write-Host "Verified optional PowerToys companion $Version ($RuntimeIdentifier), activation metadata, and checksums."
