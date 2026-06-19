[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputRoot = "artifacts\release"
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
    throw "Portable release version must use four integer parts, for example 1.0.0.0."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRootPath = Resolve-RepoPath $OutputRoot
$publishDirectory = Join-Path $outputRootPath "publish\$RuntimeIdentifier"
$archiveName = "FileSearch-$Version-$RuntimeIdentifier-portable.zip"
$archivePath = Join-Path $outputRootPath $archiveName
$checksumPath = Join-Path $outputRootPath "SHA256SUMS-$RuntimeIdentifier.txt"

Assert-InRepo $outputRootPath $repoRoot
Assert-InRepo $publishDirectory $repoRoot

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }

Publish-Project (Resolve-RepoPath "src\FileSearch.Gui\FileSearch.Gui.csproj") "FileSearch.Gui" $publishDirectory
Publish-Project (Resolve-RepoPath "src\FileSearch.Indexer\FileSearch.Indexer.csproj") "FileSearch.Indexer" $publishDirectory
Publish-Project (Resolve-RepoPath "src\FileSearch.ExtractorHost\FileSearch.ExtractorHost.csproj") "FileSearch.ExtractorHost" $publishDirectory

Assert-FileExists (Join-Path $publishDirectory "FileSearch.Gui.exe") "Published GUI executable"
Assert-FileExists (Join-Path $publishDirectory "FileSearch.Indexer.exe") "Published background indexer executable"
Assert-FileExists (Join-Path $publishDirectory "FileSearch.ExtractorHost.exe") "Published extractor host executable"
Assert-FileExists (Join-Path $publishDirectory "Help\index.html") "Published help bundle"

$tempArchive = [System.IO.Path]::ChangeExtension($archivePath, ".tmp.zip")
if (Test-Path -LiteralPath $tempArchive) { Remove-Item -LiteralPath $tempArchive -Force }
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $tempArchive -CompressionLevel Optimal
Move-Item -LiteralPath $tempArchive -Destination $archivePath -Force

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath
"$($hash.Hash.ToLowerInvariant())  $archiveName" | Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Portable release archive: $archivePath"
Write-Host "Portable release checksums: $checksumPath"
