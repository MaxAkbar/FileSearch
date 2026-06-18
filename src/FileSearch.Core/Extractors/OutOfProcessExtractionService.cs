using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Extractors;

public sealed class OutOfProcessExtractionService : IOutOfProcessExtractionService, IDisposable
{
    private const int MaxErrorMessageLength = 2_000;

    private readonly OutOfProcessExtractionOptions _options;
    private readonly IExtractorHostProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly object _poolLock = new();
    private ReusableExtractorHostPool? _pool;

    public OutOfProcessExtractionService(
        OutOfProcessExtractionOptions? options = null,
        ILogger<OutOfProcessExtractionService>? logger = null)
        : this(options, new DefaultExtractorHostProcessRunner(), logger)
    {
    }

    internal OutOfProcessExtractionService(
        OutOfProcessExtractionOptions? options,
        IExtractorHostProcessRunner runner,
        ILogger<OutOfProcessExtractionService>? logger = null)
    {
        _options = options ?? new OutOfProcessExtractionOptions();
        _runner = runner;
        _logger = logger ?? NullLogger<OutOfProcessExtractionService>.Instance;
    }

    public bool ShouldUse(ITextExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        return _options.Enabled &&
            _options.ExtractorIds.Contains(extractor.ExtractorId) &&
            ResolveCommand() is not null;
    }

    public async Task<OutOfProcessExtractionResult> ExtractAsync(
        string path,
        ITextExtractor extractor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(extractor);

        var command = ResolveCommand();
        if (command is null)
            throw new ExtractorHostException("extractor_host_unavailable", "Extractor host executable was not found.");

        var request = new ExtractorHostRequest(
            ExtractorHostProtocol.CurrentVersion,
            path,
            extractor.ExtractorId);
        if (_options.UseReusableHostPool)
            return await GetPool(command).ExtractAsync(request, cancellationToken).ConfigureAwait(false);

        return await ExtractOneShotAsync(command, request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_poolLock)
        {
            _pool?.Dispose();
            _pool = null;
        }
    }

    private ReusableExtractorHostPool GetPool(ExtractorHostCommand command)
    {
        lock (_poolLock)
        {
            _pool ??= new ReusableExtractorHostPool(command, _options, _logger);
            return _pool;
        }
    }

    private async Task<OutOfProcessExtractionResult> ExtractOneShotAsync(
        ExtractorHostCommand command,
        ExtractorHostRequest request,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(request, ExtractorHostProtocol.JsonOptions);
        var run = await _runner.RunAsync(command, requestJson, _options.Timeout, cancellationToken).ConfigureAwait(false);
        if (run.TimedOut)
        {
            throw new ExtractorHostException(
                "extractor_host_timeout",
                $"Extractor host timed out after {_options.Timeout.TotalSeconds:n0} seconds.");
        }

        var response = TryReadResponse(run);
        if (response is null)
        {
            var code = run.ExitCode == 0 ? "extractor_host_protocol_error" : "extractor_host_crashed";
            var detail = string.IsNullOrWhiteSpace(run.StandardError)
                ? $"Extractor host exited with code {run.ExitCode}."
                : run.StandardError;
            throw new ExtractorHostException(code, TrimMessage(detail));
        }

        if (response.ProtocolVersion != ExtractorHostProtocol.CurrentVersion)
        {
            throw new ExtractorHostException(
                "extractor_host_protocol_error",
                $"Extractor host returned unsupported protocol version {response.ProtocolVersion}.");
        }

        if (!response.Success)
        {
            throw new ExtractorHostException(
                string.IsNullOrWhiteSpace(response.ErrorCode) ? "extractor_host_failed" : response.ErrorCode,
                TrimMessage(response.ErrorMessage ?? "Extractor host failed."));
        }

        if (run.ExitCode != 0)
        {
            throw new ExtractorHostException(
                "extractor_host_crashed",
                $"Extractor host returned a successful response but exited with code {run.ExitCode}.");
        }

        return new OutOfProcessExtractionResult(response.Lines, response.Issues);
    }

