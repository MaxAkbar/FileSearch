using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using Microsoft.Extensions.Logging;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Owns the CSharpDB connection lifecycle for the file index. One shared
/// long-lived handle serves everything in-process: reads take snapshot
/// <see cref="Database.ReaderSession"/> leases (safe during concurrent
/// writes — separate per-operation handles crash ~95% of reads that overlap
/// a write and can suppress the writer's checkpoint, discarding it), writes
/// serialize through <see cref="_writeGate"/> plus a cross-process lock
/// file. Writes from other processes are detected via a file stamp and swap
/// the handle in. All schema DDL lives here; DML lives in
/// <see cref="IndexTables"/>.
/// </summary>
internal sealed class IndexDatabase : IDisposable
{
    internal const string CurrentSchemaVersion = "27";
    private const string UpgradeableSchemaVersion = "26";
    private static readonly TimeSpan CompactLeaseDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checkpoint policy: a checkpoint merges the WAL into the main file and
    /// its cost grows with database size, so running one after EVERY write
    /// session made per-file upserts O(database size) — the standard
    /// benchmark's 100k-row seeding did not finish in six hours. Committed
    /// data is durable in the WAL regardless (it replays on open), and the
    /// shared in-process handle sees it immediately; checkpoints only bound
    /// WAL growth and speed up other processes' opens.
    /// </summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(60);
    private const long CheckpointWalThresholdBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Reads resolve pages through the unmerged WAL, and that lookup cost
    /// grows with accumulated frames — deferring checkpoints too long makes
    /// READS pathologically slow, not just the WAL large. Bound the number
    /// of uncheckpointed write sessions as well as size and age.
    /// </summary>
    private const int CheckpointSessionThreshold = 128;

    private static readonly string[] s_schemaProbeQueries =
    {
        "SELECT drive_kind, last_checked_utc_ticks FROM index_volumes WHERE id = -1",
        "SELECT location_kind, last_full_validation_utc_ticks FROM index_roots WHERE id = -1",
        "SELECT directory_path, file_name_lower, extractor_id, extraction_attempt_count FROM files WHERE id = -1",
        "SELECT content_unit_id, anchor_json FROM lines WHERE id = -1",
        "SELECT root_id, trigram_code, file_id FROM file_trigrams WHERE file_id = -1",
        "SELECT root_id, token, file_id FROM file_metadata_tokens WHERE id = -1",
        "SELECT kind, locator_json, content_hash FROM content_units WHERE id = -1",
        "SELECT member_path, severity FROM extraction_issues WHERE id = -1",
        "SELECT kind, observed_utc_ticks FROM validation_drifts WHERE id = -1",
    };

    private static readonly string[] s_hybridHotTableNames =
    [
        "files",
        "file_metadata_tokens",
    ];

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _handleGate = new(1, 1);
    private readonly ILogger _logger;
    private readonly bool _useHybridHotTables;

    private SharedHandle? _current;
    private FileStamp _stamp;
    private long _generation;

    /// <summary>
    /// True while an in-process write session is running. Lease acquisition
    /// must not treat the writer's own in-flight file changes as an external
    /// modification (a mid-write handle swap would reintroduce the racy
    /// fresh-handle reads the shared handle exists to prevent).
    /// </summary>
    private volatile bool _writeSessionActive;

    /// <summary>
    /// True once this process has run the schema DDL against the current
    /// database file; lets subsequent opens skip the schema work. Only read
    /// and written while holding <see cref="_writeGate"/>.
    /// </summary>
    private bool _schemaEnsured;

    /// <summary>Last successful checkpoint; only touched under <see cref="_writeGate"/>.</summary>
    private DateTime _lastCheckpointUtc;

    /// <summary>Write sessions since the last checkpoint; only touched under <see cref="_writeGate"/>.</summary>
    private int _writeSessionsSinceCheckpoint;

    /// <summary>Set once by <see cref="Dispose"/>; later calls are no-ops.</summary>
    private int _disposedFlag;

