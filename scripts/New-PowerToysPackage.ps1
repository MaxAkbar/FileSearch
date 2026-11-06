[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Version = '1.10.0.0',
    [string]$PackageIdentityName = 'MaxAkbar.FileSearch.PowerToys',
    [string]$Publisher = 'CN=CE11F335-A232-43CF-824D-292CFB1D1A12',
    [string]$OutputRoot = 'artifacts\powertoys',
    [string]$CertificateThumbprint = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $outputPath "package\$RuntimeIdentifier"))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
foreach ($targetPath in @($outputPath, $packageRoot)) {
    if (-not $targetPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output must be inside the repository: $targetPath"
    }
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'The package version must have four integer parts.'
}

function Find-SdkTool([string]$Name) {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter $Name -Recurse |
        Where-Object { $_.FullName -like "*\x64\$Name" } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "Install the Windows SDK to use $Name." }
    return $tool.FullName
}

$makeAppx = Find-SdkTool 'makeappx.exe'
# Only this companion's staging directory is replaced. Main application
# installers and publish output are deliberately separate.
if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$project = Join-Path $repoRoot 'src\FileSearch.PowerToys\FileSearch.PowerToys.csproj'
& dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Publishing the companion failed.' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\Assets') -Destination $packageRoot -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Public') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $packageRoot 'Public\README.txt') -Value 'FileSearch Command Palette extension.'
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\PowerToysManifest.template.xml') -Raw
$manifest = $manifest.Replace('__IDENTITY__', [Security.SecurityElement]::Escape($PackageIdentityName))
$manifest = $manifest.Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('__VERSION__', $Version)
$manifest = $manifest.Replace('__ARCHITECTURE__', $RuntimeIdentifier.Substring(4))
Set-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Value $manifest -Encoding utf8

$msix = Join-Path $outputPath "FileSearch.PowerToys_${Version}_$RuntimeIdentifier.msix"
& $makeAppx pack /d $packageRoot /p $msix /o
if ($LASTEXITCODE -ne 0) { throw 'Creating the companion MSIX failed.' }
if ($CertificateThumbprint) {
    $signTool = Find-SdkTool 'signtool.exe'
    & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw 'Signing the companion failed.' }
}
$hash = (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums = @("$hash  $([IO.Path]::GetFileName($msix))")
if (-not $CertificateThumbprint) {
    $developmentGuide = @'
FileSearch for PowerToys - unsigned development build

This companion is optional. Install FileSearch 1.10.0 or later separately.
The normal FileSearch application works without this companion or PowerToys.

This archive is for local testing on Windows 11 with Developer Mode already
enabled. It does not enable Developer Mode or change Windows security settings.
The unsigned MSIX cannot be installed normally without a matching trusted
signing certificate.

1. Extract this entire ZIP into a permanent folder.
2. In PowerShell, register the extracted manifest:
   Add-AppxPackage -Register 'C:\YourPermanentFolder\AppxManifest.xml'
3. Enable PowerToys Command Palette and reload its extensions.
4. Choose 'FileSearch: file names' or 'FileSearch: file contents'.

Keep this folder in place while the companion is installed. Uninstall
'FileSearch for PowerToys' in Windows Installed apps before moving or deleting
it. In the extension settings, set 'FileSearch application' to FileSearch.Gui.exe
if you use a portable or custom installation.

Full setup and limitations:
https://github.com/MaxAkbar/FileSearch/blob/master/README.PowerToys.md
'@
    Set-Content -LiteralPath (Join-Path $packageRoot 'README-DEVELOPMENT.txt') -Value $developmentGuide -Encoding utf8
    $developmentZip = Join-Path $outputPath "FileSearch.PowerToys_${Version}_$RuntimeIdentifier-development.zip"
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $developmentZip -CompressionLevel Optimal -Force
    $zipHash = (Get-FileHash -LiteralPath $developmentZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$zipHash  $([IO.Path]::GetFileName($developmentZip))"
    Write-Host "Unsigned development layout: $developmentZip"
}
Set-Content -LiteralPath (Join-Path $outputPath "SHA256SUMS-PowerToys-$RuntimeIdentifier.txt") -Value $checksums -Encoding ascii
Write-Host "Optional companion package: $msix"
Write-Host 'The companion is not installed automatically. The normal FileSearch installers are unchanged.'
