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

internal sealed record ExistingFileRow(long Id, string Path, long SizeBytes, long ModifiedUtcTicks, string Status);

internal sealed record RootRow(long Id, long IndexedUtcTicks, string OptionsHash);

internal sealed record IndexedLine(
    string Path,
    string FileName,
    string Extension,
    long SizeBytes,
    long ModifiedUtcTicks,
    int LineNumber,
    string Content);

/// <summary>
/// Every DML statement against the index tables lives here, with
/// <see cref="SqlText"/> as the single escaping choke point so a forgotten
/// escape can't slip into an ad-hoc query elsewhere. Schema DDL lives in
/// <see cref="IndexDatabase"/>.
/// </summary>
internal static class IndexTables
{
    public static string SqlText(string? value) =>
        value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // ----- index_roots -----

    public static async Task<long> EnsureRootAsync(Database db, string root, string profile, CancellationToken cancellationToken)
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

    public static async Task<RootRow?> GetRootAsync(Database db, string root, CancellationToken cancellationToken)
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

    public static Task MarkRootRefreshedAsync(Database db, long rootId, string profile, CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            $"UPDATE index_roots SET indexed_utc_ticks = {DateTime.UtcNow.Ticks}, options_hash = {SqlText(profile)} WHERE id = {rootId}",
            cancellationToken);

    public static Task DeleteRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, $"DELETE FROM index_roots WHERE id = {rootId}", cancellationToken);

    // ----- files -----

    public static async Task<ExistingFileRow?> GetFileRowAsync(
        Database db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            $"SELECT id, path, size_bytes, modified_utc_ticks, status FROM files WHERE root_id = {rootId} AND path = {SqlText(path)}",
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new ExistingFileRow(
            result.Current[0].AsInteger,
            result.Current[1].AsText,
            result.Current[2].AsInteger,
            result.Current[3].AsInteger,
            result.Current[4].AsText);
    }

    public static async Task<Dictionary<string, ExistingFileRow>> LoadExistingFilesAsync(
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

    public static async Task<long> EnsureFileRowAsync(
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

    public static Task SetFileStatusAsync(
        Database db,
        long fileId,
        string status,
        string? error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            $"UPDATE files SET status = {SqlText(status)}, error = {SqlText(error)}, indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}",
            cancellationToken);

    /// <summary>Hard-deletes a file row and its lines (no tombstones).</summary>
    public static async Task DeleteFileAsync(
        Database db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        var ids = await ReadIdsAsync(db, $"SELECT id FROM files WHERE root_id = {rootId} AND path = {SqlText(path)}", cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await DeleteLinesAsync(db, id, cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync($"DELETE FROM files WHERE id = {id}", cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task<List<long>> ReadFileIdsForRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ReadIdsAsync(db, $"SELECT id FROM files WHERE root_id = {rootId}", cancellationToken);

    public static Task DeleteFilesForRootAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, $"DELETE FROM files WHERE root_id = {rootId}", cancellationToken);

    public static Task<long> CountOkFilesAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        GetCountAsync(db, $"SELECT COUNT(*) FROM files WHERE root_id = {rootId} AND status = '{FileStatus.Ok}'", cancellationToken);

    // ----- lines -----

    public static Task DeleteLinesAsync(Database db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, $"DELETE FROM lines WHERE file_id = {fileId}", cancellationToken);

    public static Task DeleteLinesForFilesAsync(Database db, IEnumerable<long> fileIds, CancellationToken cancellationToken) =>
        ExecuteAsync(db, $"DELETE FROM lines WHERE file_id IN ({string.Join(",", fileIds)})", cancellationToken);

    public static Task<long> CountOkLinesAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        GetCountAsync(
            db,
            $"SELECT COUNT(*) FROM lines l INNER JOIN files f ON f.id = l.file_id WHERE f.root_id = {rootId} AND f.status = '{FileStatus.Ok}'",
            cancellationToken);

    public static string SelectLinesSql(long rootId, string linePredicate) =>
        "SELECT f.path, f.file_name, f.extension, f.size_bytes, f.modified_utc_ticks, l.line_number, l.content " +
        "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
        $"WHERE f.root_id = {rootId} AND f.status = '{FileStatus.Ok}' {linePredicate} " +
        "ORDER BY f.path, l.line_number";

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
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(
            $"DELETE FROM pending_changes WHERE root_path = {SqlText(root)} AND path = {SqlText(path)}",
            cancellationToken).ConfigureAwait(false);

        var id = await GetNextIdAsync(db, "pending_changes", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            "INSERT INTO pending_changes VALUES (" +
            $"{id}, {SqlText(root)}, {SqlText(path)}, {(long)kind}, {DateTime.UtcNow.Ticks})",
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
                result.Current[2].AsText,
                (IndexChangeKind)result.Current[3].AsInteger));
        }

        return changes;
    }

    public static Task DeletePendingChangeAsync(
        Database db,
        string root,
        string path,
        IndexChangeKind kind,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            "DELETE FROM pending_changes WHERE " +
            $"root_path = {SqlText(root)} AND path = {SqlText(path)} AND kind = {(long)kind}",
            cancellationToken);

    public static Task DeletePendingChangesForRootAsync(Database db, string root, CancellationToken cancellationToken) =>
        ExecuteAsync(db, $"DELETE FROM pending_changes WHERE root_path = {SqlText(root)}", cancellationToken);

    // ----- shared helpers -----

    public static async Task<long> GetNextIdAsync(Database db, string tableName, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync($"SELECT MAX(id) FROM {tableName}", cancellationToken).ConfigureAwait(false);
        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return 1;

        return result.Current[0].IsNull ? 1 : result.Current[0].AsInteger + 1;
    }

    private static async Task ExecuteAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
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
}
