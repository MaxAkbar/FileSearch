using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Indexing;

/// <summary>Values stored in the files.status column.</summary>
internal static class FileStatus
{
    public const string Ok = "ok";
    public const string Indexing = "indexing";
    public const string Skipped = "skipped";
    public const string Error = "error";
    public const string Stale = "stale";
}

internal sealed record ExistingFileRow(
    long Id,
    string Path,
    long SizeBytes,
    long CreatedUtcTicks,
    long ModifiedUtcTicks,
    long Attributes,
    string Status,
    string ContentVersion,
    string ExtractorId,
    string ExtractorVersion,
    long ExtractionAttemptCount);

internal sealed record InsertedFileRow(long Id, IReadOnlyList<string> ReplacedPaths);

internal sealed record RootRow(
    long Id,
    long IndexedUtcTicks,
    string OptionsHash,
    long? VolumeId,
    string? RootFileReferenceNumber,
    string? RootParentFileReferenceNumber,
    string ContentVersion,
    long LastFullScanUtcTicks,
    long LastFullValidationUtcTicks,
    string LastValidationStatus,
    string? LastValidationMessage,
    long LastValidationFilesChecked,
    long LastValidationMissingFromIndexCount,
    long LastValidationChangedCount,
    long LastValidationMissingFromDiskCount,
    long LastValidationFailedCount);

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
    long CreatedUtcTicks,
    long ModifiedUtcTicks,
    string Status,
    string ExtractorId,
    string FileTypeCategory,
    int LineNumber,
    string Content,
    SourceAnchor? Anchor,
    long? ContentUnitId,
    ContentUnitKind ContentUnitKind,
    SourceLocator? Locator,
    string ContentHash,
    string Language,
    string UnitExtractorId,
    string UnitExtractorVersion);

internal sealed record IndexedFileMetadata(
    string Path,
    string DirectoryPath,
    string FileName,
    string Extension,
    long SizeBytes,
    long CreatedUtcTicks,
    long ModifiedUtcTicks,
    long Attributes,
    string FileTypeCategory,
    long OpenCount,
    long LastOpenedUtcTicks,
    string Status,
    string ExtractorId);

internal sealed record LineTrigramSource(long Id, string Content);

internal sealed record CachedIndexedLine(long Id, long FileId, IndexedLine Line);

internal sealed record FileChangeRow(long Id, string Path, string Status);

/// <summary>
/// Every DML statement against the index tables lives here, composed through
/// <see cref="Sql.Format"/> so values can't reach the SQL text unescaped —
/// the handler has no raw-string hole to forget. Schema DDL lives in
/// <see cref="IndexDatabase"/>.
/// </summary>
internal static partial class IndexTables
{
    private static readonly JsonSerializerOptions s_anchorJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly JsonSerializerOptions s_locatorJsonOptions = new();

    private const int MetadataTokenPrefixMinLength = 2;
    private const int MetadataTokenPrefixMaxLength = 8;
    private const int MetadataTokenInsertBatchSize = 512;
    private const int DeleteIdBatchSize = 500;

    private const string SelectLinesColumns =
        "SELECT f.path, f.file_name, f.extension, f.size_bytes, f.created_utc_ticks, f.modified_utc_ticks, " +
        "f.status, f.extractor_id, f.file_type_category, l.line_number, l.content, l.anchor_json, " +
        "l.content_unit_id " +
        "FROM lines l INNER JOIN files f ON f.id = l.file_id WHERE ";

    private const string SelectLinesOrder = " ORDER BY f.path, l.line_number";

    private const string SelectContentUnitColumns =
        "SELECT u.id, u.file_id, u.kind, u.locator_json, u.unit_text, u.content_hash, " +
        "u.language, u.extractor_id, u.extractor_version FROM content_units u ";

    // ----- index_roots -----

    public static async Task<long> EnsureRootAsync(DbExec db, string root, string profile, CancellationToken cancellationToken)
    {
        var existing = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing.Id;

        var id = await GetNextIdAsync(db, "index_roots", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO index_roots (id, root_path, indexed_utc_ticks, options_hash, volume_id, last_full_scan_utc_ticks, root_file_reference_number, root_parent_file_reference_number, content_version, location_kind, update_strategy, strategy_warning, usn_catch_up_enabled, watcher_recommended, last_full_validation_utc_ticks, last_validation_status, last_validation_message, last_validation_files_checked, last_validation_missing_from_index_count, last_validation_changed_count, last_validation_missing_from_disk_count, last_validation_failed_count) " +
                $"VALUES ({id}, {root}, 0, {profile}, {(long?)null}, {(long?)null}, {(string?)null}, {(string?)null}, {IndexContentVersion.Current}, {IndexLocationKind.Unknown.ToString()}, {IndexUpdateStrategy.Unknown.ToString()}, {(string?)null}, 0, 1, 0, {"never"}, {(string?)null}, 0, 0, 0, 0, 0)"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public static async Task<RootRow?> GetRootAsync(DbExec db, string root, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT id, indexed_utc_ticks, options_hash, volume_id, root_file_reference_number, " +
                $"root_parent_file_reference_number, content_version, last_full_scan_utc_ticks, " +
                $"last_full_validation_utc_ticks, last_validation_status, last_validation_message, " +
                $"last_validation_files_checked, last_validation_missing_from_index_count, " +
                $"last_validation_changed_count, last_validation_missing_from_disk_count, " +
                $"last_validation_failed_count FROM index_roots WHERE root_path = {root}"),
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
            result.Current[6].IsNull ? string.Empty : result.Current[6].AsText,
            result.Current[7].IsNull ? 0 : result.Current[7].AsInteger,
            result.Current[8].IsNull ? 0 : result.Current[8].AsInteger,
            result.Current[9].IsNull ? string.Empty : result.Current[9].AsText,
            result.Current[10].IsNull ? null : result.Current[10].AsText,
            result.Current[11].IsNull ? 0 : result.Current[11].AsInteger,
            result.Current[12].IsNull ? 0 : result.Current[12].AsInteger,
            result.Current[13].IsNull ? 0 : result.Current[13].AsInteger,
            result.Current[14].IsNull ? 0 : result.Current[14].AsInteger,
            result.Current[15].IsNull ? 0 : result.Current[15].AsInteger);
    }

    public static async Task<IndexRootIdentity?> GetRootIdentityAsync(
        DbExec db,
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

    public static async Task<long?> GetRootIdAsync(DbExec db, string root, CancellationToken cancellationToken)
    {
        var row = await GetRootAsync(db, root, cancellationToken).ConfigureAwait(false);
        return row?.Id;
    }

    public static async Task<List<string>> ListRootPathsAsync(DbExec db, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        await using var result = await db.ExecuteAsync(
            "SELECT root_path FROM index_roots ORDER BY root_path",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            roots.Add(result.Current[0].AsText);

        return roots;
    }

    public static Task MarkRootRefreshStartedAsync(DbExec db, long rootId, string profile, CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format($"UPDATE index_roots SET indexed_utc_ticks = 0, options_hash = {profile}, content_version = {IndexContentVersion.Current} WHERE id = {rootId}"),
            cancellationToken);

    public static Task MarkRootRefreshedAsync(DbExec db, long rootId, string profile, CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_roots SET indexed_utc_ticks = {DateTime.UtcNow.Ticks}, " +
                $"options_hash = {profile}, content_version = {IndexContentVersion.Current}, " +
                $"last_full_scan_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {rootId}"),
            cancellationToken);

    public static Task MarkRootValidatedAsync(
        DbExec db,
        long rootId,
        IndexValidationResult result,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_roots SET last_full_validation_utc_ticks = {result.CheckedUtc.Ticks}, " +
                $"last_validation_status = {result.Status.ToString()}, " +
                $"last_validation_message = {result.Message}, " +
                $"last_validation_files_checked = {result.FilesChecked}, " +
                $"last_validation_missing_from_index_count = {result.MissingFromIndex}, " +
                $"last_validation_changed_count = {result.ChangedSinceIndex}, " +
                $"last_validation_missing_from_disk_count = {result.MissingFromDisk}, " +
                $"last_validation_failed_count = {result.FailedChecks} WHERE id = {rootId}"),
            cancellationToken);

