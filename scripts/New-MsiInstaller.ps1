[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ProductName = "FileSearch",
    [string]$Manufacturer = "Max Akbar",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$OutputRoot = "artifacts\msi"
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Assert-InRepo([string]$Path, [string]$RepoRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside repository. Path: $fullPath"
    }
}

function Find-WindowsKitTool([string]$ToolName) {
    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

    foreach ($root in $kitRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter $ToolName -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\$([Regex]::Escape($ToolName))$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw "$ToolName was not found. Install the Windows SDK or add it to PATH."
}

function Invoke-CheckedCommand([string]$FileName, [string[]]$Arguments, [string]$Message) {
    Write-Host $Message
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName failed with exit code $LASTEXITCODE."
    }
}

function Publish-Project(
    [string]$ProjectPath,
    [string]$ProjectDisplayName,
    [string]$Destination
) {
    Invoke-CheckedCommand "dotnet" @(
        "publish",
        $ProjectPath,
        "--configuration",
        $Configuration,
        "--runtime",
        $RuntimeIdentifier,
        "--self-contained",
        "true",
        "-p:PublishSingleFile=false",
        "-p:PublishReadyToRun=true",
        "-p:DebugType=portable",
        "-p:DebugSymbols=true",
        "-o",
        $Destination
    ) "Publishing $ProjectDisplayName $RuntimeIdentifier $Configuration..."
}

function Assert-FileExists([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSI version must use four integer parts, for example 1.0.0.0."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRootPath = Resolve-RepoPath $OutputRoot
$publishDirectory = Join-Path $outputRootPath "publish\$RuntimeIdentifier"
$wixOutputDirectory = Join-Path $outputRootPath "wix\$RuntimeIdentifier"
$msiName = "$ProductName-$Version-$RuntimeIdentifier.msi"
$msiPath = Join-Path $outputRootPath $msiName
$checksumPath = Join-Path $outputRootPath "SHA256SUMS-$RuntimeIdentifier.txt"
$installerProject = Resolve-RepoPath "installer\FileSearch.Installer\FileSearch.Installer.wixproj"

Assert-InRepo $outputRootPath $repoRoot
Assert-InRepo $publishDirectory $repoRoot
Assert-InRepo $wixOutputDirectory $repoRoot

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path -LiteralPath $wixOutputDirectory) { Remove-Item -LiteralPath $wixOutputDirectory -Recurse -Force }
if (Test-Path -LiteralPath $msiPath) { Remove-Item -LiteralPath $msiPath -Force }
if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }

Publish-Project (Resolve-RepoPath "src\FileSearch.Gui\FileSearch.Gui.csproj") "FileSearch.Gui" $publishDirectory
Publish-Project (Resolve-RepoPath "src\FileSearch.Indexer\FileSearch.Indexer.csproj") "FileSearch.Indexer" $publishDirectory
Publish-Project (Resolve-RepoPath "src\FileSearch.ExtractorHost\FileSearch.ExtractorHost.csproj") "FileSearch.ExtractorHost" $publishDirectory

Assert-FileExists (Join-Path $publishDirectory "FileSearch.Gui.exe") "Published GUI executable"
Assert-FileExists (Join-Path $publishDirectory "FileSearch.Indexer.exe") "Published background indexer executable"
Assert-FileExists (Join-Path $publishDirectory "FileSearch.ExtractorHost.exe") "Published extractor host executable"
Assert-FileExists (Join-Path $publishDirectory "Help\index.html") "Published help bundle"

New-Item -ItemType Directory -Force -Path $wixOutputDirectory | Out-Null
Invoke-CheckedCommand "dotnet" @(
    "build",
    $installerProject,
    "--configuration",
    $Configuration,
    "-p:RuntimeIdentifier=$RuntimeIdentifier",
    "-p:ProductVersion=$Version",
    "-p:ProductName=$ProductName",
    "-p:Manufacturer=$Manufacturer",
    "-p:PublishDirectory=$publishDirectory",
    "-o",
    $wixOutputDirectory
) "Building MSI installer..."

$builtMsi = Get-ChildItem -LiteralPath $wixOutputDirectory -Filter "*.msi" -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $builtMsi) {
    throw "WiX build did not produce an MSI in $wixOutputDirectory."
}

Move-Item -LiteralPath $builtMsi.FullName -Destination $msiPath -Force

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $signTool = Find-WindowsKitTool "signtool.exe"
    $signArguments = @(
        "sign",
        "/fd", "SHA256",
        "/sha1", $CertificateThumbprint
    )

    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signArguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }

    $signArguments += $msiPath
    Invoke-CheckedCommand $signTool $signArguments "Signing MSI with certificate thumbprint $CertificateThumbprint..."
}
else {
    Write-Host "No certificate thumbprint supplied; MSI will be unsigned."
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $msiPath
"$($hash.Hash.ToLowerInvariant())  $msiName" | Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "MSI installer: $msiPath"
Write-Host "MSI checksums: $checksumPath"
