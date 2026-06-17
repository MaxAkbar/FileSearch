using FileSearch.Core.Indexing;

namespace FileSearch.Indexer;

internal sealed class WorkerSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public WorkerSingleInstance()
    {
        _mutex = new Mutex(
            initiallyOwned: true,
            name: $@"Local\{BackgroundIndexerEndpoint.PipeName}",
            createdNew: out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex.Dispose();
    }
}
