using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Tests;

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public void Update(Action<AppSettings> mutate) => mutate(Current);
}

internal sealed class FakePreviewService : IFilePreviewService
{
    public Task<string> LoadHitsPreviewAsync(string path, IReadOnlyList<int> hitLineNumbers, int contextLines, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);

    public Task<string> LoadFullTextAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);
}

internal sealed class FakeFileLauncher : IFileLauncher
{
    public void Open(string path)
    {
    }

    public void RevealInExplorer(string path)
    {
    }

    public void CopyToClipboard(string text)
    {
    }
}

internal sealed class FakeFileTypeOptionsStore : IFileTypeOptionsStore
{
    public FileTypeOptions Load() => new();

    public void Save(FileTypeOptions options)
    {
    }
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialDirectory) => null;
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeIndexingService : IIndexingService
{
    public event EventHandler<IndexingStatus>? StatusChanged;

    public IndexingStatus CurrentStatus { get; set; } = new(false, false, false, 0, "Idle");

    public bool IsPaused { get; private set; }

    public int PauseCallCount { get; private set; }

    public int ResumeCallCount { get; private set; }

    public Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AddOrUpdateLocationAsync(IndexedLocation location, bool queueInitialRefresh, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveLocationAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnqueueRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken) => Task.CompletedTask;

    public void SetForegroundSearchActive(bool isActive)
    {
    }

    public void Pause()
    {
        PauseCallCount++;
        IsPaused = true;
        CurrentStatus = CurrentStatus with { IsPaused = true };
    }

    public void Resume()
    {
        ResumeCallCount++;
        IsPaused = false;
        CurrentStatus = CurrentStatus with { IsPaused = false };
    }

    public void RaiseStatus(IndexingStatus status)
    {
        CurrentStatus = status;
        IsPaused = status.IsPaused;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class FakeFileIndex : IFileIndex
{
    public IndexDatabaseInfo DatabaseInfo { get; set; } = new(
        string.Empty,
        Exists: false,
        IsCompatible: false,
        SchemaVersion: "3",
        DatabaseBytes: 0,
        WalBytes: 0,
        ShmBytes: 0,
        LocationCount: 0,
        TotalFileCount: 0,
        TotalLineCount: 0,
        PendingChangeCount: 0,
        LastIndexedUtc: null);

    public int CompactCallCount { get; private set; }

    public string DatabasePath => DatabaseInfo.DatabasePath;

    public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new IndexCoverage(IndexCoverageStatus.Missing, "fake"));

    public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
        Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

    public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

    public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DatabaseInfo);

    public Task CompactAsync(CancellationToken cancellationToken)
    {
        CompactCallCount++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SavePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

    public Task RemovePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
}