    private ExtractorHostCommand? ResolveCommand()
    {
        foreach (var path in EnumerateHostPathCandidates())
        {
            if (!File.Exists(path))
                continue;

            return Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? new ExtractorHostCommand("dotnet", new[] { path }, path)
                : new ExtractorHostCommand(path, Array.Empty<string>(), path);
        }

        return null;
    }

    private IEnumerable<string> EnumerateHostPathCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.HostPath))
        {
            yield return _options.HostPath;
            yield break;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var executableName = OperatingSystem.IsWindows()
            ? "FileSearch.ExtractorHost.exe"
            : "FileSearch.ExtractorHost";
        yield return Path.Combine(baseDirectory, executableName);
        yield return Path.Combine(baseDirectory, "FileSearch.ExtractorHost.dll");

        var repositoryRoot = FindAncestorContaining(baseDirectory, "FileSearch.slnx");
        if (repositoryRoot is null)
            yield break;

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var hostOutput = Path.Combine(
                repositoryRoot,
                "src",
                "FileSearch.ExtractorHost",
                "bin",
                configuration,
                "net10.0");
            yield return Path.Combine(hostOutput, executableName);
            yield return Path.Combine(hostOutput, "FileSearch.ExtractorHost.dll");
        }
    }

    private static string? FindAncestorContaining(string startDirectory, string fileName)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, fileName)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private ExtractorHostResponse? TryReadResponse(ExtractorHostRunResult run)
    {
        if (string.IsNullOrWhiteSpace(run.StandardOutput))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ExtractorHostResponse>(
                run.StandardOutput,
                ExtractorHostProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Extractor host returned invalid JSON.");
            return null;
        }
    }

    private static string TrimMessage(string message)
    {
        if (message.Length <= MaxErrorMessageLength)
            return message;

        return message[..MaxErrorMessageLength];
    }
}

internal sealed class ReusableExtractorHostPool : IDisposable
{
    private readonly ReusableExtractorHostSession[] _sessions;
    private readonly TimeSpan _timeout;
    private int _nextSession;

    public ReusableExtractorHostPool(
        ExtractorHostCommand command,
        OutOfProcessExtractionOptions options,
        ILogger logger)
    {
        var poolSize = Math.Clamp(options.HostPoolSize, 1, 16);
        _timeout = options.Timeout;
        _sessions = Enumerable
            .Range(0, poolSize)
            .Select(_ => new ReusableExtractorHostSession(command, logger))
            .ToArray();
    }

    public Task<OutOfProcessExtractionResult> ExtractAsync(
        ExtractorHostRequest request,
        CancellationToken cancellationToken)
    {
        var session = _sessions[(int)((uint)Interlocked.Increment(ref _nextSession) % (uint)_sessions.Length)];
        return session.ExtractAsync(request, _timeout, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
    }
}

internal sealed class ReusableExtractorHostSession : IDisposable
{
    private readonly ExtractorHostCommand _command;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<string>? _stderrTask;
    private bool _disposed;

    public ReusableExtractorHostSession(ExtractorHostCommand command, ILogger logger)
    {
        _command = command;
        _logger = logger;
    }

    public async Task<OutOfProcessExtractionResult> ExtractAsync(
        ExtractorHostRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var process = EnsureStarted();
            var requestJson = JsonSerializer.Serialize(request, ExtractorHostProtocol.JsonOptions);
            try
            {
                await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess();
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                KillProcess();
                throw new ExtractorHostException("extractor_host_crashed", TrimMessage(ex.Message));
            }

            var responseLine = await ReadResponseLineAsync(process, timeout, cancellationToken).ConfigureAwait(false);
            ExtractorHostResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<ExtractorHostResponse>(
                    responseLine,
                    ExtractorHostProtocol.JsonOptions);
            }
            catch (JsonException ex)
            {
                KillProcess();
                _logger.LogDebug(ex, "Reusable extractor host returned invalid JSON.");
                throw new ExtractorHostException("extractor_host_protocol_error", TrimMessage(ex.Message));
            }

