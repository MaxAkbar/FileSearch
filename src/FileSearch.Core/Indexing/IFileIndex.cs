using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

public interface IFileIndex
{
    string DatabasePath { get; }

    Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken);

    Task RefreshRootAsync(
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken);

    Task UpsertFileAsync(
        string root,
        string path,
        FileSearch.Core.Walker.WalkerOptions options,
        CancellationToken cancellationToken);

    Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken);

    IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken);

    Task ClearAsync(string root, CancellationToken cancellationToken);

    Task SavePendingChangeAsync(
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken);

    Task RemovePendingChangeAsync(
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken);
}
