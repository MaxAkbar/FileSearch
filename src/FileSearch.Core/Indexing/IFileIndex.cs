using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

/// <summary>Read side: indexed search and coverage checks.</summary>
public interface IIndexSearch
{
    IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Read side: structured indexed content units for snippets, citations and grounded answers.</summary>
public interface IContentUnitReader
{
    Task<ContentUnit?> GetContentUnitAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult<ContentUnit?>(null);

    Task<IReadOnlyList<ContentUnit>> GetContentUnitsForFileAsync(long fileId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContentUnit>>(System.Array.Empty<ContentUnit>());

    Task<IReadOnlyList<ContentUnit>> GetNeighboringUnitsAsync(
        long contentUnitId,
        int before,
        int after,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContentUnit>>(System.Array.Empty<ContentUnit>());

    Task<string?> GetFilePathAsync(long fileId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<long?> GetFileIdAsync(string root, string path, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(null);

    Task<IReadOnlyList<long>> GetFileIdsForRootAsync(string root, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<long>>(System.Array.Empty<long>());

    Task<IReadOnlyList<long>> GetContentUnitIdsForRootAsync(string root, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<long>>(System.Array.Empty<long>());
}

/// <summary>Write side: building, refreshing, and removing indexed content.</summary>
public interface IIndexWriter
{
    Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken);

    Task RefreshRootAsync(
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken);

    Task UpsertFileAsync(
        string root,
        string path,
        WalkerOptions options,
        CancellationToken cancellationToken);

    Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken);

    Task ClearAsync(string root, CancellationToken cancellationToken);
}

/// <summary>Introspection: stats and location listings for UIs.</summary>
public interface IIndexMaintenance
{
    string DatabasePath { get; }

    Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken);

    Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken);

    Task<IndexValidationResult> ValidateRootAsync(IndexRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(IndexValidationResult.Failed(
            request.Root,
            System.DateTime.UtcNow,
            "Index validation is not supported by this index implementation."));

    Task<IReadOnlyList<IndexValidationDriftInfo>> GetValidationDriftAsync(
        string root,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexValidationDriftInfo>>(System.Array.Empty<IndexValidationDriftInfo>());

    Task<IReadOnlyList<IndexFailureInfo>> GetFailedFilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexFailureInfo>>(System.Array.Empty<IndexFailureInfo>());

    Task ExportFailedFilesAsync(
        string path,
        IndexFailureExportFormat format,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task CompactAsync(CancellationToken cancellationToken);
}

/// <summary>Durable queue of file and root-refresh changes awaiting indexing (crash recovery).</summary>
public interface IPendingChangeStore
{
    Task SavePendingChangeAsync(
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken);

    Task RemovePendingChangeAsync(
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken);
}

/// <summary>
/// Composite of all index roles. Implementations provide everything; most
/// consumers should depend on the narrowest role interface they need.
/// </summary>
public interface IFileIndex : IIndexSearch, IContentUnitReader, IIndexWriter, IIndexMaintenance, IPendingChangeStore
{
}
