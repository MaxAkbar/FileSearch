using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
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
    private const int FileIdAllocationBlockSize = 512;
    private const int LineIdAllocationBlockSize = 8_192;
    private const int LineInsertBatchSize = 4_096;
    private const int FileTrigramInsertBatchSize = 16_384;
    private const int IdQueryBatchSize = 500;
    private const int MetadataHitLimit = 200;
    private const int MaxCachedCandidateLinesPerRoot = 50_000;
    private const int MaxCachedTrigramPostingsPerRoot = 4_096;
    private const int MaxCachedTrigramPostingIds = 1_000_000;
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
    private readonly MetadataNameCache _metadataNameCache = new();
    private readonly CurrentOkFileIdCache _currentOkFileIdCache = new();
    private readonly LineCandidateCache _lineCandidateCache = new();
    private readonly TrigramPostingCache _trigramPostingCache = new();
    private readonly ContentTrigramCache _contentTrigramCache = new();
    private readonly bool _analyzeAfterBuild;

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
        var indexOptions = options ?? new FileIndexOptions();
        _analyzeAfterBuild = indexOptions.AnalyzeAfterBuild;
        _database = new IndexDatabase(indexOptions, _logger);
    }

    public string DatabasePath => _database.DatabasePath;

    /// <summary>
    /// Diagnostic hook invoked once per <see cref="SearchAsync"/> call with the
    /// phase timing breakdown. Benchmarks attach this to attribute fixed query
    /// overhead; when null (the default), searches do no timing bookkeeping.
    /// </summary>
    internal Action<IndexSearchTimings>? SearchTimingsCallback { get; set; }

    public void Dispose() => _database.Dispose();

    internal Task RunExclusiveWriteAsync(
        Func<Database, Task> action,
        CancellationToken cancellationToken) =>
        _database.RunExclusiveWriteAsync(action, cancellationToken);

    public async Task<ContentUnit?> GetContentUnitAsync(long id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return null;

        try
        {
            return await IndexTables.ReadContentUnitAsync(db, id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ContentUnit>> GetContentUnitsForFileAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<ContentUnit>();

        try
        {
            return await IndexTables.ReadContentUnitsForFileAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ContentUnit>> GetNeighboringUnitsAsync(
        long contentUnitId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentUnitId);
        ArgumentOutOfRangeException.ThrowIfNegative(before);
        ArgumentOutOfRangeException.ThrowIfNegative(after);

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<ContentUnit>();

        try
        {
            return await IndexTables.ReadNeighboringContentUnitsAsync(
                    db,
                    contentUnitId,
                    before,
                    after,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<string?> GetFilePathAsync(long fileId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return null;

        try
        {
            return await IndexTables.ReadFilePathAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<long?> GetFileIdAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return null;

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return null;

        try
        {
            var rootId = await IndexTables.GetRootIdAsync(db, IndexPath.NormalizeRoot(root), cancellationToken)
                .ConfigureAwait(false);
            return rootId is null
                ? null
                : await IndexTables.ReadFileIdAsync(
                        db,
                        rootId.Value,
                        IndexPath.NormalizeFile(path),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<long>> GetFileIdsForRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Array.Empty<long>();

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<long>();

        try
        {
            var rootId = await IndexTables.GetRootIdAsync(db, IndexPath.NormalizeRoot(root), cancellationToken)
                .ConfigureAwait(false);
            return rootId is null
                ? Array.Empty<long>()
                : await IndexTables.ReadFileIdsForRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<long>> GetContentUnitIdsForRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Array.Empty<long>();

        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<long>();

        try
        {
            var rootId = await IndexTables.GetRootIdAsync(db, IndexPath.NormalizeRoot(root), cancellationToken)
                .ConfigureAwait(false);
            return rootId is null
                ? Array.Empty<long>()
                : await IndexTables.ReadContentUnitIdsForRootAsync(db, rootId.Value, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

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

        long updatedRootId = 0;
        string? updatedPath = null;
        bool wroteFile = false;
        List<CachedIndexedLine>? cachedLines = null;
        List<string>? cacheRemovedPaths = null;
        List<long>? cacheFileIds = null;
        await _database.RunExclusiveWriteAsync(async db =>
        {
            var indexingOptions = IndexWalkerOptions.ForIndexing(options);
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var normalizedPath = IndexPath.NormalizeFile(path);
            updatedPath = normalizedPath;

            if (!IsUnderRoot(normalizedRoot, normalizedPath))
                return;

            var profile = BuildIndexProfile(indexingOptions);
            var rootId = await IndexTables.EnsureRootAsync(db, normalizedRoot, profile, cancellationToken).ConfigureAwait(false);
            updatedRootId = rootId;
            var volumeContext = await TryPrepareVolumeAsync(db, rootId, normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (volumeContext?.RootIdentityChanged == true)
                await ClearRootContentAsync(db, rootId, cancellationToken).ConfigureAwait(false);

            cachedLines = new List<CachedIndexedLine>();
            cacheRemovedPaths = new List<string>();
            cacheFileIds = new List<long>();
            wroteFile = await UpsertFileCoreAsync(
                db,
                rootId,
                normalizedRoot,
                normalizedPath,
                indexingOptions,
                volumeContext,
                cachedLines,
                cacheRemovedPaths,
                cacheFileIds,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (updatedRootId <= 0)
            return;

        if (wroteFile && updatedPath is not null)
            _contentTrigramCache.ApplyFileUpsert(
                updatedRootId,
                _database.CurrentGeneration,
                updatedPath,
                cacheRemovedPaths ?? [],
                cacheFileIds is { Count: > 0 } ? cacheFileIds.Max() : 0,
                cachedLines ?? []);
        else
            _contentTrigramCache.AdvanceGeneration(updatedRootId, _database.CurrentGeneration);
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

            var normalizedPath = IndexPath.NormalizeFile(path);
            var deleted = await IndexTables.DeleteFileAsync(db, rootId.Value, normalizedPath, cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
            {
                // No file row matched, so the deleted path may have been a
                // directory; sweep any indexed children under it.
                await IndexTables.DeleteFilesUnderDirectoryAsync(db, rootId.Value, normalizedPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Reads that overlap an index write (same process before the reader
        // gate engages, or a writer in another process) can crash inside the
        // storage engine. Restart the search a bounded number of times,
        // deduplicating already-yielded hits so consumers never see doubles.
        const int maxAttempts = 3;
        var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var enumerator = SearchCoreAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var retry = false;
            try
            {
                while (true)
                {
                    Hit hit;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            break;

                        hit = enumerator.Current;
                    }
                    catch (Exception ex) when (
                        attempt < maxAttempts &&
                        IndexStorageFailure.IsStorageEngineFailure(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "Indexed search attempt {Attempt}/{MaxAttempts} failed inside the storage engine; retrying.",
                            attempt,
                            maxAttempts);
                        retry = true;
                        break;
                    }

                    if (yieldedKeys.Add(BuildHitKey(hit)))
                        yield return hit;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (!retry)
                yield break;

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildHitKey(Hit hit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)hit.Kind}|{hit.LineNumber}|{hit.Path}");

    private async IAsyncEnumerable<Hit> SearchCoreAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IndexedSearchRequestSupport.ThrowIfUnsupported(request);

        if (request.Expression is UnifiedQuery { HasUnavailableSemantic: true })
        {
            request.Status?.Invoke(UnifiedQuery.SemanticUnavailableMessage);
            yield break;
        }

        // Coverage gating routes multi-root requests to the live searcher;
        // reaching here with anything but one root is a caller bug — fail
        // loudly instead of silently searching only the first root.
        if (request.Roots.Count != 1)
            throw new ArgumentException("Indexed search requires exactly one root.", nameof(request));

        IndexReadLease? lease = null;
        var timings = SearchTimingsCallback is null ? null : new IndexSearchTimings();
        var searchStartTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var openStart = Stopwatch.GetTimestamp();
            lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (timings is not null)
                timings.OpenTicks = Stopwatch.GetTimestamp() - openStart;

            if (lease is null)
                yield break;

            var db = lease.Session;

            var root = IndexPath.NormalizeRoot(request.Roots[0]);
            var rootStart = Stopwatch.GetTimestamp();
            var rootId = await IndexTables.GetRootIdAsync(db, root, cancellationToken).ConfigureAwait(false);
            if (timings is not null)
                timings.RootResolveTicks = Stopwatch.GetTimestamp() - rootStart;

            if (rootId is null)
                yield break;

            var highlightBuffer = new List<MatchSpan>(4);
            var hitsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileFilterVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var trigramClauses = QueryTrigramTerms.BuildCandidateClauses(request.Expression);
            HashSet<string>? metadataHitPaths = null;
            var metadataOnly = request.SearchTarget != SearchTarget.Content;

            if (request.Expression is UnifiedQuery unified && !unified.HasContentCriteria)
            {
                await foreach (var hit in SearchUnifiedMetadataOnlyAsync(
                        db,
                        rootId.Value,
                        root,
                        request.WalkerOptions,
                        unified,
                        cancellationToken).ConfigureAwait(false))
                {
                    yield return hit;
                }

                yield break;
            }

            if (MetadataSearchSpec.TryCreate(request, out var metadataSpec))
            {
                var metadataStart = Stopwatch.GetTimestamp();
                var metadataHits = await SearchMetadataAsync(
                        db,
                        rootId.Value,
                        root,
                        request.WalkerOptions,
                        request.Expression,
                        metadataSpec,
                        lease.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (timings is not null)
                    timings.MetadataTicks = Stopwatch.GetTimestamp() - metadataStart;

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

            if (metadataOnly)
                yield break;

            if (trigramClauses.Count > 0)
            {
                await foreach (var hit in ResolveTrigramCandidateLinesAsync(
                        db,
                        rootId.Value,
                        root,
                        request,
                        trigramClauses,
                        hitsByPath,
                        fileFilterVerdicts,
                        highlightBuffer,
                        metadataHitPaths,
                        timings,
                        lease.Generation,
                        cancellationToken).ConfigureAwait(false))
                {
                    yield return hit;
                }
            }
            else
            {
                await foreach (var hit in ResolveFullScanAsync(
                        db,
                        rootId.Value,
                        root,
                        request,
                        hitsByPath,
                        fileFilterVerdicts,
                        highlightBuffer,
                        metadataHitPaths,
                        timings,
                        lease.Generation,
                        cancellationToken).ConfigureAwait(false))
                {
                    yield return hit;
                }
            }
        }
        finally
        {
            if (timings is not null)
            {
                timings.TotalTicks = Stopwatch.GetTimestamp() - searchStartTimestamp;
                SearchTimingsCallback?.Invoke(timings);
            }

            lease?.Dispose();
        }
    }

    private async IAsyncEnumerable<Hit> ResolveTrigramCandidateLinesAsync(
        DbExec db,
        long rootId,
        string root,
        SearchRequest request,
        IReadOnlyList<IReadOnlyList<string>> trigramClauses,
        Dictionary<string, int> hitsByPath,
        Dictionary<string, bool> fileFilterVerdicts,
        List<MatchSpan> highlightBuffer,
        HashSet<string>? metadataHitPaths,
        IndexSearchTimings? timings,
        long databaseGeneration,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (timings is not null)
            timings.UsedTrigramIndex = true;

        if (_contentTrigramCache.TryGetFresh(rootId, databaseGeneration, out var cachedIndex, out _))
        {
            var trigramIndex = cachedIndex!;
            var cachedSeenIds = new HashSet<long>();
            var cachedBatchIds = new List<long>(IdQueryBatchSize);
            foreach (var clause in trigramClauses)
            {
                var lookupStart = Stopwatch.GetTimestamp();
                var candidateIds = trigramIndex.FindCandidates(clause);
                if (timings is not null)
                    timings.TrigramLookupTicks += Stopwatch.GetTimestamp() - lookupStart;

                foreach (var lineId in candidateIds)
                {
                    if (!cachedSeenIds.Add(lineId))
                        continue;

                    cachedBatchIds.Add(lineId);
                    if (cachedBatchIds.Count < IdQueryBatchSize)
                        continue;

                    await foreach (var hit in ResolveCandidateLinesAsync(
                            EnumerateCachedLinesAsync(trigramIndex.GetLines(cachedBatchIds), cancellationToken),
                            root,
                            request,
                            hitsByPath,
                            fileFilterVerdicts,
                            highlightBuffer,
                            metadataHitPaths,
                            timings,
                            cancellationToken).ConfigureAwait(false))
                    {
                        yield return hit;
                    }

                    cachedBatchIds.Clear();
                }
            }

            if (cachedBatchIds.Count > 0)
            {
                await foreach (var hit in ResolveCandidateLinesAsync(
                        EnumerateCachedLinesAsync(trigramIndex.GetLines(cachedBatchIds), cancellationToken),
                        root,
                        request,
                        hitsByPath,
                        fileFilterVerdicts,
                        highlightBuffer,
                        metadataHitPaths,
                        timings,
                        cancellationToken).ConfigureAwait(false))
                {
                    yield return hit;
                }
            }

            yield break;
        }

        var seenFileIds = new HashSet<long>();
        var batchFileIds = new List<long>(IdQueryBatchSize);
        HashSet<long>? currentFileIds = null;
        foreach (var clause in trigramClauses)
        {
            var candidateIds = await ReadCandidateFileIdsForTrigramClauseAsync(
                    db,
                    rootId,
                    clause,
                    timings,
                    databaseGeneration,
                    cancellationToken)
                .ConfigureAwait(false);

            currentFileIds ??= await _currentOkFileIdCache.GetOrLoadAsync(
                    db,
                    rootId,
                    databaseGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var fileId in candidateIds)
            {
                if (!currentFileIds.Contains(fileId) || !seenFileIds.Add(fileId))
                    continue;

                batchFileIds.Add(fileId);
                if (batchFileIds.Count < IdQueryBatchSize)
                    continue;

                await foreach (var hit in ResolveCandidateLinesAsync(
                        ReadCurrentFileLineBatchAsync(db, rootId, batchFileIds, databaseGeneration, cancellationToken),
                        root,
                        request,
                        hitsByPath,
                        fileFilterVerdicts,
                        highlightBuffer,
                        metadataHitPaths,
                        timings,
                        cancellationToken).ConfigureAwait(false))
                {
                    yield return hit;
                }

                batchFileIds.Clear();
            }
        }

        if (batchFileIds.Count > 0)
        {
            await foreach (var hit in ResolveCandidateLinesAsync(
                    ReadCurrentFileLineBatchAsync(db, rootId, batchFileIds, databaseGeneration, cancellationToken),
                    root,
                    request,
                    hitsByPath,
                    fileFilterVerdicts,
                    highlightBuffer,
                    metadataHitPaths,
                    timings,
                    cancellationToken).ConfigureAwait(false))
            {
                yield return hit;
            }
        }
    }

    private async Task<List<long>> ReadCandidateFileIdsForTrigramClauseAsync(
        DbExec db,
        long rootId,
        IReadOnlyList<string> trigrams,
        IndexSearchTimings? timings,
        long databaseGeneration,
        CancellationToken cancellationToken)
    {
        HashSet<long>? intersection = null;
        foreach (var trigram in trigrams)
        {
            var lookupStart = Stopwatch.GetTimestamp();
            var ids = await _trigramPostingCache.GetOrLoadAsync(
                    db,
                    rootId,
                    databaseGeneration,
                    trigram,
                    cancellationToken)
                .ConfigureAwait(false);
            if (timings is not null)
                timings.TrigramLookupTicks += Stopwatch.GetTimestamp() - lookupStart;

            if (ids.Count == 0)
                return new List<long>();

            if (intersection is null)
            {
                intersection = new HashSet<long>(ids);
            }
            else
            {
                intersection.IntersectWith(ids);
                if (intersection.Count == 0)
                    return new List<long>();
            }
        }

        if (intersection is null || intersection.Count == 0)
            return new List<long>();

        var result = intersection.ToList();
        result.Sort();
        return result;
    }

    private async IAsyncEnumerable<Hit> ResolveFullScanAsync(
        DbExec db,
        long rootId,
        string root,
        SearchRequest request,
        Dictionary<string, int> hitsByPath,
        Dictionary<string, bool> fileFilterVerdicts,
        List<MatchSpan> highlightBuffer,
        HashSet<string>? metadataHitPaths,
        IndexSearchTimings? timings,
        long databaseGeneration,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (timings is not null)
            timings.UsedFullScan = true;

        if (!_contentTrigramCache.TryGetFresh(rootId, databaseGeneration, out var trigramIndex, out _))
        {
            await foreach (var hit in ResolveDatabaseFullScanAsync(
                    db,
                    rootId,
                    root,
                    request,
                    hitsByPath,
                    fileFilterVerdicts,
                    highlightBuffer,
                    metadataHitPaths,
                    timings,
                    cancellationToken).ConfigureAwait(false))
            {
                yield return hit;
            }

            yield break;
        }

        await foreach (var hit in ResolveCandidateLinesAsync(
                EnumerateCachedLinesAsync(trigramIndex!.GetAllLines(), cancellationToken),
                root,
                request,
                hitsByPath,
                fileFilterVerdicts,
                highlightBuffer,
                metadataHitPaths,
                timings,
                cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }
    }

    private async IAsyncEnumerable<Hit> ResolveDatabaseFullScanAsync(
        DbExec db,
        long rootId,
        string root,
        SearchRequest request,
        Dictionary<string, int> hitsByPath,
        Dictionary<string, bool> fileFilterVerdicts,
        List<MatchSpan> highlightBuffer,
        HashSet<string>? metadataHitPaths,
        IndexSearchTimings? timings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (timings is not null)
            timings.UsedFullScan = true;

        await foreach (var hit in ResolveCandidateLinesAsync(
                IndexTables.ReadCurrentLinesAsync(db, rootId, cancellationToken),
                root,
                request,
                hitsByPath,
                fileFilterVerdicts,
                highlightBuffer,
                metadataHitPaths,
                timings,
                cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }
    }

    private static async IAsyncEnumerable<IndexedLine> EnumerateCachedLinesAsync(
        IEnumerable<IndexedLine> lines,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Streams candidate line rows through the per-line filter/recheck and
    /// yields surviving hits, accumulating fetch time (minus recheck time)
    /// into <paramref name="timings"/> when instrumentation is attached.
    /// </summary>
    private async IAsyncEnumerable<Hit> ResolveCandidateLinesAsync(
        IAsyncEnumerable<IndexedLine> lines,
        string root,
        SearchRequest request,
        Dictionary<string, int> hitsByPath,
        Dictionary<string, bool> fileFilterVerdicts,
        List<MatchSpan> highlightBuffer,
        HashSet<string>? metadataHitPaths,
        IndexSearchTimings? timings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fetchStart = Stopwatch.GetTimestamp();
        var recheckBefore = timings?.RecheckTicks ?? 0;

        await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!TryCreateHit(root, line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, timings, out var hit))
                continue;

            if (metadataHitPaths?.Contains(hit.Path) == true)
                continue;

            yield return hit;
        }

        if (timings is not null)
            timings.LineFetchTicks += Stopwatch.GetTimestamp() - fetchStart - (timings.RecheckTicks - recheckBefore);
    }

    private async IAsyncEnumerable<IndexedLine> ReadCurrentLineBatchAsync(
        DbExec db,
        long rootId,
        List<long> lineIds,
        HashSet<long> currentFileIds,
        long databaseGeneration,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var lines = new List<IndexedLine>(lineIds.Count);
        var missingLineIds = new List<long>(lineIds.Count);
        foreach (var lineId in lineIds)
        {
            if (_lineCandidateCache.TryGet(rootId, databaseGeneration, lineId, out var cached))
                lines.Add(cached);
            else
                missingLineIds.Add(lineId);
        }

        if (missingLineIds.Count > 0)
        {
            await foreach (var row in IndexTables.ReadCachedLinesAsync(db, rootId, missingLineIds, cancellationToken)
                           .ConfigureAwait(false))
            {
                if (!currentFileIds.Contains(row.FileId))
                    continue;

                _lineCandidateCache.Add(rootId, databaseGeneration, row.Id, row.Line);
                lines.Add(row.Line);
            }
        }

        foreach (var line in lines
                     .OrderBy(static line => line.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static line => line.LineNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private async IAsyncEnumerable<IndexedLine> ReadCurrentFileLineBatchAsync(
        DbExec db,
        long rootId,
        List<long> fileIds,
        long databaseGeneration,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lines = new List<IndexedLine>();
        await foreach (var row in IndexTables.ReadCachedLinesForFilesAsync(db, rootId, fileIds, cancellationToken)
                           .ConfigureAwait(false))
        {
            _lineCandidateCache.Add(rootId, databaseGeneration, row.Id, row.Line);
            lines.Add(row.Line);
        }

        foreach (var line in lines
                     .OrderBy(static line => line.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static line => line.LineNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private async Task<List<Hit>> SearchMetadataAsync(
        DbExec db,
        long rootId,
        string root,
        WalkerOptions options,
        Query query,
        MetadataSearchSpec spec,
        long databaseGeneration,
        CancellationToken cancellationToken)
    {
        var hits = new List<Hit>();
        var nameIndex = await _metadataNameCache.GetOrLoadAsync(db, rootId, databaseGeneration, cancellationToken)
            .ConfigureAwait(false);
        var candidates = spec.SearchTarget == SearchTarget.FileNames
            ? nameIndex.FindFileNameCandidates(spec.Terms, spec.RequireAllTerms).ToList()
            : nameIndex.FindCandidates(
                IndexTables.BuildQueryMetadataTokens(spec.Terms),
                spec.RequireAllTerms).ToList();

        if (candidates.Count == 0)
            return hits;

        foreach (var file in candidates)
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

            if (query is UnifiedQuery unified &&
                !unified.MatchesFile(
                    root,
                    file.Path,
                    file.FileName,
                    file.Extension,
                    file.SizeBytes,
                    file.CreatedUtcTicks,
                    file.ModifiedUtcTicks,
                    file.Status,
                    file.ExtractorId,
                    file.FileTypeCategory))
            {
                continue;
            }

            if (spec.SearchTarget == SearchTarget.FileNames && !query.IsMatch(file.FileName))
                continue;

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
                file.ModifiedUtcTicks > 0 ? new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc) : null,
                HitRoute.Indexed));
        }

        var ordered = hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.ModifiedUtc ?? DateTime.MinValue)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase);
        return spec.SearchTarget == SearchTarget.FileNames
            ? ordered.ToList()
            : ordered.Take(MetadataHitLimit).ToList();
    }

    private static async IAsyncEnumerable<Hit> SearchUnifiedMetadataOnlyAsync(
        DbExec db,
        long rootId,
        string root,
        WalkerOptions options,
        UnifiedQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in IndexTables.ReadFileMetadataAsync(db, rootId, cancellationToken).ConfigureAwait(false))
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

            if (!query.MatchesFile(
                    root,
                    file.Path,
                    file.FileName,
                    file.Extension,
                    file.SizeBytes,
                    file.CreatedUtcTicks,
                    file.ModifiedUtcTicks,
                    file.Status,
                    file.ExtractorId,
                    file.FileTypeCategory))
            {
                continue;
            }

            var relative = GetRelativePath(root, file.Path);
            var displayText = string.IsNullOrWhiteSpace(relative) || relative == "."
                ? file.FileName
                : relative;
            yield return new Hit(
                file.Path,
                0,
                $"File match: {displayText}",
                Array.Empty<MatchSpan>(),
                HitKind.Metadata,
                700 + RecencyScore(file.ModifiedUtcTicks),
                file.SizeBytes,
                file.ModifiedUtcTicks > 0 ? new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc) : null,
                HitRoute.Indexed);
        }
    }

    public async Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (!request.UseIndex)
            return new IndexCoverage(IndexCoverageStatus.Disabled, "Index disabled");

        if (request.Roots.Count != 1)
            return new IndexCoverage(IndexCoverageStatus.Unsupported, "Indexed search supports one root at a time");

        try
        {
            var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
            var db = lease?.Session;
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
                lease?.Dispose();
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
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
        }
    }

    public async Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken)
    {
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
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
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<IndexFailureInfo>();

        try
        {
            return await IndexTables.ListFailedFilesAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<IReadOnlyList<IndexValidationDriftInfo>> GetValidationDriftAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<IndexValidationDriftInfo>();

        try
        {
            return await IndexTables.ListValidationDriftsAsync(db, IndexPath.NormalizeRoot(root), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
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
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
        if (db is null)
            return Array.Empty<PendingIndexChange>();

        try
        {
            return await IndexTables.ReadPendingChangesAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
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
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
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
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
        }
    }

    internal async Task<IndexRootIdentity?> GetRootIdentityCoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
        }
    }

    internal async Task<bool> IsRootProfileCurrentCoreAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        var lease = await _database.OpenReadLeaseAsync(cancellationToken).ConfigureAwait(false);
        var db = lease?.Session;
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
            lease?.Dispose();
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
                    null,
                    null,
                    null,
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
            Interlocked.Read(ref filesEnumerated),
            Interlocked.Read(ref filesIndexed),
            Interlocked.Read(ref filesSkipped),
            Interlocked.Read(ref filesRemoved),
            Interlocked.Read(ref filesFailed),
            Interlocked.Read(ref linesIndexed)));

        await RefreshChangedFilesWithParallelExtractionAsync(
                db,
                rootId,
                root,
                walkerOptions,
                volumeContext,
                existing,
                seen,
                mode,
                request,
                Publish,
                () => Interlocked.Read(ref filesEnumerated),
                value => Interlocked.Add(ref filesEnumerated, value),
                value => Interlocked.Add(ref filesIndexed, value),
                value => Interlocked.Add(ref filesSkipped, value),
                value => Interlocked.Add(ref filesFailed, value),
                value => Interlocked.Add(ref linesIndexed, value),
                cancellationToken)
            .ConfigureAwait(false);

        if (mode == IndexRefreshMode.Full || mode == IndexRefreshMode.Incremental)
        {
            foreach (var stale in existing.Values.Where(file => !seen.Contains(file.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexTables.DeleteFileAsync(db, rootId, stale.Path, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref filesRemoved);
                Publish();
                if (request.Throttle is { } throttle)
                    await throttle.PauseAfterFileAsync(
                        Interlocked.Read(ref filesEnumerated) + Interlocked.Read(ref filesRemoved),
                        cancellationToken).ConfigureAwait(false);
            }
        }

        await IndexTables.MarkRootRefreshedAsync(db, rootId, profile, cancellationToken).ConfigureAwait(false);
        if (_analyzeAfterBuild &&
            (Interlocked.Read(ref filesIndexed) > 0 || Interlocked.Read(ref filesRemoved) > 0))
        {
            await TryAnalyzeAsync(db, cancellationToken).ConfigureAwait(false);
        }

        await TryCommitRefreshCheckpointAsync(db, volumeContext, beforeJournal, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task TryAnalyzeAsync(Database db, CancellationToken cancellationToken)
    {
        try
        {
            await IndexTables.AnalyzeAsync(db, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index statistics analysis failed; searches will remain correct but query planning may be slower.");
        }
    }

    private async Task RefreshChangedFilesWithParallelExtractionAsync(
        Database db,
        long rootId,
        string root,
        WalkerOptions walkerOptions,
        IndexVolumeContext? volumeContext,
        IReadOnlyDictionary<string, ExistingFileRow> existing,
        HashSet<string> seen,
        IndexRefreshMode mode,
        IndexRequest request,
        Action publish,
        Func<long> getFilesEnumerated,
        Action<long> addFilesEnumerated,
        Action<long> addFilesIndexed,
        Action<long> addFilesSkipped,
        Action<long> addFilesFailed,
        Action<long> addLinesIndexed,
        CancellationToken cancellationToken)
    {
        var parallelism = Math.Max(1, _searchOptions.MaxDegreeOfParallelism);
        var channelCapacity = Math.Max(1, parallelism * 2);
        var candidates = Channel.CreateBounded<IndexFileCandidate>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var results = Channel.CreateBounded<ExtractedFileResult>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCts.Token;
        var producer = ProduceRefreshCandidatesAsync(
            root,
            walkerOptions,
            volumeContext,
            existing,
            seen,
            mode,
            request,
            candidates.Writer,
            publish,
            addFilesEnumerated,
            addFilesSkipped,
            addFilesFailed,
            getFilesEnumerated,
            pipelineToken);
        var extractorTasks = Enumerable.Range(0, parallelism)
            .Select(_ => RunExtractorWorkerAsync(candidates.Reader, results.Writer, walkerOptions, pipelineToken))
            .ToArray();
        var resultCompletion = CompleteResultChannelWhenExtractorsFinishAsync(extractorTasks, results.Writer);
        var fileIdAllocator = new DbIdBlockAllocator(db, "files", FileIdAllocationBlockSize);
        var lineIdAllocator = new DbIdBlockAllocator(db, "lines", LineIdAllocationBlockSize);

        Exception? writeFailure = null;
        try
        {
            await foreach (var result in results.Reader.ReadAllAsync(pipelineToken).ConfigureAwait(false))
            {
                try
                {
                    var indexedLines = await WriteExtractedFileAsync(
                            db,
                            rootId,
                            result,
                            cacheLines: null,
                            cacheRemovedPaths: null,
                            cacheFileIds: null,
                            pipelineToken,
                            tombstoneReplacedIdentities: false,
                            fileIdAllocator,
                            lineIdAllocator)
                        .ConfigureAwait(false);
                    addLinesIndexed(indexedLines);
                    addFilesIndexed(1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Refresh failed for file {Path}.", result.Candidate.Path);
                    addFilesFailed(1);
                }

                publish();
            }

            await producer.ConfigureAwait(false);
            await resultCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writeFailure = ex;
            throw;
        }
        finally
        {
            if (writeFailure is not null)
            {
                pipelineCts.Cancel();
                candidates.Writer.TryComplete(writeFailure);
                results.Writer.TryComplete(writeFailure);
                await IgnorePipelineShutdownAsync(producer).ConfigureAwait(false);
                await IgnorePipelineShutdownAsync(resultCompletion).ConfigureAwait(false);
            }
        }
    }

    private async Task ProduceRefreshCandidatesAsync(
        string root,
        WalkerOptions walkerOptions,
        IndexVolumeContext? volumeContext,
        IReadOnlyDictionary<string, ExistingFileRow> existing,
        HashSet<string> seen,
        IndexRefreshMode mode,
        IndexRequest request,
        ChannelWriter<IndexFileCandidate> writer,
        Action publish,
        Action<long> addFilesEnumerated,
        Action<long> addFilesSkipped,
        Action<long> addFilesFailed,
        Func<long> getFilesEnumerated,
        CancellationToken cancellationToken)
    {
        async Task PauseIfNeededAsync()
        {
            if (request.Throttle is { } throttle)
                await throttle.PauseAfterFileAsync(getFilesEnumerated(), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var path in _walker.Enumerate(new[] { root }, walkerOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                addFilesEnumerated(1);

                var normalizedPath = IndexPath.NormalizeFile(path);
                seen.Add(normalizedPath);

                try
                {
                    if (!File.Exists(normalizedPath))
                    {
                        addFilesFailed(1);
                        publish();
                        await PauseIfNeededAsync().ConfigureAwait(false);
                        continue;
                    }

                    var info = new FileInfo(normalizedPath);
                    var existingFile = existing.TryGetValue(normalizedPath, out var row) ? row : null;
                    var extractor = _extractors.GetFor(normalizedPath);
                    if (mode != IndexRefreshMode.Full &&
                        existingFile is not null &&
                        IsUnchanged(existingFile, info, extractor))
                    {
                        addFilesSkipped(1);
                        publish();
                        await PauseIfNeededAsync().ConfigureAwait(false);
                        continue;
                    }

                    var identity = TryGetIndexedFileIdentity(volumeContext?.VolumeId, normalizedPath, lastObservedUsn: null);
                    await writer.WriteAsync(
                            new IndexFileCandidate(
                                normalizedPath,
                                info,
                                identity,
                                extractor,
                                existingFile?.Id ?? 0,
                                existingFile?.ExtractionAttemptCount ?? 0),
                            cancellationToken)
                        .ConfigureAwait(false);
                    publish();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Refresh failed for file {Path}.", path);
                    addFilesFailed(1);
                    publish();
                }

                await PauseIfNeededAsync().ConfigureAwait(false);
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private async Task RunExtractorWorkerAsync(
        ChannelReader<IndexFileCandidate> reader,
        ChannelWriter<ExtractedFileResult> writer,
        WalkerOptions walkerOptions,
        CancellationToken cancellationToken)
    {
        await foreach (var candidate in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = await ExtractFileAsync(candidate, walkerOptions, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CompleteResultChannelWhenExtractorsFinishAsync(
        IReadOnlyCollection<Task> extractorTasks,
        ChannelWriter<ExtractedFileResult> writer)
    {
        try
        {
            await Task.WhenAll(extractorTasks).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private static async Task IgnorePipelineShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
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

        var rootVolumeCurrent =
            rootRow is { VolumeId: not null } &&
            rootRow.VolumeId.Value == volumeId &&
            ((rootIdentity is null && rootRow.RootFileReferenceNumber is null) ||
             (rootIdentity is not null &&
              string.Equals(rootRow.RootFileReferenceNumber, rootIdentity.FileReferenceNumber, StringComparison.Ordinal) &&
              string.Equals(rootRow.RootParentFileReferenceNumber, rootIdentity.ParentFileReferenceNumber, StringComparison.Ordinal)));
        if (!rootVolumeCurrent)
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

    private async Task<bool> UpsertFileCoreAsync(
        Database db,
        long rootId,
        string root,
        string path,
        WalkerOptions indexingOptions,
        IndexVolumeContext? volumeContext,
        List<CachedIndexedLine>? cacheLines,
        List<string>? cacheRemovedPaths,
        List<long>? cacheFileIds,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return await IndexTables.DeleteFileAsync(db, rootId, path, cancellationToken).ConfigureAwait(false) > 0;
        }

        var info = new FileInfo(path);
        var identity = TryGetIndexedFileIdentity(volumeContext?.VolumeId, path, lastObservedUsn: null);
        await EnsureParentDirectoryIdentitiesAsync(db, rootId, root, path, volumeContext, cancellationToken)
            .ConfigureAwait(false);
        if (ShouldSkipSingleFile(root, info, indexingOptions))
        {
            var deleted = 0;
            if (identity is not null)
            {
                await IndexTables.DeleteFilesByIdentityAsync(
                    db,
                    identity.VolumeId,
                    identity.FileReferenceNumber,
                    cancellationToken).ConfigureAwait(false);
                deleted = 1;
            }
            else
            {
                deleted = await IndexTables.DeleteFileAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
            }

            return deleted > 0;
        }

        var extractor = _extractors.GetFor(path);
        var existingRow = await IndexTables.GetFileRowAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
        if (existingRow is not null && IsUnchanged(existingRow, info, extractor))
            return false;

        await IndexSingleFileAsync(
                db,
                rootId,
                path,
                info,
                identity,
                extractor,
                existingRow,
                indexingOptions,
                cacheLines,
                cacheRemovedPaths,
                cacheFileIds,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
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

    private async Task<ExtractedFileResult> ExtractFileAsync(
        IndexFileCandidate candidate,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        var extractor = candidate.Extractor;
        if (extractor is null)
        {
            var fallback = await TryExtractWithWindowsIFilterAsync(
                    candidate.Path,
                    primaryExtractor: null,
                    primaryFailure: null,
                    primaryLineCount: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            return fallback is not null
                ? ExtractedFileResult.Success(candidate, string.Empty, string.Empty, fallback.ExtractorId, fallback.ExtractorVersion, fallback.Lines, fallback.Issues, recordFallbackAttempt: true)
                : ExtractedFileResult.Skipped(candidate, "No extractor registered.");
        }

        var extractorId = GetExtractorId(extractor);
        var extractorVersion = GetExtractorVersion(extractor);
        var issueSink = new ListExtractionIssueSink();
        var lines = new List<TextLine>();
        try
        {
            var requiresContextualExtraction = extractor is IContextualTextExtractor && options.EnableOcr;
            if (!requiresContextualExtraction && _outOfProcessExtraction?.ShouldUse(extractor) == true)
            {
                var result = await _outOfProcessExtraction.ExtractAsync(candidate.Path, extractor, cancellationToken).ConfigureAwait(false);
                foreach (var issue in result.Issues)
                    issueSink.Report(issue);
                lines.AddRange(result.Lines);
            }
            else
            {
                var context = new TextExtractionContext(options.EnableOcr);
                var extracted = options.EnableOcr && extractor is IContextualDiagnosticTextExtractor contextualDiagnosticExtractor
                    ? contextualDiagnosticExtractor.ExtractAsync(candidate.Path, context, issueSink, cancellationToken)
                    : extractor is IDiagnosticTextExtractor diagnosticExtractor
                    ? diagnosticExtractor.ExtractAsync(candidate.Path, issueSink, cancellationToken)
                    : extractor.ExtractWithContextAsync(candidate.Path, context, cancellationToken);

                await foreach (var line in extracted.ConfigureAwait(false))
                    lines.Add(line);
            }

            if (lines.Count == 0)
            {
                var fallback = await TryExtractWithWindowsIFilterAsync(
                        candidate.Path,
                        extractor,
                        primaryFailure: null,
                        primaryLineCount: lines.Count,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallback is not null)
                    return ExtractedFileResult.Success(candidate, extractorId, extractorVersion, fallback.ExtractorId, fallback.ExtractorVersion, fallback.Lines, fallback.Issues, recordFallbackAttempt: true);
            }

            return ExtractedFileResult.Success(candidate, extractorId, extractorVersion, extractorId, extractorVersion, lines, issueSink.Issues, recordFallbackAttempt: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fallback = await TryExtractWithWindowsIFilterAsync(
                    candidate.Path,
                    extractor,
                    primaryFailure: ex,
                    primaryLineCount: lines.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fallback is not null)
                return ExtractedFileResult.Success(candidate, extractorId, extractorVersion, fallback.ExtractorId, fallback.ExtractorVersion, fallback.Lines, fallback.Issues, recordFallbackAttempt: true);

            var error = ex is ExtractorHostException hostException
                ? $"{hostException.Code}: {hostException.Message}"
                : ex.Message;
            return ExtractedFileResult.Failed(candidate, extractorId, extractorVersion, error);
        }
    }

    private async Task<long> WriteExtractedFileAsync(
        Database db,
        long rootId,
        ExtractedFileResult result,
        List<CachedIndexedLine>? cacheLines,
        List<string>? cacheRemovedPaths,
        List<long>? cacheFileIds,
        CancellationToken cancellationToken,
        bool tombstoneReplacedIdentities = true,
        DbIdBlockAllocator? fileIdAllocator = null,
        DbIdBlockAllocator? lineIdAllocator = null)
    {
        var candidate = result.Candidate;
        var finalStatus = ToFileStatus(result.Status);
        var initialMetadata = BuildFinalExtractionMetadata(result, candidate.ExistingExtractionAttemptCount);
        var fileId = fileIdAllocator is null
            ? (long?)null
            : await fileIdAllocator.NextAsync(cancellationToken).ConfigureAwait(false);
        var insertedFile = await IndexTables.InsertFileRowAsync(
            db,
            rootId,
            candidate.Path,
            candidate.Info,
            finalStatus,
            result.Error,
            candidate.Identity,
            initialMetadata.ExtractorId,
            initialMetadata.ExtractorVersion,
            initialMetadata.AttemptCount,
            initialMetadata.LastAttemptUtcTicks,
            cancellationToken,
            tombstoneReplacedIdentities,
            fileId).ConfigureAwait(false);
        var insertedFileId = insertedFile.Id;
        cacheRemovedPaths?.AddRange(insertedFile.ReplacedPaths);
        cacheFileIds?.Add(insertedFileId);

        long linesIndexed = 0;
        if (result.Status == ExtractedFileStatus.Ok)
        {
            await IndexTables.ReplaceExtractionIssuesAsync(db, insertedFileId, result.Issues, deleteExisting: false, cancellationToken).ConfigureAwait(false);
            linesIndexed = await InsertLinesAsync(
                    db,
                    rootId,
                    insertedFileId,
                    candidate.Info,
                    result.Lines,
                    result.LineExtractorId,
                    result.LineExtractorVersion,
                    cacheLines,
                    lineIdAllocator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidate.ExistingFileId > 0 && candidate.ExistingFileId != insertedFileId)
        {
            await IndexTables.DeleteFileVersionAsync(db, candidate.ExistingFileId, cancellationToken)
                .ConfigureAwait(false);
        }

        return linesIndexed;
    }

    private static string ToFileStatus(ExtractedFileStatus status) =>
        status switch
        {
            ExtractedFileStatus.Ok => FileStatus.Ok,
            ExtractedFileStatus.Skipped => FileStatus.Skipped,
            ExtractedFileStatus.Error => FileStatus.Error,
            _ => FileStatus.Error,
        };

    private static ExtractionMetadata BuildFinalExtractionMetadata(ExtractedFileResult result, long existingAttemptCount)
    {
        var currentAttemptCount = 0L;
        var extractorId = result.PrimaryExtractorId;
        var extractorVersion = result.PrimaryExtractorVersion;

        if (!string.IsNullOrEmpty(result.PrimaryExtractorId))
            currentAttemptCount++;

        if (result.RecordFallbackAttempt)
        {
            currentAttemptCount++;
            extractorId = result.LineExtractorId;
            extractorVersion = result.LineExtractorVersion;
        }

        var attemptCount = existingAttemptCount + currentAttemptCount;
        return new ExtractionMetadata(
            extractorId,
            extractorVersion,
            attemptCount,
            currentAttemptCount > 0 ? DateTime.UtcNow.Ticks : 0);
    }

    private async Task<long> IndexSingleFileAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        IndexedFileIdentity? identity,
        ITextExtractor? extractor,
        ExistingFileRow? existingRow,
        WalkerOptions options,
        List<CachedIndexedLine>? cacheLines,
        List<string>? cacheRemovedPaths,
        List<long>? cacheFileIds,
        CancellationToken cancellationToken)
    {
        var candidate = new IndexFileCandidate(
            path,
            info,
            identity,
            extractor,
            existingRow?.Id ?? 0,
            existingRow?.ExtractionAttemptCount ?? 0);
        var result = await ExtractFileAsync(candidate, options, cancellationToken).ConfigureAwait(false);
        return await WriteExtractedFileAsync(db, rootId, result, cacheLines, cacheRemovedPaths, cacheFileIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long?> TryIndexWithWindowsIFilterAsync(
        Database db,
        long rootId,
        long fileId,
        string path,
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount,
        CancellationToken cancellationToken)
    {
        var result = await TryExtractWithWindowsIFilterAsync(
                path,
                primaryExtractor,
                primaryFailure,
                primaryLineCount,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return null;

        await IndexTables.DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await IndexTables.RecordExtractionAttemptAsync(
                db,
                fileId,
                result.ExtractorId,
                result.ExtractorVersion,
                cancellationToken)
            .ConfigureAwait(false);
        await IndexTables.ReplaceExtractionIssuesAsync(db, fileId, result.Issues, deleteExisting: true, cancellationToken).ConfigureAwait(false);
        if (result.Lines.Count == 0)
        {
            await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var linesIndexed = await InsertLinesAsync(
                db,
                rootId,
                fileId,
                new FileInfo(path),
                result.Lines,
                result.ExtractorId,
                result.ExtractorVersion,
                cacheLines: null,
                lineIdAllocator: null,
                cancellationToken)
            .ConfigureAwait(false);
        await IndexTables.SetFileStatusAsync(db, fileId, FileStatus.Ok, null, cancellationToken).ConfigureAwait(false);
        return linesIndexed;
    }

    private async Task<FallbackExtractionResult?> TryExtractWithWindowsIFilterAsync(
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

        var issues = new List<ExtractionIssue>(result.Issues.Count + 1)
        {
            new(
                MemberPath: null,
                Code: "ifilter_fallback_used",
                Message: FormatIFilterFallbackMessage(primaryExtractor, primaryFailure, primaryLineCount),
                Severity: "info"),
        };
        issues.AddRange(result.Issues);
        return new FallbackExtractionResult(
            fallback.ExtractorId,
            fallback.ExtractorVersion,
            result.Lines,
            issues);
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
        long rootId,
        long fileId,
        FileInfo info,
        IReadOnlyList<TextLine> lines,
        string extractorId,
        string extractorVersion,
        List<CachedIndexedLine>? cacheLines,
        DbIdBlockAllocator? lineIdAllocator,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return 0;

        var nextLineId = lineIdAllocator is null
            ? await IndexTables.AllocateIdsAsync(db, "lines", lines.Count, cancellationToken).ConfigureAwait(false)
            : await lineIdAllocator.NextRangeAsync(lines.Count, cancellationToken).ConfigureAwait(false);
        var lineBatch = db.PrepareInsertBatch("lines", LineInsertBatchSize);
        var fileTrigrams = new HashSet<string>(StringComparer.Ordinal);
        var fileTrigramBatch = db.PrepareInsertBatch("file_trigrams", FileTrigramInsertBatchSize);
        long linesIndexed = 0;
        foreach (var line in lines)
        {
            var lineId = nextLineId++;
            AddLineToBatch(
                lineBatch,
                lineId,
                fileId,
                line);
            QueryTrigramTerms.AddLineTrigrams(line.Content, fileTrigrams);
            cacheLines?.Add(CreateCachedLine(lineId, fileId, info, line, extractorId, extractorVersion));
            linesIndexed++;
            if (lineBatch.Count >= LineInsertBatchSize)
                await FlushBatchAsync(lineBatch, cancellationToken).ConfigureAwait(false);
        }

        foreach (var trigram in fileTrigrams)
        {
            AddFileTrigramToBatch(fileTrigramBatch, rootId, fileId, trigram);
            if (fileTrigramBatch.Count >= FileTrigramInsertBatchSize)
                await FlushBatchAsync(fileTrigramBatch, cancellationToken).ConfigureAwait(false);
        }

        await FlushBatchAsync(lineBatch, cancellationToken).ConfigureAwait(false);
        await FlushBatchAsync(fileTrigramBatch, cancellationToken).ConfigureAwait(false);
        return linesIndexed;
    }

    private static CachedIndexedLine CreateCachedLine(
        long lineId,
        long fileId,
        FileInfo info,
        TextLine line,
        string extractorId,
        string extractorVersion)
    {
        var fileName = info.Name;
        var extension = info.Extension.ToLowerInvariant();
        var locator = SourceLocator.FromAnchor(line.Anchor, line.Number);
        return new CachedIndexedLine(
            lineId,
            fileId,
            new IndexedLine(
                info.FullName,
                fileName,
                extension,
                info.Length,
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks,
                FileStatus.Ok,
                extractorId,
                FileTypeCategory.ForExtension(extension),
                line.Number,
                line.Content,
                line.Anchor,
                lineId,
                ContentUnitKind.Text,
                locator,
                string.Empty,
                string.Empty,
                extractorId,
                extractorVersion));
    }

    private static void AddLineToBatch(InsertBatch batch, long lineId, long fileId, TextLine line)
    {
        batch.AddRow(
            DbValue.FromInteger(lineId),
            DbValue.FromInteger(fileId),
            DbValue.FromInteger(lineId),
            DbValue.FromInteger(line.Number),
            DbValue.FromText(line.Content),
            IndexTables.SerializeAnchor(line.Anchor));
    }

    private static void AddFileTrigramToBatch(InsertBatch batch, long rootId, long fileId, string trigram)
    {
        batch.AddRow(
            DbValue.FromInteger(rootId),
            DbValue.FromInteger(QueryTrigramTerms.EncodeTrigram(trigram)),
            DbValue.FromInteger(fileId));
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

        public async ValueTask<long> NextRangeAsync(long count, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

            if (count > _remaining)
            {
                var allocationSize = Math.Max(_blockSize, count);
                _nextId = await IndexTables.AllocateIdsAsync(_db, _sequenceName, allocationSize, cancellationToken).ConfigureAwait(false);
                _remaining = allocationSize;
            }

            var firstId = _nextId;
            _nextId += count;
            _remaining -= count;
            return firstId;
        }
    }

    private static async Task<IndexedLocationInfo?> GetLocationInfoAsync(
        DbExec db,
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
        IndexSearchTimings? timings,
        out Hit hit)
    {
        if (timings is null)
            return TryCreateHitCore(root, line, query, options, hitsByPath, fileFilterVerdicts, highlightBuffer, out hit);

        var recheckStart = Stopwatch.GetTimestamp();
        var created = TryCreateHitCore(root, line, query, options, hitsByPath, fileFilterVerdicts, highlightBuffer, out hit);
        timings.RecheckTicks += Stopwatch.GetTimestamp() - recheckStart;
        timings.LinesExamined++;
        if (created)
            timings.HitCount++;

        return created;
    }

    private bool TryCreateHitCore(
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
                    root, line.Path, line.FileName, line.Extension, line.SizeBytes, line.ModifiedUtcTicks, options) &&
                (query is not UnifiedQuery unified ||
                 unified.MatchesFile(
                     root,
                     line.Path,
                     line.FileName,
                     line.Extension,
                     line.SizeBytes,
                     line.CreatedUtcTicks,
                     line.ModifiedUtcTicks,
                     line.Status,
                     line.ExtractorId,
                     line.FileTypeCategory));
            fileFilterVerdicts[line.Path] = fileAllowed;
        }

        if (!fileAllowed)
            return false;

        hitsByPath.TryGetValue(line.Path, out var hitsForFile);
        if (hitsForFile >= _searchOptions.MaxHitsPerFile)
            return false;

        highlightBuffer.Clear();
        if (!query.TryCollectHighlights(line.Content, highlightBuffer))
            return false;

        hitsByPath[line.Path] = hitsForFile + 1;
        hit = new Hit(
            line.Path,
            line.LineNumber,
            line.Content,
            highlightBuffer.ToArray(),
            Route: HitRoute.Indexed,
            Anchor: line.Anchor,
            ContentUnitId: line.ContentUnitId,
            Locator: line.Locator);
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

    private static string GetRelativePath(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private static double RecencyScore(long modifiedUtcTicks)
    {
        if (modifiedUtcTicks <= 0)
            return 0;

        var age = DateTime.UtcNow - new DateTime(modifiedUtcTicks, DateTimeKind.Utc);
        if (age <= TimeSpan.FromDays(7))
            return 50;
        if (age <= TimeSpan.FromDays(30))
            return 25;
        if (age <= TimeSpan.FromDays(365))
            return 10;

        return 0;
    }

    private sealed class CurrentOkFileIdCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, CurrentOkFileIdCacheEntry> _roots = new();

        public async Task<HashSet<long>> GetOrLoadAsync(
            DbExec db,
            long rootId,
            long databaseGeneration,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached.FileIds;
                }
            }

            var fileIds = (await IndexTables.ReadCurrentOkFileIdsForRootAsync(db, rootId, cancellationToken)
                    .ConfigureAwait(false))
                .ToHashSet();
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached.FileIds;
                }

                _roots[rootId] = new CurrentOkFileIdCacheEntry(databaseGeneration, fileIds);
                return fileIds;
            }
        }
    }

    private sealed record CurrentOkFileIdCacheEntry(
        long DatabaseGeneration,
        HashSet<long> FileIds);

    private sealed class LineCandidateCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, LineCandidateCacheEntry> _roots = new();

        public bool TryGet(long rootId, long databaseGeneration, long lineId, out IndexedLine line)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration &&
                    cached.Lines.TryGetValue(lineId, out line!))
                {
                    return true;
                }
            }

            line = null!;
            return false;
        }

        public void Add(long rootId, long databaseGeneration, long lineId, IndexedLine line)
        {
            lock (_gate)
            {
                if (!_roots.TryGetValue(rootId, out var cached) ||
                    cached.DatabaseGeneration != databaseGeneration)
                {
                    cached = new LineCandidateCacheEntry(databaseGeneration, new Dictionary<long, IndexedLine>());
                    _roots[rootId] = cached;
                }

                if (cached.Lines.Count >= MaxCachedCandidateLinesPerRoot)
                    cached.Lines.Clear();

                cached.Lines[lineId] = line;
            }
        }
    }

    private sealed record LineCandidateCacheEntry(
        long DatabaseGeneration,
        Dictionary<long, IndexedLine> Lines);

    private sealed class TrigramPostingCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, TrigramPostingRootCache> _roots = new();

        public async Task<List<long>> GetOrLoadAsync(
            DbExec db,
            long rootId,
            long databaseGeneration,
            string trigram,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration &&
                    cached.Postings.TryGetValue(trigram, out var posting))
                {
                    return posting;
                }
            }

            var loaded = await IndexTables.ReadFileIdsForTrigramAsync(db, rootId, trigram, cancellationToken)
                .ConfigureAwait(false);
            if (loaded.Count > MaxCachedTrigramPostingIds)
                return loaded;

            lock (_gate)
            {
                if (!_roots.TryGetValue(rootId, out var cached) ||
                    cached.DatabaseGeneration != databaseGeneration)
                {
                    cached = new TrigramPostingRootCache(
                        databaseGeneration,
                        new Dictionary<string, List<long>>(StringComparer.Ordinal));
                    _roots[rootId] = cached;
                }

                if (cached.Postings.Count >= MaxCachedTrigramPostingsPerRoot)
                    cached.Postings.Clear();

                cached.Postings[trigram] = loaded;
                return loaded;
            }
        }
    }

    private sealed record TrigramPostingRootCache(
        long DatabaseGeneration,
        Dictionary<string, List<long>> Postings);

    private sealed class MetadataNameCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, MetadataNameIndex> _roots = new();

        public async Task<MetadataNameIndex> GetOrLoadAsync(
            DbExec db,
            long rootId,
            long databaseGeneration,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached;
                }
            }

            var files = new List<IndexedFileMetadata>();
            await foreach (var file in IndexTables.ReadFileMetadataAsync(db, rootId, cancellationToken)
                               .ConfigureAwait(false))
            {
                files.Add(file);
            }

            var loaded = MetadataNameIndex.Create(databaseGeneration, files);
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached;
                }

                _roots[rootId] = loaded;
                return loaded;
            }
        }

    }

    private sealed class MetadataNameIndex
    {
        private static readonly IndexedFileMetadata[] s_emptyFiles = [];

        private readonly IndexedFileMetadata[] _files;
        private readonly Dictionary<string, int[]> _tokenToFileIndexes;
        private readonly Dictionary<long, int[]> _fileNameTrigramToFileIndexes;

        private MetadataNameIndex(
            long databaseGeneration,
            IndexedFileMetadata[] files,
            Dictionary<string, int[]> tokenToFileIndexes,
            Dictionary<long, int[]> fileNameTrigramToFileIndexes)
        {
            DatabaseGeneration = databaseGeneration;
            _files = files;
            _tokenToFileIndexes = tokenToFileIndexes;
            _fileNameTrigramToFileIndexes = fileNameTrigramToFileIndexes;
        }

        public long DatabaseGeneration { get; }

        public static MetadataNameIndex Create(long databaseGeneration, List<IndexedFileMetadata> files)
        {
            if (files.Count == 0)
            {
                return new MetadataNameIndex(
                    databaseGeneration,
                    s_emptyFiles,
                    new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<long, int[]>());
            }

            var fileArray = files.ToArray();
            var buckets = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var trigramBuckets = new Dictionary<long, List<int>>();
            var fileNameTrigrams = new HashSet<long>();
            for (var index = 0; index < fileArray.Length; index++)
            {
                var file = fileArray[index];
                foreach (var token in BuildIndexTokens(file))
                {
                    if (!buckets.TryGetValue(token, out var bucket))
                    {
                        bucket = new List<int>();
                        buckets[token] = bucket;
                    }

                    bucket.Add(index);
                }

                fileNameTrigrams.Clear();
                AddFileNameTrigrams(file.FileName, fileNameTrigrams);
                foreach (var trigram in fileNameTrigrams)
                {
                    if (!trigramBuckets.TryGetValue(trigram, out var bucket))
                    {
                        bucket = [];
                        trigramBuckets[trigram] = bucket;
                    }

                    bucket.Add(index);
                }
            }

            var tokenToFileIndexes = new Dictionary<string, int[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (token, bucket) in buckets)
                tokenToFileIndexes[token] = bucket.ToArray();

            var fileNameTrigramToFileIndexes = new Dictionary<long, int[]>(trigramBuckets.Count);
            foreach (var (trigram, bucket) in trigramBuckets)
                fileNameTrigramToFileIndexes[trigram] = bucket.ToArray();

            return new MetadataNameIndex(
                databaseGeneration,
                fileArray,
                tokenToFileIndexes,
                fileNameTrigramToFileIndexes);
        }

        private static HashSet<string> BuildIndexTokens(IndexedFileMetadata file)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTokenParts(tokens, file.FileName, includePrefixes: true);
            AddTokenParts(tokens, Path.GetFileNameWithoutExtension(file.FileName), includePrefixes: true);
            AddTokenParts(tokens, file.Extension.TrimStart('.'), includePrefixes: false);
            AddTokenParts(tokens, file.FileTypeCategory, includePrefixes: false);

            foreach (var segment in file.DirectoryPath.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                AddTokenParts(tokens, segment, includePrefixes: false);
                AddTokenParts(tokens, Path.GetFileNameWithoutExtension(segment), includePrefixes: false);
            }

            return tokens;
        }

        private static void AddTokenParts(HashSet<string> tokens, string value, bool includePrefixes)
        {
            var start = -1;
            for (var i = 0; i <= value.Length; i++)
            {
                var isTokenChar = i < value.Length && (char.IsLetterOrDigit(value[i]) || value[i] == '_');
                if (isTokenChar)
                {
                    if (start < 0)
                        start = i;
                    continue;
                }

                if (start < 0)
                    continue;

                AddToken(tokens, value[start..i], includePrefixes);

                start = -1;
            }
        }

        private static void AddToken(HashSet<string> tokens, string value, bool includePrefixes)
        {
            var token = value.ToLowerInvariant();
            if (token.Length == 0)
                return;

            AddSingleToken(tokens, token, includePrefixes);
            if (!token.Contains('_', StringComparison.Ordinal))
                return;

            foreach (var part in token.Split('_', StringSplitOptions.RemoveEmptyEntries))
                AddSingleToken(tokens, part, includePrefixes);
        }

        private static void AddSingleToken(HashSet<string> tokens, string token, bool includePrefixes)
        {
            tokens.Add(token);
            if (!includePrefixes)
                return;

            var max = Math.Min(32, token.Length);
            for (var length = 2; length < max; length++)
                tokens.Add(token[..length]);
        }

        private static void AddFileNameTrigrams(string fileName, HashSet<long> trigrams)
        {
            var normalized = fileName.ToLowerInvariant();
            for (var index = 0; index <= normalized.Length - QueryTrigramTerms.TrigramLength; index++)
            {
                trigrams.Add(
                    ((long)normalized[index] << 32) |
                    ((long)normalized[index + 1] << 16) |
                    normalized[index + 2]);
            }
        }

        public IEnumerable<IndexedFileMetadata> FindFileNameCandidates(
            IReadOnlyList<string> terms,
            bool requireAllTerms)
        {
            if (terms.Count == 0)
                return _files;

            return requireAllTerms
                ? FindAllFileNameTermCandidates(terms)
                : FindAnyFileNameTermCandidates(terms);
        }

        private IEnumerable<IndexedFileMetadata> FindAllFileNameTermCandidates(IReadOnlyList<string> terms)
        {
            HashSet<int>? candidates = null;
            foreach (var term in terms)
            {
                var termCandidates = FindFileNameTermCandidateIndexes(term);
                if (termCandidates is null)
                    continue;
                if (termCandidates.Count == 0)
                    return s_emptyFiles;

                if (candidates is null)
                    candidates = termCandidates;
                else
                    candidates.IntersectWith(termCandidates);

                if (candidates.Count == 0)
                    return s_emptyFiles;
            }

            return candidates is null ? _files : Materialize(candidates);
        }

        private IEnumerable<IndexedFileMetadata> FindAnyFileNameTermCandidates(IReadOnlyList<string> terms)
        {
            var candidates = new HashSet<int>();
            foreach (var term in terms)
            {
                var termCandidates = FindFileNameTermCandidateIndexes(term);
                if (termCandidates is null)
                    return _files;

                candidates.UnionWith(termCandidates);
            }

            return candidates.Count == 0 ? s_emptyFiles : Materialize(candidates);
        }

        private HashSet<int>? FindFileNameTermCandidateIndexes(string term)
        {
            var normalized = term.ToLowerInvariant();
            if (normalized.Length < QueryTrigramTerms.TrigramLength)
                return null;

            var seenTrigrams = new HashSet<long>();
            var buckets = new List<int[]>();
            for (var index = 0; index <= normalized.Length - QueryTrigramTerms.TrigramLength; index++)
            {
                var trigram =
                    ((long)normalized[index] << 32) |
                    ((long)normalized[index + 1] << 16) |
                    normalized[index + 2];
                if (!seenTrigrams.Add(trigram))
                    continue;

                if (!_fileNameTrigramToFileIndexes.TryGetValue(trigram, out var bucket))
                    return [];

                buckets.Add(bucket);
            }

            buckets.Sort(static (left, right) => left.Length.CompareTo(right.Length));
            var candidates = buckets[0].ToHashSet();
            for (var index = 1; index < buckets.Count; index++)
            {
                candidates.IntersectWith(buckets[index]);
                if (candidates.Count == 0)
                    break;
            }

            return candidates;
        }

        public IEnumerable<IndexedFileMetadata> FindCandidates(
            IReadOnlyList<string> tokens,
            bool requireAllTokens)
        {
            if (tokens.Count == 0)
                return _files;

            return requireAllTokens
                ? FindAllTokenCandidates(tokens)
                : FindAnyTokenCandidates(tokens);
        }

        private IEnumerable<IndexedFileMetadata> FindAllTokenCandidates(IReadOnlyList<string> tokens)
        {
            var buckets = new List<int[]>(tokens.Count);
            foreach (var token in tokens)
            {
                if (!_tokenToFileIndexes.TryGetValue(token, out var bucket))
                    return s_emptyFiles;

                buckets.Add(bucket);
            }

            buckets.Sort(static (left, right) => left.Length.CompareTo(right.Length));
            var candidates = buckets[0].ToHashSet();
            for (var i = 1; i < buckets.Count; i++)
            {
                candidates.IntersectWith(buckets[i]);
                if (candidates.Count == 0)
                    return s_emptyFiles;
            }

            return Materialize(candidates);
        }

        private IEnumerable<IndexedFileMetadata> FindAnyTokenCandidates(IReadOnlyList<string> tokens)
        {
            var candidates = new HashSet<int>();
            foreach (var token in tokens)
            {
                if (_tokenToFileIndexes.TryGetValue(token, out var bucket))
                    candidates.UnionWith(bucket);
            }

            return candidates.Count == 0 ? s_emptyFiles : Materialize(candidates);
        }

        private List<IndexedFileMetadata> Materialize(IEnumerable<int> indexes)
        {
            var files = new List<IndexedFileMetadata>();
            foreach (var index in indexes)
                files.Add(_files[index]);

            return files;
        }
    }

    private sealed class ContentTrigramCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, ContentTrigramIndex> _roots = new();

        public bool TryGetFresh(long rootId, long databaseGeneration, out ContentTrigramIndex? index, out bool hasStale)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached))
                {
                    hasStale = cached.DatabaseGeneration != databaseGeneration;
                    index = hasStale ? null : cached;
                    return !hasStale;
                }
            }

            index = null;
            hasStale = false;
            return false;
        }

        public async Task<ContentTrigramIndex> GetOrLoadAsync(
            DbExec db,
            long rootId,
            long databaseGeneration,
            CancellationToken cancellationToken)
        {
            ContentTrigramIndex? stale = null;
            long staleMaxFileId = 0;
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached;
                }

                if (cached is not null)
                {
                    stale = cached;
                    staleMaxFileId = cached.MaxFileId;
                }
            }

            if (stale is not null)
            {
                var changes = await LoadChangesAsync(db, rootId, staleMaxFileId, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_roots.TryGetValue(rootId, out var cached) &&
                        ReferenceEquals(cached, stale) &&
                        cached.MaxFileId == staleMaxFileId)
                    {
                        cached.ApplyFileChanges(databaseGeneration, changes);
                        return cached;
                    }

                    if (_roots.TryGetValue(rootId, out var refreshed) &&
                        refreshed.DatabaseGeneration == databaseGeneration)
                    {
                        return refreshed;
                    }
                }
            }

            var lineIdsByTrigram = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var linesById = new Dictionary<long, IndexedLine>();
            await foreach (var cachedLine in IndexTables.ReadCachedLinesAsync(db, rootId, cancellationToken)
                               .ConfigureAwait(false))
            {
                linesById[cachedLine.Id] = cachedLine.Line;
                foreach (var trigram in QueryTrigramTerms.BuildLineTrigrams(cachedLine.Line.Content))
                {
                    if (!lineIdsByTrigram.TryGetValue(trigram, out var lineIds))
                    {
                        lineIds = new List<long>();
                        lineIdsByTrigram[trigram] = lineIds;
                    }

                    lineIds.Add(cachedLine.Id);
                }
            }

            var maxFileId = await IndexTables.ReadMaxFileIdForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
            var loaded = ContentTrigramIndex.Create(databaseGeneration, maxFileId, lineIdsByTrigram, linesById);
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration)
                {
                    return cached;
                }

                _roots[rootId] = loaded;
                return loaded;
            }
        }

        private static async Task<List<ContentFileChange>> LoadChangesAsync(
            DbExec db,
            long rootId,
            long afterFileId,
            CancellationToken cancellationToken)
        {
            var rows = new List<FileChangeRow>();
            await foreach (var row in IndexTables.ReadFileChangesAfterIdAsync(db, rootId, afterFileId, cancellationToken)
                               .ConfigureAwait(false))
            {
                rows.Add(row);
            }

            var changes = new List<ContentFileChange>(rows.Count);
            foreach (var row in rows)
            {
                var lines = new List<CachedIndexedLine>();
                if (string.Equals(row.Status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase))
                {
                    await foreach (var line in IndexTables.ReadCachedLinesForFileAsync(db, row.Id, cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        lines.Add(line);
                    }
                }

                changes.Add(new ContentFileChange(row.Id, row.Path, row.Status, lines));
            }

            return changes;
        }

        public void ApplyFileUpsert(
            long rootId,
            long databaseGeneration,
            string path,
            IReadOnlyList<string> removedPaths,
            long maxFileId,
            List<CachedIndexedLine> lines)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration - 1)
                {
                    cached.ApplyFileUpsert(databaseGeneration, path, removedPaths, maxFileId, lines);
                }
            }
        }

        public void AdvanceGeneration(long rootId, long databaseGeneration)
        {
            lock (_gate)
            {
                if (_roots.TryGetValue(rootId, out var cached) &&
                    cached.DatabaseGeneration == databaseGeneration - 1)
                {
                    cached.AdvanceGeneration(databaseGeneration);
                }
            }
        }
    }

    private sealed record ContentFileChange(
        long FileId,
        string Path,
        string Status,
        List<CachedIndexedLine> Lines);

    private sealed class ContentTrigramIndex
    {
        private static readonly IReadOnlyList<long> s_emptyLineIds = [];
        private static readonly Comparison<IndexedLine> s_lineComparison = static (left, right) =>
        {
            var path = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            return path != 0 ? path : left.LineNumber.CompareTo(right.LineNumber);
        };

        private readonly object _gate = new();
        private readonly Dictionary<string, long[]> _lineIdsByTrigram;
        private readonly Dictionary<long, IndexedLine> _linesById;
        private readonly Dictionary<string, List<long>> _lineIdsByPath;
        private readonly Dictionary<long, string[]> _trigramsByLineId;
        private readonly List<IndexedLine> _allLines;

        private ContentTrigramIndex(
            long databaseGeneration,
            long maxFileId,
            Dictionary<string, long[]> lineIdsByTrigram,
            Dictionary<long, IndexedLine> linesById,
            Dictionary<string, List<long>> lineIdsByPath,
            Dictionary<long, string[]> trigramsByLineId,
            List<IndexedLine> allLines)
        {
            DatabaseGeneration = databaseGeneration;
            MaxFileId = maxFileId;
            _lineIdsByTrigram = lineIdsByTrigram;
            _linesById = linesById;
            _lineIdsByPath = lineIdsByPath;
            _trigramsByLineId = trigramsByLineId;
            _allLines = allLines;
        }

        public long DatabaseGeneration { get; private set; }

        public long MaxFileId { get; private set; }

        public static ContentTrigramIndex Create(
            long databaseGeneration,
            long maxFileId,
            Dictionary<string, List<long>> lineIdsByTrigram,
            Dictionary<long, IndexedLine> linesById)
        {
            var compacted = new Dictionary<string, long[]>(lineIdsByTrigram.Count, StringComparer.Ordinal);
            foreach (var (trigram, lineIds) in lineIdsByTrigram)
            {
                lineIds.Sort();
                compacted[trigram] = lineIds.ToArray();
            }

            var lineIdsByPath = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var trigramsByLineId = new Dictionary<long, string[]>();
            foreach (var (lineId, line) in linesById)
            {
                if (!lineIdsByPath.TryGetValue(line.Path, out var pathLineIds))
                {
                    pathLineIds = new List<long>();
                    lineIdsByPath[line.Path] = pathLineIds;
                }

                pathLineIds.Add(lineId);
                trigramsByLineId[lineId] = QueryTrigramTerms.BuildLineTrigrams(line.Content).ToArray();
            }

            var allLines = linesById.Values.ToList();
            allLines.Sort(s_lineComparison);

            return new ContentTrigramIndex(databaseGeneration, maxFileId, compacted, linesById, lineIdsByPath, trigramsByLineId, allLines);
        }

        public IReadOnlyList<long> FindCandidates(IReadOnlyList<string> trigrams)
        {
            if (trigrams.Count == 0)
                return s_emptyLineIds;

            lock (_gate)
            {
                var buckets = new List<long[]>(trigrams.Count);
                foreach (var trigram in trigrams)
                {
                    if (!_lineIdsByTrigram.TryGetValue(trigram, out var bucket))
                        return s_emptyLineIds;

                    buckets.Add(bucket);
                }

                buckets.Sort(static (left, right) => left.Length.CompareTo(right.Length));
                var candidates = new HashSet<long>(buckets[0]);
                for (var i = 1; i < buckets.Count; i++)
                {
                    candidates.IntersectWith(buckets[i]);
                    if (candidates.Count == 0)
                        return s_emptyLineIds;
                }

                var result = candidates.ToList();
                result.Sort();
                return result;
            }
        }

        public List<IndexedLine> GetLines(List<long> lineIds)
        {
            if (lineIds.Count == 0)
                return [];

            lock (_gate)
            {
                var lines = new List<IndexedLine>(lineIds.Count);
                foreach (var lineId in lineIds)
                {
                    if (_linesById.TryGetValue(lineId, out var line))
                        lines.Add(line);
                }

                lines.Sort(s_lineComparison);
                return lines;
            }
        }

        public IndexedLine[] GetAllLines()
        {
            lock (_gate)
                return _allLines.ToArray();
        }

        public void ApplyFileUpsert(
            long databaseGeneration,
            string path,
            IReadOnlyList<string> removedPaths,
            long maxFileId,
            List<CachedIndexedLine> lines)
        {
            lock (_gate)
            {
                RemovePath(path);
                foreach (var removedPath in removedPaths)
                    RemovePath(removedPath);
                foreach (var line in lines)
                    AddLine(line);

                DatabaseGeneration = databaseGeneration;
                MaxFileId = Math.Max(MaxFileId, maxFileId);
            }
        }

        public void ApplyFileChanges(long databaseGeneration, List<ContentFileChange> changes)
        {
            lock (_gate)
            {
                foreach (var change in changes)
                {
                    RemovePath(change.Path);
                    if (string.Equals(change.Status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var line in change.Lines)
                            AddLine(line);
                    }

                    MaxFileId = Math.Max(MaxFileId, change.FileId);
                }

                DatabaseGeneration = databaseGeneration;
            }
        }

        public void AdvanceGeneration(long databaseGeneration)
        {
            lock (_gate)
                DatabaseGeneration = databaseGeneration;
        }

        private void RemovePath(string path)
        {
            if (_lineIdsByPath.Remove(path, out var lineIds))
            {
                foreach (var lineId in lineIds)
                {
                    _linesById.Remove(lineId);
                    if (!_trigramsByLineId.Remove(lineId, out var trigrams))
                        continue;

                    foreach (var trigram in trigrams)
                        RemoveLineId(trigram, lineId);
                }
            }

            _allLines.RemoveAll(line => string.Equals(line.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        private void AddLine(CachedIndexedLine cachedLine)
        {
            _linesById[cachedLine.Id] = cachedLine.Line;
            if (!_lineIdsByPath.TryGetValue(cachedLine.Line.Path, out var pathLineIds))
            {
                pathLineIds = new List<long>();
                _lineIdsByPath[cachedLine.Line.Path] = pathLineIds;
            }

            pathLineIds.Add(cachedLine.Id);
            var trigrams = QueryTrigramTerms.BuildLineTrigrams(cachedLine.Line.Content).ToArray();
            _trigramsByLineId[cachedLine.Id] = trigrams;
            foreach (var trigram in trigrams)
                AddLineId(trigram, cachedLine.Id);

            var index = _allLines.BinarySearch(cachedLine.Line, Comparer<IndexedLine>.Create(s_lineComparison));
            _allLines.Insert(index >= 0 ? index : ~index, cachedLine.Line);
        }

        private void AddLineId(string trigram, long lineId)
        {
            if (!_lineIdsByTrigram.TryGetValue(trigram, out var bucket))
            {
                _lineIdsByTrigram[trigram] = [lineId];
                return;
            }

            var updated = new long[bucket.Length + 1];
            Array.Copy(bucket, updated, bucket.Length);
            updated[^1] = lineId;
            _lineIdsByTrigram[trigram] = updated;
        }

        private void RemoveLineId(string trigram, long lineId)
        {
            if (!_lineIdsByTrigram.TryGetValue(trigram, out var bucket))
                return;

            var index = Array.BinarySearch(bucket, lineId);
            if (index < 0)
                return;

            if (bucket.Length == 1)
            {
                _lineIdsByTrigram.Remove(trigram);
                return;
            }

            var updated = new long[bucket.Length - 1];
            if (index > 0)
                Array.Copy(bucket, 0, updated, 0, index);
            if (index < bucket.Length - 1)
                Array.Copy(bucket, index + 1, updated, index, bucket.Length - index - 1);
            _lineIdsByTrigram[trigram] = updated;
        }
    }

    private sealed record IndexVolumeContext(
        long VolumeId,
        IndexVolumeInfo Volume,
        IndexLocationStrategy Strategy,
        bool RootIdentityChanged);

    private sealed record IndexFileCandidate(
        string Path,
        FileInfo Info,
        IndexedFileIdentity? Identity,
        ITextExtractor? Extractor,
        long ExistingFileId,
        long ExistingExtractionAttemptCount);

    private sealed record ExtractionMetadata(
        string ExtractorId,
        string ExtractorVersion,
        long AttemptCount,
        long LastAttemptUtcTicks);

    private enum ExtractedFileStatus
    {
        Ok,
        Skipped,
        Error,
    }

    private sealed record ExtractedFileResult(
        IndexFileCandidate Candidate,
        ExtractedFileStatus Status,
        string PrimaryExtractorId,
        string PrimaryExtractorVersion,
        string LineExtractorId,
        string LineExtractorVersion,
        IReadOnlyList<TextLine> Lines,
        IReadOnlyList<ExtractionIssue> Issues,
        bool RecordFallbackAttempt,
        string? Error)
    {
        public static ExtractedFileResult Success(
            IndexFileCandidate candidate,
            string primaryExtractorId,
            string primaryExtractorVersion,
            string lineExtractorId,
            string lineExtractorVersion,
            IReadOnlyList<TextLine> lines,
            IReadOnlyList<ExtractionIssue> issues,
            bool recordFallbackAttempt) =>
            new(
                candidate,
                ExtractedFileStatus.Ok,
                primaryExtractorId,
                primaryExtractorVersion,
                lineExtractorId,
                lineExtractorVersion,
                lines,
                issues,
                recordFallbackAttempt,
                null);

        public static ExtractedFileResult Skipped(IndexFileCandidate candidate, string error) =>
            new(
                candidate,
                ExtractedFileStatus.Skipped,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<TextLine>(),
                Array.Empty<ExtractionIssue>(),
                false,
                error);

        public static ExtractedFileResult Failed(
            IndexFileCandidate candidate,
            string primaryExtractorId,
            string primaryExtractorVersion,
            string error) =>
            new(
                candidate,
                ExtractedFileStatus.Error,
                primaryExtractorId,
                primaryExtractorVersion,
                string.Empty,
                string.Empty,
                Array.Empty<TextLine>(),
                Array.Empty<ExtractionIssue>(),
                false,
                error);
    }

    private sealed record FallbackExtractionResult(
        string ExtractorId,
        string ExtractorVersion,
        IReadOnlyList<TextLine> Lines,
        IReadOnlyList<ExtractionIssue> Issues);

    private sealed record ReplayRootContext(
        long RootId,
        WalkerOptions Options,
        IndexVolumeContext? VolumeContext);
}
