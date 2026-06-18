using FileSearch.Core.Indexing;

namespace FileSearch.Gui.Services;

public interface IBackgroundIndexerProcessService
{
    Task<bool> EnsureRunningAsync(CancellationToken cancellationToken);

    Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken);

    Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken);

    Task<bool> PauseAsync(CancellationToken cancellationToken);

    Task<bool> ResumeAsync(CancellationToken cancellationToken);

    Task<bool> SetResourceProfileAsync(IndexerResourceProfile profile, CancellationToken cancellationToken);

    Task<bool> SetRuntimeOptionsAsync(IndexerRuntimeOptions options, CancellationToken cancellationToken);

    Task<bool> SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken);

    Task<bool> AddOrUpdateLocationAsync(
        IndexedLocation location,
        CancellationToken cancellationToken);

    Task<bool> RemoveLocationAsync(string root, CancellationToken cancellationToken);

    Task<bool> RefreshRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken);

    Task<bool> QueueRootRefreshAsync(
        IndexedLocation location,
        IndexQueuePriority priority,
        CancellationToken cancellationToken);

    Task<IndexValidationResult?> ValidateRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken);

    Task<bool> CompactDatabaseAsync(CancellationToken cancellationToken);
}
