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

namespace FileSearch.Core.Indexing;

public sealed class CSharpDbFileIndex : IFileIndex
{
    private const string SchemaVersion = "2";
    private const string FullTextIndexName = "fts_lines";
    private const int LineInsertBatchSize = 250;
    private const int IdQueryBatchSize = 500;

    private readonly FileIndexOptions _options;
    private readonly IFileWalker _walker;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions _searchOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public CSharpDbFileIndex(
        FileIndexOptions? options,
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions = null)
    {
        _options = options ?? new FileIndexOptions();
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _searchOptions = searchOptions ?? new SearchOptions();
    }

    public string DatabasePath => _options.DatabasePath;

    public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) =>
        RefreshRootAsync(request, IndexRefreshMode.Full, cancellationToken);

    public async Task RefreshRootAsync(
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Root)) throw new ArgumentException("Root is required.", nameof(request));
        if (!Directory.Exists(request.Root)) throw new DirectoryNotFoundException(request.Root);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            await RefreshRootCoreAsync(db, request, mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
            {
                await TryCheckpointAsync(db).ConfigureAwait(false);
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            }
            _writeGate.Release();
        }
    }

    public async Task UpsertFileAsync(
        string root,
        string path,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var normalizedPath = IndexPath.NormalizeFile(path);

            if (!IsUnderRoot(normalizedRoot, normalizedPath))
                return;

            var profile = IndexProfile.FromWalkerOptions(options).ToStorageString();
            var rootId = await EnsureRootAsync(db, normalizedRoot, profile, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(normalizedPath))
            {
                await DeleteFileCoreAsync(db, rootId, normalizedPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            var info = new FileInfo(normalizedPath);
            if (ShouldSkipSingleFile(info, options))
            {
                await DeleteFileCoreAsync(db, rootId, normalizedPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            await IndexSingleFileAsync(db, rootId, normalizedPath, info, cancellationToken).ConfigureAwait(false);
            await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            _writeGate.Release();
        }
    }

    public async Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            var rootId = await GetRootIdAsync(db, IndexPath.NormalizeRoot(root), cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                return;

            await DeleteFileCoreAsync(db, rootId.Value, IndexPath.NormalizeFile(path), cancellationToken).ConfigureAwait(false);
            await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Database? db = null;
        try
        {
            db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
            if (db is null || request.Roots.Count == 0)
                yield break;

            var root = IndexPath.NormalizeRoot(request.Roots[0]);
            var rootId = await GetRootIdAsync(db, root, cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                yield break;

            var highlightBuffer = new List<MatchSpan>(4);
            var hitsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ftsQueries = QueryFtsTerms.BuildCandidateQueries(request.Expression);

            if (ftsQueries.Count > 0)
            {
                var lineIds = new HashSet<long>();
                foreach (var ftsQuery in ftsQueries)
                {
                    var ftsHits = await db.SearchAsync(FullTextIndexName, ftsQuery, cancellationToken).ConfigureAwait(false);
                    foreach (var hit in ftsHits)
                        lineIds.Add(hit.RowId);
                }

                foreach (var batch in lineIds.Chunk(IdQueryBatchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var idList = string.Join(",", batch);
                    var sql = SelectLinesSql(rootId.Value, $"AND l.id IN ({idList})");
                    await foreach (var line in ReadLinesAsync(db, sql, cancellationToken).ConfigureAwait(false))
                    {
                        if (TryCreateHit(line, request.Expression, request.WalkerOptions, hitsByPath, highlightBuffer, out var hit))
                            yield return hit;
                    }
                }
            }
            else
            {
                var sql = SelectLinesSql(rootId.Value, string.Empty);
                await foreach (var line in ReadLinesAsync(db, sql, cancellationToken).ConfigureAwait(false))
                {
                    if (TryCreateHit(line, request.Expression, request.WalkerOptions, hitsByPath, highlightBuffer, out var hit))
                        yield return hit;
                }
            }
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
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
            var db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
            if (db is null)
                return new IndexCoverage(IndexCoverageStatus.Missing, "Index does not cover this folder");

            try
            {
                var root = IndexPath.NormalizeRoot(request.Roots[0]);
                var rootRow = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
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
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new IndexCoverage(IndexCoverageStatus.Error, $"Index unavailable: {ex.Message}");
        }
    }

    public async Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
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
            await DisposeDatabaseAsync(db).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        var db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<IndexedLocationInfo>();

        try
        {
            var roots = new List<string>();
            await using (var result = await db.ExecuteAsync("SELECT root_path FROM index_roots ORDER BY root_path", cancellationToken).ConfigureAwait(false))
            {
                while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    roots.Add(result.Current[0].AsText);
            }

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
            await DisposeDatabaseAsync(db).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync(string root, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var rootId = await GetRootIdAsync(db, normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (rootId is null)
                return;

            var fileIds = await ReadIdsAsync(db, $"SELECT id FROM files WHERE root_id = {rootId.Value}", cancellationToken).ConfigureAwait(false);
            foreach (var batch in fileIds.Chunk(IdQueryBatchSize))
            {
                var ids = string.Join(",", batch);
                await db.ExecuteAsync($"DELETE FROM lines WHERE file_id IN ({ids})", cancellationToken).ConfigureAwait(false);
            }

            await db.ExecuteAsync($"DELETE FROM files WHERE root_id = {rootId.Value}", cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync($"DELETE FROM index_roots WHERE id = {rootId.Value}", cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync($"DELETE FROM pending_changes WHERE root_path = {SqlText(normalizedRoot)}", cancellationToken).ConfigureAwait(false);
            await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            _writeGate.Release();
        }
    }

    public async Task SavePendingChangeAsync(
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            var normalizedPath = IndexPath.NormalizeFile(path);
            await db.ExecuteAsync(
                $"DELETE FROM pending_changes WHERE root_path = {SqlText(normalizedRoot)} AND path = {SqlText(normalizedPath)}",
                cancellationToken).ConfigureAwait(false);

            var id = await GetNextIdAsync(db, "pending_changes", cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                "INSERT INTO pending_changes VALUES (" +
                $"{id}, {SqlText(normalizedRoot)}, {SqlText(normalizedPath)}, {(long)kind}, {DateTime.UtcNow.Ticks})",
                cancellationToken).ConfigureAwait(false);
            await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken)
    {
        var db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
        if (db is null)
            return Array.Empty<PendingIndexChange>();

        try
        {
            var changes = new List<PendingIndexChange>();
            await using var result = await db.ExecuteAsync(
                "SELECT id, root_path, path, kind FROM pending_changes ORDER BY queued_utc_ticks",
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new PendingIndexChange(
                    result.Current[0].AsInteger,
                    result.Current[1].AsText,
                    result.Current[2].AsText,
                    (IndexChangeKind)result.Current[3].AsInteger));
            }

            return changes;
        }
        finally
        {
            await DisposeDatabaseAsync(db).ConfigureAwait(false);
        }
    }

    public async Task RemovePendingChangeAsync(
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Database? db = null;
        try
        {
            db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                "DELETE FROM pending_changes WHERE " +
                $"root_path = {SqlText(IndexPath.NormalizeRoot(root))} AND " +
                $"path = {SqlText(IndexPath.NormalizeFile(path))} AND kind = {(long)kind}",
                cancellationToken).ConfigureAwait(false);
            await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (db is not null)
                await DisposeDatabaseAsync(db).ConfigureAwait(false);
            _writeGate.Release();
        }
    }

    private async Task RefreshRootCoreAsync(
        Database db,
        IndexRequest request,
        IndexRefreshMode mode,
        CancellationToken cancellationToken)
    {
        var root = IndexPath.NormalizeRoot(request.Root);
        var profile = IndexProfile.FromWalkerOptions(request.WalkerOptions).ToStorageString();
        var rootId = await EnsureRootAsync(db, root, profile, cancellationToken).ConfigureAwait(false);
        var existing = await LoadExistingFilesAsync(db, rootId, cancellationToken).ConfigureAwait(false);

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

        foreach (var path in _walker.Enumerate(new[] { root }, request.WalkerOptions, cancellationToken))
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
                if (existingFile is not null &&
                    existingFile.SizeBytes == info.Length &&
                    existingFile.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    string.Equals(existingFile.Status, "ok", StringComparison.OrdinalIgnoreCase))
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
            catch
            {
                filesFailed++;
            }

            Publish();
        }

        if (mode == IndexRefreshMode.Full || mode == IndexRefreshMode.Incremental)
        {
            foreach (var stale in existing.Values.Where(file => !seen.Contains(file.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteFileCoreAsync(db, rootId, stale.Path, cancellationToken).ConfigureAwait(false);
                filesRemoved++;
                Publish();
            }
        }

        await db.ExecuteAsync(
            $"UPDATE index_roots SET indexed_utc_ticks = {DateTime.UtcNow.Ticks}, options_hash = {SqlText(profile)} WHERE id = {rootId}",
            cancellationToken).ConfigureAwait(false);
        await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task<long> IndexSingleFileAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        CancellationToken cancellationToken)
    {
        var fileId = await EnsureFileRowAsync(db, rootId, path, info, "indexing", null, cancellationToken).ConfigureAwait(false);
        await DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);

        var extractor = _extractors.GetFor(path);
        if (extractor is null)
        {
            await SetFileStatusAsync(db, fileId, "skipped", "No extractor registered.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        long linesIndexed = 0;
        try
        {
            var nextLineId = await GetNextIdAsync(db, "lines", cancellationToken).ConfigureAwait(false);
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
            await SetFileStatusAsync(db, fileId, "ok", null, cancellationToken).ConfigureAwait(false);
            return linesIndexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
            await SetFileStatusAsync(db, fileId, "error", ex.Message, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private static bool ShouldSkipSingleFile(FileInfo info, WalkerOptions options)
    {
        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && !options.IncludeHidden)
            return true;

        return !IndexedFileFilter.Matches(
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
        List<MatchSpan> highlightBuffer,
        out Hit hit)
    {
        hit = null!;

        if (!IndexedFileFilter.Matches(line.FileName, line.Extension, line.SizeBytes, line.ModifiedUtcTicks, options))
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

    private async ValueTask<Database> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);

        var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
        if (version is not null && version != SchemaVersion)
        {
            await DisposeDatabaseAsync(db).ConfigureAwait(false);
            DeleteDatabaseFiles();
            db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
            await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
        return db;
    }

    private async ValueTask<Database?> OpenExistingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
            return null;

        var db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
            if (version == SchemaVersion)
                return db;
        }
        catch
        {
        }

        await DisposeDatabaseAsync(db).ConfigureAwait(false);
        return null;
    }

    private static async ValueTask DisposeDatabaseAsync(Database db)
    {
        try
        {
            await db.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWalCleanupFailure(ex))
        {
            // CSharpDB can throw after successful operations if another handle
            // still owns the WAL sidecar. Treat that as cleanup-only noise.
        }
    }

    private static bool IsWalCleanupFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("WAL file", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task TryCheckpointAsync(Database db)
    {
        try
        {
            await db.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWalCleanupFailure(ex))
        {
        }
        catch
        {
        }
    }

    private static async Task EnsureMetaTableAsync(Database db, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(Database db, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_roots (id INTEGER PRIMARY KEY, root_path TEXT, indexed_utc_ticks INTEGER, options_hash TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, file_name TEXT, extension TEXT, size_bytes INTEGER, modified_utc_ticks INTEGER, indexed_utc_ticks INTEGER, status TEXT, error TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS lines (id INTEGER PRIMARY KEY, file_id INTEGER, line_number INTEGER, content TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS pending_changes (id INTEGER PRIMARY KEY, root_path TEXT, path TEXT, kind INTEGER, queued_utc_ticks INTEGER)", cancellationToken).ConfigureAwait(false);

        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_roots_path ON index_roots(root_path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_path ON files(path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_files_path_unique ON files(path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_ext ON files(extension)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_utc_ticks)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_lines_file_line ON lines(file_id, line_number)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_pending_root_path ON pending_changes(root_path, path)", cancellationToken).ConfigureAwait(false);

        await db.ExecuteAsync("DELETE FROM meta WHERE name = 'schema_version'", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync($"INSERT INTO meta VALUES ('schema_version', {SqlText(SchemaVersion)})", cancellationToken).ConfigureAwait(false);
        await db.EnsureFullTextIndexAsync(
            FullTextIndexName,
            "lines",
            new[] { "content" },
            new FullTextIndexOptions { StorePositions = true },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryExecuteAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void DeleteDatabaseFiles()
    {
        TryDelete(DatabasePath);
        TryDelete(DatabasePath + ".wal");
        TryDelete(DatabasePath + ".shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task<string?> GetMetaAsync(Database db, string key, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            $"SELECT value FROM meta WHERE name = {SqlText(key)}",
            cancellationToken).ConfigureAwait(false);

        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsText
            : null;
    }

    private static async Task<long> EnsureRootAsync(Database db, string root, string profile, CancellationToken cancellationToken)
    {
        var existing = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await db.ExecuteAsync(
                $"UPDATE index_roots SET options_hash = {SqlText(profile)} WHERE id = {existing.Id}",
                cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var id = await GetNextIdAsync(db, "index_roots", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            $"INSERT INTO index_roots VALUES ({id}, {SqlText(root)}, 0, {SqlText(profile)})",
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    private static async Task<RootRow?> GetRootAsync(Database db, string root, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            $"SELECT id, indexed_utc_ticks, options_hash FROM index_roots WHERE root_path = {SqlText(root)}",
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new RootRow(
            result.Current[0].AsInteger,
            result.Current[1].AsInteger,
            result.Current[2].AsText);
    }

    private static async Task<long?> GetRootIdAsync(Database db, string root, CancellationToken cancellationToken)
    {
        var row = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        return row?.Id;
    }

    private static async Task<IndexedLocationInfo?> GetLocationInfoAsync(
        Database db,
        string root,
        CancellationToken cancellationToken)
    {
        var rootRow = await GetRootAsync(db, IndexPath.NormalizeRoot(root), cancellationToken).ConfigureAwait(false);
        if (rootRow is null)
            return null;

        var fileCount = await GetCountAsync(db, $"SELECT COUNT(*) FROM files WHERE root_id = {rootRow.Id} AND status = 'ok'", cancellationToken).ConfigureAwait(false);
        var lineCount = await GetCountAsync(
            db,
            $"SELECT COUNT(*) FROM lines l INNER JOIN files f ON f.id = l.file_id WHERE f.root_id = {rootRow.Id} AND f.status = 'ok'",
            cancellationToken).ConfigureAwait(false);
        var indexedUtc = rootRow.IndexedUtcTicks > 0
            ? new DateTime(rootRow.IndexedUtcTicks, DateTimeKind.Utc)
            : (DateTime?)null;

        return new IndexedLocationInfo(root, fileCount, lineCount, indexedUtc, rootRow.OptionsHash, Exists: true);
    }

    private static async Task<Dictionary<string, ExistingFileRow>> LoadExistingFilesAsync(
        Database db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, ExistingFileRow>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            $"SELECT id, path, size_bytes, modified_utc_ticks, status FROM files WHERE root_id = {rootId}",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new ExistingFileRow(
                result.Current[0].AsInteger,
                result.Current[1].AsText,
                result.Current[2].AsInteger,
                result.Current[3].AsInteger,
                result.Current[4].AsText);
            rows[row.Path] = row;
        }

        return rows;
    }

    private static async Task<long> EnsureFileRowAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var existing = await ReadIdsAsync(db, $"SELECT id FROM files WHERE root_id = {rootId} AND path = {SqlText(path)}", cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;

        if (existing.Count == 0)
        {
            var id = await GetNextIdAsync(db, "files", cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                "INSERT INTO files VALUES (" +
                $"{id}, {rootId}, {SqlText(path)}, {SqlText(fileName)}, {SqlText(extension)}, " +
                $"{info.Length}, {info.LastWriteTimeUtc.Ticks}, {now}, {SqlText(status)}, {SqlText(error)})",
                cancellationToken).ConfigureAwait(false);
            return id;
        }

        var fileId = existing[0];
        await db.ExecuteAsync(
            "UPDATE files SET " +
            $"file_name = {SqlText(fileName)}, extension = {SqlText(extension)}, size_bytes = {info.Length}, " +
            $"modified_utc_ticks = {info.LastWriteTimeUtc.Ticks}, indexed_utc_ticks = {now}, " +
            $"status = {SqlText(status)}, error = {SqlText(error)} WHERE id = {fileId}",
            cancellationToken).ConfigureAwait(false);
        return fileId;
    }

    private static async Task SetFileStatusAsync(
        Database db,
        long fileId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(
            $"UPDATE files SET status = {SqlText(status)}, error = {SqlText(error)}, indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteFileCoreAsync(
        Database db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        var ids = await ReadIdsAsync(db, $"SELECT id FROM files WHERE root_id = {rootId} AND path = {SqlText(path)}", cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return;

        foreach (var id in ids)
        {
            await DeleteLinesAsync(db, id, cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                $"UPDATE files SET status = 'deleted', indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {id}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteLinesAsync(Database db, long fileId, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync($"DELETE FROM lines WHERE file_id = {fileId}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlushBatchAsync(InsertBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private static async Task<long> GetNextIdAsync(Database db, string tableName, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync($"SELECT MAX(id) FROM {tableName}", cancellationToken).ConfigureAwait(false);
        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return 1;

        return result.Current[0].IsNull ? 1 : result.Current[0].AsInteger + 1;
    }

    private static async Task<long> GetCountAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsInteger
            : 0;
    }

    private static async Task<List<long>> ReadIdsAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(result.Current[0].AsInteger);
        return ids;
    }

    private static string SelectLinesSql(long rootId, string linePredicate) =>
        "SELECT f.path, f.file_name, f.extension, f.size_bytes, f.modified_utc_ticks, l.line_number, l.content " +
        "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
        $"WHERE f.root_id = {rootId} AND f.status = 'ok' {linePredicate} " +
        "ORDER BY f.path, l.line_number";

    private static async IAsyncEnumerable<IndexedLine> ReadLinesAsync(
        Database db,
        string sql,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            yield return new IndexedLine(
                row[0].AsText,
                row[1].AsText,
                row[2].AsText,
                row[3].AsInteger,
                row[4].AsInteger,
                checked((int)row[5].AsInteger),
                row[6].AsText);
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 &&
            relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static string SqlText(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed record ExistingFileRow(long Id, string Path, long SizeBytes, long ModifiedUtcTicks, string Status);
    private sealed record RootRow(long Id, long IndexedUtcTicks, string OptionsHash);
    private sealed record IndexedLine(
        string Path,
        string FileName,
        string Extension,
        long SizeBytes,
        long ModifiedUtcTicks,
        int LineNumber,
        string Content);
}
