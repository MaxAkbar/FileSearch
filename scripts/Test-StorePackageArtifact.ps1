[CmdletBinding()]
param(
    [string]$ArtifactDirectory = "artifacts\store",
    [string]$Version = "",
    [ValidateSet("", "win-x64", "win-x86", "win-arm64")]
    [string]$RuntimeIdentifier = "",
    [string]$PackageIdentityName = "",
    [string]$Publisher = "",
    [switch]$RequireSigned,
    [switch]$RequireTrustedSignature,
    [switch]$RequireTimestamp,
    [switch]$RequireSymbols,
    [string]$ChecksumPath = ""
)

$ErrorActionPreference = "Stop"

function ConvertTo-PackageArchitecture([string]$Rid) {
    switch ($Rid) {
        "" { return "" }
        "win-x64" { return "x64" }
        "win-x86" { return "x86" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported runtime identifier: $Rid" }
    }
}

function Select-SingleFile([string]$Directory, [string]$Pattern, [string]$Description) {
    $files = Get-ChildItem -LiteralPath $Directory -Filter $Pattern -File
    if ($files.Count -eq 0) {
        throw "$Description was not found in $Directory."
    }

    if ($files.Count -gt 1) {
        $names = $files | ForEach-Object { $_.Name }
        throw "Expected one $Description in $Directory, found $($files.Count): $($names -join ', ')"
    }

    return $files[0]
}

function Assert-ZipEntry(
    [System.IO.Compression.ZipArchive]$Archive,
    [string]$EntryName,
    [string]$Description
) {
    $normalized = $EntryName.Replace('\', '/')
    $entry = $Archive.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq $normalized
    } | Select-Object -First 1

    if ($null -eq $entry) {
        throw "$Description is missing from package: $EntryName"
    }

    return $entry
}

function Read-ZipEntryText(
    [System.IO.Compression.ZipArchive]$Archive,
    [string]$EntryName
) {
    $entry = Assert-ZipEntry $Archive $EntryName $EntryName
    $stream = $entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-AuthenticodeSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "MSIX is not signed: $Path"
    }

    if ($null -eq $signature.SignerCertificate) {
        throw "MSIX signature has no signer certificate: $Path"
    }

    if ($RequireTrustedSignature -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "MSIX signature is not trusted. Status: $($signature.Status). Message: $($signature.StatusMessage)"
    }

    if ($RequireTimestamp -and $null -eq $signature.TimeStamperCertificate) {
        throw "MSIX signature does not include a timestamp certificate."
    }

    Write-Host "MSIX signature status: $($signature.Status)"
    Write-Host "MSIX signer: $($signature.SignerCertificate.Subject)"
}

function Write-Checksums([System.IO.FileInfo[]]$Files, [string]$DestinationPath, [string]$BaseDirectory) {
    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        $DestinationPath = Join-Path $BaseDirectory "SHA256SUMS.txt"
    }

    $lines = foreach ($file in $Files | Sort-Object Name) {
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($file.Name)"
    }

    Set-Content -LiteralPath $DestinationPath -Value $lines -Encoding ASCII
    Write-Host "Wrote checksums: $DestinationPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$artifactPath = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
    throw "Artifact directory does not exist: $artifactPath"
}

$architecture = ConvertTo-PackageArchitecture $RuntimeIdentifier
$msix = Select-SingleFile $artifactPath "*.msix" "MSIX package"
$upload = Select-SingleFile $artifactPath "*.msixupload" "Store upload package"
$symbols = Get-ChildItem -LiteralPath $artifactPath -Filter "*.appxsym" -File

if ($RequireSymbols -and $symbols.Count -eq 0) {
    throw "Symbols package was required but no .appxsym file was found in $artifactPath."
}

if ($symbols.Count -gt 1) {
    throw "Expected at most one symbols package in $artifactPath, found $($symbols.Count)."
}

if ($RequireSigned) {
    Assert-AuthenticodeSignature $msix.FullName
}

$msixArchive = [System.IO.Compression.ZipFile]::OpenRead($msix.FullName)
try {
    Assert-ZipEntry $msixArchive "AppxManifest.xml" "MSIX manifest" | Out-Null
    Assert-ZipEntry $msixArchive "FileSearch.Gui.exe" "GUI executable" | Out-Null
    Assert-ZipEntry $msixArchive "FileSearch.Indexer.exe" "background indexer executable" | Out-Null
    Assert-ZipEntry $msixArchive "FileSearch.ExtractorHost.exe" "extractor host executable" | Out-Null
    Assert-ZipEntry $msixArchive "Help/index.html" "help bundle" | Out-Null

    [xml]$manifest = Read-ZipEntryText $msixArchive "AppxManifest.xml"
    $identity = $manifest.Package.Identity
    if (-not [string]::IsNullOrWhiteSpace($Version) -and $identity.Version -ne $Version) {
        throw "MSIX version mismatch. Expected $Version, found $($identity.Version)."
    }

    if (-not [string]::IsNullOrWhiteSpace($architecture) -and $identity.ProcessorArchitecture -ne $architecture) {
        throw "MSIX architecture mismatch. Expected $architecture, found $($identity.ProcessorArchitecture)."
    }

    if (-not [string]::IsNullOrWhiteSpace($PackageIdentityName) -and $identity.Name -ne $PackageIdentityName) {
        throw "MSIX package identity mismatch. Expected $PackageIdentityName, found $($identity.Name)."
    }

    if (-not [string]::IsNullOrWhiteSpace($Publisher) -and $identity.Publisher -ne $Publisher) {
        throw "MSIX publisher mismatch. Expected $Publisher, found $($identity.Publisher)."
    }
}
finally {
    $msixArchive.Dispose()
}

$uploadArchive = [System.IO.Compression.ZipFile]::OpenRead($upload.FullName)
try {
    Assert-ZipEntry $uploadArchive $msix.Name "MSIX inside Store upload package" | Out-Null
    if ($symbols.Count -eq 1) {
        Assert-ZipEntry $uploadArchive $symbols[0].Name "symbols inside Store upload package" | Out-Null
    }
}
finally {
    $uploadArchive.Dispose()
}

$checksumFiles = @($msix, $upload)
if ($symbols.Count -eq 1) {
    $checksumFiles += $symbols[0]
}

Write-Checksums $checksumFiles $ChecksumPath $artifactPath
Write-Host "Store package artifact verification passed."
