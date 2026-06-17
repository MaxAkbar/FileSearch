using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public interface IIndexingSearchCoordinator
{
    Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken);

    Task SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken);

    Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken);
}

internal sealed class IndexingServiceSearchCoordinator : IIndexingSearchCoordinator
{
    private readonly IIndexingService _indexingService;

    public IndexingServiceSearchCoordinator(IIndexingService indexingService) =>
        _indexingService = indexingService;

    public Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IndexingStatus?>(_indexingService.CurrentStatus);

    public Task SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken)
    {
        _indexingService.SetForegroundSearchActive(isActive);
        return Task.CompletedTask;
    }

    public Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken) =>
        _indexingService.EnqueueRootRefreshAsync(root, options, priority, cancellationToken);
}