    public static async Task DeleteRootAsync(DbExec db, long rootId, CancellationToken cancellationToken)
    {
        await DeleteValidationDriftsForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM index_roots WHERE id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task SetRootVolumeAsync(
        DbExec db,
        long rootId,
        long volumeId,
        IndexedFileIdentity? rootIdentity,
        IndexLocationStrategy strategy,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE index_roots SET volume_id = {volumeId}, " +
                $"root_file_reference_number = {rootIdentity?.FileReferenceNumber}, " +
                $"root_parent_file_reference_number = {rootIdentity?.ParentFileReferenceNumber}, " +
                $"location_kind = {strategy.LocationKind.ToString()}, " +
                $"update_strategy = {strategy.UpdateStrategy.ToString()}, " +
                $"strategy_warning = {NullIfEmpty(strategy.Warning)}, " +
                $"usn_catch_up_enabled = {Bool(strategy.UsnCatchUpEnabled)}, " +
                $"watcher_recommended = {Bool(strategy.WatcherRecommended)} " +
                $"WHERE id = {rootId}"),
            cancellationToken);

    public static async Task<List<IndexRootStrategyInfo>> ListRootStrategiesAsync(
        DbExec db,
        CancellationToken cancellationToken)
    {
        var strategies = new List<IndexRootStrategyInfo>();
        await using var result = await db.ExecuteAsync(
            "SELECT root_path, location_kind, update_strategy, strategy_warning, usn_catch_up_enabled, watcher_recommended " +
            "FROM index_roots ORDER BY root_path",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            var kind = ParseEnum(row[1].IsNull ? null : row[1].AsText, IndexLocationKind.Unknown);
            var updateStrategy = ParseEnum(row[2].IsNull ? null : row[2].AsText, IndexUpdateStrategy.Unknown);
            var warning = row[3].IsNull ? string.Empty : row[3].AsText;
            var strategy = IndexLocationStrategyResolver.FromStored(
                kind,
                updateStrategy,
                warning,
                row[4].AsInteger != 0,
                row[5].AsInteger != 0);
            strategies.Add(new IndexRootStrategyInfo(
                row[0].AsText,
                kind,
                updateStrategy,
                strategy.StrategyLabel,
                warning,
                strategy.UsnCatchUpEnabled,
                strategy.WatcherRecommended));
        }

        return strategies;
    }

    // ----- index_directories -----

    public static Task DeleteDirectoriesForRootAsync(DbExec db, long rootId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM index_directories WHERE root_id = {rootId}"), cancellationToken);

    public static async Task EnsureDirectoryAsync(
        DbExec db,
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
            return;

        var id = await GetNextIdAsync(db, "index_directories", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            Sql.Format(
                $"INSERT INTO index_directories (id, root_id, path, volume_id, directory_reference_number, parent_file_reference_number, observed_utc_ticks) " +
                $"VALUES ({id}, {rootId}, {path}, {identity.VolumeId}, {identity.FileReferenceNumber}, {identity.ParentFileReferenceNumber}, {DateTime.UtcNow.Ticks})"),
            cancellationToken).ConfigureAwait(false);
    }

    // ----- index_volumes -----

    public static async Task<long> EnsureVolumeAsync(
        DbExec db,
        IndexVolumeInfo volume,
        CancellationToken cancellationToken)
    {
        var existing = await GetVolumeRowAsync(db, volume.VolumeKey, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;
        if (existing is not null)
            return existing.Id;

        var id = await GetNextIdAsync(db, "index_volumes", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO index_volumes (id, volume_key, volume_serial, filesystem_name, is_remote, usn_supported, drive_kind, journal_id, last_committed_usn, health, last_checked_utc_ticks, last_error) " +
                $"VALUES ({id}, {volume.VolumeKey}, {volume.VolumeSerial}, {volume.FileSystemName}, {Bool(volume.IsRemote)}, {Bool(volume.UsnSupported)}, {volume.DriveKind.ToString()}, {(string?)null}, 0, {"unknown"}, {now}, {(string?)null})"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public static async Task<VolumeRow?> GetVolumeRowAsync(
        DbExec db,
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

    public static async Task<string?> GetVolumeKeyAsync(
        DbExec db,
        long volumeId,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT volume_key FROM index_volumes WHERE id = {volumeId}"),
            cancellationToken).ConfigureAwait(false);

        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsText
            : null;
    }

    public static async Task<List<IndexVolumeHealthInfo>> ListVolumeHealthAsync(
        DbExec db,
        CancellationToken cancellationToken)
    {
        var volumes = new List<IndexVolumeHealthInfo>();
        await using var result = await db.ExecuteAsync(
            "SELECT volume_key, filesystem_name, is_remote, usn_supported, drive_kind, journal_id, " +
            "last_committed_usn, health, last_error, last_checked_utc_ticks " +
            "FROM index_volumes ORDER BY volume_key",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            var journalText = row[5].IsNull ? null : row[5].AsText;
            var lastCheckedTicks = row[9].IsNull ? 0 : row[9].AsInteger;
            volumes.Add(new IndexVolumeHealthInfo(
                row[0].AsText,
                row[1].AsText,
                row[2].AsInteger != 0,
                row[3].AsInteger != 0,
                ulong.TryParse(journalText, out var journalId) ? journalId : null,
                row[6].AsInteger,
                row[7].AsText,
                row[8].IsNull ? null : row[8].AsText,
                lastCheckedTicks > 0 ? new DateTime(lastCheckedTicks, DateTimeKind.Utc) : null,
                row[4].IsNull ? IndexVolumeDriveKind.Unknown.ToString() : row[4].AsText));
        }

        return volumes;
    }

    public static async Task<IndexReplayReferenceSet> ReadReplayReferencesAsync(
        DbExec db,
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
        DbExec db,
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
        DbExec db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        ExistingFileRow? latest = null;
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT id, path, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, status, content_version, extractor_id, extractor_version, extraction_attempt_count " +
                $"FROM files WHERE root_id = {rootId} AND path = {path} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadExistingFileRow(result.Current);
            if (latest is null || row.Id > latest.Id)
                latest = row;
        }

        return latest is not null && !IsStaleStatus(latest.Status) ? latest : null;
    }

    public static async Task<Dictionary<string, ExistingFileRow>> LoadExistingFilesAsync(
        DbExec db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var latestRows = new Dictionary<string, ExistingFileRow>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT id, path, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, status, content_version, extractor_id, extractor_version, extraction_attempt_count " +
                $"FROM files WHERE root_id = {rootId} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadExistingFileRow(result.Current);
            if (!latestRows.TryGetValue(row.Path, out var existing) || row.Id > existing.Id)
                latestRows[row.Path] = row;
        }

        var rows = new Dictionary<string, ExistingFileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, row) in latestRows)
        {
            if (!IsStaleStatus(row.Status))
                rows[path] = row;
        }

        return rows;
    }

    private static ExistingFileRow ReadExistingFileRow(DbValue[] row) =>
        new(
            row[0].AsInteger,
            row[1].AsText,
            row[2].AsInteger,
            row[3].AsInteger,
            row[4].AsInteger,
            row[5].AsInteger,
            row[6].AsText,
            row[7].IsNull ? string.Empty : row[7].AsText,
            row[8].IsNull ? string.Empty : row[8].AsText,
            row[9].IsNull ? string.Empty : row[9].AsText,
            row[10].IsNull ? 0 : row[10].AsInteger);

