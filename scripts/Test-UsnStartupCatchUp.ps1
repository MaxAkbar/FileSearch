[CmdletBinding()]
param(
    [string]$Root = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindows) {
    throw "USN startup catch-up smoke testing requires Windows."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repoRoot "FileSearch.slnx"

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = [System.IO.Path]::GetTempPath()
}

$fullRoot = [System.IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
    throw "Smoke root does not exist: $fullRoot"
}

Write-Host "Running USN startup catch-up smoke test..."
Write-Host "  Root: $fullRoot"
Write-Host "  Configuration: $Configuration"

$previousRun = $env:FILESEARCH_RUN_USN_SMOKE
$previousRoot = $env:FILESEARCH_USN_SMOKE_ROOT
try {
    $env:FILESEARCH_RUN_USN_SMOKE = "1"
    $env:FILESEARCH_USN_SMOKE_ROOT = $fullRoot

    dotnet test $solution `
        --configuration $Configuration `
        --filter "FullyQualifiedName~UsnStartupCatchUpSmokeTests.RealVolumeStartupCatchUpReplaysChanges"
}
finally {
    if ($null -eq $previousRun) {
        Remove-Item Env:\FILESEARCH_RUN_USN_SMOKE -ErrorAction SilentlyContinue
    }
    else {
        $env:FILESEARCH_RUN_USN_SMOKE = $previousRun
    }

    if ($null -eq $previousRoot) {
        Remove-Item Env:\FILESEARCH_USN_SMOKE_ROOT -ErrorAction SilentlyContinue
    }
    else {
        $env:FILESEARCH_USN_SMOKE_ROOT = $previousRoot
    }
}
