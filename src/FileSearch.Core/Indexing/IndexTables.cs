using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;

namespace FileSearch.Core.Indexing;

/// <summary>Values stored in the files.status column.</summary>
internal static class FileStatus
{
    public const string Ok = "ok";
    public const string Indexing = "indexing";
    public const string Skipped = "skipped";
    public const string Error = "error";
}

internal sealed record ExistingFileRow(
    long Id,
    string Path,
    long SizeBytes,
    long ModifiedUtcTicks,
    string Status,
    string ContentVersion);

internal sealed record RootRow(
    long Id,
    long IndexedUtcTicks,
    string OptionsHash,
    long? VolumeId,
    string? RootFileReferenceNumber,
    string? RootParentFileReferenceNumber,
    string ContentVersion);

internal sealed record VolumeRow(
    long Id,
    string VolumeKey,
    ulong? JournalId,
    long LastCommittedUsn,
    string Health,
    string? LastError);

internal sealed record IndexedLine(
    string Path,
    string FileName,
    string Extension,
    long SizeBytes,
    long ModifiedUtcTicks,
    int LineNumber,
    string Content);

/// <summary>
/// Every DML statement against the index tables lives here, composed through
/// <see cref="Sql.Format"/> so values can't reach the SQL text unescaped —
/// the handler has no raw-string hole to forget. Schema DDL lives in
/// <see cref="IndexDatabase"/>.
/// </summary>
internal static class IndexTables
{
    private const string SelectLinesColumns =
        "SELECT f.path, f.file_name, f.extension, f.size_bytes, f.modified_utc_ticks, l.line_number, l.content " +
        "FROM lines l INNER JOIN files f ON f.id = l.file_id WHERE ";

    private const string SelectLinesOrder = " ORDER BY f.path, l.line_number";

    // ----- index_roots -----

    public static async Task<long> EnsureRootAsync(Database db, string root, string profile, CancellationToken cancellationToken)
    {
        var existing = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing.Id;

        var id = await GetNextIdAsync(db, "index_roots", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO index_roots (id, root_path, indexed_utc_ticks, options_hash, volume_id, last_full_scan_utc_ticks, root_file_reference_number, root_parent_file_reference_number, content_version) " +
                $"VALUES ({id}, {root}, 0, {profile}, {(long?)null}, {(long?)null}, {(string?)null}, {(string?)null}, {IndexContentVersion.Current})"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public static async Task<RootRow?> GetRootAsync(Database db, string root, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, indexed_utc_ticks, options_hash, volume_id, root_file_reference_number, root_parent_file_reference_number, content_version FROM index_roots WHERE root_path = {root}"),
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new RootRow(
            result.Current[0].AsInteger,
            result.Current[1].AsInteger,
            result.Current[2].AsText,
            result.Current[3].IsNull ? null : result.Current[3].AsInteger,
            result.Current[4].IsNull ? null : result.Current[4].AsText,
            result.Current[5].IsNull ? null : result.Current[5].AsText,
            result.Current[6].IsNull ? string.Empty : result.Current[6].AsText);
    }

    public static async Task<IndexRootIdentity?> GetRootIdentityAsync(
        Database db,
        string root,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT v.volume_key, r.root_file_reference_number, r.root_parent_file_reference_number " +
                $"FROM index_roots r INNER JOIN index_volumes v ON v.id = r.volume_id " +
                $"WHERE r.root_path = {root}"),
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        if (result.Current[1].IsNull)
            return null;

        return new IndexRootIdentity(
            result.Current[0].AsText,
            result.Current[1].AsText,
            result.Current[2].IsNull ? null : result.Current[2].AsText);
    }

    public static async Task<long?> GetRootIdAsync(Database db, string root, CancellationToken cancellationToken)
    {
        var row = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        return row?.Id;
    }

    public static async Task<List<string>> ListRootPathsAsync(Database db, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        await using var result = await db.ExecuteAsync(
            "SELECT root_path FROM index_roots ORDER BY root_path",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            roots.Add(result.Current[0].AsText);

        return roots;
    }

    public static Task MarkRootRefreshStartedAsync(Database db, long rootId, string profile, CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format($"UPDATE index_roots SET indexed_utc_ticks = 0, options_hash = {profile}, content_version = {IndexContentVersion.Current} WHERE id = {rootId}"),
            cancellationToken);

    public static Task MarkRootRefreshedAsync(Database db, long rootId, string profile, CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_roots SET indexed_utc_ticks = {DateTime.UtcNow.Ticks}, " +
                $"options_hash = {profile}, content_version = {IndexContentVersion.Current}, " +
                $"last_full_scan_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {rootId}"),
            cancellationToken);

    public static Task DeleteRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM index_roots WHERE id = {rootId}"), cancellationToken);

    public static Task SetRootVolumeAsync(
        Database db,
        long rootId,
        long volumeId,
        IndexedFileIdentity? rootIdentity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_roots SET volume_id = {volumeId}, " +
                $"root_file_reference_number = {rootIdentity?.FileReferenceNumber}, " +
                $"root_parent_file_reference_number = {rootIdentity?.ParentFileReferenceNumber} " +
                $"WHERE id = {rootId}"),
            cancellationToken);

