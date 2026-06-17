using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using Microsoft.Extensions.Logging;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Owns the CSharpDB connection lifecycle for the file index: schema
/// creation and versioning, per-operation open/close, and write exclusion
/// (in-process gate plus a cross-process lock file). All schema DDL lives
/// here; DML lives in <see cref="IndexTables"/>.
/// </summary>
internal sealed class IndexDatabase : IDisposable
{
    internal const string CurrentSchemaVersion = "5";
    internal const string FullTextIndexName = "fts_lines";

    private static readonly string[] s_fullTextColumns = { "content" };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger _logger;

    /// <summary>
    /// True once this process has run the schema DDL against the current
    /// database file; lets subsequent opens skip the schema work. Only read
    /// and written while holding <see cref="_writeGate"/>.
    /// </summary>
    private bool _schemaEnsured;

    public IndexDatabase(string databasePath, ILogger logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
    }

    public string DatabasePath { get; }

    public void Dispose() => _writeGate.Dispose();

    /// <summary>
    /// Runs a write operation with exclusive access: serialized against other
    /// writers in this process (<see cref="_writeGate"/>) and in other
    /// processes (a sibling .lock file held with no sharing — the GUI and CLI
    /// share the same database). IDs are allocated with MAX(id)+1, which is
    /// only safe because every allocation happens inside this exclusion.
    /// The OS releases the file lock if the process dies, so a crash can't
    /// strand other writers.
    /// </summary>
    public async Task RunExclusiveWriteAsync(Func<Database, Task> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var db = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(db).ConfigureAwait(false);
            }
            finally
            {
                await TryCheckpointAsync(db).ConfigureAwait(false);
                await CloseQuietlyAsync(db).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Opens the database for reading, or returns null when it doesn't exist
    /// or its schema version doesn't match. Callers must close the handle via
    /// <see cref="CloseQuietlyAsync"/>.
    /// </summary>
    public async ValueTask<Database?> OpenExistingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
            return null;

        var db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
            if (version == CurrentSchemaVersion)
                return db;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index schema probe failed; treating index as missing.");
        }

        await CloseQuietlyAsync(db).ConfigureAwait(false);
        return null;
    }

    public static async ValueTask CloseQuietlyAsync(Database db)
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

    private async ValueTask<Database> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        var db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);

        // The version probe stays on every open so a recreate by another
        // process is noticed, but the schema DDL (and its meta rewrite) only
        // runs until it has succeeded once against the current file.
        var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
        if (version == CurrentSchemaVersion && _schemaEnsured)
            return db;

        if (version is not null && version != CurrentSchemaVersion)
        {
            await CloseQuietlyAsync(db).ConfigureAwait(false);
            DeleteDatabaseFiles();
            db = await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
            await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
        _schemaEnsured = true;
        return db;
    }

    public async Task CompactAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = DatabasePath + ".compact-" + Guid.NewGuid().ToString("N");
        try
        {
            var folder = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var db = await OpenExistingAsync(cancellationToken).ConfigureAwait(false);
            if (db is null)
                return;

            try
            {
                await db.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                await db.SaveToFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await CloseQuietlyAsync(db).ConfigureAwait(false);
            }

            DeleteIfExists(DatabasePath + ".wal");
            DeleteIfExists(DatabasePath + ".shm");
            File.Move(tempPath, DatabasePath, overwrite: true);
            TryDelete(tempPath + ".wal");
            TryDelete(tempPath + ".shm");
        }
        finally
        {
            TryDelete(tempPath);
            _writeGate.Release();
        }
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = DatabasePath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another process is writing; poll until it finishes.
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TryCheckpointAsync(Database db)
    {
        try
        {
            await db.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWalCleanupFailure(ex))
        {
            // Benign WAL-sidecar contention; see CloseQuietlyAsync.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index checkpoint failed.");
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

    private static async Task EnsureMetaTableAsync(Database db, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)", cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(Database db, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_volumes (id INTEGER PRIMARY KEY, volume_key TEXT, volume_serial TEXT, filesystem_name TEXT, is_remote INTEGER, usn_supported INTEGER, journal_id TEXT, last_committed_usn INTEGER, health TEXT, last_checked_utc_ticks INTEGER, last_error TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_roots (id INTEGER PRIMARY KEY, root_path TEXT, indexed_utc_ticks INTEGER, options_hash TEXT, volume_id INTEGER, last_full_scan_utc_ticks INTEGER, root_file_reference_number TEXT, root_parent_file_reference_number TEXT, content_version TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_directories (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, volume_id INTEGER, directory_reference_number TEXT, parent_file_reference_number TEXT, observed_utc_ticks INTEGER)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, file_name TEXT, extension TEXT, size_bytes INTEGER, modified_utc_ticks INTEGER, indexed_utc_ticks INTEGER, status TEXT, error TEXT, volume_id INTEGER, file_reference_number TEXT, parent_file_reference_number TEXT, last_observed_usn INTEGER, content_version TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS lines (id INTEGER PRIMARY KEY, file_id INTEGER, line_number INTEGER, content TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS pending_changes (id INTEGER PRIMARY KEY, root_path TEXT, path TEXT, kind INTEGER, queued_utc_ticks INTEGER)", cancellationToken).ConfigureAwait(false);

        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_roots_path ON index_roots(root_path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_volumes_key ON index_volumes(volume_key)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_index_roots_volume ON index_roots(volume_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_directories_root_path ON index_directories(root_id, path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_index_directories_volume_ref ON index_directories(volume_id, directory_reference_number)", cancellationToken).ConfigureAwait(false);
        // Paths are unique per root, not globally: overlapping indexed roots
        // (e.g. C:\Code and C:\Code\ProjectA) each keep their own row for the
        // same file instead of silently failing to index the nested root.
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_files_root_path ON files(root_id, path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_volume_file_ref ON files(volume_id, file_reference_number)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_ext ON files(extension)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_utc_ticks)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_lines_file_line ON lines(file_id, line_number)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_pending_root_path ON pending_changes(root_path, path)", cancellationToken).ConfigureAwait(false);

        await db.ExecuteAsync("DELETE FROM meta WHERE name = 'schema_version'", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(Sql.Format($"INSERT INTO meta VALUES ('schema_version', {CurrentSchemaVersion})"), cancellationToken).ConfigureAwait(false);
        await db.EnsureFullTextIndexAsync(
            FullTextIndexName,
            "lines",
            s_fullTextColumns,
            new FullTextIndexOptions { StorePositions = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryExecuteAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Index DDL is best-effort (queries still work, just slower), but
            // a failure here must be visible — a silently missing unique
            // index has masked real bugs before.
            _logger.LogWarning(ex, "Index DDL failed: {Sql}", sql);
        }
    }

    private void DeleteDatabaseFiles()
    {
        TryDelete(DatabasePath);
        TryDelete(DatabasePath + ".wal");
        TryDelete(DatabasePath + ".shm");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete stale index file {Path}.", path);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static async Task<string?> GetMetaAsync(Database db, string key, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT value FROM meta WHERE name = {key}"),
            cancellationToken).ConfigureAwait(false);

        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsText
            : null;
    }
}