    public IndexDatabase(string databasePath, ILogger logger)
        : this(new FileIndexOptions { DatabasePath = databasePath }, logger)
    {
    }

    public IndexDatabase(FileIndexOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        DatabasePath = options.DatabasePath;
        _logger = logger;
        _useHybridHotTables = options.UseHybridHotTables;
    }

    public string DatabasePath { get; }

    public long CurrentGeneration => Volatile.Read(ref _generation);

    public void Dispose()
    {
        // WPF shutdown can dispose the host through both its async and sync
        // paths, reaching here twice — and on the UI thread. No gate waits
        // (the gates may already be torn down) and strictly once.
        if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
            return;

        var current = Interlocked.Exchange(ref _current, null);
        if (current is not null)
        {
            Volatile.Write(ref current.Retired, true);
            if (Volatile.Read(ref current.Leases) == 0)
                _ = DisposeRetiredAsync(current);
        }

        _writeGate.Dispose();
        _handleGate.Dispose();
    }

    /// <summary>
    /// Runs a write operation with exclusive access: serialized against other
    /// writers in this process (<see cref="_writeGate"/>) and in other
    /// processes (a sibling .lock file held with no sharing). IDs are
    /// allocated from index_sequences, which is only safe because every
    /// allocation happens inside this exclusion. Readers are NOT blocked:
    /// they hold snapshot sessions on the same shared handle. A failed
    /// checkpoint is logged and retried on later sessions — the data stays
    /// live in the open handle and its WAL, which replays on reopen.
    /// </summary>
    public async Task RunExclusiveWriteAsync(Func<Database, Task> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) == 1, this);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var handle = await EnsureHandleForWriteAsync(cancellationToken).ConfigureAwait(false);
            _writeSessionActive = true;
            try
            {
                await action(handle.Db).ConfigureAwait(false);
            }
            finally
            {
                _writeSessionsSinceCheckpoint++;
                if (ShouldCheckpoint())
                {
                    if (await TryCheckpointWithRetryAsync(handle.Db).ConfigureAwait(false))
                    {
                        _lastCheckpointUtc = DateTime.UtcNow;
                        _writeSessionsSinceCheckpoint = 0;
                    }
                }

                _writeSessionActive = false;
                await UpdateStampAsync(CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _generation);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Opens a snapshot read lease on the shared handle, or returns null when
    /// the database doesn't exist or its schema doesn't match. The schema is
    /// probed once per handle open, not per lease.
    /// </summary>
    public async ValueTask<IndexReadLease?> OpenReadLeaseAsync(CancellationToken cancellationToken)
    {
        // A search racing shutdown degrades to "no index" instead of
        // throwing ObjectDisposedException from a torn-down gate.
        if (Volatile.Read(ref _disposedFlag) == 1 || !File.Exists(DatabasePath))
            return null;

        SharedHandle handle;
        await _handleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is null || _current.Retired || (!_writeSessionActive && HasExternalChange()))
            {
                await RetireCurrentAndDisposeLockedAsync(cancellationToken).ConfigureAwait(false);
                var opened = await TryOpenValidatedAsync(cancellationToken).ConfigureAwait(false);
                if (opened is null)
                    return null;

                _current = new SharedHandle { Db = opened };
                _stamp = ReadStamp();
                Interlocked.Increment(ref _generation);
            }

            handle = _current;
            Interlocked.Increment(ref handle.Leases);
        }
        finally
        {
            _handleGate.Release();
        }

        try
        {
            var session = handle.Db.CreateReaderSession();
            return new IndexReadLease(
                handle.Db,
                session,
                Volatile.Read(ref _generation),
                () => ReleaseLease(handle));
        }
        catch
        {
            ReleaseLease(handle);
            throw;
        }
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

            // Compaction replaces the live database file, so the shared
            // handle must go: retire it and give active leases a bounded
            // window to finish (a straggler makes the File.Move below fail,
            // which aborts the compact cleanly).
            var retired = await RetireCurrentAsync(disposeWhenUnused: false).ConfigureAwait(false);
            if (retired is not null)
            {
                var deadline = DateTime.UtcNow + CompactLeaseDrainTimeout;
                while (Volatile.Read(ref retired.Leases) > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                if (Volatile.Read(ref retired.Leases) > 0)
                {
                    Volatile.Write(ref retired.DisposeWhenUnused, true);
                    if (Volatile.Read(ref retired.Leases) == 0)
                        await DisposeRetiredAsync(retired).ConfigureAwait(false);

                    throw new IOException(
                        $"Cannot compact the index database: '{DatabasePath}' still has active readers.");
                }

                await DisposeRetiredAsync(retired).ConfigureAwait(false);
            }

            if (!File.Exists(DatabasePath))
                return;

            var db = await TryOpenValidatedAsync(cancellationToken).ConfigureAwait(false);
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

    private sealed class SharedHandle
    {
        public required Database Db { get; init; }

        public int Leases;

        public bool Retired;

        public bool DisposeWhenUnused = true;

        public int DisposedFlag;
    }

    private readonly record struct FileStamp(
        long MainLength,
        long MainTicks,
        long WalLength,
        long WalTicks,
        ulong WalHeaderFingerprint);

    private FileStamp ReadStamp()
    {
        static (long Length, long Ticks) Stat(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, -1);
        }

        var main = Stat(DatabasePath);
        var walPath = DatabasePath + ".wal";
        var wal = Stat(walPath);
        return new FileStamp(
            main.Length,
            main.Ticks,
            wal.Length,
            wal.Ticks,
            ReadWalHeaderFingerprint(walPath));
    }

    private bool HasExternalChange() => ReadStamp() != _stamp;

    private static ulong ReadWalHeaderFingerprint(string walPath)
    {
        const int walHeaderSize = 32;
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        Span<byte> header = stackalloc byte[walHeaderSize];
        try
        {
            using var handle = File.OpenHandle(
                walPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytesRead = RandomAccess.Read(handle, header, 0);
            var hash = fnvOffset;
            hash = (hash ^ (byte)bytesRead) * fnvPrime;
            foreach (var value in header[..bytesRead])
                hash = (hash ^ value) * fnvPrime;

            return hash;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private bool ShouldCheckpoint()
    {
        if (_writeSessionsSinceCheckpoint >= CheckpointSessionThreshold)
            return true;

        if (DateTime.UtcNow - _lastCheckpointUtc >= CheckpointInterval)
            return true;

        var wal = new FileInfo(DatabasePath + ".wal");
        return wal.Exists && wal.Length >= CheckpointWalThresholdBytes;
    }

    private async ValueTask<Database> OpenDatabaseAsync(bool preferHybrid, CancellationToken cancellationToken)
    {
        if (!_useHybridHotTables || !preferHybrid)
            return await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);

        try
        {
            return await Database.OpenHybridAsync(
                    DatabasePath,
                    new DatabaseOptions(),
                    new HybridDatabaseOptions
                    {
                        HotTableNames = s_hybridHotTableNames,
                        PersistenceMode = HybridPersistenceMode.IncrementalDurable,
                        PersistenceTriggers = HybridPersistenceTriggers.Commit | HybridPersistenceTriggers.Dispose,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hybrid index database open failed; falling back to disk-backed handle.");
            return await Database.OpenAsync(DatabasePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateStampAsync(CancellationToken cancellationToken)
    {
        await _handleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stamp = ReadStamp();
        }
        finally
        {
            _handleGate.Release();
        }
    }

    private void ReleaseLease(SharedHandle handle)
    {
        if (Interlocked.Decrement(ref handle.Leases) == 0 &&
            Volatile.Read(ref handle.Retired) &&
            Volatile.Read(ref handle.DisposeWhenUnused))
        {
            _ = DisposeRetiredAsync(handle);
        }
    }

    private void RetireCurrentLocked(bool disposeWhenUnused = true)
    {
        var current = _current;
        if (current is null)
            return;

        _current = null;
        Volatile.Write(ref current.Retired, true);
        Volatile.Write(ref current.DisposeWhenUnused, disposeWhenUnused);
        if (disposeWhenUnused && Volatile.Read(ref current.Leases) == 0)
            _ = DisposeRetiredAsync(current);
    }

    private async Task RetireCurrentAndDisposeLockedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _current;
        if (current is null)
            return;

        RetireCurrentLocked(disposeWhenUnused: false);
        // A detached CSharpDB handle must finish its checkpoint/delete close
        // before a replacement can safely open the same WAL.
        while (Volatile.Read(ref current.Leases) > 0)
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);

        await DisposeRetiredAsync(current).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask<SharedHandle?> RetireCurrentAsync(bool disposeWhenUnused = true)
    {
        await _handleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = _current;
            RetireCurrentLocked(disposeWhenUnused);
            return current;
        }
        finally
        {
            _handleGate.Release();
        }
    }

    private async Task DisposeRetiredAsync(SharedHandle handle)
    {
        if (Interlocked.Exchange(ref handle.DisposedFlag, 1) == 1)
            return;

        try
        {
            await CloseQuietlyAsync(handle.Db).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing a retired index handle failed.");
        }
    }

    /// <summary>
    /// Opens the database and validates schema version and shape; returns
    /// null (and closes the probe handle) when it doesn't match. Read path.
    /// </summary>
    private async Task<Database?> TryOpenValidatedAsync(CancellationToken cancellationToken)
    {
        var db = await OpenDatabaseAsync(preferHybrid: true, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
            if ((version == CurrentSchemaVersion || version == UpgradeableSchemaVersion) &&
                await HasCurrentSchemaShapeAsync(db, cancellationToken).ConfigureAwait(false))
            {
                return db;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index schema probe failed; treating index as missing.");
        }

        await CloseQuietlyAsync(db).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Ensures the shared handle exists, matches the on-disk state, and has
    /// the current schema, creating or rebuilding the database when needed.
    /// Caller holds <see cref="_writeGate"/> and the cross-process lock.
    /// </summary>
    private async Task<SharedHandle> EnsureHandleForWriteAsync(CancellationToken cancellationToken)
    {
        await _handleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is not null && !_current.Retired && _schemaEnsured && !HasExternalChange())
                return _current;

            await RetireCurrentAndDisposeLockedAsync(cancellationToken).ConfigureAwait(false);

            var databaseExisted = File.Exists(DatabasePath);
            var db = await OpenDatabaseAsync(preferHybrid: databaseExisted, cancellationToken).ConfigureAwait(false);
            await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);

            // The version probe stays on every open so a recreate by another
            // process is noticed, but the schema DDL (and its meta rewrite)
            // only runs until it has succeeded once against the current file.
            var version = await GetMetaAsync(db, "schema_version", cancellationToken).ConfigureAwait(false);
            if (version == UpgradeableSchemaVersion &&
                await HasCurrentSchemaShapeAsync(db, cancellationToken).ConfigureAwait(false))
            {
                // Widen metadata in place: retain all locations, lines, and vectors.
                // Each ALTER is atomic and can be retried after interruption. Only
                // advance the version after every column has been widened.
                try
                {
                    await db.ExecuteAsync("ALTER TABLE index_volumes ALTER COLUMN last_committed_usn TYPE BIGINT", cancellationToken).ConfigureAwait(false);
                    await db.ExecuteAsync("ALTER TABLE files ALTER COLUMN size_bytes TYPE BIGINT", cancellationToken).ConfigureAwait(false);
                    await db.ExecuteAsync("ALTER TABLE files ALTER COLUMN last_observed_usn TYPE BIGINT", cancellationToken).ConfigureAwait(false);
                    await db.ExecuteAsync(Sql.Format($"UPDATE meta SET value = {CurrentSchemaVersion} WHERE name = 'schema_version'"), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await CloseQuietlyAsync(db).ConfigureAwait(false);
                    throw;
                }
                version = CurrentSchemaVersion;
            }
            if (version == CurrentSchemaVersion &&
                (_schemaEnsured || await HasCurrentSchemaShapeAsync(db, cancellationToken).ConfigureAwait(false)))
            {
                _schemaEnsured = true;
            }
            else
            {
                if (version == CurrentSchemaVersion)
                    _logger.LogWarning("Index schema version is current but required columns are missing; rebuilding index database.");

                if (version is not null || databaseExisted)
                {
                    await CloseQuietlyAsync(db).ConfigureAwait(false);

                    // Rebuilding replaces the files on disk. The previous
                    // handle was drained before this open; any surviving
                    // external handle makes the deletes silently fail, after
                    // which
                    // CREATE TABLE IF NOT EXISTS would keep the OLD tables and
                    // the meta rewrite would stamp them with the CURRENT
                    // version — a poisoned database that fails its shape probe
                    // on every open, forever. Verify the files are really gone
                    // and fail loudly if they are not (e.g. an older FileSearch
                    // process still has the index open).
                    DeleteDatabaseFiles();
                    if (File.Exists(DatabasePath) ||
                        File.Exists(DatabasePath + ".wal") ||
                        File.Exists(DatabasePath + ".shm"))
                    {
                        throw new IOException(
                            $"Cannot rebuild the index database: '{DatabasePath}' is still in use. " +
                            "Another FileSearch process (GUI, tray indexer, or CLI) may be running an older version — close it and retry.");
                    }

                    db = await OpenDatabaseAsync(preferHybrid: false, cancellationToken).ConfigureAwait(false);
                    await EnsureMetaTableAsync(db, cancellationToken).ConfigureAwait(false);
                }

                await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
                _schemaEnsured = true;
            }

            _current = new SharedHandle { Db = db };
            _stamp = ReadStamp();
            return _current;
        }
        finally
        {
            _handleGate.Release();
        }
    }

    private async Task<bool> HasCurrentSchemaShapeAsync(Database db, CancellationToken cancellationToken)
    {
        foreach (var query in s_schemaProbeQueries)
        {
            try
            {
                await using var result = await db.ExecuteAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Index schema shape probe failed; treating index as stale.");
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Checkpoints the write session, retrying with backoff. Failure is not
    /// fatal on the shared handle — the data stays live in the handle and
    /// its WAL (which replays on reopen); the next session retries — but it
    /// is logged loudly because persistent failure delays cross-process
    /// visibility and grows the WAL.
    /// </summary>
    private async Task<bool> TryCheckpointWithRetryAsync(Database db)
    {
        const int maxAttempts = 10;
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                if (attempt < maxAttempts)
                {
                    _logger.LogDebug(
                        ex,
                        "Index checkpoint attempt {Attempt}/{MaxAttempts} failed; retrying.",
                        attempt,
                        maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        _logger.LogError(
            lastFailure,
            "Index checkpoint failed after {MaxAttempts} attempts; data remains in the WAL and will be checkpointed by a later write session.",
            maxAttempts);
        return false;
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
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_volumes (id INTEGER PRIMARY KEY, volume_key TEXT, volume_serial TEXT, filesystem_name TEXT, is_remote INTEGER, usn_supported INTEGER, drive_kind TEXT, journal_id TEXT, last_committed_usn BIGINT, health TEXT, last_checked_utc_ticks BIGINT, last_error TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_roots (id INTEGER PRIMARY KEY, root_path TEXT, indexed_utc_ticks BIGINT, options_hash TEXT, volume_id INTEGER, last_full_scan_utc_ticks BIGINT, root_file_reference_number TEXT, root_parent_file_reference_number TEXT, content_version TEXT, location_kind TEXT, update_strategy TEXT, strategy_warning TEXT, usn_catch_up_enabled INTEGER, watcher_recommended INTEGER, last_full_validation_utc_ticks BIGINT, last_validation_status TEXT, last_validation_message TEXT, last_validation_files_checked INTEGER, last_validation_missing_from_index_count INTEGER, last_validation_changed_count INTEGER, last_validation_missing_from_disk_count INTEGER, last_validation_failed_count INTEGER)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_directories (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, volume_id INTEGER, directory_reference_number TEXT, parent_file_reference_number TEXT, observed_utc_ticks BIGINT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, path_lower TEXT, directory_path TEXT, directory_path_lower TEXT, file_name TEXT, file_name_lower TEXT, extension TEXT, size_bytes BIGINT, created_utc_ticks BIGINT, modified_utc_ticks BIGINT, attributes INTEGER, file_type_category TEXT, indexed_utc_ticks BIGINT, status TEXT, error TEXT, volume_id INTEGER, file_reference_number TEXT, parent_file_reference_number TEXT, last_observed_usn BIGINT, content_version TEXT, open_count INTEGER, last_opened_utc_ticks BIGINT, extractor_id TEXT, extractor_version TEXT, extraction_attempt_count INTEGER, last_extraction_attempt_utc_ticks BIGINT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS extraction_issues (id INTEGER PRIMARY KEY, file_id INTEGER, member_path TEXT, code TEXT, message TEXT, severity TEXT, created_utc_ticks BIGINT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS validation_drifts (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, kind TEXT, message TEXT, observed_utc_ticks BIGINT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS file_metadata_tokens (id BIGINT PRIMARY KEY, root_id INTEGER, file_id INTEGER, token TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS content_units (id INTEGER PRIMARY KEY, file_id INTEGER, kind TEXT, locator_json TEXT, unit_text TEXT, content_hash TEXT, language TEXT, extractor_id TEXT, extractor_version TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS lines (id INTEGER PRIMARY KEY, file_id INTEGER, content_unit_id INTEGER, line_number INTEGER, content TEXT, anchor_json TEXT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS file_trigrams (root_id INTEGER, trigram_code BIGINT, file_id INTEGER)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS pending_changes (id INTEGER PRIMARY KEY, root_path TEXT, path TEXT, kind INTEGER, queued_utc_ticks BIGINT)", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_sequences (name TEXT PRIMARY KEY, next_id INTEGER)", cancellationToken).ConfigureAwait(false);

        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_roots_path ON index_roots(root_path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_volumes_key ON index_volumes(volume_key)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_index_roots_volume ON index_roots(volume_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS idx_index_directories_root_path ON index_directories(root_id, path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_index_directories_volume_ref ON index_directories(volume_id, directory_reference_number)", cancellationToken).ConfigureAwait(false);
        // A changed file publishes a new row before removing its superseded
        // version. This index stays non-unique so both versions can coexist
        // during that handoff and overlapping roots remain independent.
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_root_path ON files(root_id, path)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_root_id ON files(root_id, id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_root_file_name_lower ON files(root_id, file_name_lower)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_root_path_lower ON files(root_id, path_lower)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_volume_file_ref ON files(volume_id, file_reference_number)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_ext ON files(extension)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified_utc_ticks)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_files_status ON files(status)", cancellationToken).ConfigureAwait(false);
        // Schema 21: the token lookup must lead with token. A root-first
        // index degenerates when a corpus has one large root because the
        // engine can walk every token row for that root before applying the
        // token predicate.
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_file_metadata_tokens_token_root ON file_metadata_tokens(token, root_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_file_metadata_tokens_file ON file_metadata_tokens(file_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_extraction_issues_file ON extraction_issues(file_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_validation_drifts_root_kind ON validation_drifts(root_id, kind)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_content_units_file ON content_units(file_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_lines_id ON lines(id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_lines_file_line ON lines(file_id, line_number)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_file_trigrams_code_root ON file_trigrams(trigram_code, root_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_file_trigrams_file ON file_trigrams(file_id)", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(db, "CREATE INDEX IF NOT EXISTS idx_pending_root_path ON pending_changes(root_path, path)", cancellationToken).ConfigureAwait(false);

        await db.ExecuteAsync("DELETE FROM meta WHERE name = 'schema_version'", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(Sql.Format($"INSERT INTO meta VALUES ('schema_version', {CurrentSchemaVersion})"), cancellationToken).ConfigureAwait(false);
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