    // ----- index_directories -----

    public static Task DeleteDirectoriesForRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM index_directories WHERE root_id = {rootId}"), cancellationToken);

    public static async Task EnsureDirectoryAsync(
        Database db,
        long rootId,
        string path,
        IndexedFileIdentity identity,
        CancellationToken cancellationToken)
    {
        var existing = await ReadIdsAsync(
            db,
            Sql.Format($"SELECT id FROM index_directories WHERE root_id = {rootId} AND path = {path}"),
            cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            await db.ExecuteAsync(
                Sql.Format(
                    $"UPDATE index_directories SET volume_id = {identity.VolumeId}, " +
                    $"directory_reference_number = {identity.FileReferenceNumber}, " +
                    $"parent_file_reference_number = {identity.ParentFileReferenceNumber}, " +
                    $"observed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {existing[0]}"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var id = await GetNextIdAsync(db, "index_directories", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            Sql.Format(
                $"INSERT INTO index_directories (id, root_id, path, volume_id, directory_reference_number, parent_file_reference_number, observed_utc_ticks) " +
                $"VALUES ({id}, {rootId}, {path}, {identity.VolumeId}, {identity.FileReferenceNumber}, {identity.ParentFileReferenceNumber}, {DateTime.UtcNow.Ticks})"),
            cancellationToken).ConfigureAwait(false);
    }

    // ----- index_volumes -----

    public static async Task<long> EnsureVolumeAsync(
        Database db,
        IndexVolumeInfo volume,
        CancellationToken cancellationToken)
    {
        var existing = await GetVolumeRowAsync(db, volume.VolumeKey, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;
        if (existing is not null)
        {
            await db.ExecuteAsync(
                Sql.Format(
                    $"UPDATE index_volumes SET volume_serial = {volume.VolumeSerial}, filesystem_name = {volume.FileSystemName}, " +
                    $"is_remote = {Bool(volume.IsRemote)}, usn_supported = {Bool(volume.UsnSupported)}, " +
                    $"last_checked_utc_ticks = {now} WHERE id = {existing.Id}"),
                cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var id = await GetNextIdAsync(db, "index_volumes", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO index_volumes (id, volume_key, volume_serial, filesystem_name, is_remote, usn_supported, journal_id, last_committed_usn, health, last_checked_utc_ticks, last_error) " +
                $"VALUES ({id}, {volume.VolumeKey}, {volume.VolumeSerial}, {volume.FileSystemName}, {Bool(volume.IsRemote)}, {Bool(volume.UsnSupported)}, {(string?)null}, 0, {"unknown"}, {now}, {(string?)null})"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public static async Task<VolumeRow?> GetVolumeRowAsync(
        Database db,
        string volumeKey,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, volume_key, journal_id, last_committed_usn, health, last_error FROM index_volumes WHERE volume_key = {volumeKey}"),
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var journalText = result.Current[2].IsNull ? null : result.Current[2].AsText;
        return new VolumeRow(
            result.Current[0].AsInteger,
            result.Current[1].AsText,
            ulong.TryParse(journalText, out var journalId) ? journalId : null,
            result.Current[3].AsInteger,
            result.Current[4].AsText,
            result.Current[5].IsNull ? null : result.Current[5].AsText);
    }

    public static async Task<List<IndexVolumeHealthInfo>> ListVolumeHealthAsync(
        Database db,
        CancellationToken cancellationToken)
    {
        var volumes = new List<IndexVolumeHealthInfo>();
        await using var result = await db.ExecuteAsync(
            "SELECT volume_key, filesystem_name, is_remote, usn_supported, journal_id, " +
            "last_committed_usn, health, last_error, last_checked_utc_ticks " +
            "FROM index_volumes ORDER BY volume_key",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            var journalText = row[4].IsNull ? null : row[4].AsText;
            var lastCheckedTicks = row[8].IsNull ? 0 : row[8].AsInteger;
            volumes.Add(new IndexVolumeHealthInfo(
                row[0].AsText,
                row[1].AsText,
                row[2].AsInteger != 0,
                row[3].AsInteger != 0,
                ulong.TryParse(journalText, out var journalId) ? journalId : null,
                row[5].AsInteger,
                row[6].AsText,
                row[7].IsNull ? null : row[7].AsText,
                lastCheckedTicks > 0 ? new DateTime(lastCheckedTicks, DateTimeKind.Utc) : null));
        }

        return volumes;
    }

    public static async Task<IndexReplayReferenceSet> ReadReplayReferencesAsync(
        Database db,
        long volumeId,
        CancellationToken cancellationToken)
    {
        var fileReferences = new HashSet<string>(StringComparer.Ordinal);
        var directoryReferences = new HashSet<string>(StringComparer.Ordinal);

        await using (var result = await db.ExecuteAsync(
            Sql.Format($"SELECT file_reference_number FROM files WHERE volume_id = {volumeId} AND file_reference_number IS NOT NULL"),
            cancellationToken).ConfigureAwait(false))
        {
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                fileReferences.Add(result.Current[0].AsText);
        }

        await using (var result = await db.ExecuteAsync(
            Sql.Format($"SELECT directory_reference_number FROM index_directories WHERE volume_id = {volumeId} AND directory_reference_number IS NOT NULL"),
            cancellationToken).ConfigureAwait(false))
        {
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                directoryReferences.Add(result.Current[0].AsText);
        }

        return new IndexReplayReferenceSet(fileReferences, directoryReferences);
    }

    public static Task UpdateVolumeCheckpointAsync(
        Database db,
        long volumeId,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_volumes SET journal_id = {journalId.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"last_committed_usn = {lastCommittedUsn}, health = {health}, last_checked_utc_ticks = {DateTime.UtcNow.Ticks}, " +
                $"last_error = {error} WHERE id = {volumeId}"),
            cancellationToken);

    // ----- files -----

    public static async Task<ExistingFileRow?> GetFileRowAsync(
        Database db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, path, size_bytes, modified_utc_ticks, status, content_version FROM files WHERE root_id = {rootId} AND path = {path}"),
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new ExistingFileRow(
            result.Current[0].AsInteger,
            result.Current[1].AsText,
            result.Current[2].AsInteger,
            result.Current[3].AsInteger,
            result.Current[4].AsText,
            result.Current[5].IsNull ? string.Empty : result.Current[5].AsText);
    }

    public static async Task<Dictionary<string, ExistingFileRow>> LoadExistingFilesAsync(
        Database db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, ExistingFileRow>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, path, size_bytes, modified_utc_ticks, status, content_version FROM files WHERE root_id = {rootId}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new ExistingFileRow(
                result.Current[0].AsInteger,
                result.Current[1].AsText,
                result.Current[2].AsInteger,
                result.Current[3].AsInteger,
                result.Current[4].AsText,
                result.Current[5].IsNull ? string.Empty : result.Current[5].AsText);
            rows[row.Path] = row;
        }

        return rows;
    }

    public static async Task<long> EnsureFileRowAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        string status,
        string? error,
        IndexedFileIdentity? identity,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var existingByIdentity = identity is null
            ? new List<long>()
            : await ReadIdsAsync(
                db,
                Sql.Format(
                    $"SELECT id FROM files WHERE root_id = {rootId} AND volume_id = {identity.VolumeId} " +
                    $"AND file_reference_number = {identity.FileReferenceNumber}"),
                cancellationToken).ConfigureAwait(false);
        var existingByPath = await ReadIdsAsync(
                db,
                Sql.Format($"SELECT id FROM files WHERE root_id = {rootId} AND path = {path}"),
                cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;
        var fileId = existingByIdentity.FirstOrDefault();
        var pathId = existingByPath.FirstOrDefault();

        if (fileId > 0 && pathId > 0 && fileId != pathId)
            await DeleteFileByIdAsync(db, pathId, cancellationToken).ConfigureAwait(false);

        foreach (var duplicateId in existingByIdentity.Skip(1))
            await DeleteFileByIdAsync(db, duplicateId, cancellationToken).ConfigureAwait(false);

        if (fileId == 0)
            fileId = pathId;

        if (fileId == 0)
        {
            var id = await GetNextIdAsync(db, "files", cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO files (id, root_id, path, file_name, extension, size_bytes, modified_utc_ticks, indexed_utc_ticks, status, error, volume_id, file_reference_number, parent_file_reference_number, last_observed_usn, content_version) " +
                    $"VALUES ({id}, {rootId}, {path}, {fileName}, {extension}, {info.Length}, {info.LastWriteTimeUtc.Ticks}, " +
                    $"{now}, {status}, {error}, {identity?.VolumeId}, {identity?.FileReferenceNumber}, " +
                    $"{identity?.ParentFileReferenceNumber}, {identity?.LastObservedUsn}, {IndexContentVersion.Current})"),
                cancellationToken).ConfigureAwait(false);
            return id;
        }

        await db.ExecuteAsync(
            Sql.Format(
                $"UPDATE files SET path = {path}, file_name = {fileName}, extension = {extension}, size_bytes = {info.Length}, " +
                $"modified_utc_ticks = {info.LastWriteTimeUtc.Ticks}, indexed_utc_ticks = {now}, " +
                $"status = {status}, error = {error}, volume_id = {identity?.VolumeId}, " +
                $"file_reference_number = {identity?.FileReferenceNumber}, " +
                $"parent_file_reference_number = {identity?.ParentFileReferenceNumber}, " +
                $"last_observed_usn = {identity?.LastObservedUsn}, " +
                $"content_version = {IndexContentVersion.Current} WHERE id = {fileId}"),
            cancellationToken).ConfigureAwait(false);
        return fileId;
    }

    public static Task SetFileStatusAsync(
        Database db,
        long fileId,
        string status,
        string? error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format($"UPDATE files SET status = {status}, error = {error}, indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}"),
            cancellationToken);

    /// <summary>Hard-deletes a file row and its lines (no tombstones).</summary>
    public static async Task DeleteFileAsync(
        Database db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        var ids = await ReadIdsAsync(
            db,
            Sql.Format($"SELECT id FROM files WHERE root_id = {rootId} AND path = {path}"),
            cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await DeleteLinesAsync(db, id, cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(Sql.Format($"DELETE FROM files WHERE id = {id}"), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task DeleteFilesByIdentityAsync(
        Database db,
        long volumeId,
        string fileReferenceNumber,
        CancellationToken cancellationToken)
    {
        var ids = await ReadIdsAsync(
            db,
            Sql.Format($"SELECT id FROM files WHERE volume_id = {volumeId} AND file_reference_number = {fileReferenceNumber}"),
            cancellationToken).ConfigureAwait(false);

        foreach (var id in ids)
            await DeleteFileByIdAsync(db, id, cancellationToken).ConfigureAwait(false);
    }

    public static Task<List<long>> ReadFileIdsForRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ReadIdsAsync(db, Sql.Format($"SELECT id FROM files WHERE root_id = {rootId}"), cancellationToken);

    public static Task DeleteFilesForRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM files WHERE root_id = {rootId}"), cancellationToken);

    public static Task<long> CountOkFilesAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        GetCountAsync(db, Sql.Format($"SELECT COUNT(*) FROM files WHERE root_id = {rootId} AND status = {FileStatus.Ok}"), cancellationToken);

    // ----- lines -----

    public static Task DeleteLinesAsync(Database db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM lines WHERE file_id = {fileId}"), cancellationToken);

    private static async Task DeleteFileByIdAsync(Database db, long fileId, CancellationToken cancellationToken)
    {
        await DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(Sql.Format($"DELETE FROM files WHERE id = {fileId}"), cancellationToken).ConfigureAwait(false);
    }

    public static Task DeleteLinesForFilesAsync(Database db, IEnumerable<long> fileIds, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM lines WHERE file_id IN ({new Sql.IdList(fileIds)})"), cancellationToken);

    public static Task<long> CountOkLinesAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        GetCountAsync(
            db,
            Sql.Format($"SELECT COUNT(*) FROM lines l INNER JOIN files f ON f.id = l.file_id WHERE f.root_id = {rootId} AND f.status = {FileStatus.Ok}"),
            cancellationToken);

    public static string SelectLinesSql(long rootId) =>
        SelectLinesColumns +
        Sql.Format($"f.root_id = {rootId} AND f.status = {FileStatus.Ok}") +
        SelectLinesOrder;

    public static string SelectLinesSql(long rootId, IReadOnlyList<long> lineIds) =>
        SelectLinesColumns +
        Sql.Format($"f.root_id = {rootId} AND f.status = {FileStatus.Ok} AND l.id IN ({new Sql.IdList(lineIds)})") +
        SelectLinesOrder;

    public static async IAsyncEnumerable<IndexedLine> ReadLinesAsync(
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

    // ----- pending_changes -----

    public static async Task UpsertPendingChangeAsync(
        Database db,
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == IndexChangeKind.RefreshRoot && path is null)
        {
            await DeletePendingChangesForRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.ExecuteAsync(
                Sql.Format($"DELETE FROM pending_changes WHERE root_path = {root}") + " AND " + PendingPathPredicate(path),
                cancellationToken).ConfigureAwait(false);
        }

        var id = await GetNextIdAsync(db, "pending_changes", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            Sql.Format($"INSERT INTO pending_changes VALUES ({id}, {root}, {path}, {(long)kind}, {DateTime.UtcNow.Ticks})"),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<PendingIndexChange>> ReadPendingChangesAsync(Database db, CancellationToken cancellationToken)
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
                result.Current[2].IsNull ? null : result.Current[2].AsText,
                (IndexChangeKind)result.Current[3].AsInteger));
        }

        return changes;
    }

    public static Task DeletePendingChangeAsync(
        Database db,
        string root,
        string? path,
        IndexChangeKind kind,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format($"DELETE FROM pending_changes WHERE root_path = {root}") +
            " AND " + PendingPathPredicate(path) +
            Sql.Format($" AND kind = {(long)kind}"),
            cancellationToken);

    public static Task DeletePendingChangesForRootAsync(Database db, string root, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM pending_changes WHERE root_path = {root}"), cancellationToken);

    // ----- shared helpers -----

    private static string PendingPathPredicate(string? path) =>
        path is null
            ? "path IS NULL"
            : Sql.Format($"path = {path}");

    public static async Task<long> GetNextIdAsync(Database db, string tableName, CancellationToken cancellationToken)
    {
        // Table names come from internal constants, but validate anyway so
        // this hole can never carry SQL even if a caller misuses it.
        var table = new Sql.Identifier(tableName);
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT MAX(id) FROM {table}"),
            cancellationToken).ConfigureAwait(false);
        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return 1;

        return result.Current[0].IsNull ? 1 : result.Current[0].AsInteger + 1;
    }

    private static async Task ExecuteAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private static int Bool(bool value) => value ? 1 : 0;

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
}
