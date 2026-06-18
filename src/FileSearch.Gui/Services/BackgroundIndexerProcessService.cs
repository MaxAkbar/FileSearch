using System.Diagnostics;
using System.IO;
using FileSearch.Core.Indexing;

namespace FileSearch.Gui.Services;

public sealed class BackgroundIndexerProcessService : IBackgroundIndexerProcessService
{
    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(1);

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
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public async Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Shutdown, cancellationToken).ConfigureAwait(false);
        if (response?.Success != true)
            return false;

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
                return true;
        }

        return true;
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
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Success == true ? response.ValidationResult : null;
    }

    public async Task<bool> CompactDatabaseAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.CompactDatabase, cancellationToken)
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

    private static async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(BackgroundIndexerCommand.Ping, cancellationToken).ConfigureAwait(false);
        return response?.Success == true;
    }

    private static Task<BackgroundIndexerResponse?> SendLocationAsync(
        BackgroundIndexerCommand command,
        IndexedLocation location,
        CancellationToken cancellationToken) =>
        SendAsync(
            new BackgroundIndexerRequest(
                command,
                Location: BackgroundIndexedLocation.FromIndexedLocation(location)),
            cancellationToken);

    private static Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerCommand command,
        CancellationToken cancellationToken) =>
        SendAsync(new BackgroundIndexerRequest(command), cancellationToken);

    private static Task<BackgroundIndexerResponse?> SendAsync(
        BackgroundIndexerRequest request,
        CancellationToken cancellationToken) =>
        BackgroundIndexerClient.TrySendAsync(request, IpcTimeout, cancellationToken);
}
