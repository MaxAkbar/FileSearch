using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions s_failureJsonOptions = new() { WriteIndented = true };

    private readonly IndexDatabase _database;
    private readonly IFileWalker _walker;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions _searchOptions;
    private readonly IIndexVolumeResolver? _volumeResolver;
    private readonly IUsnJournalReader? _journalReader;
    private readonly IOutOfProcessExtractionService? _outOfProcessExtraction;
    private readonly IWindowsIFilterExtractionService? _windowsIFilterExtraction;
    private readonly ILogger _logger;

    public CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions = null,
        ILogger<CSharpDbFileIndex>? logger = null,
        IOutOfProcessExtractionService? outOfProcessExtraction = null,
        IWindowsIFilterExtractionService? windowsIFilterExtraction = null)
        : this(options, walker, extractors, searchOptions, logger, null, null, outOfProcessExtraction, windowsIFilterExtraction)
    {
    }

    internal CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions,
        ILogger<CSharpDbFileIndex>? logger,
        IIndexVolumeResolver? volumeResolver,
        IUsnJournalReader? journalReader,
        IOutOfProcessExtractionService? outOfProcessExtraction = null,
        IWindowsIFilterExtractionService? windowsIFilterExtraction = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _searchOptions = searchOptions ?? new SearchOptions();
        _volumeResolver = volumeResolver;
        _journalReader = journalReader;
        _outOfProcessExtraction = outOfProcessExtraction;
        _windowsIFilterExtraction = windowsIFilterExtraction;
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

            var profile = BuildIndexProfile(indexingOptions);
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

                if (!IsExtractorProfileCurrent(rootRow.OptionsHash))
                    return new IndexCoverage(IndexCoverageStatus.Incompatible, "Index extractor versions are out of date");

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
        long failedFileCount = 0;
        var pendingChangeCount = 0;
        IReadOnlyList<IndexVolumeHealthInfo> volumeHealth = Array.Empty<IndexVolumeHealthInfo>();
        IReadOnlyList<IndexRootStrategyInfo> rootStrategies = Array.Empty<IndexRootStrategyInfo>();
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
            failedFileCount = await IndexTables.CountFailedFilesAsync(db, cancellationToken).ConfigureAwait(false);
            volumeHealth = await IndexTables.ListVolumeHealthAsync(db, cancellationToken).ConfigureAwait(false);
            rootStrategies = await IndexTables.ListRootStrategiesAsync(db, cancellationToken).ConfigureAwait(false);
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
            volumeHealth,
            rootStrategies,
            failedFileCount);
    }

    public async Task<IReadOnlyList<IndexFailureInfo>> GetFailedFilesAsync(CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<IndexFailureInfo>();

        try
        {
            return await IndexTables.ListFailedFilesAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<IndexValidationDriftInfo>> GetValidationDriftAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<IndexValidationDriftInfo>();

        try
        {
            return await IndexTables.ListValidationDriftsAsync(db, IndexPath.NormalizeRoot(root), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await IndexDatabase.CloseQuietlyAsync(db).ConfigureAwait(false);
        }
    }

    public async Task<IndexValidationResult> ValidateRootAsync(
        IndexRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Root)) throw new ArgumentException("Root is required.", nameof(request));

        IndexValidationResult? validation = null;
        await _database.RunExclusiveWriteAsync(async db =>
        {
            validation = await ValidateRootCoreAsync(db, request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return validation ?? IndexValidationResult.Failed(
            IndexPath.NormalizeRoot(request.Root),
            DateTime.UtcNow,
            "Validation did not complete.");
    }

    public async Task ExportFailedFilesAsync(
        string path,
        IndexFailureExportFormat format,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));

        var failures = await GetFailedFilesAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        switch (format)
        {
            case IndexFailureExportFormat.Csv:
                await File.WriteAllTextAsync(fullPath, BuildFailureCsv(failures), cancellationToken).ConfigureAwait(false);
                break;
            case IndexFailureExportFormat.Json:
                await using (var stream = File.Create(fullPath))
                {
                    await JsonSerializer.SerializeAsync(
                            stream,
                            failures,
                            s_failureJsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown failure export format.");
        }
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

    internal async Task<bool> IsRootProfileCurrentCoreAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        var db = await _database.OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return false;

        try
        {
            var root = IndexPath.NormalizeRoot(location.Root);
            var row = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
            if (row is null || row.IndexedUtcTicks <= 0)
                return false;

            var profile = BuildIndexProfile(IndexWalkerOptions.ForIndexing(location.WalkerOptions));
            return string.Equals(row.OptionsHash, profile, StringComparison.Ordinal) &&
                string.Equals(row.ContentVersion, IndexContentVersion.Current, StringComparison.Ordinal);
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
                var profile = BuildIndexProfile(indexingOptions);
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
        var profile = BuildIndexProfile(walkerOptions);
        var rootId = await IndexTables.EnsureRootAsync(db, root, profile, cancellationToken).ConfigureAwait(false);
        var volumeContext = await TryPrepareVolumeAsync(db, rootId, root, cancellationToken).ConfigureAwait(false);
        var beforeJournal = await TryQueryJournalAsync(volumeContext, cancellationToken).ConfigureAwait(false);
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
                var extractor = _extractors.GetFor(normalizedPath);
                if (existingFile is not null && IsUnchanged(existingFile, info, extractor))
                {
                    filesSkipped++;
                    Publish();
                    continue;
                }

                var identity = TryGetIndexedFileIdentity(volumeContext?.VolumeId, normalizedPath, lastObservedUsn: null);
                var indexedLines = await IndexSingleFileAsync(db, rootId, normalizedPath, info, identity, extractor, cancellationToken).ConfigureAwait(false);
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

    private async Task<IndexValidationResult> ValidateRootCoreAsync(
        Database db,
        IndexRequest request,
        CancellationToken cancellationToken)
    {
        var root = IndexPath.NormalizeRoot(request.Root);
        var checkedUtc = DateTime.UtcNow;
        var rootRow = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        if (rootRow is null)
            return IndexValidationResult.MissingIndex(root, checkedUtc);

        IndexValidationResult result;
        if (!Directory.Exists(root))
        {
            result = IndexValidationResult.Unavailable(root, checkedUtc, "Folder is not reachable.");
            await IndexTables.ReplaceValidationDriftsAsync(
                    db,
                    rootRow.Id,
                    Array.Empty<IndexValidationDriftInfo>(),
                    cancellationToken)
                .ConfigureAwait(false);
            await IndexTables.MarkRootValidatedAsync(db, rootRow.Id, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        try
        {
            var walkerOptions = IndexWalkerOptions.ForIndexing(request.WalkerOptions);
            var existing = await IndexTables.LoadExistingFilesAsync(db, rootRow.Id, cancellationToken).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long filesChecked = 0;
            long filesMatched = 0;
            long missingFromIndex = 0;
            long changedSinceIndex = 0;
            long failedChecks = 0;
            var drift = new List<IndexValidationDriftInfo>();
            void AddDrift(string path, IndexValidationDriftKind kind, string message) =>
                drift.Add(new IndexValidationDriftInfo(root, path, kind, message, checkedUtc));

            void PublishValidation() => request.ValidationProgress?.Invoke(new IndexValidationProgress(
                filesChecked,
                filesMatched,
                missingFromIndex,
                changedSinceIndex,
                MissingFromDisk: 0,
                failedChecks));

            foreach (var path in _walker.Enumerate(new[] { root }, walkerOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesChecked++;
                var normalizedPath = IndexPath.NormalizeFile(path);
                seen.Add(normalizedPath);

                try
                {
                    if (!File.Exists(normalizedPath))
                    {
                        failedChecks++;
                        AddDrift(
                            normalizedPath,
                            IndexValidationDriftKind.FailedCheck,
                            "File disappeared while validation was reading it.");
                        continue;
                    }

                    if (!existing.TryGetValue(normalizedPath, out var row))
                    {
                        missingFromIndex++;
                        AddDrift(
                            normalizedPath,
                            IndexValidationDriftKind.MissingFromIndex,
                            "File exists on disk but is not indexed.");
                        continue;
                    }

                    var info = new FileInfo(normalizedPath);
                    var extractor = _extractors.GetFor(normalizedPath);
                    if (IsValidationCurrent(row, info, extractor))
                    {
                        filesMatched++;
                    }
                    else
                    {
                        changedSinceIndex++;
                        AddDrift(
                            normalizedPath,
                            IndexValidationDriftKind.ChangedSinceIndex,
                            "File metadata or extractor version differs from the indexed row.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Validation failed for file {Path}.", path);
                    failedChecks++;
                    AddDrift(normalizedPath, IndexValidationDriftKind.FailedCheck, ex.Message);
                }

                if (request.Throttle is { } throttle)
                    await throttle.PauseAfterFileAsync(filesChecked, cancellationToken).ConfigureAwait(false);

                PublishValidation();
            }

            var missingFromDiskRows = existing.Values.Where(file => !seen.Contains(file.Path)).ToArray();
            var missingFromDisk = missingFromDiskRows.LongLength;
            foreach (var file in missingFromDiskRows)
            {
                AddDrift(
                    file.Path,
                    IndexValidationDriftKind.MissingFromDisk,
                    "Indexed file was not found by validation; it may be deleted or no longer match index filters.");
            }

            request.ValidationProgress?.Invoke(new IndexValidationProgress(
                filesChecked,
                filesMatched,
                missingFromIndex,
                changedSinceIndex,
                missingFromDisk,
                failedChecks));
            result = IndexValidationResult.Create(
                root,
                checkedUtc,
                filesChecked,
                filesMatched,
                missingFromIndex,
                changedSinceIndex,
                missingFromDisk,
                failedChecks,
                drift);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index validation failed for {Root}.", root);
            result = IndexValidationResult.Failed(root, checkedUtc, ex.Message);
        }

        await IndexTables.ReplaceValidationDriftsAsync(db, rootRow.Id, result.DriftDetails, cancellationToken)
            .ConfigureAwait(false);
        await IndexTables.MarkRootValidatedAsync(db, rootRow.Id, result, cancellationToken).ConfigureAwait(false);
        return result;
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
        var strategy = IndexLocationStrategyResolver.Classify(root, volume);
        var rootIdentity = TryGetIndexedFileIdentity(volumeId, root, lastObservedUsn: null);
        var rootRow = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        var rootIdentityChanged =
            rootRow is { VolumeId: not null, RootFileReferenceNumber: not null } &&
            rootIdentity is not null &&
            (rootRow.VolumeId.Value != volumeId ||
             !string.Equals(rootRow.RootFileReferenceNumber, rootIdentity.FileReferenceNumber, StringComparison.Ordinal));

        await IndexTables.SetRootVolumeAsync(db, rootId, volumeId, rootIdentity, strategy, cancellationToken).ConfigureAwait(false);
        return new IndexVolumeContext(volumeId, volume, strategy, rootIdentityChanged);
    }

    private async Task<UsnJournalSnapshot?> TryQueryJournalAsync(
        IndexVolumeContext? volumeContext,
        CancellationToken cancellationToken)
    {
        if (volumeContext is null ||
            _journalReader is null ||
            !volumeContext.Strategy.UsnCatchUpEnabled ||
            volumeContext.Volume.IsRemote ||
            !volumeContext.Volume.UsnSupported)
        {
            return null;
        }

        try
        {
            var volume = volumeContext.Volume;
            return await _journalReader.QueryAsync(volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Could not query USN journal for volume {VolumeKey}.", volumeContext.Volume.VolumeKey);
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

        var afterJournal = await TryQueryJournalAsync(volumeContext, cancellationToken).ConfigureAwait(false);
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

        var extractor = _extractors.GetFor(path);
        var existingRow = await IndexTables.GetFileRowAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
        if (existingRow is not null && IsUnchanged(existingRow, info, extractor))
            return;

        await IndexSingleFileAsync(db, rootId, path, info, identity, extractor, cancellationToken).ConfigureAwait(false);
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
        ITextExtractor? extractor,
        CancellationToken cancellationToken)
    {
        var extractorId = GetExtractorId(extractor);
        var extractorVersion = GetExtractorVersion(extractor);
        var fileId = await IndexTables.EnsureFileRowAsync(
            db,
            rootId,
            path,
            info,
            FileStatus.Indexing,
            null,
            identity,
            extractorId,
            extractorVersion,
            cancellationToken).ConfigureAwait(false);
        await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await IndexTables.ReplaceExtractionIssuesAsync(db, fileId, Array.Empty<ExtractionIssue>(), cancellationToken).ConfigureAwait(false);

        if (extractor is null)
        {
            var fallbackLines = await TryIndexWithWindowsIFilterAsync(
                    db,
                    fileId,
                    path,
                    primaryExtractor: null,
                    primaryFailure: null,
                    primaryLineCount: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fallbackLines is not null)
                return fallbackLines.Value;

            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Skipped, "No extractor registered.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        long linesIndexed = 0;
        var issueSink = new ListExtractionIssueSink();
        try
        {
            await IndexTables.RecordExtractionAttemptAsync(db, fileId, extractorId, extractorVersion, cancellationToken).ConfigureAwait(false);
            var lineIds = new DbIdBlockAllocator(db, "lines", LineInsertBatchSize);
            var batch = db.PrepareInsertBatch("lines", LineInsertBatchSize);

            if (_outOfProcessExtraction?.ShouldUse(extractor) == true)
            {
                var result = await _outOfProcessExtraction.ExtractAsync(path, extractor, cancellationToken).ConfigureAwait(false);
                foreach (var issue in result.Issues)
                    issueSink.Report(issue);

                foreach (var line in result.Lines)
                {
                    AddLineToBatch(batch, await lineIds.NextAsync(cancellationToken).ConfigureAwait(false), fileId, line);
                    linesIndexed++;
                    if (batch.Count >= LineInsertBatchSize)
                        await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var lines = extractor is IDiagnosticTextExtractor diagnosticExtractor
                    ? diagnosticExtractor.ExtractAsync(path, issueSink, cancellationToken)
                    : extractor.ExtractAsync(path, cancellationToken);

                await foreach (var line in lines.ConfigureAwait(false))
                {
                    AddLineToBatch(batch, await lineIds.NextAsync(cancellationToken).ConfigureAwait(false), fileId, line);
                    linesIndexed++;
                    if (batch.Count >= LineInsertBatchSize)
                        await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            if (linesIndexed == 0)
            {
                var fallbackLines = await TryIndexWithWindowsIFilterAsync(
                        db,
                        fileId,
                        path,
                        extractor,
                        primaryFailure: null,
                        primaryLineCount: linesIndexed,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallbackLines is not null)
                    return fallbackLines.Value;
            }

            await IndexTables.ReplaceExtractionIssuesAsync(db, fileId, issueSink.Issues, cancellationToken).ConfigureAwait(false);
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
            return linesIndexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fallbackLines = await TryIndexWithWindowsIFilterAsync(
                    db,
                    fileId,
                    path,
                    extractor,
                    primaryFailure: ex,
                    primaryLineCount: linesIndexed,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fallbackLines is not null)
                return fallbackLines.Value;

            // The failure is recorded on the file row; log at Debug since
            // unreadable files are routine during background indexing.
            _logger.LogDebug(ex, "Indexing failed for file {Path}.", path);
            await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
            var error = ex is ExtractorHostException hostException
                ? $"{hostException.Code}: {hostException.Message}"
                : ex.Message;
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Error, error, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private async Task<long?> TryIndexWithWindowsIFilterAsync(
        Database db,
        long fileId,
        string path,
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount,
        CancellationToken cancellationToken)
    {
        var fallback = _windowsIFilterExtraction;
        if (fallback is null ||
            !fallback.CanTryFallback(path, primaryExtractor, primaryFailure, primaryLineCount))
        {
            return null;
        }

        WindowsIFilterExtractionResult? result;
        try
        {
            result = await fallback.TryExtractAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IFilter fallback extraction failed for file {Path}.", path);
            return null;
        }

        if (result is null)
            return null;

        await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await IndexTables.RecordExtractionAttemptAsync(
                db,
                fileId,
                fallback.ExtractorId,
                fallback.ExtractorVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var issues = new List<ExtractionIssue>(result.Issues.Count + 1)
        {
            new(
                MemberPath: null,
                Code: "ifilter_fallback_used",
                Message: FormatIFilterFallbackMessage(primaryExtractor, primaryFailure, primaryLineCount),
                Severity: "info"),
        };
        issues.AddRange(result.Issues);
        await IndexTables.ReplaceExtractionIssuesAsync(db, fileId, issues, cancellationToken).ConfigureAwait(false);
        if (result.Lines.Count == 0)
        {
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var linesIndexed = await InsertLinesAsync(db, fileId, result.Lines, cancellationToken).ConfigureAwait(false);
        await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
        return linesIndexed;
    }

    private static string FormatIFilterFallbackMessage(
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount)
    {
        if (primaryExtractor is null)
            return "Windows IFilter fallback was used because no primary extractor was registered.";

        if (primaryFailure is not null)
            return $"Windows IFilter fallback was used after {primaryExtractor.ExtractorId} failed: {primaryFailure.Message}";

        return $"Windows IFilter fallback was used because {primaryExtractor.ExtractorId} returned {primaryLineCount:n0} lines.";
    }

    private static async Task<long> InsertLinesAsync(
        Database db,
        long fileId,
        IReadOnlyList<TextLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return 0;

        var nextLineId = await IndexTables.AllocateIdsAsync(db, "lines", lines.Count, cancellationToken).ConfigureAwait(false);
        var batch = db.PrepareInsertBatch("lines", LineInsertBatchSize);
        long linesIndexed = 0;
        foreach (var line in lines)
        {
            AddLineToBatch(batch, nextLineId++, fileId, line);
            linesIndexed++;
            if (batch.Count >= LineInsertBatchSize)
                await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        return linesIndexed;
    }

    private static void AddLineToBatch(InsertBatch batch, long lineId, long fileId, TextLine line)
    {
        batch.AddRow(
            DbValue.FromInteger(lineId),
            DbValue.FromInteger(fileId),
            DbValue.FromInteger(line.Number),
            DbValue.FromText(line.Content));
    }

    private sealed class DbIdBlockAllocator
    {
        private readonly Database _db;
        private readonly string _sequenceName;
        private readonly long _blockSize;
        private long _nextId;
        private long _remaining;

        public DbIdBlockAllocator(Database db, string sequenceName, long blockSize)
        {
            _db = db;
            _sequenceName = sequenceName;
            _blockSize = blockSize;
        }

        public async ValueTask<long> NextAsync(CancellationToken cancellationToken)
        {
            if (_remaining == 0)
            {
                _nextId = await IndexTables.AllocateIdsAsync(_db, _sequenceName, _blockSize, cancellationToken).ConfigureAwait(false);
                _remaining = _blockSize;
            }

            _remaining--;
            return _nextId++;
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
        var lastFullScanUtc = rootRow.LastFullScanUtcTicks > 0
            ? new DateTime(rootRow.LastFullScanUtcTicks, DateTimeKind.Utc)
            : (DateTime?)null;
        var lastFullValidationUtc = rootRow.LastFullValidationUtcTicks > 0
            ? new DateTime(rootRow.LastFullValidationUtcTicks, DateTimeKind.Utc)
            : (DateTime?)null;
        var volumeKey = rootRow.VolumeId is { } volumeId
            ? await IndexTables.GetVolumeKeyAsync(db, volumeId, cancellationToken).ConfigureAwait(false)
            : null;

        return new IndexedLocationInfo(
            root,
            fileCount,
            lineCount,
            indexedUtc,
            rootRow.OptionsHash,
            Exists: true,
            lastFullScanUtc,
            volumeKey,
            lastFullValidationUtc,
            rootRow.LastValidationStatus,
            rootRow.LastValidationMessage ?? string.Empty,
            rootRow.LastValidationFilesChecked,
            rootRow.LastValidationMissingFromIndexCount,
            rootRow.LastValidationChangedCount,
            rootRow.LastValidationMissingFromDiskCount,
            rootRow.LastValidationFailedCount);
    }

    private IndexDatabaseInfo CreateDatabaseInfo(
        bool isCompatible,
        int locationCount = 0,
        long totalFileCount = 0,
        long totalLineCount = 0,
        int pendingChangeCount = 0,
        DateTime? lastIndexedUtc = null,
        IReadOnlyList<IndexVolumeHealthInfo>? volumeHealth = null,
        IReadOnlyList<IndexRootStrategyInfo>? rootStrategies = null,
        long failedFileCount = 0)
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
            volumeHealth,
            rootStrategies,
            failedFileCount);
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

    private string BuildIndexProfile(WalkerOptions options) =>
        $"{IndexProfile.FromWalkerOptions(options).ToStorageString()}|extractorProfile={BuildExtractorProfileHash()}";

    private bool IsExtractorProfileCurrent(string storedProfile) =>
        storedProfile.Contains($"|extractorProfile={BuildExtractorProfileHash()}", StringComparison.Ordinal);

    private string BuildExtractorProfileHash()
    {
        var builder = new StringBuilder();
        foreach (var extension in _extractors.SupportedExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var extractor = _extractors.GetFor("probe" + extension);
            builder
                .Append(extension)
                .Append('\t')
                .Append(GetExtractorId(extractor))
                .Append('\t')
                .Append(GetExtractorVersion(extractor))
                .Append('\n');
        }

        var fallback = _extractors.GetFor("filesearch-unknown-extension");
        if (fallback is not null)
        {
            builder
                .Append("<fallback>")
                .Append('\t')
                .Append(GetExtractorId(fallback))
                .Append('\t')
                .Append(GetExtractorVersion(fallback))
                .Append('\n');
        }

        if (_windowsIFilterExtraction is not null)
        {
            builder
                .Append("<ifilter-fallback>")
                .Append('\t')
                .Append(_windowsIFilterExtraction.ExtractorId)
                .Append('\t')
                .Append(_windowsIFilterExtraction.ExtractorVersion)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private bool IsUnchanged(ExistingFileRow row, FileInfo info, ITextExtractor? extractor) =>
        row.SizeBytes == info.Length &&
        row.CreatedUtcTicks == info.CreationTimeUtc.Ticks &&
        row.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
        row.Attributes == (long)info.Attributes &&
        string.Equals(row.Status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.ContentVersion, IndexContentVersion.Current, StringComparison.Ordinal) &&
        IsExtractorMetadataCurrent(row, extractor);

    private bool IsValidationCurrent(ExistingFileRow row, FileInfo info, ITextExtractor? extractor) =>
        row.SizeBytes == info.Length &&
        row.CreatedUtcTicks == info.CreationTimeUtc.Ticks &&
        row.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
        row.Attributes == (long)info.Attributes &&
        !string.Equals(row.Status, FileStatus.Indexing, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.ContentVersion, IndexContentVersion.Current, StringComparison.Ordinal) &&
        IsExtractorMetadataCurrent(row, extractor);

    private bool IsExtractorMetadataCurrent(ExistingFileRow row, ITextExtractor? extractor)
    {
        if (string.Equals(row.ExtractorId, GetExtractorId(extractor), StringComparison.Ordinal) &&
            string.Equals(row.ExtractorVersion, GetExtractorVersion(extractor), StringComparison.Ordinal))
        {
            return true;
        }

        return _windowsIFilterExtraction is not null &&
            string.Equals(row.ExtractorId, _windowsIFilterExtraction.ExtractorId, StringComparison.Ordinal) &&
            string.Equals(row.ExtractorVersion, _windowsIFilterExtraction.ExtractorVersion, StringComparison.Ordinal);
    }

    private static string GetExtractorId(ITextExtractor? extractor) => extractor?.ExtractorId ?? string.Empty;

    private static string GetExtractorVersion(ITextExtractor? extractor) => extractor?.ExtractorVersion ?? string.Empty;

    private static string BuildFailureCsv(IReadOnlyList<IndexFailureInfo> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("root,path,member_path,kind,code,severity,extractor_id,extractor_version,error,retry_count,attempt_count,last_attempt_utc");
        foreach (var failure in failures)
        {
            AppendCsvField(builder, failure.Root);
            builder.Append(',');
            AppendCsvField(builder, failure.Path);
            builder.Append(',');
            AppendCsvField(builder, failure.MemberPath ?? string.Empty);
            builder.Append(',');
            AppendCsvField(builder, failure.FailureKind);
            builder.Append(',');
            AppendCsvField(builder, failure.IssueCode ?? string.Empty);
            builder.Append(',');
            AppendCsvField(builder, failure.Severity ?? string.Empty);
            builder.Append(',');
            AppendCsvField(builder, failure.ExtractorId);
            builder.Append(',');
            AppendCsvField(builder, failure.ExtractorVersion);
            builder.Append(',');
            AppendCsvField(builder, failure.Error);
            builder.Append(',');
            builder.Append(failure.RetryCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(failure.ExtractionAttemptCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            AppendCsvField(builder, failure.LastAttemptUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendCsvField(StringBuilder builder, string value)
    {
        var needsQuotes =
            value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal);
        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        builder.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

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

    private sealed record IndexVolumeContext(
        long VolumeId,
        IndexVolumeInfo Volume,
        IndexLocationStrategy Strategy,
        bool RootIdentityChanged);

    private sealed record ReplayRootContext(
        long RootId,
        WalkerOptions Options,
        IndexVolumeContext? VolumeContext);
}
