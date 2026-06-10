using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public interface IIndexingService
{
    event EventHandler<IndexingStatus>? StatusChanged;

    IndexingStatus CurrentStatus { get; }

    bool IsPaused { get; }

    Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task AddOrUpdateLocationAsync(
        IndexedLocation location,
        bool queueInitialRefresh,
        CancellationToken cancellationToken);

    Task RemoveLocationAsync(string root, CancellationToken cancellationToken);

    Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken);

    void SetForegroundSearchActive(bool isActive);

    void Pause();

    void Resume();
}
