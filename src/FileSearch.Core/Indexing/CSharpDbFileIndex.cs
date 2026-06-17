using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Indexing;

/// <summary>
/// The CSharpDB-backed file index. Orchestrates indexing and search policy
/// (what to extract, when to skip, how to match) while delegating connection
/// lifecycle and locking to <see cref="IndexDatabase"/> and all SQL to
/// <see cref="IndexTables"/>.
/// </summary>
public sealed class CSharpDbFileIndex : IFileIndex, IIndexReplayWriter, IIndexUsageStore, IDisposable
{
    private const int LineInsertBatchSize = 250;
    private const int IdQueryBatchSize = 500;
    private const int MetadataHitLimit = 200;

    private readonly IndexDatabase _database;
    private readonly IFileWalker _walker;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions _searchOptions;
    private readonly IIndexVolumeResolver? _volumeResolver;
    private readonly IUsnJournalReader? _journalReader;
    private readonly ILogger _logger;

    public CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions = null,
        ILogger<CSharpDbFileIndex>? logger = null)
        : this(options, walker, extractors, searchOptions, logger, null, null)
    {
    }

    internal CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions,
        ILogger<CSharpDbFileIndex>? logger,
        IIndexVolumeResolver? volumeResolver,
        IUsnJournalReader? journalReader)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _searchOptions = searchOptions ?? new SearchOptions();
        _volumeResolver = volumeResolver;
        _journalReader = journalReader;
        _logger = logger ?? NullLogger<CSharpDbFileIndex>.Instance;
        _database = new IndexDatabase((options ?? new FileIndexOptions()).DatabasePath, _logger);
    }

    public string DatabasePath => _database.DatabasePath;

    public void Dispose() => _database.Dispose();

    public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) =>
        RefreshRootAsync(request, IndexRefreshMode.Full, cancellationToken);

    public async Task RefreshRootAsync(
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Root)) throw new ArgumentException("Root is required.", nameof(request));
        if (!Directory.Exists(request.Root)) throw new DirectoryNotFoundException(request.Root);

        await _database.RunExclusiveWriteAsync(
            db => RefreshRootCoreAsync(db, request, mode, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertFileAsync(
        string root,
        string path,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return;

        await _database.RunExclusiveWriteAsync(async db =>
        {
            var indexingOptions = IndexWalkerOptions.ForIndexing(options);
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var normalizedPath = IndexPath.NormalizeFile(path);

            if (!IsUnderRoot(normalizedRoot, normalizedPath))
                return;

            var profile = IndexProfile.FromWalkerOptions(indexingOptions).ToStorageString();
            var rootId = await IndexTables.EnsureRootAsync(db, normalizedRoot, profile, cancellationToken).ConfigureAwait(false);
            var volumeContext = await TryPrepareVolumeAsync(db, rootId, normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (volumeContext?.RootIdentityChanged == true)
                await ClearRootContentAsync(db, rootId, cancellationToken).ConfigureAwait(false);

            await UpsertFileCoreAsync(
                db,
                rootId,
                normalizedRoot,
                normalizedPath,
                indexingOptions,
                volumeContext,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return;

        await _database.RunExclusiveWriteAsync(async db =>
        {
            var rootId = await IndexTables.GetRootIdAsync(db, IndexPath.NormalizeRoot(root), cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                return;

            await IndexTables.DeleteFileAsync(db, rootId.Value, IndexPath.NormalizeFile(path), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Coverage gating routes multi-root requests to the live searcher;
        // reaching here with anything but one root is a caller bug — fail
        // loudly instead of silently searching only the first root.
        if (request.Roots.Count != 1)
            throw new ArgumentException("Indexed search requires exactly one root.", nameof(request));

        Database? db = null;
        try
        {
            db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
            if (db is null)
                yield break;

            var root = IndexPath.NormalizeRoot(request.Roots[0]);
            var rootId = await IndexTables.GetRootIdAsync(db, root, cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                yield break;

            var highlightBuffer = new List<MatchSpan>(4);
            var hitsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileFilterVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ftsQueries = QueryFtsTerms.BuildCandidateQueries(request.Expression);
            HashSet<string>? metadataHitPaths = null;

            if (MetadataSearchSpec.TryCreate(request, out var metadataSpec))
            {
                var metadataHits = await SearchMetadataAsync(
                        db,
                        rootId.Value,
                        root,
                        request.WalkerOptions,
                        metadataSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (metadataHits.Count > 0)
                {
                    request.Status?.Invoke("Using metadata index");
                    metadataHitPaths = metadataHits
                        .Select(hit => hit.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var hit in metadataHits)
                        yield return hit;
                }
            }

            if (ftsQueries.Count > 0)
            {
                // Stream: resolve each batch of row ids to hits as soon as
                // it fills instead of materializing every id from every FTS
                // query first — first results reach the caller sooner.
                var seenIds = new HashSet<long>();
                var batchIds = new List<long>(IdQueryBatchSize);

                foreach (var ftsQuery in ftsQueries)
                {
                    var ftsHits = await db.SearchAsync(IndexDatabase.FullTextIndexName, ftsQuery, cancellationToken).ConfigureAwait(false);
                    foreach (var ftsHit in ftsHits)
                    {
                        if (!seenIds.Add(ftsHit.RowId))
                            continue;

                        batchIds.Add(ftsHit.RowId);
                        if (batchIds.Count < IdQueryBatchSize)
                            continue;

                        await foreach (var line in ReadLineBatchAsync(db, rootId.Value, batchIds, cancellationToken).ConfigureAwait(false))
                        {
                            if (TryCreateHit(root, line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                            {
                                if (metadataHitPaths?.Contains(hit.Path) == true)
                                    continue;

                                yield return hit;
                            }
                        }

                        batchIds.Clear();
                    }
                }

                if (batchIds.Count > 0)
                {
                    await foreach (var line in ReadLineBatchAsync(db, rootId.Value, batchIds, cancellationToken).ConfigureAwait(false))
                    {
                        if (TryCreateHit(root, line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                        {
                            if (metadataHitPaths?.Contains(hit.Path) == true)
                                continue;

                            yield return hit;
                        }
                    }
                }
            }
            else
            {
                var sql = IndexTables.SelectLinesSql(rootId.Value);
                await foreach (var line in IndexTables.ReadLinesAsync(db, sql, cancellationToken).ConfigureAwait(false))
                {
                    if (TryCreateHit(root, line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                    {
                        if (metadataHitPaths?.Contains(hit.Path) == true)
                            continue;

                        yield return hit;
                    }
                }
            }
        }
        finally
        {
            if (db is not null)
                await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    private static IAsyncEnumerable<IndexedLine> ReadLineBatchAsync(
        Database db,
        long rootId,
        IReadOnlyList<long> lineIds,
        CancellationToken cancellationToken)
    {
        var sql = IndexTables.SelectLinesSql(rootId, lineIds);
        return IndexTables.ReadLinesAsync(db, sql, cancellationToken);
    }

    private static async Task<List<Hit>> SearchMetadataAsync(
        Database db,
        long rootId,
        string root,
        WalkerOptions options,
        MetadataSearchSpec spec,
        CancellationToken cancellationToken)
    {
        var hits = new List<Hit>();
        var candidateTokens = IndexTables.BuildQueryMetadataTokens(spec.Terms);
        var candidateIds = candidateTokens.Count == 0
            ? new List<long>()
            : await IndexTables.ReadMetadataCandidateFileIdsAsync(
                    db,
                    rootId,
                    candidateTokens,
                    spec.RequireAllTerms,
                    cancellationToken)
                .ConfigureAwait(false);
        if (candidateTokens.Count > 0 && candidateIds.Count == 0)
            return hits;

        var files = candidateIds.Count > 0
            ? IndexTables.ReadFileMetadataAsync(db, rootId, candidateIds, cancellationToken)
            : IndexTables.ReadFileMetadataAsync(db, rootId, cancellationToken);

        await foreach (var file in files.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.IncludeHidden &&
                (((FileAttributes)file.Attributes) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                continue;
            }

            if (!IndexedFileFilter.Matches(
                    root,
                    file.Path,
                    file.FileName,
                    file.Extension,
                    file.SizeBytes,
                    file.ModifiedUtcTicks,
                    options))
            {
                continue;
            }

            var score = spec.Score(file, root, out var displayText);
            if (score <= 0)
                continue;

            hits.Add(new Hit(
                file.Path,
                0,
                displayText,
                spec.CollectHighlights(displayText),
                HitKind.Metadata,
                score,
                file.SizeBytes,
                file.ModifiedUtcTicks > 0 ? new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc) : null));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.ModifiedUtc ?? DateTime.MinValue)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MetadataHitLimit)
            .ToList();
    }

    public async Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (!request.UseIndex)
            return new IndexCoverage(IndexCoverageStatus.Disabled, "Index disabled");

        if (request.Roots.Count != 1)
            return new IndexCoverage(IndexCoverageStatus.Unsupported, "Indexed search supports one root at a time");

        try
        {
            var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
            if (db is null)
                return new IndexCoverage(IndexCoverageStatus.Missing, "Index does not cover this folder");

            try
            {
                var root = IndexPath.NormalizeRoot(request.Roots[0]);
                var rootRow = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
                if (rootRow is null)
                    return new IndexCoverage(IndexCoverageStatus.Missing, "Index does not cover this folder");

                if (rootRow.IndexedUtcTicks <= 0)
                    return new IndexCoverage(IndexCoverageStatus.Missing, "Index refresh for this folder is incomplete");

                if (!string.Equals(rootRow.ContentVersion, IndexContentVersion.Current, StringComparison.Ordinal))
                    return new IndexCoverage(IndexCoverageStatus.Incompatible, "Index content version is out of date");

                if (!IsRootIdentityCurrent(root, rootRow))
                    return new IndexCoverage(IndexCoverageStatus.Incompatible, "Indexed folder identity changed");

                if (!IndexProfile.TryParse(rootRow.OptionsHash, out var profile))
                    return new IndexCoverage(IndexCoverageStatus.Incompatible, "Index profile is incompatible");

                return profile.Covers(request.WalkerOptions)
                    ? new IndexCoverage(IndexCoverageStatus.Covered, "Using indexed search")
                    : new IndexCoverage(IndexCoverageStatus.Incompatible, "Index does not cover this search");
            }
            finally
            {
                await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index coverage check failed for {Root}.", request.Roots.Count > 0 ? request.Roots[0] : "(none)");
            return new IndexCoverage(IndexCoverageStatus.Error, $"Index unavailable: {ex.Message}");
        }
    }

    public async Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return new IndexStats(normalizedRoot, 0, 0, null, Exists: false);

        try
        {
            var info = await GetLocationInfoAsync(db, normalizedRoot, cancellationToken).ConfigureAwait(false);
            return info is null
                ? new IndexStats(normalizedRoot, 0, 0, null, Exists: false)
                : new IndexStats(info.Root, info.FileCount, info.LineCount, info.IndexedUtc, info.Exists);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<IndexedLocationInfo>();

        try
        {
            var roots = await IndexTables.ListRootPathsAsync(db, cancellationToken).ConfigureAwait(false);
            var locations = new List<IndexedLocationInfo>(roots.Count);
            foreach (var root in roots)
            {
                var info = await GetLocationInfoAsync(db, root, cancellationToken).ConfigureAwait(false);
                if (info is not null)
                    locations.Add(info);
            }

            return locations;
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    public async Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return CreateDatabaseInfo(isCompatible: false);

        var locationCount = 0;
        long totalFiles = 0;
        long totalLines = 0;
        var pendingChangeCount = 0;
        IReadOnlyList<IndexVolumeHealthInfo> volumeHealth = Array.Empty<IndexVolumeHealthInfo>();
        DateTime? lastIndexedUtc = null;

        try
        {
            var roots = await IndexTables.ListRootPathsAsync(db, cancellationToken).ConfigureAwait(false);
            locationCount = roots.Count;
            foreach (var root in roots)
            {
                var info = await GetLocationInfoAsync(db, root, cancellationToken).ConfigureAwait(false);
                if (info is null)
                    continue;

                totalFiles += info.FileCount;
                totalLines += info.LineCount;
                if (info.IndexedUtc is { } indexedUtc &&
                    (lastIndexedUtc is null || indexedUtc > lastIndexedUtc.Value))
                {
                    lastIndexedUtc = indexedUtc;
                }
            }

            pendingChangeCount = (await IndexTables.ReadPendingChangesAsync(db, cancellationToken).ConfigureAwait(false)).Count;
            volumeHealth = await IndexTables.ListVolumeHealthAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }

        return CreateDatabaseInfo(
            isCompatible: true,
            locationCount,
            totalFiles,
            totalLines,
            pendingChangeCount,
            lastIndexedUtc,
            volumeHealth);
    }

    public Task CompactAsync(CancellationToken cancellationToken) =>
        _database.CompactAsync(cancellationToken);

    public async Task RecordFileOpenedAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        await _database.RunExclusiveWriteAsync(
                db => IndexTables.RecordFileOpenedAsync(db, IndexPath.NormalizeFile(path), cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearAsync(string root, CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(async db =>
        {
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var rootId = await IndexTables.GetRootIdAsync(db, normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                return;

            var fileIds = await IndexTables.ReadFileIdsForRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            foreach (var batch in fileIds.Chunk(IdQueryBatchSize))
                await IndexTables.DeleteLinesForFilesAsync(db, batch, cancellationToken).ConfigureAwait(false);

            await IndexTables.DeleteFilesForRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            await IndexTables.DeleteDirectoriesForRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            await IndexTables.DeleteRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            await IndexTables.DeletePendingChangesForRootAsync(db, normalizedRoot, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePendingChangeAsync(
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(
            db => IndexTables.UpsertPendingChangeAsync(
                db,
                IndexPath.NormalizeRoot(root),
                path is null ? null : IndexPath.NormalizeFile(path),
                kind,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<PendingIndexChange>();

        try
        {
            return await IndexTables.ReadPendingChangesAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    public async Task RemovePendingChangeAsync(
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(
            db => IndexTables.DeletePendingChangeAsync(
                db,
                IndexPath.NormalizeRoot(root),
                path is null ? null : IndexPath.NormalizeFile(path),
                kind,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IndexVolumeCheckpoint?> GetVolumeCheckpointCoreAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return null;

        try
        {
            var row = await IndexTables.GetVolumeRowAsync(db, volume.VolumeKey, cancellationToken).ConfigureAwait(false);
            return row is null
                ? null
                : new IndexVolumeCheckpoint(
                    row.Id,
                    row.VolumeKey,
                    row.JournalId,
                    row.LastCommittedUsn,
                    row.Health,
                    row.LastError);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    internal async Task DeleteFileByIdentityCoreAsync(
        string volumeKey,
        string fileReferenceNumber,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(async db =>
        {
            var volume = await IndexTables.GetVolumeRowAsync(db, volumeKey, cancellationToken).ConfigureAwait(false);
            if (volume is null)
                return;

            await IndexTables.DeleteFilesByIdentityAsync(
                db,
                volume.Id,
                fileReferenceNumber,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IndexReplayReferenceSet> GetReplayReferencesCoreAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return IndexReplayReferenceSet.Empty;

        try
        {
            var row = await IndexTables.GetVolumeRowAsync(db, volume.VolumeKey, cancellationToken).ConfigureAwait(false);
            return row is null
                ? IndexReplayReferenceSet.Empty
                : await IndexTables.ReadReplayReferencesAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    internal async Task<IndexRootIdentity?> GetRootIdentityCoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return null;

        try
        {
            return await IndexTables.GetRootIdentityAsync(
                db,
                IndexPath.NormalizeRoot(root),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    async Task IIndexReplayWriter.ApplyReplayBatchAsync(
        IndexVolumeInfo volume,
        IReadOnlyCollection<IndexedLocation> locations,
        IReadOnlyList<IndexReplayChange> changes,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(async db =>
        {
            var volumeId = await IndexTables.EnsureVolumeAsync(db, volume, cancellationToken).ConfigureAwait(false);

            var locationByRoot = locations.ToDictionary(
                location => IndexPath.NormalizeRoot(location.Root),
                location => location,
                StringComparer.OrdinalIgnoreCase);
            var rootContexts = new Dictionary<string, ReplayRootContext>(StringComparer.OrdinalIgnoreCase);

            async Task<ReplayRootContext?> GetContextAsync(IndexReplayChange change)
            {
                if (change.Root is null || !locationByRoot.TryGetValue(change.Root, out var location))
                    return null;

                if (rootContexts.TryGetValue(change.Root, out var context))
                    return context;

                var indexingOptions = IndexWalkerOptions.ForIndexing(location.WalkerOptions);
                var profile = IndexProfile.FromWalkerOptions(indexingOptions).ToStorageString();
                var rootId = await IndexTables.EnsureRootAsync(db, change.Root, profile, cancellationToken).ConfigureAwait(false);
                var volumeContext = await TryPrepareVolumeAsync(db, rootId, change.Root, cancellationToken).ConfigureAwait(false);
                context = new ReplayRootContext(rootId, indexingOptions, volumeContext);
                rootContexts[change.Root] = context;
                return context;
            }

            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Kind == IndexReplayChangeKind.DeleteByIdentity)
                {
                    await IndexTables.DeleteFilesByIdentityAsync(
                        db,
                        volumeId,
                        change.FileReferenceNumber,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (change is not { Root: not null, Path: not null })
                    continue;

                var context = await GetContextAsync(change).ConfigureAwait(false);
                if (context is null)
                {
                    continue;
                }

                if (change.Kind == IndexReplayChangeKind.EnsureDirectory)
                {
                    await EnsureDirectoryIdentityChainAsync(
                        db,
                        context.RootId,
                        change.Root,
                        change.Path,
                        context.VolumeContext,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await UpsertFileCoreAsync(
                    db,
                    context.RootId,
                    change.Root,
                    change.Path,
                    context.Options,
                    context.VolumeContext,
                    cancellationToken).ConfigureAwait(false);
            }

            await IndexTables.UpdateVolumeCheckpointAsync(
                db,
                volumeId,
                journalId,
                lastCommittedUsn,
                health,
                error,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task UpdateVolumeCheckpointCoreAsync(
        IndexVolumeInfo volume,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(async db =>
        {
            var volumeId = await IndexTables.EnsureVolumeAsync(db, volume, cancellationToken).ConfigureAwait(false);
            await IndexTables.UpdateVolumeCheckpointAsync(
                db,
                volumeId,
                journalId,
                lastCommittedUsn,
                health,
                error,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshRootCoreAsync(
        Database db,
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        var root = IndexPath.NormalizeRoot(request.Root);
        var walkerOptions = IndexWalkerOptions.ForIndexing(request.WalkerOptions);
        var profile = IndexProfile.FromWalkerOptions(walkerOptions).ToStorageString();
        var rootId = await IndexTables.EnsureRootAsync(db, root, profile, cancellationToken).ConfigureAwait(false);
        var volumeContext = await TryPrepareVolumeAsync(db, rootId, root, cancellationToken).ConfigureAwait(false);
        var beforeJournal = await TryQueryJournalAsync(volumeContext?.Volume, cancellationToken).ConfigureAwait(false);
        if (volumeContext?.RootIdentityChanged == true)
            await ClearRootContentAsync(db, rootId, cancellationToken).ConfigureAwait(false);

        await IndexTables.MarkRootRefreshStartedAsync(db, rootId, profile, cancellationToken).ConfigureAwait(false);
        await RefreshDirectoryIdentitiesAsync(db, rootId, root, walkerOptions, volumeContext, cancellationToken).ConfigureAwait(false);
        var existing = await IndexTables.LoadExistingFilesAsync(db, rootId, cancellationToken).ConfigureAwait(false);

        long filesEnumerated = 0;
        long filesIndexed = 0;
        long filesSkipped = 0;
        long filesRemoved = 0;
        long filesFailed = 0;
        long linesIndexed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Publish() => request.Progress?.Invoke(new IndexProgress(
            filesEnumerated,
            filesIndexed,
            filesSkipped,
            filesRemoved,
            filesFailed,
            linesIndexed));

        foreach (var path in _walker.Enumerate(new[] { root }, walkerOptions, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesEnumerated++;

            var normalizedPath = IndexPath.NormalizeFile(path);
            seen.Add(normalizedPath);

            try
            {
                if (!File.Exists(normalizedPath))
                {
                    filesFailed++;
                    Publish();
                    continue;
                }

                var info = new FileInfo(normalizedPath);
                var existingFile = existing.TryGetValue(normalizedPath, out var row) ? row : null;
                if (existingFile is not null && IsUnchanged(existingFile, info))
                {
                    filesSkipped++;
                    Publish();
                    continue;
                }

                var identity = TryGetIndexedFileIdentity(volumeContext?.VolumeId, normalizedPath, lastObservedUsn: null);
                var indexedLines = await IndexSingleFileAsync(db, rootId, normalizedPath, info, identity, cancellationToken).ConfigureAwait(false);
                linesIndexed += indexedLines;
                filesIndexed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Refresh failed for file {Path}.", path);
                filesFailed++;
            }

            Publish();
            if (request.Throttle is { } throttle)
                await throttle.PauseAfterFileAsync(filesEnumerated, cancellationToken).ConfigureAwait(false);
        }

        if (mode == IndexRefreshMode.Full || mode == IndexRefreshMode.Incremental)
        {
            foreach (var stale in existing.Values.Where(file => !seen.Contains(file.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexTables.DeleteFileAsync(db, rootId, stale.Path, cancellationToken).ConfigureAwait(false);
                filesRemoved++;
                Publish();
                if (request.Throttle is { } throttle)
                    await throttle.PauseAfterFileAsync(filesEnumerated + filesRemoved, cancellationToken).ConfigureAwait(false);
            }
        }

        await IndexTables.MarkRootRefreshedAsync(db, rootId, profile, cancellationToken).ConfigureAwait(false);
        await TryCommitRefreshCheckpointAsync(db, volumeContext, beforeJournal, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task<IndexVolumeContext?> TryPrepareVolumeAsync(
        Database db,
        long rootId,
        string root,
        CancellationToken cancellationToken)
    {
        if (_volumeResolver is null)
            return null;

        if (!_volumeResolver.TryResolveVolume(root, out var volume, out var reason))
        {
            _logger.LogDebug("Could not resolve index volume for {Root}: {Reason}", root, reason);
            return null;
        }

        var volumeId = await IndexTables.EnsureVolumeAsync(db, volume, cancellationToken).ConfigureAwait(false);
        var rootIdentity = TryGetIndexedFileIdentity(volumeId, root, lastObservedUsn: null);
        var rootRow = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        var rootIdentityChanged =
            rootRow is { VolumeId: not null, RootFileReferenceNumber: not null } &&
            rootIdentity is not null &&
            (rootRow.VolumeId.Value != volumeId ||
             !string.Equals(rootRow.RootFileReferenceNumber, rootIdentity.FileReferenceNumber, StringComparison.Ordinal));

        await IndexTables.SetRootVolumeAsync(db, rootId, volumeId, rootIdentity, cancellationToken).ConfigureAwait(false);
        return new IndexVolumeContext(volumeId, volume, rootIdentityChanged);
    }

    private async Task<UsnJournalSnapshot?> TryQueryJournalAsync(
        IndexVolumeInfo? volume,
        CancellationToken cancellationToken)
    {
        if (volume is null || _journalReader is null || volume.IsRemote || !volume.UsnSupported)
            return null;

        try
        {
            return await _journalReader.QueryAsync(volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Could not query USN journal for volume {VolumeKey}.", volume.VolumeKey);
            return null;
        }
    }

    private async Task TryCommitRefreshCheckpointAsync(
        Database db,
        IndexVolumeContext? volumeContext,
        UsnJournalSnapshot? beforeJournal,
        CancellationToken cancellationToken)
    {
        if (volumeContext is null || beforeJournal is null)
            return;

        var afterJournal = await TryQueryJournalAsync(volumeContext.Volume, cancellationToken).ConfigureAwait(false);
        if (afterJournal is null || afterJournal.JournalId != beforeJournal.JournalId)
            return;

        await IndexTables.UpdateVolumeCheckpointAsync(
            db,
            volumeContext.VolumeId,
            afterJournal.JournalId,
            afterJournal.NextUsn,
            "healthy",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshDirectoryIdentitiesAsync(
        Database db,
        long rootId,
        string root,
        WalkerOptions options,
        IndexVolumeContext? volumeContext,
        CancellationToken cancellationToken)
    {
        await IndexTables.DeleteDirectoriesForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        if (volumeContext is null)
            return;

        foreach (var directory in EnumerateIndexDirectories(root, options, cancellationToken))
        {
            var identity = TryGetIndexedFileIdentity(volumeContext.VolumeId, directory, lastObservedUsn: null);
            if (identity is null)
                continue;

            await IndexTables.EnsureDirectoryAsync(db, rootId, directory, identity, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureParentDirectoryIdentitiesAsync(
        Database db,
        long rootId,
        string root,
        string filePath,
        IndexVolumeContext? volumeContext,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        await EnsureDirectoryIdentityChainAsync(
            db,
            rootId,
            root,
            directory,
            volumeContext,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureDirectoryIdentityChainAsync(
        Database db,
        long rootId,
        string root,
        string directoryPath,
        IndexVolumeContext? volumeContext,
        CancellationToken cancellationToken)
    {
        if (volumeContext is null)
            return;

        var directories = new Stack<string>();
        var directory = directoryPath;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var normalized = IndexPath.NormalizeRoot(directory);
            if (!IndexPath.EqualsPath(normalized, root) && !IsUnderRoot(root, normalized))
                break;

            directories.Push(normalized);
            if (IndexPath.EqualsPath(normalized, root))
                break;

            directory = Path.GetDirectoryName(normalized);
        }

        while (directories.Count > 0)
        {
            var current = directories.Pop();
            var identity = TryGetIndexedFileIdentity(volumeContext.VolumeId, current, lastObservedUsn: null);
            if (identity is null)
                continue;

            await IndexTables.EnsureDirectoryAsync(db, rootId, current, identity, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task UpsertFileCoreAsync(
        Database db,
        long rootId,
        string root,
        string path,
        WalkerOptions indexingOptions,
        IndexVolumeContext? volumeContext,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            await IndexTables.DeleteFileAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(path);
        var identity = TryGetIndexedFileIdentity(volumeContext?.VolumeId, path, lastObservedUsn: null);
        await EnsureParentDirectoryIdentitiesAsync(db, rootId, root, path, volumeContext, cancellationToken)
            .ConfigureAwait(false);
        if (ShouldSkipSingleFile(root, info, indexingOptions))
        {
            if (identity is not null)
            {
                await IndexTables.DeleteFilesByIdentityAsync(
                    db,
                    identity.VolumeId,
                    identity.FileReferenceNumber,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await IndexTables.DeleteFileAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var existingRow = await IndexTables.GetFileRowAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
        if (existingRow is not null && IsUnchanged(existingRow, info))
            return;

        await IndexSingleFileAsync(db, rootId, path, info, identity, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateIndexDirectories(
        string root,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        yield return IndexPath.NormalizeRoot(root);

        if (!options.Recursive)
            yield break;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipDirectoryIdentity(child, options))
                    continue;

                var normalized = IndexPath.NormalizeRoot(child);
                yield return normalized;
                stack.Push(normalized);
            }
        }
    }

    private static bool ShouldSkipDirectoryIdentity(string directory, WalkerOptions options)
    {
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(name) && options.ExcludeDirectories.Contains(name))
            return true;

        try
        {
            if (!options.IncludeHidden &&
                (File.GetAttributes(directory) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    private IndexedFileIdentity? TryGetIndexedFileIdentity(
        long? volumeId,
        string path,
        long? lastObservedUsn)
    {
        if (volumeId is null || _volumeResolver is null)
            return null;

        return _volumeResolver.TryGetFileIdentity(path, out var identity)
            ? new IndexedFileIdentity(
                volumeId.Value,
                identity.FileReferenceNumber,
                identity.ParentFileReferenceNumber,
                lastObservedUsn)
            : null;
    }

    private async Task ClearRootContentAsync(
        Database db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var fileIds = await IndexTables.ReadFileIdsForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        foreach (var batch in fileIds.Chunk(IdQueryBatchSize))
            await IndexTables.DeleteLinesForFilesAsync(db, batch, cancellationToken).ConfigureAwait(false);

        await IndexTables.DeleteFilesForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        await IndexTables.DeleteDirectoriesForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> IndexSingleFileAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        IndexedFileIdentity? identity,
        CancellationToken cancellationToken)
    {
        var fileId = await IndexTables.EnsureFileRowAsync(db, rootId, path, info, FileStatus.Indexing, null, identity, cancellationToken).ConfigureAwait(false);
        await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);

        var extractor = _extractors.GetFor(path);
        if (extractor is null)
        {
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Skipped, "No extractor registered.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        long linesIndexed = 0;
        try
        {
            var nextLineId = await IndexTables.GetNextIdAsync(db, "lines", cancellationToken).ConfigureAwait(false);
            var batch = db.PrepareInsertBatch("lines", LineInsertBatchSize);
            await foreach (var line in extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false))
            {
                batch.AddRow(
                    DbValue.FromInteger(nextLineId++),
                    DbValue.FromInteger(fileId),
                    DbValue.FromInteger(line.Number),
                    DbValue.FromText(line.Content));

                linesIndexed++;
                if (batch.Count >= LineInsertBatchSize)
                    await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
            return linesIndexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The failure is recorded on the file row; log at Debug since
            // unreadable files are routine during background indexing.
            _logger.LogDebug(ex, "Indexing failed for file {Path}.", path);
            await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Error, ex.Message, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private static async Task<IndexedLocationInfo?> GetLocationInfoAsync(
        Database db,
        string root,
        CancellationToken cancellationToken)
    {
        var rootRow = await IndexTables.GetRootAsync(db, IndexPath.NormalizeRoot(root), cancellationToken).ConfigureAwait(false);
        if (rootRow is null)
            return null;

        var fileCount = await IndexTables.CountOkFilesAsync(db, rootRow.Id, cancellationToken).ConfigureAwait(false);
        var lineCount = await IndexTables.CountOkLinesAsync(db, rootRow.Id, cancellationToken).ConfigureAwait(false);
        var indexedUtc = rootRow.IndexedUtcTicks > 0
            ? new DateTime(rootRow.IndexedUtcTicks, DateTimeKind.Utc)
            : (DateTime?)null;

        return new IndexedLocationInfo(root, fileCount, lineCount, indexedUtc, rootRow.OptionsHash, Exists: true);
    }

    private IndexDatabaseInfo CreateDatabaseInfo(
        bool isCompatible,
        int locationCount = 0,
        long totalFileCount = 0,
        long totalLineCount = 0,
        int pendingChangeCount = 0,
        DateTime? lastIndexedUtc = null,
        IReadOnlyList<IndexVolumeHealthInfo>? volumeHealth = null)
    {
        var databaseBytes = GetFileLength(DatabasePath);
        var walBytes = GetFileLength(DatabasePath + ".wal");
        var shmBytes = GetFileLength(DatabasePath + ".shm");

        return new IndexDatabaseInfo(
            DatabasePath,
            File.Exists(DatabasePath),
            isCompatible,
            IndexDatabase.CurrentSchemaVersion,
            databaseBytes,
            walBytes,
            shmBytes,
            locationCount,
            totalFileCount,
            totalLineCount,
            pendingChangeCount,
            lastIndexedUtc,
            volumeHealth);
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsUnchanged(ExistingFileRow row, FileInfo info) =>
        row.SizeBytes == info.Length &&
        row.CreatedUtcTicks == info.CreationTimeUtc.Ticks &&
        row.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
        row.Attributes == (long)info.Attributes &&
        string.Equals(row.Status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.ContentVersion, IndexContentVersion.Current, StringComparison.Ordinal);

    private bool IsRootIdentityCurrent(string root, RootRow rootRow)
    {
        if (_volumeResolver is null || rootRow.RootFileReferenceNumber is null)
            return true;

        return _volumeResolver.TryGetFileIdentity(root, out var identity) &&
            string.Equals(identity.FileReferenceNumber, rootRow.RootFileReferenceNumber, StringComparison.Ordinal) &&
            string.Equals(
                identity.ParentFileReferenceNumber,
                rootRow.RootParentFileReferenceNumber,
                StringComparison.Ordinal);
    }

    private static bool ShouldSkipSingleFile(string root, FileInfo info, WalkerOptions options)
    {
        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && !options.IncludeHidden)
            return true;

        return !IndexedFileFilter.Matches(
            root,
            info.FullName,
            info.Name,
            info.Extension.ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            options);
    }

    private bool TryCreateHit(
        string root,
        IndexedLine line,
        Query query,
        WalkerOptions options,
        Dictionary<string, int> hitsByPath,
        Dictionary<string, bool> fileFilterVerdicts,
        List<MatchSpan> highlightBuffer,
        out Hit hit)
    {
        hit = null!;

        // All of IndexedFileFilter's checks are per-file, and result rows are
        // ordered by path, so memoize the verdict instead of re-running the
        // glob/extension/directory checks for every line of the same file.
        if (!fileFilterVerdicts.TryGetValue(line.Path, out var fileAllowed))
        {
            fileAllowed = IndexedFileFilter.Matches(
                root, line.Path, line.FileName, line.Extension, line.SizeBytes, line.ModifiedUtcTicks, options);
            fileFilterVerdicts[line.Path] = fileAllowed;
        }

        if (!fileAllowed)
            return false;

        if (!query.IsMatch(line.Content))
            return false;

        hitsByPath.TryGetValue(line.Path, out var hitsForFile);
        if (hitsForFile >= _searchOptions.MaxHitsPerFile)
            return false;

        highlightBuffer.Clear();
        query.CollectHighlights(line.Content, highlightBuffer);

        hitsByPath[line.Path] = hitsForFile + 1;
        hit = new Hit(line.Path, line.LineNumber, line.Content, highlightBuffer.ToArray());
        return true;
    }

    private static async Task FlushBatchAsync(InsertBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 &&
            relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private sealed record IndexVolumeContext(long VolumeId, IndexVolumeInfo Volume, bool RootIdentityChanged);

    private sealed record ReplayRootContext(
        long RootId,
        WalkerOptions Options,
        IndexVolumeContext? VolumeContext);
}