    /// <summary>
    /// Inserts a new file-version row. Changed-file indexing is append-only:
    /// readers resolve the latest row per root/path, and delete/skip paths
    /// append tombstones instead of updating or deleting old rows.
    /// </summary>
    public static async Task<InsertedFileRow> InsertFileRowAsync(
        Database db,
        long rootId,
        string path,
        FileInfo info,
        string status,
        string? error,
        IndexedFileIdentity? identity,
        string extractorId,
        string extractorVersion,
        long extractionAttemptCount,
        long lastExtractionAttemptUtcTicks,
        CancellationToken cancellationToken)
    {
        List<string> replacedPaths = [];
        if (identity is not null)
        {
            replacedPaths = await InsertIdentityTombstonesAsync(
                    db,
                    rootId,
                    path,
                    identity.VolumeId,
                    identity.FileReferenceNumber,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var values = CreateFileRowValues(
            rootId,
            path,
            info.Length,
            info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks,
            (long)info.Attributes,
            status,
            error,
            identity,
            extractorId,
            extractorVersion,
            extractionAttemptCount,
            lastExtractionAttemptUtcTicks);
        var id = await InsertFileRowValuesAsync(db, values, cancellationToken).ConfigureAwait(false);
        if (!IsStaleStatus(status) && !string.Equals(status, FileStatus.Indexing, StringComparison.OrdinalIgnoreCase))
        {
            await InsertMetadataTokensAsync(
                    db,
                    rootId,
                    id,
                    path,
                    values.DirectoryPath,
                    values.FileName,
                    values.Extension,
                    values.FileTypeCategory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new InsertedFileRow(id, replacedPaths);
    }

    private static async Task<List<string>> InsertIdentityTombstonesAsync(
        DbExec db,
        long rootId,
        string currentPath,
        long volumeId,
        string fileReferenceNumber,
        CancellationToken cancellationToken)
    {
        var latestRows = new Dictionary<string, (long Id, string Status)>(StringComparer.OrdinalIgnoreCase);
        var replacedPaths = new List<string>();
        await using (var result = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT id, path, status FROM files WHERE root_id = {rootId} AND volume_id = {volumeId} " +
                    $"AND file_reference_number = {fileReferenceNumber} AND status != {FileStatus.Indexing}"),
                cancellationToken).ConfigureAwait(false))
        {
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = result.Current[1].AsText;
                var row = (Id: result.Current[0].AsInteger, Status: result.Current[2].AsText);
                if (!latestRows.TryGetValue(path, out var existing) || row.Id > existing.Id)
                    latestRows[path] = row;
            }
        }

        foreach (var (path, row) in latestRows)
        {
            if (!IsStaleStatus(row.Status) &&
                !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                await InsertFileTombstoneAsync(db, rootId, path, identity: null, cancellationToken).ConfigureAwait(false);
                replacedPaths.Add(path);
            }
        }

        return replacedPaths;
    }

    public static async Task<long> InsertFileTombstoneAsync(
        DbExec db,
        long rootId,
        string path,
        IndexedFileIdentity? identity,
        CancellationToken cancellationToken)
    {
        var values = CreateFileRowValues(
            rootId,
            path,
            sizeBytes: 0,
            createdUtcTicks: 0,
            modifiedUtcTicks: 0,
            attributes: 0,
            status: FileStatus.Stale,
            error: null,
            identity,
            extractorId: string.Empty,
            extractorVersion: string.Empty,
            extractionAttemptCount: 0,
            lastExtractionAttemptUtcTicks: 0);
        return await InsertFileRowValuesAsync(db, values, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> InsertFileRowValuesAsync(
        DbExec db,
        FileRowValues values,
        CancellationToken cancellationToken)
    {
        var id = await GetNextIdAsync(db, "files", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            Sql.Format(
                $"INSERT INTO files (id, root_id, path, path_lower, directory_path, directory_path_lower, file_name, file_name_lower, extension, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, file_type_category, indexed_utc_ticks, status, error, volume_id, file_reference_number, parent_file_reference_number, last_observed_usn, content_version, open_count, last_opened_utc_ticks, extractor_id, extractor_version, extraction_attempt_count, last_extraction_attempt_utc_ticks) " +
                $"VALUES ({id}, {values.RootId}, {values.Path}, {values.PathLower}, {values.DirectoryPath}, {values.DirectoryPathLower}, {values.FileName}, {values.FileNameLower}, {values.Extension}, {values.SizeBytes}, {values.CreatedUtcTicks}, {values.ModifiedUtcTicks}, {values.Attributes}, {values.FileTypeCategory}, {DateTime.UtcNow.Ticks}, {values.Status}, {values.Error}, {values.Identity?.VolumeId}, {values.Identity?.FileReferenceNumber}, " +
                $"{values.Identity?.ParentFileReferenceNumber}, {values.Identity?.LastObservedUsn}, {IndexContentVersion.Current}, 0, 0, {values.ExtractorId}, {values.ExtractorVersion}, {values.ExtractionAttemptCount}, {values.LastExtractionAttemptUtcTicks})"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    private static FileRowValues CreateFileRowValues(
        long rootId,
        string path,
        long sizeBytes,
        long createdUtcTicks,
        long modifiedUtcTicks,
        long attributes,
        string status,
        string? error,
        IndexedFileIdentity? identity,
        string extractorId,
        string extractorVersion,
        long extractionAttemptCount,
        long lastExtractionAttemptUtcTicks)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var directoryPath = Path.GetDirectoryName(path) ?? string.Empty;
        return new FileRowValues(
            rootId,
            path,
            path.ToLowerInvariant(),
            directoryPath,
            directoryPath.ToLowerInvariant(),
            fileName,
            fileName.ToLowerInvariant(),
            extension,
            sizeBytes,
            createdUtcTicks,
            modifiedUtcTicks,
            attributes,
            FileTypeCategory.ForExtension(extension),
            status,
            error,
            identity,
            extractorId,
            extractorVersion,
            extractionAttemptCount,
            lastExtractionAttemptUtcTicks);
    }

    private sealed record FileRowValues(
        long RootId,
        string Path,
        string PathLower,
        string DirectoryPath,
        string DirectoryPathLower,
        string FileName,
        string FileNameLower,
        string Extension,
        long SizeBytes,
        long CreatedUtcTicks,
        long ModifiedUtcTicks,
        long Attributes,
        string FileTypeCategory,
        string Status,
        string? Error,
        IndexedFileIdentity? Identity,
        string ExtractorId,
        string ExtractorVersion,
        long ExtractionAttemptCount,
        long LastExtractionAttemptUtcTicks);

    public static Task SupersedeFileRowAsync(
        DbExec db,
        long fileId,
        string originalPath,
        CancellationToken cancellationToken)
    {
        var tombstonePath = $"{originalPath}.__filesearch_stale_{fileId}";
        var fileName = Path.GetFileName(tombstonePath);
        var directoryPath = Path.GetDirectoryName(tombstonePath) ?? string.Empty;
        return ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE files SET path = {tombstonePath}, path_lower = {tombstonePath.ToLowerInvariant()}, " +
                $"directory_path = {directoryPath}, directory_path_lower = {directoryPath.ToLowerInvariant()}, " +
                $"file_name = {fileName}, file_name_lower = {fileName.ToLowerInvariant()}, " +
                $"status = {FileStatus.Stale}, indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}"),
            cancellationToken);
    }

    public static async Task<List<long>> ReadMetadataCandidateFileIdsAsync(
        DbExec db,
        long rootId,
        IReadOnlyList<string> tokens,
        bool requireAllTokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Count == 0)
            return new List<long>();

        HashSet<long>? candidates = null;
        foreach (var token in tokens)
        {
            List<long> matches;
            try
            {
                matches = await ReadIdsAsync(
                        db,
                        Sql.Format(
                            $"SELECT file_id FROM file_metadata_tokens WHERE token = {token} AND root_id = {rootId}"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CSharpDbException)
            {
                // The token table is an accelerator; callers can scan files.
                return new List<long>();
            }

            var set = matches.ToHashSet();

            if (candidates is null)
            {
                candidates = set;
            }
            else if (requireAllTokens)
            {
                candidates.IntersectWith(set);
            }
            else
            {
                candidates.UnionWith(set);
            }

            if (requireAllTokens && candidates.Count == 0)
                return new List<long>();
        }

        return candidates?.ToList() ?? new List<long>();
    }

    public static async IAsyncEnumerable<IndexedFileMetadata> ReadFileMetadataAsync(
        DbExec db,
        long rootId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, (long Id, IndexedFileMetadata File)>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT id, path, directory_path, file_name, extension, size_bytes, created_utc_ticks, " +
                $"modified_utc_ticks, attributes, file_type_category, open_count, last_opened_utc_ticks, status, extractor_id " +
                $"FROM files WHERE root_id = {rootId} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            var id = row[0].AsInteger;
            var file = new IndexedFileMetadata(
                row[1].AsText,
                row[2].IsNull ? string.Empty : row[2].AsText,
                row[3].AsText,
                row[4].AsText,
                row[5].AsInteger,
                row[6].IsNull ? 0 : row[6].AsInteger,
                row[7].AsInteger,
                row[8].IsNull ? 0 : row[8].AsInteger,
                row[9].IsNull ? string.Empty : row[9].AsText,
                row[10].IsNull ? 0 : row[10].AsInteger,
                row[11].IsNull ? 0 : row[11].AsInteger,
                row[12].AsText,
                row[13].IsNull ? string.Empty : row[13].AsText);
            if (!latest.TryGetValue(file.Path, out var existing) || id > existing.Id)
                latest[file.Path] = (id, file);
        }

        foreach (var file in latest.Values
                     .Where(static item => !IsStaleStatus(item.File.Status))
                     .Select(static item => item.File)
                     .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    public static async IAsyncEnumerable<IndexedFileMetadata> ReadFileMetadataAsync(
        DbExec db,
        long rootId,
        IReadOnlyList<long> fileIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidateRows = new List<(long Id, IndexedFileMetadata File)>();
        foreach (var batch in fileIds.Chunk(500))
        {
            await using var result = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT id, path, directory_path, file_name, extension, size_bytes, created_utc_ticks, " +
                    $"modified_utc_ticks, attributes, file_type_category, open_count, last_opened_utc_ticks, status, extractor_id " +
                    $"FROM files WHERE root_id = {rootId} AND id IN ({new Sql.IdList(batch)}) AND status != {FileStatus.Indexing} AND status != {FileStatus.Stale}"),
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = result.Current;
                candidateRows.Add((
                    row[0].AsInteger,
                    new IndexedFileMetadata(
                        row[1].AsText,
                        row[2].IsNull ? string.Empty : row[2].AsText,
                        row[3].AsText,
                        row[4].AsText,
                        row[5].AsInteger,
                        row[6].IsNull ? 0 : row[6].AsInteger,
                        row[7].AsInteger,
                        row[8].IsNull ? 0 : row[8].AsInteger,
                        row[9].IsNull ? string.Empty : row[9].AsText,
                        row[10].IsNull ? 0 : row[10].AsInteger,
                        row[11].IsNull ? 0 : row[11].AsInteger,
                        row[12].AsText,
                        row[13].IsNull ? string.Empty : row[13].AsText)));
            }
        }

        foreach (var group in candidateRows.GroupBy(row => row.File.Path, StringComparer.OrdinalIgnoreCase))
        {
            var latestCandidate = group.MaxBy(static row => row.Id);
            var currentId = await ReadFileIdAsync(db, rootId, latestCandidate.File.Path, cancellationToken).ConfigureAwait(false);
            if (currentId == latestCandidate.Id)
                yield return latestCandidate.File;
        }
    }

    public static Task SetFileStatusAsync(
        DbExec db,
        long fileId,
        string status,
        string? error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format($"UPDATE files SET status = {status}, error = {error}, indexed_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}"),
            cancellationToken);

    public static Task RecordExtractionAttemptAsync(
        DbExec db,
        long fileId,
        string extractorId,
        string extractorVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            db,
            Sql.Format(
                $"UPDATE files SET extractor_id = {extractorId}, extractor_version = {extractorVersion}, " +
                $"extraction_attempt_count = extraction_attempt_count + 1, " +
                $"last_extraction_attempt_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {fileId}"),
            cancellationToken);

    public static async Task<IReadOnlyList<IndexFailureInfo>> ListFailedFilesAsync(
        DbExec db,
        CancellationToken cancellationToken)
    {
        var failures = new List<IndexFailureInfo>();
        var activeIds = await ReadCurrentFileIdsAsync(db, IsVisibleCurrentStatus, cancellationToken).ConfigureAwait(false);

        // Each result gets its own scope: reader sessions allow only one
        // active query, so the first result must be disposed before the
        // second query executes.
        foreach (var batch in activeIds.Chunk(DeleteIdBatchSize))
        {
            await using var result = await db.ExecuteAsync(
                    Sql.Format(
                        $"SELECT r.root_path, f.path, f.extractor_id, f.extractor_version, f.error, " +
                        $"f.extraction_attempt_count, f.last_extraction_attempt_utc_ticks " +
                        $"FROM files f INNER JOIN index_roots r ON r.id = f.root_id " +
                        $"WHERE f.id IN ({new Sql.IdList(batch)}) AND f.status = {FileStatus.Error} ORDER BY f.path"),
                    cancellationToken).ConfigureAwait(false);
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var lastAttemptTicks = result.Current[6].IsNull ? 0 : result.Current[6].AsInteger;
                failures.Add(new IndexFailureInfo(
                    result.Current[0].AsText,
                    result.Current[1].AsText,
                    result.Current[2].IsNull ? string.Empty : result.Current[2].AsText,
                    result.Current[3].IsNull ? string.Empty : result.Current[3].AsText,
                    result.Current[4].IsNull ? string.Empty : result.Current[4].AsText,
                    result.Current[5].IsNull ? 0 : result.Current[5].AsInteger,
                    lastAttemptTicks > 0
                        ? new DateTime(lastAttemptTicks, DateTimeKind.Utc)
                        : null));
            }
        }

        foreach (var batch in activeIds.Chunk(DeleteIdBatchSize))
        {
            await using var issueResult = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT r.root_path, f.path, f.extractor_id, f.extractor_version, i.member_path, " +
                    $"i.code, i.message, i.severity, f.extraction_attempt_count, f.last_extraction_attempt_utc_ticks " +
                    $"FROM extraction_issues i INNER JOIN files f ON f.id = i.file_id " +
                    $"INNER JOIN index_roots r ON r.id = f.root_id WHERE f.id IN ({new Sql.IdList(batch)}) ORDER BY f.path, i.member_path"),
                cancellationToken).ConfigureAwait(false);

            while (await issueResult.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var lastAttemptTicks = issueResult.Current[9].IsNull ? 0 : issueResult.Current[9].AsInteger;
                var code = issueResult.Current[5].IsNull ? string.Empty : issueResult.Current[5].AsText;
                var message = issueResult.Current[6].IsNull ? string.Empty : issueResult.Current[6].AsText;
                failures.Add(new IndexFailureInfo(
                    issueResult.Current[0].AsText,
                    issueResult.Current[1].AsText,
                    issueResult.Current[2].IsNull ? string.Empty : issueResult.Current[2].AsText,
                    issueResult.Current[3].IsNull ? string.Empty : issueResult.Current[3].AsText,
                    message,
                    issueResult.Current[8].IsNull ? 0 : issueResult.Current[8].AsInteger,
                    lastAttemptTicks > 0
                        ? new DateTime(lastAttemptTicks, DateTimeKind.Utc)
                        : null,
                    issueResult.Current[4].IsNull ? null : issueResult.Current[4].AsText,
                    "extraction_issue",
                    code,
                    issueResult.Current[7].IsNull ? null : issueResult.Current[7].AsText));
            }
        }

        return failures
            .OrderBy(static x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.MemberPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task ReplaceValidationDriftsAsync(
        Database db,
        long rootId,
        IReadOnlyList<IndexValidationDriftInfo> drifts,
        CancellationToken cancellationToken)
    {
        await DeleteValidationDriftsForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        if (drifts.Count == 0)
            return;

        var id = await AllocateIdsAsync(db, "validation_drifts", drifts.Count, cancellationToken).ConfigureAwait(false);
        var batch = db.PrepareInsertBatch("validation_drifts", 250);
        foreach (var drift in drifts)
        {
            batch.AddRow(
                DbValue.FromInteger(id++),
                DbValue.FromInteger(rootId),
                DbValue.FromText(drift.Path),
                DbValue.FromText(drift.Kind.ToString()),
                DbValue.FromText(drift.Message),
                DbValue.FromInteger(drift.ObservedUtc.Ticks));

            if (batch.Count >= 250)
            {
                await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<IndexValidationDriftInfo>> ListValidationDriftsAsync(
        DbExec db,
        string root,
        CancellationToken cancellationToken)
    {
        var drifts = new List<IndexValidationDriftInfo>();
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT r.root_path, d.path, d.kind, d.message, d.observed_utc_ticks " +
                $"FROM validation_drifts d INNER JOIN index_roots r ON r.id = d.root_id " +
                $"WHERE r.root_path = {root} ORDER BY d.kind, d.path"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var observedTicks = result.Current[4].IsNull ? 0 : result.Current[4].AsInteger;
            drifts.Add(new IndexValidationDriftInfo(
                result.Current[0].AsText,
                result.Current[1].AsText,
                ParseEnum(result.Current[2].IsNull ? null : result.Current[2].AsText, IndexValidationDriftKind.FailedCheck),
                result.Current[3].IsNull ? string.Empty : result.Current[3].AsText,
                observedTicks > 0 ? new DateTime(observedTicks, DateTimeKind.Utc) : DateTime.MinValue));
        }

        return drifts;
    }

    public static async Task ReplaceExtractionIssuesAsync(
        Database db,
        long fileId,
        IReadOnlyList<ExtractionIssue> issues,
        bool deleteExisting,
        CancellationToken cancellationToken)
    {
        if (deleteExisting)
            await DeleteExtractionIssuesAsync(db, fileId, cancellationToken).ConfigureAwait(false);

        if (issues.Count == 0)
            return;

        var id = await AllocateIdsAsync(db, "extraction_issues", issues.Count, cancellationToken).ConfigureAwait(false);
        var batch = db.PrepareInsertBatch("extraction_issues", 100);
        var now = DateTime.UtcNow.Ticks;
        foreach (var issue in issues)
        {
            batch.AddRow(
                DbValue.FromInteger(id++),
                DbValue.FromInteger(fileId),
                DbValue.FromText(issue.MemberPath ?? string.Empty),
                DbValue.FromText(issue.Code),
                DbValue.FromText(issue.Message),
                DbValue.FromText(issue.Severity),
                DbValue.FromInteger(now));

            if (batch.Count >= 100)
            {
                await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends a tombstone so the deleted path hides older file-version rows.</summary>
    public static async Task<int> DeleteFileAsync(
        DbExec db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        var existing = await GetFileRowAsync(db, rootId, path, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return 0;

        await InsertFileTombstoneAsync(db, rootId, path, identity: null, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Deletes every indexed file whose path sits under the given directory.
    /// Watchers report one Deleted/Renamed event for a removed folder, not
    /// one per child, so a directory-path delete has to sweep the subtree.
    /// Streams id+path and filters in memory: prefix predicates in SQL are
    /// dialect-sensitive, and this path only runs when an exact-path delete
    /// matched nothing.
    /// </summary>
    public static async Task<int> DeleteFilesUnderDirectoryAsync(
        DbExec db,
        long rootId,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var prefix = directoryPath.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + System.IO.Path.DirectorySeparatorChar;

        var deletedPaths = new List<string>();
        await foreach (var file in ReadFileMetadataAsync(db, rootId, cancellationToken).ConfigureAwait(false))
        {
            if (file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                deletedPaths.Add(file.Path);
        }

        foreach (var path in deletedPaths)
            await InsertFileTombstoneAsync(db, rootId, path, identity: null, cancellationToken).ConfigureAwait(false);

        return deletedPaths.Count;
    }

    public static async Task DeleteFilesByIdentityAsync(
        DbExec db,
        long volumeId,
        string fileReferenceNumber,
        CancellationToken cancellationToken)
    {
        var latestRows = new Dictionary<(long RootId, string Path), (long Id, string Status)>();
        await using (var result = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT id, root_id, path, status FROM files WHERE volume_id = {volumeId} " +
                    $"AND file_reference_number = {fileReferenceNumber} AND status != {FileStatus.Indexing}"),
                cancellationToken).ConfigureAwait(false))
        {
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (result.Current[1].AsInteger, result.Current[2].AsText);
                var row = (Id: result.Current[0].AsInteger, Status: result.Current[3].AsText);
                if (!latestRows.TryGetValue(key, out var existing) || row.Id > existing.Id)
                    latestRows[key] = row;
            }
        }

        foreach (var (key, row) in latestRows)
        {
            if (!IsStaleStatus(row.Status))
                await InsertFileTombstoneAsync(db, key.RootId, key.Path, identity: null, cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task<List<long>> ReadFileIdsForRootAsync(DbExec db, long rootId, CancellationToken cancellationToken) =>
        ReadCurrentFileIdsForRootAsync(db, rootId, IsVisibleCurrentStatus, cancellationToken);

    public static Task<List<long>> ReadCurrentOkFileIdsForRootAsync(
        DbExec db,
        long rootId,
        CancellationToken cancellationToken) =>
        ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken);

    public static async Task DeleteFilesForRootAsync(DbExec db, long rootId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, Sql.Format($"DELETE FROM line_trigrams WHERE root_id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM file_metadata_tokens WHERE root_id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                db,
                Sql.Format($"DELETE FROM extraction_issues WHERE file_id IN (SELECT id FROM files WHERE root_id = {rootId})"),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                db,
                Sql.Format($"DELETE FROM lines WHERE file_id IN (SELECT id FROM files WHERE root_id = {rootId})"),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                db,
                Sql.Format($"DELETE FROM content_units WHERE file_id IN (SELECT id FROM files WHERE root_id = {rootId})"),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM files WHERE root_id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<long> CountOkFilesAsync(DbExec db, long rootId, CancellationToken cancellationToken)
    {
        var ids = await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false);
        return ids.Count;
    }

    public static async Task<long> CountFailedFilesAsync(DbExec db, CancellationToken cancellationToken)
    {
        var activeIds = await ReadCurrentFileIdsAsync(db, IsVisibleCurrentStatus, cancellationToken).ConfigureAwait(false);
        var fileErrors = await ReadCurrentFileIdsAsync(db, IsErrorStatus, cancellationToken).ConfigureAwait(false);
        long extractionIssues = 0;
        foreach (var batch in activeIds.Chunk(DeleteIdBatchSize))
        {
            extractionIssues += await GetCountAsync(
                    db,
                    Sql.Format($"SELECT COUNT(*) FROM extraction_issues WHERE file_id IN ({new Sql.IdList(batch)}) AND severity != 'info'"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return fileErrors.Count + extractionIssues;
    }

    // ----- lines -----

    public static async Task DeleteLinesAsync(DbExec db, long fileId, CancellationToken cancellationToken)
    {
        var lineIds = await ReadIdsAsync(db, Sql.Format($"SELECT id FROM lines WHERE file_id = {fileId}"), cancellationToken)
            .ConfigureAwait(false);
        await DeleteLineTrigramsAsync(db, lineIds, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM lines WHERE file_id = {fileId}"), cancellationToken)
            .ConfigureAwait(false);
        await DeleteContentUnitsAsync(db, fileId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteFileByIdAsync(DbExec db, long fileId, CancellationToken cancellationToken)
    {
        // Per-file DELETE on file_metadata_tokens is both expensive in
        // CSharpDB and can stall watcher updates. Orphaned token rows are
        // harmless after the file row is gone; root rebuilds clear by root_id.
        await DeleteExtractionIssuesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(Sql.Format($"DELETE FROM files WHERE id = {fileId}"), cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecordFileOpenedAsync(DbExec db, string path, CancellationToken cancellationToken)
    {
        var ids = await ReadIdsAsync(
            db,
            Sql.Format($"SELECT id FROM files WHERE path = {path}"),
            cancellationToken).ConfigureAwait(false);

        foreach (var id in ids)
        {
            await db.ExecuteAsync(
                    Sql.Format(
                        $"UPDATE files SET open_count = open_count + 1, last_opened_utc_ticks = {DateTime.UtcNow.Ticks} WHERE id = {id}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task DeleteMetadataTokensAsync(DbExec db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM file_metadata_tokens WHERE file_id = {fileId}"), cancellationToken);

    private static Task DeleteExtractionIssuesAsync(DbExec db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM extraction_issues WHERE file_id = {fileId}"), cancellationToken);

    private static async Task ReplaceMetadataTokensAsync(
        Database db,
        long rootId,
        long fileId,
        string path,
        string directoryPath,
        string fileName,
        string extension,
        string fileTypeCategory,
        bool deleteExisting,
        CancellationToken cancellationToken)
    {
        try
        {
            // DELETE is a full table scan in CSharpDB; skip it for rows that
            // cannot have tokens yet (freshly inserted files).
            if (deleteExisting)
                await DeleteMetadataTokensAsync(db, fileId, cancellationToken).ConfigureAwait(false);

            var tokens = BuildIndexedMetadataTokens(path, directoryPath, fileName, extension, fileTypeCategory);
            if (tokens.Count == 0)
                return;

            var batch = db.PrepareInsertBatch("file_metadata_tokens", MetadataTokenInsertBatchSize);
            foreach (var token in tokens)
            {
                var id = CreateMetadataTokenId(rootId, fileId, token);
                batch.AddRow(
                    DbValue.FromInteger(id),
                    DbValue.FromInteger(rootId),
                    DbValue.FromInteger(fileId),
                    DbValue.FromText(token));

                if (batch.Count >= MetadataTokenInsertBatchSize)
                {
                    await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CSharpDbException)
        {
            // Metadata search remains correct by scanning files when tokens are unavailable.
        }
    }

    public static async Task InsertMetadataTokensAsync(
        Database db,
        long rootId,
        long fileId,
        string path,
        string directoryPath,
        string fileName,
        string extension,
        string fileTypeCategory,
        CancellationToken cancellationToken)
    {
        var tokens = BuildIndexedMetadataTokens(path, directoryPath, fileName, extension, fileTypeCategory);
        if (tokens.Count == 0)
            return;

        var batch = PrepareMetadataTokenBatch(db);
        AddMetadataTokens(batch, rootId, fileId, tokens);
        await FlushMetadataTokenBatchAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    public static InsertBatch PrepareMetadataTokenBatch(Database db) =>
        db.PrepareInsertBatch("file_metadata_tokens", MetadataTokenInsertBatchSize);

    public static void AddMetadataTokens(InsertBatch batch, long rootId, long fileId, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            batch.AddRow(
                DbValue.FromInteger(CreateMetadataTokenId(rootId, fileId, token)),
                DbValue.FromInteger(rootId),
                DbValue.FromInteger(fileId),
                DbValue.FromText(token));
        }
    }

    public static async Task FlushMetadataTokenBatchAsync(InsertBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    public static IReadOnlyList<string> BuildQueryMetadataTokens(IEnumerable<string> terms)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
            AddTextTokens(tokens, term, includePrefixes: false);

        return tokens.ToList();
    }

    public static List<string> BuildMetadataTokens(
        string path,
        string directoryPath,
        string fileName,
        string extension,
        string fileTypeCategory)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTextTokens(tokens, path, includePrefixes: false);
        AddTextTokens(tokens, directoryPath, includePrefixes: false);
        AddIndexedFileNameTokens(tokens, fileName);
        AddIndexedFileNameTokens(tokens, Path.GetFileNameWithoutExtension(fileName));
        AddTextTokens(tokens, extension.TrimStart('.'), includePrefixes: false);
        AddTextTokens(tokens, fileTypeCategory, includePrefixes: false);
        foreach (var segment in path.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            AddTextTokens(tokens, segment, includePrefixes: true);
            AddTextTokens(tokens, Path.GetFileNameWithoutExtension(segment), includePrefixes: true);
        }

        return tokens.ToList();
    }

    private static void AddIndexedFileNameTokens(HashSet<string> tokens, string value)
    {
        var exactTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTextTokens(exactTokens, value, includePrefixes: false);
        foreach (var token in exactTokens)
        {
            tokens.Add(token);
            var max = Math.Min(8, token.Length);
            for (var length = MetadataTokenPrefixMinLength; length < max; length++)
                tokens.Add(token[..length]);
        }
    }

    public static List<string> BuildIndexedMetadataTokens(
        string path,
        string directoryPath,
        string fileName,
        string extension,
        string fileTypeCategory)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTextTokens(tokens, fileName, includePrefixes: true);
        AddTextTokens(tokens, Path.GetFileNameWithoutExtension(fileName), includePrefixes: true);
        AddUnderscoreParts(tokens, fileName);
        AddUnderscoreParts(tokens, Path.GetFileNameWithoutExtension(fileName));
        AddTextTokens(tokens, extension.TrimStart('.'), includePrefixes: false);
        AddTextTokens(tokens, fileTypeCategory, includePrefixes: false);
        foreach (var segment in directoryPath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            AddTextTokens(tokens, segment, includePrefixes: false);
        }

        return tokens.ToList();
    }

    public static long CreateMetadataTokenId(long rootId, long fileId, string token)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        AddInt64(rootId);
        AddInt64(fileId);
        foreach (var ch in token)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= prime;
        }

        var id = (long)(hash & 0x7FFFFFFFFFFFFFFF);
        return id == 0 ? 1 : id;

        void AddInt64(long value)
        {
            var bytes = unchecked((ulong)value);
            for (var i = 0; i < sizeof(long); i++)
            {
                hash ^= bytes & 0xFF;
                hash *= prime;
                bytes >>= 8;
            }
        }
    }

    private static void AddTextTokens(HashSet<string> tokens, string value, bool includePrefixes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (Match match in MetadataTokenRegex().Matches(value.ToLowerInvariant()))
        {
            var token = match.Value;
            if (token.Length == 0)
                continue;

            tokens.Add(token);
            if (!includePrefixes)
                continue;

            var max = Math.Min(MetadataTokenPrefixMaxLength, token.Length);
            for (var length = MetadataTokenPrefixMinLength; length < max; length++)
                tokens.Add(token[..length]);
        }
    }

    private static void AddUnderscoreParts(HashSet<string> tokens, string value)
    {
        foreach (var token in MetadataTokenRegex().Matches(value.ToLowerInvariant()).Select(match => match.Value))
        {
            if (!token.Contains('_', StringComparison.Ordinal))
                continue;

            foreach (var part in token.Split('_', StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(part);
        }
    }

    public static async Task DeleteLinesForFilesAsync(DbExec db, IEnumerable<long> fileIds, CancellationToken cancellationToken)
    {
        var ids = fileIds.ToArray();
        if (ids.Length == 0)
            return;

        var lineIds = await ReadIdsAsync(
                db,
                Sql.Format($"SELECT id FROM lines WHERE file_id IN ({new Sql.IdList(ids)})"),
                cancellationToken)
            .ConfigureAwait(false);
        await DeleteLineTrigramsAsync(db, lineIds, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM lines WHERE file_id IN ({new Sql.IdList(ids)})"), cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM content_units WHERE file_id IN ({new Sql.IdList(ids)})"), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteLineTrigramsAsync(
        DbExec db,
        List<long> lineIds,
        CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
            return;

        foreach (var batch in lineIds.Chunk(DeleteIdBatchSize))
        {
            await ExecuteAsync(
                    db,
                    Sql.Format($"DELETE FROM line_trigrams WHERE line_id IN ({new Sql.IdList(batch)})"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task DeleteContentUnitsAsync(DbExec db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM content_units WHERE file_id = {fileId}"), cancellationToken);

    public static async Task<long> CountOkLinesAsync(DbExec db, long rootId, CancellationToken cancellationToken)
    {
        var fileIds = (await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false))
            .ToHashSet();
        if (fileIds.Count == 0)
            return 0;

        long count = 0;
        await using var result = await db.ExecuteAsync("SELECT file_id FROM lines", cancellationToken).ConfigureAwait(false);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (fileIds.Contains(result.Current[0].AsInteger))
                count++;
        }

        return count;
    }

    public static string SelectLinesSql(long rootId) =>
        SelectLinesColumns +
        Sql.Format($"f.root_id = {rootId} AND f.status = {FileStatus.Ok}") +
        SelectLinesOrder;

    public static string SelectLinesSql(long rootId, IReadOnlyList<long> lineIds) =>
        SelectLinesColumns +
        Sql.Format($"l.id IN ({new Sql.IdList(lineIds)}) AND f.root_id = {rootId} AND f.status = {FileStatus.Ok}") +
        SelectLinesOrder;

    public static Task<List<long>> ReadLineIdsForTrigramAsync(
        DbExec db,
        long rootId,
        string trigram,
        CancellationToken cancellationToken) =>
        ReadIdsAsync(
            db,
            Sql.Format($"SELECT line_id FROM line_trigrams WHERE trigram = {trigram} AND root_id = {rootId}"),
            cancellationToken);

    public static async IAsyncEnumerable<LineTrigramSource> ReadLineTrigramSourcesAsync(
        DbExec db,
        long rootId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileIds = await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false);
        foreach (var batch in fileIds.Chunk(DeleteIdBatchSize))
        {
            await using var result = await db.ExecuteAsync(
                Sql.Format($"SELECT id, content FROM lines WHERE file_id IN ({new Sql.IdList(batch)})"),
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = result.Current;
                yield return new LineTrigramSource(
                    row[0].AsInteger,
                    row[1].AsText);
            }
        }
    }

    public static async IAsyncEnumerable<CachedIndexedLine> ReadCachedLinesAsync(
        DbExec db,
        long rootId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileIds = await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false);
        foreach (var batch in fileIds.Chunk(DeleteIdBatchSize))
        {
            await using var result = await db.ExecuteAsync(
                "SELECT l.id, f.id, f.path, f.file_name, f.extension, f.size_bytes, f.created_utc_ticks, f.modified_utc_ticks, " +
                "f.status, f.extractor_id, f.file_type_category, l.line_number, l.content, l.anchor_json, l.content_unit_id " +
                "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
                Sql.Format($"WHERE f.id IN ({new Sql.IdList(batch)}) AND f.status = {FileStatus.Ok}") +
                SelectLinesOrder,
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new CachedIndexedLine(
                    result.Current[0].AsInteger,
                    result.Current[1].AsInteger,
                    ReadIndexedLine(result.Current, offset: 2));
            }
        }
    }

    public static async IAsyncEnumerable<CachedIndexedLine> ReadCachedLinesForFileAsync(
        DbExec db,
        long fileId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            "SELECT l.id, f.id, f.path, f.file_name, f.extension, f.size_bytes, f.created_utc_ticks, f.modified_utc_ticks, " +
            "f.status, f.extractor_id, f.file_type_category, l.line_number, l.content, l.anchor_json, l.content_unit_id " +
            "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
            Sql.Format($"WHERE f.id = {fileId} AND f.status = {FileStatus.Ok}") +
            SelectLinesOrder,
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new CachedIndexedLine(
                result.Current[0].AsInteger,
                result.Current[1].AsInteger,
                ReadIndexedLine(result.Current, offset: 2));
        }
    }

    public static async IAsyncEnumerable<CachedIndexedLine> ReadCachedLinesAsync(
        DbExec db,
        long rootId,
        IReadOnlyList<long> lineIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
            yield break;

        await using var result = await db.ExecuteAsync(
            "SELECT l.id, f.id, f.path, f.file_name, f.extension, f.size_bytes, f.created_utc_ticks, f.modified_utc_ticks, " +
            "f.status, f.extractor_id, f.file_type_category, l.line_number, l.content, l.anchor_json, l.content_unit_id " +
            "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
            Sql.Format($"WHERE l.id IN ({new Sql.IdList(lineIds)}) AND f.root_id = {rootId} AND f.status = {FileStatus.Ok}") +
            SelectLinesOrder,
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new CachedIndexedLine(
                result.Current[0].AsInteger,
                result.Current[1].AsInteger,
                ReadIndexedLine(result.Current, offset: 2));
        }
    }

    public static async IAsyncEnumerable<FileChangeRow> ReadFileChangesAfterIdAsync(
        DbExec db,
        long rootId,
        long afterFileId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT id, path, status FROM files WHERE root_id = {rootId} AND id > {afterFileId} " +
                $"AND status != {FileStatus.Indexing} ORDER BY id"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new FileChangeRow(
                result.Current[0].AsInteger,
                result.Current[1].AsText,
                result.Current[2].AsText);
        }
    }

    public static async Task<long> ReadMaxFileIdForRootAsync(
        DbExec db,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id FROM files WHERE root_id = {rootId} ORDER BY id"),
            cancellationToken).ConfigureAwait(false);

        var max = 0L;
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            max = Math.Max(max, result.Current[0].AsInteger);

        return max;
    }

    public static async IAsyncEnumerable<IndexedLine> ReadCurrentLinesAsync(
        DbExec db,
        long rootId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileIds = await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false);
        foreach (var batch in fileIds.Chunk(DeleteIdBatchSize))
        {
            await using var result = await db.ExecuteAsync(
                SelectLinesColumns +
                Sql.Format($"f.id IN ({new Sql.IdList(batch)}) AND f.status = {FileStatus.Ok}") +
                SelectLinesOrder,
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                yield return ReadIndexedLine(result.Current, offset: 0);
        }
    }

    public static async IAsyncEnumerable<IndexedLine> ReadLinesAsync(
        DbExec db,
        string sql,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadIndexedLine(result.Current, offset: 0);
        }
    }

    private static IndexedLine ReadIndexedLine(DbValue[] row, int offset)
    {
        var lineNumber = checked((int)row[offset + 9].AsInteger);
        var anchor = row[offset + 11].IsNull ? null : DeserializeAnchor(row[offset + 11].AsText);
        return new IndexedLine(
            row[offset].AsText,
            row[offset + 1].AsText,
            row[offset + 2].AsText,
            row[offset + 3].AsInteger,
            row[offset + 4].IsNull ? 0 : row[offset + 4].AsInteger,
            row[offset + 5].AsInteger,
            row[offset + 6].AsText,
            row[offset + 7].IsNull ? string.Empty : row[offset + 7].AsText,
            row[offset + 8].IsNull ? string.Empty : row[offset + 8].AsText,
            lineNumber,
            row[offset + 10].AsText,
            anchor,
            row[offset + 12].IsNull ? null : row[offset + 12].AsInteger,
            ContentUnitKind.Text,
            SourceLocator.FromAnchor(anchor, lineNumber),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    public static async Task<ContentUnit?> ReadContentUnitAsync(
        DbExec db,
        long id,
        CancellationToken cancellationToken)
    {
        await using (var result = await db.ExecuteAsync(
                SelectContentUnitColumns +
                "INNER JOIN files f ON f.id = u.file_id " +
                Sql.Format($"WHERE u.id = {id} AND f.status != {FileStatus.Stale}"),
                cancellationToken)
            .ConfigureAwait(false))
        {
            if (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                return ReadContentUnit(result.Current);
        }

        return await ReadTextLineContentUnitAsync(db, id, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> ReadFilePathAsync(
        DbExec db,
        long fileId,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT path FROM files WHERE id = {fileId} AND status != {FileStatus.Stale}"),
            cancellationToken).ConfigureAwait(false);

        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsText
            : null;
    }

    public static async Task<long?> ReadFileIdAsync(
        DbExec db,
        long rootId,
        string path,
        CancellationToken cancellationToken)
    {
        long? latestId = null;
        string? latestStatus = null;
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, status FROM files WHERE root_id = {rootId} AND path = {path} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = result.Current[0].AsInteger;
            if (latestId is null || id > latestId.Value)
            {
                latestId = id;
                latestStatus = result.Current[1].AsText;
            }
        }

        return latestId is not null && latestStatus is not null && !IsStaleStatus(latestStatus)
            ? latestId.Value
            : null;
    }

    public static async Task<IReadOnlyList<long>> ReadContentUnitIdsForRootAsync(
        DbExec db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        var fileIds = await ReadCurrentFileIdsForRootAsync(db, rootId, IsOkStatus, cancellationToken).ConfigureAwait(false);

        foreach (var batch in fileIds.Chunk(DeleteIdBatchSize))
        {
            await using var result = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT content_unit_id FROM lines WHERE file_id IN ({new Sql.IdList(batch)}) ORDER BY id"),
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(result.Current[0].AsInteger);
        }

        return ids;
    }

    public static async Task<IReadOnlyList<ContentUnit>> ReadContentUnitsForFileAsync(
        DbExec db,
        long fileId,
        CancellationToken cancellationToken)
    {
        var units = new List<ContentUnit>();
        await using var result = await db.ExecuteAsync(
            "SELECT l.content_unit_id, l.file_id, l.line_number, l.content, l.anchor_json, " +
            "f.extractor_id, f.extractor_version, u.id, u.file_id, u.kind, u.locator_json, u.unit_text, " +
            "u.content_hash, u.language, u.extractor_id, u.extractor_version " +
            "FROM lines l INNER JOIN files f ON f.id = l.file_id " +
            "LEFT JOIN content_units u ON u.id = l.content_unit_id " +
            Sql.Format($"WHERE l.file_id = {fileId} AND f.status != {FileStatus.Stale}") +
            " ORDER BY l.line_number, l.id",
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            units.Add(ReadLineContentUnit(result.Current));

        return units;
    }

    public static async Task<IReadOnlyList<ContentUnit>> ReadNeighboringContentUnitsAsync(
        DbExec db,
        long contentUnitId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        var center = await ReadContentUnitAsync(db, contentUnitId, cancellationToken).ConfigureAwait(false);
        if (center is null)
            return Array.Empty<ContentUnit>();

        var units = await ReadContentUnitsForFileAsync(db, center.FileId, cancellationToken).ConfigureAwait(false);
        var centerIndex = units
            .Select((unit, index) => new { unit.Id, Index = index })
            .FirstOrDefault(unit => unit.Id == contentUnitId)
            ?.Index;
        if (centerIndex is null)
            return Array.Empty<ContentUnit>();

        var start = Math.Max(0, centerIndex.Value - before);
        var count = Math.Min(units.Count - start, before + 1 + after);
        return units.Skip(start).Take(count).ToArray();
    }

    public static DbValue SerializeAnchor(SourceAnchor? anchor) =>
        anchor is null
            ? DbValue.Null
            : DbValue.FromText(JsonSerializer.Serialize(anchor, s_anchorJsonOptions));

    public static DbValue SerializeLocator(SourceLocator locator) =>
        DbValue.FromText(JsonSerializer.Serialize(locator, s_locatorJsonOptions));

    private static ContentUnit ReadContentUnit(DbValue[] row) =>
        new(
            row[0].AsInteger,
            row[1].AsInteger,
            ParseEnum(row[2].IsNull ? null : row[2].AsText, ContentUnitKind.Text),
            row[3].IsNull ? new SourceLocator() : DeserializeLocator(row[3].AsText) ?? new SourceLocator(),
            row[4].IsNull ? string.Empty : row[4].AsText,
            row[5].IsNull ? string.Empty : row[5].AsText,
            row[6].IsNull ? string.Empty : row[6].AsText,
            row[7].IsNull ? string.Empty : row[7].AsText,
            row[8].IsNull ? string.Empty : row[8].AsText);

    private static async Task<ContentUnit?> ReadTextLineContentUnitAsync(
        DbExec db,
        long id,
        CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT l.content_unit_id, l.file_id, l.line_number, l.content, l.anchor_json, " +
                $"f.extractor_id, f.extractor_version FROM lines l INNER JOIN files f ON f.id = l.file_id " +
                $"WHERE l.id = {id} AND f.status != {FileStatus.Stale}"),
            cancellationToken).ConfigureAwait(false);

        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSynthesizedLineContentUnit(result.Current)
            : null;
    }

    private static ContentUnit ReadLineContentUnit(DbValue[] row)
    {
        if (!row[7].IsNull)
        {
            return new ContentUnit(
                row[7].AsInteger,
                row[8].AsInteger,
                ParseEnum(row[9].IsNull ? null : row[9].AsText, ContentUnitKind.Text),
                row[10].IsNull ? new SourceLocator() : DeserializeLocator(row[10].AsText) ?? new SourceLocator(),
                row[11].IsNull ? string.Empty : row[11].AsText,
                row[12].IsNull ? string.Empty : row[12].AsText,
                row[13].IsNull ? string.Empty : row[13].AsText,
                row[14].IsNull ? string.Empty : row[14].AsText,
                row[15].IsNull ? string.Empty : row[15].AsText);
        }

        return ReadSynthesizedLineContentUnit(row);
    }

    private static ContentUnit ReadSynthesizedLineContentUnit(DbValue[] row)
    {
        var anchor = row[4].IsNull ? null : DeserializeAnchor(row[4].AsText);
        var line = new TextLine(
            checked((int)row[2].AsInteger),
            row[3].IsNull ? string.Empty : row[3].AsText,
            anchor);
        return ContentUnit.FromTextLine(
            row[0].AsInteger,
            row[1].AsInteger,
            line,
            row[5].IsNull ? string.Empty : row[5].AsText,
            row[6].IsNull ? string.Empty : row[6].AsText);
    }

    private static SourceAnchor? DeserializeAnchor(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SourceAnchor>(json, s_anchorJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static SourceLocator? DeserializeLocator(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SourceLocator>(json, s_locatorJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // ----- pending_changes -----

    public static async Task UpsertPendingChangeAsync(
        DbExec db,
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

    public static async Task<List<PendingIndexChange>> ReadPendingChangesAsync(DbExec db, CancellationToken cancellationToken)
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
        DbExec db,
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

    public static Task DeletePendingChangesForRootAsync(DbExec db, string root, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM pending_changes WHERE root_path = {root}"), cancellationToken);

    // ----- shared helpers -----

    private static string PendingPathPredicate(string? path) =>
        path is null
            ? "path IS NULL"
            : Sql.Format($"path = {path}");

    private static Task DeleteValidationDriftsForRootAsync(
        DbExec db,
        long rootId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM validation_drifts WHERE root_id = {rootId}"), cancellationToken);

    public static Task AnalyzeAsync(DbExec db, CancellationToken cancellationToken) =>
        ExecuteAsync(db, "ANALYZE", cancellationToken);

    public static async Task<long> GetNextIdAsync(DbExec db, string tableName, CancellationToken cancellationToken)
    {
        return await AllocateIdsAsync(db, tableName, 1, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> AllocateIdsAsync(DbExec db, string tableName, long count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Table names come from internal constants, but validate anyway so
        // this hole can never carry SQL even if a caller misuses it.
        _ = new Sql.Identifier(tableName);
        long? nextId = null;
        await using (var result = await db.ExecuteAsync(
                         Sql.Format($"SELECT next_id FROM index_sequences WHERE name = {tableName}"),
                         cancellationToken).ConfigureAwait(false))
        {
            if (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                nextId = result.Current[0].AsInteger;
        }

        var firstId = nextId ?? 1;
        var followingId = checked(firstId + count);

        if (nextId is null)
        {
            await db.ExecuteAsync(
                    Sql.Format($"INSERT INTO index_sequences VALUES ({tableName}, {followingId})"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await db.ExecuteAsync(
                    Sql.Format($"UPDATE index_sequences SET next_id = {followingId} WHERE name = {tableName}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return firstId;
    }

    private static async Task ExecuteAsync(DbExec db, string sql, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<long>> ReadCurrentFileIdsForRootAsync(
        DbExec db,
        long rootId,
        Func<string, bool> includeStatus,
        CancellationToken cancellationToken)
    {
        var latestByPath = new Dictionary<string, (long Id, string Status)>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, path, status FROM files WHERE root_id = {rootId} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = result.Current[1].AsText;
            var row = (Id: result.Current[0].AsInteger, Status: result.Current[2].AsText);
            if (!latestByPath.TryGetValue(path, out var existing) || row.Id > existing.Id)
                latestByPath[path] = row;
        }

        var ids = latestByPath.Values
            .Where(row => includeStatus(row.Status))
            .Select(row => row.Id)
            .ToList();
        ids.Sort();
        return ids;
    }

    private static async Task<List<long>> ReadCurrentFileIdsAsync(
        DbExec db,
        Func<string, bool> includeStatus,
        CancellationToken cancellationToken)
    {
        var latestByPath = new Dictionary<string, (long Id, string Status)>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, root_id, path, status FROM files WHERE status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = result.Current[1].AsInteger.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + result.Current[2].AsText;
            var row = (Id: result.Current[0].AsInteger, Status: result.Current[3].AsText);
            if (!latestByPath.TryGetValue(key, out var existing) || row.Id > existing.Id)
                latestByPath[key] = row;
        }

        var ids = latestByPath.Values
            .Where(row => includeStatus(row.Status))
            .Select(row => row.Id)
            .ToList();
        ids.Sort();
        return ids;
    }

    private static bool IsVisibleCurrentStatus(string status) =>
        !IsStaleStatus(status) && !string.Equals(status, FileStatus.Indexing, StringComparison.OrdinalIgnoreCase);

    private static bool IsOkStatus(string status) =>
        string.Equals(status, FileStatus.Ok, StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorStatus(string status) =>
        string.Equals(status, FileStatus.Error, StringComparison.OrdinalIgnoreCase);

    private static bool IsStaleStatus(string status) =>
        string.Equals(status, FileStatus.Stale, StringComparison.OrdinalIgnoreCase);

    private static int Bool(bool value) => value ? 1 : 0;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static async Task<long> GetCountAsync(DbExec db, string sql, CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        return await result.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? result.Current[0].AsInteger
            : 0;
    }

    private static async Task<List<long>> ReadIdsAsync(DbExec db, string sql, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var result = await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(result.Current[0].AsInteger);
        return ids;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTokenRegex();
}
