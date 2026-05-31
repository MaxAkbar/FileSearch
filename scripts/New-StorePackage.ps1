[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "1.0.0.0",
    [string]$PackageIdentityName = "FileSearch",
    [string]$Publisher = "CN=FileSearch",
    [string]$PublisherDisplayName = "FileSearch",
    [string]$DisplayName = "FileSearch",
    [string]$Description = "Search files by name and content.",
    [string]$OutputRoot = "artifacts\store",
    [string]$CertificateThumbprint = ""
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath([string]$Path) {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$Path"))
}

function Assert-InRepo([string]$Path, [string]$RepoRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository: $fullPath"
    }
}

function Find-WindowsKitTool([string]$ToolName) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        throw "Windows 10 SDK tools were not found. Install the Windows SDK, then rerun this script."
    }

    $architecture = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
    $tool = Get-ChildItem -Path $kitsRoot -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\$architecture\$ToolName" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $tool) {
        throw "$ToolName was not found under $kitsRoot."
    }

    return $tool.FullName
}

function ConvertTo-PackageArchitecture([string]$Rid) {
    switch ($Rid) {
        "win-x64" { return "x64" }
        "win-x86" { return "x86" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported runtime identifier: $Rid" }
    }
}

function Get-RelativePath([string]$BasePath, [string]$FullPath) {
    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $baseFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseFullPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $fullPathValue = [System.IO.Path]::GetFullPath($FullPath)
    $baseUri = New-Object System.Uri $baseFullPath
    $fullUri = New-Object System.Uri $fullPathValue
    $relative = $baseUri.MakeRelativeUri($fullUri).ToString()
    return [System.Uri]::UnescapeDataString($relative).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) {
        throw "Source directory does not exist: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function ConvertTo-XmlEscaped([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

function Write-Manifest(
    [string]$TemplatePath,
    [string]$DestinationPath,
    [string]$Architecture
) {
    $manifest = Get-Content -Raw $TemplatePath
    $replacements = @{
        "__PACKAGE_IDENTITY_NAME__" = ConvertTo-XmlEscaped $PackageIdentityName
        "__PUBLISHER__" = ConvertTo-XmlEscaped $Publisher
        "__VERSION__" = ConvertTo-XmlEscaped $Version
        "__ARCHITECTURE__" = ConvertTo-XmlEscaped $Architecture
        "__DISPLAY_NAME__" = ConvertTo-XmlEscaped $DisplayName
        "__PUBLISHER_DISPLAY_NAME__" = ConvertTo-XmlEscaped $PublisherDisplayName
        "__DESCRIPTION__" = ConvertTo-XmlEscaped $Description
    }

    foreach ($key in $replacements.Keys) {
        $manifest = $manifest.Replace($key, $replacements[$key])
    }

    Set-Content -Path $DestinationPath -Value $manifest -Encoding UTF8
}

function New-LogoAsset(
    [System.Drawing.Image]$SourceImage,
    [string]$DestinationPath,
    [int]$Width,
    [int]$Height,
    [bool]$FillBackground
) {
    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        if ($FillBackground) {
            $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml("#202124"))
            $maxWidth = [Math]::Floor($Width * 0.58)
            $maxHeight = [Math]::Floor($Height * 0.58)
        }
        else {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $maxWidth = [Math]::Floor($Width * 0.78)
            $maxHeight = [Math]::Floor($Height * 0.78)
        }

        $scale = [Math]::Min($maxWidth / $SourceImage.Width, $maxHeight / $SourceImage.Height)
        $drawWidth = [Math]::Max(1, [int][Math]::Round($SourceImage.Width * $scale))
        $drawHeight = [Math]::Max(1, [int][Math]::Round($SourceImage.Height * $scale))
        $x = [int][Math]::Round(($Width - $drawWidth) / 2)
        $y = [int][Math]::Round(($Height - $drawHeight) / 2)

        $graphics.DrawImage($SourceImage, $x, $y, $drawWidth, $drawHeight)
        $bitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-PackageAssets([string]$AssetsDirectory) {
    Add-Type -AssemblyName System.Drawing

    $sourceLogo = Resolve-RepoPath "src\FileSearch.Gui\Assets\FileSearch-256.png"
    if (-not (Test-Path $sourceLogo)) {
        throw "Source logo not found: $sourceLogo"
    }

    New-Item -ItemType Directory -Force -Path $AssetsDirectory | Out-Null

    $image = [System.Drawing.Image]::FromFile($sourceLogo)
    try {
        New-LogoAsset $image (Join-Path $AssetsDirectory "Square44x44Logo.png") 44 44 $false
        New-LogoAsset $image (Join-Path $AssetsDirectory "Square150x150Logo.png") 150 150 $false
        New-LogoAsset $image (Join-Path $AssetsDirectory "Square310x310Logo.png") 310 310 $false
        New-LogoAsset $image (Join-Path $AssetsDirectory "StoreLogo.png") 50 50 $false
        New-LogoAsset $image (Join-Path $AssetsDirectory "Wide310x150Logo.png") 310 150 $true
        New-LogoAsset $image (Join-Path $AssetsDirectory "SplashScreen.png") 620 300 $true
    }
    finally {
        $image.Dispose()
    }
}

function New-AppSymbols([string]$PublishDirectory, [string]$SymbolsPath, [string]$ScratchDirectory) {
    $pdbFiles = Get-ChildItem -Path $PublishDirectory -Recurse -Filter "*.pdb" -File
    if ($pdbFiles.Count -eq 0) {
        return $false
    }

    $symbolsRoot = Join-Path $ScratchDirectory "symbols"
    New-Item -ItemType Directory -Force -Path $symbolsRoot | Out-Null

    foreach ($pdb in $pdbFiles) {
        $relative = Get-RelativePath $PublishDirectory $pdb.FullName
        $target = Join-Path $symbolsRoot $relative
        New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -Path $pdb.FullName -Destination $target -Force
    }

    $zipPath = [System.IO.Path]::ChangeExtension($SymbolsPath, ".zip")
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    if (Test-Path $SymbolsPath) { Remove-Item -LiteralPath $SymbolsPath -Force }

    Compress-Archive -Path (Join-Path $symbolsRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Move-Item -LiteralPath $zipPath -Destination $SymbolsPath -Force
    return $true
}

function New-MsixUpload([string]$MsixPath, [string]$SymbolsPath, [string]$UploadPath) {
    $uploadScratch = Join-Path ([System.IO.Path]::GetDirectoryName($UploadPath)) "upload"
    if (Test-Path $uploadScratch) { Remove-Item -LiteralPath $uploadScratch -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $uploadScratch | Out-Null

    Copy-Item -Path $MsixPath -Destination $uploadScratch -Force
    if (Test-Path $SymbolsPath) {
        Copy-Item -Path $SymbolsPath -Destination $uploadScratch -Force
    }

    $zipPath = [System.IO.Path]::ChangeExtension($UploadPath, ".zip")
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    if (Test-Path $UploadPath) { Remove-Item -LiteralPath $UploadPath -Force }

    Compress-Archive -Path (Join-Path $uploadScratch "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Move-Item -LiteralPath $zipPath -Destination $UploadPath -Force
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Resolve-RepoPath "src\FileSearch.Gui\FileSearch.Gui.csproj"
$manifestTemplate = Resolve-RepoPath "packaging\AppxManifest.template.xml"
$outputRootPath = Resolve-RepoPath $OutputRoot
$publishDirectory = Join-Path $outputRootPath "publish\$RuntimeIdentifier"
$packageRoot = Join-Path $outputRootPath "package-root\$RuntimeIdentifier"
$assetsDirectory = Resolve-RepoPath "packaging\Assets"
$packageArchitecture = ConvertTo-PackageArchitecture $RuntimeIdentifier
$packageBaseName = "$($DisplayName.Replace(' ', ''))_${Version}_${packageArchitecture}"
$msixPath = Join-Path $outputRootPath "$packageBaseName.msix"
$symbolsPath = Join-Path $outputRootPath "$packageBaseName.appxsym"
$uploadPath = Join-Path $outputRootPath "$packageBaseName.msixupload"

Assert-InRepo $outputRootPath $repoRoot
Assert-InRepo $publishDirectory $repoRoot
Assert-InRepo $packageRoot $repoRoot

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSIX package version must use four integer parts, for example 1.0.0.0."
}

$makeAppx = Find-WindowsKitTool "makeappx.exe"
$signTool = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { "" } else { Find-WindowsKitTool "signtool.exe" }

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
if (Test-Path $msixPath) { Remove-Item -LiteralPath $msixPath -Force }
if (Test-Path $symbolsPath) { Remove-Item -LiteralPath $symbolsPath -Force }
if (Test-Path $uploadPath) { Remove-Item -LiteralPath $uploadPath -Force }

Write-Host "Publishing $RuntimeIdentifier $Configuration..."
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=portable `
    -p:DebugSymbols=true `
    -o $publishDirectory

Write-Host "Staging MSIX package root..."
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
Copy-DirectoryContents $publishDirectory $packageRoot
Get-ChildItem -Path $packageRoot -Recurse -Filter "*.pdb" -File | Remove-Item -Force
New-PackageAssets $assetsDirectory
Copy-DirectoryContents $assetsDirectory (Join-Path $packageRoot "Assets")
Write-Manifest $manifestTemplate (Join-Path $packageRoot "AppxManifest.xml") $packageArchitecture

Write-Host "Creating MSIX..."
& $makeAppx pack /d $packageRoot /p $msixPath /o | Write-Host

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Host "Signing MSIX with certificate thumbprint $CertificateThumbprint..."
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint $msixPath | Write-Host
}

Write-Host "Creating symbols package..."
$hasSymbols = New-AppSymbols $publishDirectory $symbolsPath $outputRootPath
if (-not $hasSymbols) {
    Write-Host "No PDB files found; continuing without .appxsym."
}

Write-Host "Creating Store upload package..."
New-MsixUpload $msixPath $symbolsPath $uploadPath

Write-Host ""
Write-Host "Created:"
Write-Host "  MSIX:       $msixPath"
if (Test-Path $symbolsPath) { Write-Host "  Symbols:    $symbolsPath" }
Write-Host "  Store file: $uploadPath"
Write-Host ""
Write-Host "For Partner Center submission, use the .msixupload file after replacing the package identity and publisher values with the values reserved for your Store app."