            if (response is null)
            {
                KillProcess();
                throw new ExtractorHostException("extractor_host_protocol_error", "Extractor host returned an empty response.");
            }

            if (response.ProtocolVersion != ExtractorHostProtocol.CurrentVersion)
            {
                KillProcess();
                throw new ExtractorHostException(
                    "extractor_host_protocol_error",
                    $"Extractor host returned unsupported protocol version {response.ProtocolVersion}.");
            }

            if (!response.Success)
            {
                throw new ExtractorHostException(
                    string.IsNullOrWhiteSpace(response.ErrorCode) ? "extractor_host_failed" : response.ErrorCode,
                    TrimMessage(response.ErrorMessage ?? "Extractor host failed."));
            }

            return new OutOfProcessExtractionResult(response.Lines, response.Issues);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        KillProcess();
        _gate.Dispose();
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false })
            return _process;

        DisposeProcess();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _command.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in _command.Arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add("--serve");

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new ExtractorHostException(
                    "extractor_host_start_failed",
                    $"Extractor host could not be started from {_command.DisplayPath}.");
            }
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new ExtractorHostException("extractor_host_start_failed", TrimMessage(ex.Message));
        }

        _process = process;
        _stderrTask = process.StandardError.ReadToEndAsync();
        return process;
    }

    private async Task<string> ReadResponseLineAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<string?> readTask;
        try
        {
            readTask = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            KillProcess();
            throw new ExtractorHostException("extractor_host_crashed", TrimMessage(ex.Message));
        }

        var completed = await Task.WhenAny(readTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != readTask)
        {
            KillProcess();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ExtractorHostException(
                "extractor_host_timeout",
                $"Extractor host timed out after {timeout.TotalSeconds:n0} seconds.");
        }

        string? responseLine;
        try
        {
            responseLine = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            KillProcess();
            throw new ExtractorHostException("extractor_host_crashed", TrimMessage(ex.Message));
        }

        if (responseLine is not null)
            return responseLine;

        var stderr = await ReadErrorIfCompletedAsync().ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(stderr)
            ? "Extractor host exited before returning a response."
            : stderr;
        KillProcess();
        throw new ExtractorHostException("extractor_host_crashed", TrimMessage(message));
    }

    private async Task<string> ReadErrorIfCompletedAsync()
    {
        if (_stderrTask is null || !_stderrTask.IsCompleted)
            return string.Empty;

        try
        {
            return await _stderrTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        finally
        {
            DisposeProcess();
        }
    }

    private void DisposeProcess()
    {
        if (_process is null)
            return;

        try
        {
            _process.Dispose();
        }
        finally
        {
            _process = null;
            _stderrTask = null;
        }
    }

    private static string TrimMessage(string message)
    {
        if (message.Length <= 2_000)
            return message;

        return message[..2_000];
    }
}

internal sealed record ExtractorHostCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string DisplayPath);

internal sealed record ExtractorHostRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal interface IExtractorHostProcessRunner
{
    Task<ExtractorHostRunResult> RunAsync(
        ExtractorHostCommand command,
        string requestJson,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class DefaultExtractorHostProcessRunner : IExtractorHostProcessRunner
{
    public async Task<ExtractorHostRunResult> RunAsync(
        ExtractorHostCommand command,
        string requestJson,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
            {
                throw new ExtractorHostException(
                    "extractor_host_start_failed",
                    $"Extractor host could not be started from {command.DisplayPath}.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new ExtractorHostException("extractor_host_start_failed", ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var waitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        if (completed != waitTask)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();

            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            return new ExtractorHostRunResult(
                ExitCode: process.HasExited ? process.ExitCode : -1,
                StandardOutput: await ReadQuietlyAsync(stdoutTask).ConfigureAwait(false),
                StandardError: await ReadQuietlyAsync(stderrTask).ConfigureAwait(false),
                TimedOut: true);
        }

        return new ExtractorHostRunResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            TimedOut: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadQuietlyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
