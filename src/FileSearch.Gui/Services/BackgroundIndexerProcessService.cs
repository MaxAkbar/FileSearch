using System.Diagnostics;
using System.IO;
using FileSearch.Core.Indexing;

namespace FileSearch.Gui.Services;

public sealed class BackgroundIndexerProcessService : IBackgroundIndexerProcessService
{
    private static readonly TimeSpan QuickIpcTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MutationIpcTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaintenanceIpcTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownIpcTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<BackgroundIndexerRequest, TimeSpan, CancellationToken, Task<BackgroundIndexerResponse?>> _sendAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public BackgroundIndexerProcessService()
        : this(BackgroundIndexerClient.TrySendAsync, Task.Delay)
    {
    }

    internal BackgroundIndexerProcessService(
        Func<BackgroundIndexerRequest, TimeSpan, CancellationToken, Task<BackgroundIndexerResponse?>> sendAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _sendAsync = sendAsync;
        _delayAsync = delayAsync;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
            return true;

        var executablePath = ResolveDefaultExecutablePath();
        if (!File.Exists(executablePath))
            return false;

        Process.Start(new ProcessStartInfo(executablePath)
        {
            Arguments = StartupRegistration.BackgroundArgument,
            UseShellExecute = true,
        });

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await _delayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public async Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Shutdown, ShutdownIpcTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (response?.Success != true)
            return false;

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await _delayAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public async Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.GetStatus, cancellationToken).ConfigureAwait(false);
        return response?.Success == true ? response.Status : null;
    }

    public async Task<bool> PauseAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Pause, cancellationToken).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> ResumeAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Resume, cancellationToken).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> SetResourceProfileAsync(
        IndexerResourceProfile profile,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.SetResourceProfile,
                    ResourceProfile: profile),
                MutationIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> SetRuntimeOptionsAsync(
        IndexerRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.SetRuntimeOptions,
                    RuntimeOptions: options.Normalize()),
                MutationIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.SetForegroundSearchActive,
                    ForegroundSearchActive: isActive),
                MutationIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> AddOrUpdateLocationAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        var response = await SendLocationAsync(
                BackgroundIndexerCommand.AddOrUpdateLocation,
                location,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> RemoveLocationAsync(string root, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.RemoveLocation,
                    Root: IndexPath.NormalizeRoot(root)),
                MutationIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> RefreshRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        var response = await SendLocationAsync(
                BackgroundIndexerCommand.RefreshRoot,
                location,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> QueueRootRefreshAsync(
        IndexedLocation location,
        IndexQueuePriority priority,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new BackgroundIndexerRequest(
                    BackgroundIndexerCommand.QueueRootRefresh,
                    Location: BackgroundIndexedLocation.FromIndexedLocation(location),
                    Priority: priority),
                MutationIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<IndexValidationResult?> ValidateRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        var response = await SendLocationAsync(
                BackgroundIndexerCommand.ValidateRoot,
                location,
                MaintenanceIpcTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true ? response.ValidationResult : null;
    }

    public async Task<bool> CompactDatabaseAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.CompactDatabase, MaintenanceIpcTimeout, cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true;
    }

    internal static string ResolveDefaultExecutablePath(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;

        var sameDirectory = Path.Combine(baseDirectory, "FileSearch.Indexer.exe");
        if (File.Exists(sameDirectory))
            return sameDirectory;

        return Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..", "..", "..", "..",
            "FileSearch.Indexer",
            "bin",
            "Debug",
            "net10.0-windows",
            "FileSearch.Indexer.exe"));
    }

    private async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Ping, cancellationToken).ConfigureAwait(false);
        return response?.Success == true;
    }

    private Task<BackgroundIndexerResponse?> SendLocationAsync(
        BackgroundIndexerCommand command,
        IndexedLocation location,
        CancellationToken cancellationToken) =>
        SendLocationAsync(command, location, MutationIpcTimeout, cancellationToken);

    private Task<BackgroundIndexerResponse?> SendLocationAsync(
        BackgroundIndexerCommand command,
        IndexedLocation location,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(
            new BackgroundIndexerRequest(
                command,
                Location: BackgroundIndexedLocation.FromIndexedLocation(location)),
            timeout,
            cancellationToken);

    private Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerCommand command,
        CancellationToken cancellationToken) =>
        SendAsync(new BackgroundIndexerRequest(command), QuickIpcTimeout, cancellationToken);

    private Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(new BackgroundIndexerRequest(command), timeout, cancellationToken);

    private Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerRequest request,
        CancellationToken cancellationToken) =>
        SendAsync(request, QuickIpcTimeout, cancellationToken);

    private Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _sendAsync(request, timeout, cancellationToken);
}
