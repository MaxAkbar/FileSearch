using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

public interface IIndexUsageStore
{
    Task RecordFileOpenedAsync(string path, CancellationToken cancellationToken);
}

internal sealed class NullIndexUsageStore : IIndexUsageStore
{
    public static NullIndexUsageStore Instance { get; } = new();

    private NullIndexUsageStore()
    {
    }

    public Task RecordFileOpenedAsync(string path, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
