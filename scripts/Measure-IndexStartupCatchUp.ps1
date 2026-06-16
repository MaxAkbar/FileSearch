[CmdletBinding()]
param(
    [string]$Root = "",
    [int]$FileCount = 2000,
    [int]$ChangeCount = 100,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindows) {
    throw "USN startup catch-up benchmarking requires Windows."
}

if ($FileCount -le 0) {
    throw "FileCount must be greater than zero."
}

if ($ChangeCount -le 0) {
    throw "ChangeCount must be greater than zero."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repoRoot "FileSearch.slnx"

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = [System.IO.Path]::GetTempPath()
}

$fullRoot = [System.IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
    throw "Benchmark root does not exist: $fullRoot"
}

$output = Join-Path ([System.IO.Path]::GetTempPath()) ("filesearch-usn-bench-" + [guid]::NewGuid().ToString("N") + ".txt")

Write-Host "Running USN startup catch-up benchmark..."
Write-Host "  Root: $fullRoot"
Write-Host "  Files: $FileCount"
Write-Host "  Changed files: $ChangeCount"
Write-Host "  Configuration: $Configuration"

$previousRun = $env:FILESEARCH_RUN_USN_BENCH
$previousRoot = $env:FILESEARCH_USN_BENCH_ROOT
$previousFileCount = $env:FILESEARCH_USN_BENCH_FILE_COUNT
$previousChangeCount = $env:FILESEARCH_USN_BENCH_CHANGE_COUNT
$previousOutput = $env:FILESEARCH_USN_BENCH_OUTPUT
try {
    $env:FILESEARCH_RUN_USN_BENCH = "1"
    $env:FILESEARCH_USN_BENCH_ROOT = $fullRoot
    $env:FILESEARCH_USN_BENCH_FILE_COUNT = $FileCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $env:FILESEARCH_USN_BENCH_CHANGE_COUNT = $ChangeCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $env:FILESEARCH_USN_BENCH_OUTPUT = $output

    dotnet test $solution `
        --configuration $Configuration `
        --filter "FullyQualifiedName~UsnStartupCatchUpBenchmarkTests.MeasureUsnCatchUpAgainstRootRefresh"

    if (Test-Path -LiteralPath $output -PathType Leaf) {
        Write-Host ""
        Get-Content -LiteralPath $output
    }
}
finally {
    if ($null -eq $previousRun) { Remove-Item Env:\FILESEARCH_RUN_USN_BENCH -ErrorAction SilentlyContinue } else { $env:FILESEARCH_RUN_USN_BENCH = $previousRun }
    if ($null -eq $previousRoot) { Remove-Item Env:\FILESEARCH_USN_BENCH_ROOT -ErrorAction SilentlyContinue } else { $env:FILESEARCH_USN_BENCH_ROOT = $previousRoot }
    if ($null -eq $previousFileCount) { Remove-Item Env:\FILESEARCH_USN_BENCH_FILE_COUNT -ErrorAction SilentlyContinue } else { $env:FILESEARCH_USN_BENCH_FILE_COUNT = $previousFileCount }
    if ($null -eq $previousChangeCount) { Remove-Item Env:\FILESEARCH_USN_BENCH_CHANGE_COUNT -ErrorAction SilentlyContinue } else { $env:FILESEARCH_USN_BENCH_CHANGE_COUNT = $previousChangeCount }
    if ($null -eq $previousOutput) { Remove-Item Env:\FILESEARCH_USN_BENCH_OUTPUT -ErrorAction SilentlyContinue } else { $env:FILESEARCH_USN_BENCH_OUTPUT = $previousOutput }
    Remove-Item -LiteralPath $output -ErrorAction SilentlyContinue
}
