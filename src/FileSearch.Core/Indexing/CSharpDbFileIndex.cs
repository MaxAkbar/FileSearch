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
public sealed class CSharpDbFileIndex : IFileIndex, IDisposable
{
    private const int LineInsertBatchSize = 250;
    private const int IdQueryBatchSize = 500;

    private readonly IndexDatabase _database;
    private readonly IFileWalker _walker;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions _searchOptions;
    private readonly ILogger _logger;

    public CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions = null,
        ILogger<CSharpDbFileIndex>? logger = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _searchOptions = searchOptions ?? new SearchOptions();
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

            if (!File.Exists(normalizedPath))
            {
                await IndexTables.DeleteFileAsync(db, rootId, normalizedPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            var info = new FileInfo(normalizedPath);
            if (ShouldSkipSingleFile(info, indexingOptions))
            {
                await IndexTables.DeleteFileAsync(db, rootId, normalizedPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Same short-circuit as the root refresh: don't re-extract a file
            // whose size and timestamp match what's already indexed.
            var existingRow = await IndexTables.GetFileRowAsync(db, rootId, normalizedPath, cancellationToken).ConfigureAwait(false);
            if (existingRow is not null && IsUnchanged(existingRow, info))
                return;

            await IndexSingleFileAsync(db, rootId, normalizedPath, info, cancellationToken).ConfigureAwait(false);
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
                            if (TryCreateHit(line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                                yield return hit;
                        }

                        batchIds.Clear();
                    }
                }

                if (batchIds.Count > 0)
                {
                    await foreach (var line in ReadLineBatchAsync(db, rootId.Value, batchIds, cancellationToken).ConfigureAwait(false))
                    {
                        if (TryCreateHit(line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                            yield return hit;
                    }
                }
            }
            else
            {
                var sql = IndexTables.SelectLinesSql(rootId.Value, string.Empty);
                await foreach (var line in IndexTables.ReadLinesAsync(db, sql, cancellationToken).ConfigureAwait(false))
                {
                    if (TryCreateHit(line, request.Expression, request.WalkerOptions, hitsByPath, fileFilterVerdicts, highlightBuffer, out var hit))
                        yield return hit;
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
        var sql = IndexTables.SelectLinesSql(rootId, $"AND l.id IN ({string.Join(",", lineIds)})");
        return IndexTables.ReadLinesAsync(db, sql, cancellationToken);
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
            await IndexTables.DeleteRootAsync(db, rootId.Value, cancellationToken).ConfigureAwait(false);
            await IndexTables.DeletePendingChangesForRootAsync(db, normalizedRoot, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePendingChangeAsync(
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(
            db => IndexTables.UpsertPendingChangeAsync(
                db,
                IndexPath.NormalizeRoot(root),
                IndexPath.NormalizeFile(path),
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
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _database.RunExclusiveWriteAsync(
            db => IndexTables.DeletePendingChangeAsync(
                db,
                IndexPath.NormalizeRoot(root),
                IndexPath.NormalizeFile(path),
                kind,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

                var indexedLines = await IndexSingleFileAsync(db, rootId, normalizedPath, info, cancellationToken).ConfigureAwait(false);
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
        }

        if (mode == IndexRefreshMode.Full || mode == IndexRefreshMode.Incremental)
        {
            foreach (var stale in existing.Values.Where(file => !seen.Contains(file.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexTables.DeleteFileAsync(db, rootId, stale.Path, cancellationToken).ConfigureAwait(false);
                filesRemoved++;
                Publish();
            }
        }

        await IndexTables.MarkRootRefreshedAsync(db, rootId, profile, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task<long> IndexSingleFileAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        CancellationToken cancellationToken)
    {
        var fileId = await IndexTables.EnsureFileRowAsync(db, rootId, path, info, FileStatus.Indexing, null, cancellationToken).ConfigureAwait(false);
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

    private static bool IsUnchanged(ExistingFileRow row, FileInfo info) =>
        row.SizeBytes == info.Length &&
        row.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
        string.Equals(row.Status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipSingleFile(FileInfo info, WalkerOptions options)
    {
        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && !options.IncludeHidden)
            return true;

        return !IndexedFileFilter.Matches(
            info.FullName,
            info.Name,
            info.Extension.ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            options);
    }

    private bool TryCreateHit(
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
                line.Path, line.FileName, line.Extension, line.SizeBytes, line.ModifiedUtcTicks, options);
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
}
