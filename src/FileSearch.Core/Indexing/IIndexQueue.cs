using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

public interface IIndexQueue
{
    int Count { get; }

    Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken);

    Task<IndexQueueItem> DequeueAsync(CancellationToken cancellationToken);

    Task LoadPendingAsync(
        System.Collections.Generic.IReadOnlyDictionary<string, IndexedLocation> locations,
        CancellationToken cancellationToken);
}
