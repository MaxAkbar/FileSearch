using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Mcp;

/// <summary>
/// Read-only rail: registered ahead of AddFileSearchCore so the searcher
/// stack never enqueues background index refreshes (which persist pending
/// change rows) and never resolves the in-process indexing service. Index
/// writes stay owned by the GUI and the tray indexer.
/// </summary>
internal sealed class NoOpIndexingSearchCoordinator : IIndexingSearchCoordinator
{
    public Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IndexingStatus?>(null);

    public Task SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
