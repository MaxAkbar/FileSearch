[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [int]$StartupTimeoutSeconds = 20,
    [int]$ShutdownTimeoutSeconds = 10,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"

function Write-SmokeLog([string]$Message) {
    Write-Host $Message
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format O) $Message"
    }
}

function Assert-FileExists([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Get-BackgroundIndexerPipeName {
    $identity = "$([Environment]::UserDomainName)\$([Environment]::UserName)"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))
    }
    finally {
        $sha.Dispose()
    }

    $builder = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt 8; $i++) {
        [void]$builder.Append($hash[$i].ToString("X2", [System.Globalization.CultureInfo]::InvariantCulture))
    }

    return "FileSearch.Indexer.$builder"
}

function Invoke-IndexerCommand(
    [string]$PipeName,
    [string]$Command,
    [int]$TimeoutMilliseconds = 5000,
    [switch]$AllowUnavailable
) {
    Write-SmokeLog "Sending $Command..."
    $options = [System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        $options)

    try {
        $timeoutCts = [System.Threading.CancellationTokenSource]::new()
        $timeoutCts.CancelAfter($TimeoutMilliseconds)
        Write-SmokeLog "Connecting $Command..."
        $pipe.ConnectAsync($timeoutCts.Token).GetAwaiter().GetResult()
        Write-SmokeLog "Connected $Command."

        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        $writer = [System.IO.StreamWriter]::new($pipe, $utf8NoBom, 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe, $utf8NoBom, $false, 1024, $true)
        try {
            $writer.AutoFlush = $true
            $payload = @{ Command = $Command } | ConvertTo-Json -Compress
            $writer.WriteLineAsync($payload).GetAwaiter().GetResult()
            Write-SmokeLog "Wrote $Command."

            $responsePayload = $reader.ReadLineAsync($timeoutCts.Token).GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($responsePayload)) {
                throw "$Command returned an empty response."
            }

            Write-SmokeLog "$Command responded."
            return $responsePayload | ConvertFrom-Json
        }
        finally {
            try { $reader.Dispose() } catch {}
            try { $writer.Dispose() } catch {}
        }
    }
    catch [System.OperationCanceledException] {
        if ($AllowUnavailable) { return $null }
        throw "Timed out waiting for $Command response from $PipeName."
    }
    catch [System.TimeoutException] {
        if ($AllowUnavailable) { return $null }
        throw
    }
    catch [System.IO.IOException] {
        if ($AllowUnavailable) { return $null }
        throw
    }
    catch [System.UnauthorizedAccessException] {
        if ($AllowUnavailable) { return $null }
        throw
    }
    finally {
        if ($null -ne $timeoutCts) {
            $timeoutCts.Dispose()
        }

        try { $pipe.Dispose() } catch {}
    }
}

function Assert-Success($Response, [string]$Command) {
    if ($null -eq $Response) {
        throw "$Command returned no response."
    }

    if ($Response.Success -ne $true) {
        throw "$Command failed: $($Response.Message)"
    }
}

$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
    throw "Publish directory does not exist: $publishPath"
}

$guiExe = Join-Path $publishPath "FileSearch.Gui.exe"
$indexerExe = Join-Path $publishPath "FileSearch.Indexer.exe"
$extractorHostExe = Join-Path $publishPath "FileSearch.ExtractorHost.exe"
$helpIndex = Join-Path $publishPath "Help\index.html"

Assert-FileExists $guiExe "Published GUI executable"
Assert-FileExists $indexerExe "Published background indexer executable"
Assert-FileExists $extractorHostExe "Published extractor host executable"
Assert-FileExists $helpIndex "Published help bundle"

$pipeName = Get-BackgroundIndexerPipeName
$existing = Invoke-IndexerCommand $pipeName "Ping" 500 -AllowUnavailable
if ($null -ne $existing) {
    throw "A FileSearch background indexer is already running for this user. Stop it before running the sidecar smoke test."
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("filesearch-sidecar-smoke-" + [guid]::NewGuid().ToString("N"))
$appData = Join-Path $scratch "Roaming"
$localAppData = Join-Path $scratch "Local"
$settingsPath = Join-Path $scratch "settings.json"
$databasePath = Join-Path $scratch "index\filesearch.db"
$workerTracePath = Join-Path $scratch "indexer-ipc-trace.log"
New-Item -ItemType Directory -Force -Path $appData, $localAppData | Out-Null
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($settingsPath)) | Out-Null
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($databasePath)) | Out-Null
@{
    IndexedLocations = @()
    LastIndexedRoot = ""
} | ConvertTo-Json -Compress | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$process = $null
try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($indexerExe)
    $startInfo.WorkingDirectory = $publishPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.EnvironmentVariables["APPDATA"] = $appData
    $startInfo.EnvironmentVariables["LOCALAPPDATA"] = $localAppData
    $startInfo.EnvironmentVariables["FILESEARCH_WORKER_SETTINGS_PATH"] = $settingsPath
    $startInfo.EnvironmentVariables["FILESEARCH_INDEX_DATABASE_PATH"] = $databasePath
    $startInfo.EnvironmentVariables["FILESEARCH_INDEXER_IPC_TRACE_PATH"] = $workerTracePath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start $indexerExe."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ping = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "FileSearch.Indexer.exe exited before accepting IPC. Exit code: $($process.ExitCode)"
        }

        $ping = Invoke-IndexerCommand $pipeName "Ping" 1000 -AllowUnavailable
        if ($null -ne $ping) {
            break
        }

        Start-Sleep -Milliseconds 250
    }

    Assert-Success $ping "Ping"
    Assert-Success (Invoke-IndexerCommand $pipeName "GetStatus") "GetStatus"
    Assert-Success (Invoke-IndexerCommand $pipeName "Pause") "Pause"
    Assert-Success (Invoke-IndexerCommand $pipeName "Resume") "Resume"
    Assert-Success (Invoke-IndexerCommand $pipeName "Shutdown") "Shutdown"

    if (-not $process.WaitForExit($ShutdownTimeoutSeconds * 1000)) {
        throw "FileSearch.Indexer.exe did not exit within $ShutdownTimeoutSeconds seconds after shutdown."
    }

    Write-SmokeLog "Published sidecar smoke test passed."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            $null = Invoke-IndexerCommand $pipeName "Shutdown" 1000 -AllowUnavailable
            if (-not $process.WaitForExit(3000)) {
                $process.Kill($true)
            }
        }
        catch {
            try { $process.Kill($true) } catch {}
        }
    }

    if ($null -ne $process) {
        $process.Dispose()
    }

    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
