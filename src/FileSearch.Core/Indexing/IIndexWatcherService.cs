namespace FileSearch.Core.Indexing;

public interface IIndexWatcherService
{
    void StartWatching(IndexedLocation location);

    void StopWatching(string root);

    void StopAll();

    IReadOnlyDictionary<string, IndexWatcherDiagnosticInfo> GetDiagnostics();
}
