using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Extractors;

public sealed class OutOfProcessExtractionService : IOutOfProcessExtractionService
{
    private const int MaxErrorMessageLength = 2_000;

    private readonly OutOfProcessExtractionOptions _options;
    private readonly IExtractorHostProcessRunner _runner;
    private readonly ILogger _logger;

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
