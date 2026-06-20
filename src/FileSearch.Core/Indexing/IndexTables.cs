using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Extractors;

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
    long CreatedUtcTicks,
    long ModifiedUtcTicks,
    long Attributes,
    string Status,
    string ContentVersion,
    string ExtractorId,
    string ExtractorVersion);

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
    SourceAnchor? Anchor);

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

    private const int MetadataTokenPrefixMinLength = 2;
    private const int MetadataTokenPrefixMaxLength = 32;

    private const string SelectLinesColumns =
        "SELECT f.path, f.file_name, f.extension, f.size_bytes, f.created_utc_ticks, f.modified_utc_ticks, " +
        "f.status, f.extractor_id, f.file_type_category, l.line_number, l.content, l.anchor_json " +
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
                    $"INSERT INTO index_roots (id, root_path, indexed_utc_ticks, options_hash, volume_id, last_full_scan_utc_ticks, root_file_reference_number, root_parent_file_reference_number, content_version, location_kind, update_strategy, strategy_warning, usn_catch_up_enabled, watcher_recommended, last_full_validation_utc_ticks, last_validation_status, last_validation_message, last_validation_files_checked, last_validation_missing_from_index_count, last_validation_changed_count, last_validation_missing_from_disk_count, last_validation_failed_count) " +
                $"VALUES ({id}, {root}, 0, {profile}, {(long?)null}, {(long?)null}, {(string?)null}, {(string?)null}, {IndexContentVersion.Current}, {IndexLocationKind.Unknown.ToString()}, {IndexUpdateStrategy.Unknown.ToString()}, {(string?)null}, 0, 1, 0, {"never"}, {(string?)null}, 0, 0, 0, 0, 0)"),
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    public static async Task<RootRow?> GetRootAsync(Database db, string root, CancellationToken cancellationToken)
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

    public static Task MarkRootValidatedAsync(
        Database db,
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

    public static async Task DeleteRootAsync(Database db, long rootId, CancellationToken cancellationToken)
    {
        await DeleteValidationDriftsForRootAsync(db, rootId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM index_roots WHERE id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task SetRootVolumeAsync(
        Database db,
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
        Database db,
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
                    $"drive_kind = {volume.DriveKind.ToString()}, " +
                    $"last_checked_utc_ticks = {now} WHERE id = {existing.Id}"),
                cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var id = await GetNextIdAsync(db, "index_volumes", cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
                Sql.Format(
                    $"INSERT INTO index_volumes (id, volume_key, volume_serial, filesystem_name, is_remote, usn_supported, drive_kind, journal_id, last_committed_usn, health, last_checked_utc_ticks, last_error) " +
                $"VALUES ({id}, {volume.VolumeKey}, {volume.VolumeSerial}, {volume.FileSystemName}, {Bool(volume.IsRemote)}, {Bool(volume.UsnSupported)}, {volume.DriveKind.ToString()}, {(string?)null}, 0, {"unknown"}, {now}, {(string?)null})"),
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

    public static async Task<string?> GetVolumeKeyAsync(
        Database db,
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
        Database db,
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
            Sql.Format($"SELECT id, path, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, status, content_version, extractor_id, extractor_version FROM files WHERE root_id = {rootId} AND path = {path}"),
            cancellationToken).ConfigureAwait(false);

        if (!await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new ExistingFileRow(
            result.Current[0].AsInteger,
            result.Current[1].AsText,
            result.Current[2].AsInteger,
            result.Current[3].AsInteger,
            result.Current[4].AsInteger,
            result.Current[5].AsInteger,
            result.Current[6].AsText,
            result.Current[7].IsNull ? string.Empty : result.Current[7].AsText,
            result.Current[8].IsNull ? string.Empty : result.Current[8].AsText,
            result.Current[9].IsNull ? string.Empty : result.Current[9].AsText);
    }

    public static async Task<Dictionary<string, ExistingFileRow>> LoadExistingFilesAsync(
        Database db,
        long rootId,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, ExistingFileRow>(StringComparer.OrdinalIgnoreCase);
        await using var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, path, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, status, content_version, extractor_id, extractor_version FROM files WHERE root_id = {rootId}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new ExistingFileRow(
                result.Current[0].AsInteger,
                result.Current[1].AsText,
                result.Current[2].AsInteger,
                result.Current[3].AsInteger,
                result.Current[4].AsInteger,
                result.Current[5].AsInteger,
                result.Current[6].AsText,
                result.Current[7].IsNull ? string.Empty : result.Current[7].AsText,
                result.Current[8].IsNull ? string.Empty : result.Current[8].AsText,
                result.Current[9].IsNull ? string.Empty : result.Current[9].AsText);
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
        string extractorId,
        string extractorVersion,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var fileNameLower = fileName.ToLowerInvariant();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var directoryPath = Path.GetDirectoryName(path) ?? string.Empty;
        var pathLower = path.ToLowerInvariant();
        var directoryPathLower = directoryPath.ToLowerInvariant();
        var attributes = (long)info.Attributes;
        var fileTypeCategory = FileTypeCategory.ForExtension(extension);
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
                    $"INSERT INTO files (id, root_id, path, path_lower, directory_path, directory_path_lower, file_name, file_name_lower, extension, size_bytes, created_utc_ticks, modified_utc_ticks, attributes, file_type_category, indexed_utc_ticks, status, error, volume_id, file_reference_number, parent_file_reference_number, last_observed_usn, content_version, open_count, last_opened_utc_ticks, extractor_id, extractor_version, extraction_attempt_count, last_extraction_attempt_utc_ticks) " +
                    $"VALUES ({id}, {rootId}, {path}, {pathLower}, {directoryPath}, {directoryPathLower}, {fileName}, {fileNameLower}, {extension}, {info.Length}, {info.CreationTimeUtc.Ticks}, {info.LastWriteTimeUtc.Ticks}, {attributes}, {fileTypeCategory}, {now}, {status}, {error}, {identity?.VolumeId}, {identity?.FileReferenceNumber}, " +
                    $"{identity?.ParentFileReferenceNumber}, {identity?.LastObservedUsn}, {IndexContentVersion.Current}, 0, 0, {extractorId}, {extractorVersion}, 0, 0)"),
                cancellationToken).ConfigureAwait(false);
            await ReplaceMetadataTokensAsync(
                db,
                rootId,
                id,
                path,
                directoryPath,
                fileName,
                extension,
                fileTypeCategory,
                cancellationToken).ConfigureAwait(false);
            return id;
        }

        await db.ExecuteAsync(
            Sql.Format(
                $"UPDATE files SET path = {path}, path_lower = {pathLower}, directory_path = {directoryPath}, " +
                $"directory_path_lower = {directoryPathLower}, file_name = {fileName}, file_name_lower = {fileNameLower}, " +
                $"extension = {extension}, size_bytes = {info.Length}, created_utc_ticks = {info.CreationTimeUtc.Ticks}, " +
                $"modified_utc_ticks = {info.LastWriteTimeUtc.Ticks}, attributes = {attributes}, file_type_category = {fileTypeCategory}, indexed_utc_ticks = {now}, " +
                $"status = {status}, error = {error}, volume_id = {identity?.VolumeId}, " +
                $"file_reference_number = {identity?.FileReferenceNumber}, " +
                $"parent_file_reference_number = {identity?.ParentFileReferenceNumber}, " +
                $"last_observed_usn = {identity?.LastObservedUsn}, " +
                $"content_version = {IndexContentVersion.Current}, extractor_id = {extractorId}, " +
                $"extractor_version = {extractorVersion} WHERE id = {fileId}"),
            cancellationToken).ConfigureAwait(false);
        await ReplaceMetadataTokensAsync(
            db,
            rootId,
            fileId,
            path,
            directoryPath,
            fileName,
            extension,
            fileTypeCategory,
            cancellationToken).ConfigureAwait(false);
        return fileId;
    }

    public static async Task<List<long>> ReadMetadataCandidateFileIdsAsync(
        Database db,
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
                            $"SELECT file_id FROM file_metadata_tokens WHERE root_id = {rootId} AND token = {token}"),
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
        Database db,
        long rootId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT path, directory_path, file_name, extension, size_bytes, created_utc_ticks, " +
                $"modified_utc_ticks, attributes, file_type_category, open_count, last_opened_utc_ticks, status, extractor_id " +
                $"FROM files WHERE root_id = {rootId} AND status != {FileStatus.Indexing}"),
            cancellationToken).ConfigureAwait(false);

        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = result.Current;
            yield return new IndexedFileMetadata(
                row[0].AsText,
                row[1].IsNull ? string.Empty : row[1].AsText,
                row[2].AsText,
                row[3].AsText,
                row[4].AsInteger,
                row[5].IsNull ? 0 : row[5].AsInteger,
                row[6].AsInteger,
                row[7].IsNull ? 0 : row[7].AsInteger,
                row[8].IsNull ? string.Empty : row[8].AsText,
                row[9].IsNull ? 0 : row[9].AsInteger,
                row[10].IsNull ? 0 : row[10].AsInteger,
                row[11].AsText,
                row[12].IsNull ? string.Empty : row[12].AsText);
        }
    }

    public static async IAsyncEnumerable<IndexedFileMetadata> ReadFileMetadataAsync(
        Database db,
        long rootId,
        IReadOnlyList<long> fileIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in fileIds.Chunk(500))
        {
            await using var result = await db.ExecuteAsync(
                Sql.Format(
                    $"SELECT path, directory_path, file_name, extension, size_bytes, created_utc_ticks, " +
                    $"modified_utc_ticks, attributes, file_type_category, open_count, last_opened_utc_ticks, status, extractor_id " +
                    $"FROM files WHERE root_id = {rootId} AND id IN ({new Sql.IdList(batch)}) AND status != {FileStatus.Indexing}"),
                cancellationToken).ConfigureAwait(false);

            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = result.Current;
                yield return new IndexedFileMetadata(
                    row[0].AsText,
                    row[1].IsNull ? string.Empty : row[1].AsText,
                    row[2].AsText,
                    row[3].AsText,
                    row[4].AsInteger,
                    row[5].IsNull ? 0 : row[5].AsInteger,
                    row[6].AsInteger,
                    row[7].IsNull ? 0 : row[7].AsInteger,
                    row[8].IsNull ? string.Empty : row[8].AsText,
                    row[9].IsNull ? 0 : row[9].AsInteger,
                    row[10].IsNull ? 0 : row[10].AsInteger,
                    row[11].AsText,
                    row[12].IsNull ? string.Empty : row[12].AsText);
            }
        }
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

    public static Task RecordExtractionAttemptAsync(
        Database db,
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
        Database db,
        CancellationToken cancellationToken)
    {
        var failures = new List<IndexFailureInfo>();
        await using var result = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT r.root_path, f.path, f.extractor_id, f.extractor_version, f.error, " +
                $"f.extraction_attempt_count, f.last_extraction_attempt_utc_ticks " +
                $"FROM files f INNER JOIN index_roots r ON r.id = f.root_id " +
                $"WHERE f.status = {FileStatus.Error} ORDER BY f.path"),
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

        await using var issueResult = await db.ExecuteAsync(
            Sql.Format(
                $"SELECT r.root_path, f.path, f.extractor_id, f.extractor_version, i.member_path, " +
                $"i.code, i.message, i.severity, f.extraction_attempt_count, f.last_extraction_attempt_utc_ticks " +
                $"FROM extraction_issues i INNER JOIN files f ON f.id = i.file_id " +
                $"INNER JOIN index_roots r ON r.id = f.root_id ORDER BY f.path, i.member_path"),
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
        Database db,
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
        CancellationToken cancellationToken)
    {
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
            await DeleteFileByIdAsync(db, id, cancellationToken).ConfigureAwait(false);
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

    public static async Task DeleteFilesForRootAsync(Database db, long rootId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, Sql.Format($"DELETE FROM file_metadata_tokens WHERE root_id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                db,
                Sql.Format($"DELETE FROM extraction_issues WHERE file_id IN (SELECT id FROM files WHERE root_id = {rootId})"),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(db, Sql.Format($"DELETE FROM files WHERE root_id = {rootId}"), cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<long> CountOkFilesAsync(Database db, long rootId, CancellationToken cancellationToken) =>
        GetCountAsync(db, Sql.Format($"SELECT COUNT(*) FROM files WHERE root_id = {rootId} AND status = {FileStatus.Ok}"), cancellationToken);

    public static async Task<long> CountFailedFilesAsync(Database db, CancellationToken cancellationToken)
    {
        var fileErrors = await GetCountAsync(
            db,
            Sql.Format($"SELECT COUNT(*) FROM files WHERE status = {FileStatus.Error}"),
            cancellationToken).ConfigureAwait(false);
        var extractionIssues = await GetCountAsync(
            db,
            "SELECT COUNT(*) FROM extraction_issues WHERE severity != 'info'",
            cancellationToken).ConfigureAwait(false);
        return fileErrors + extractionIssues;
    }

    // ----- lines -----

    public static Task DeleteLinesAsync(Database db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM lines WHERE file_id = {fileId}"), cancellationToken);

    private static async Task DeleteFileByIdAsync(Database db, long fileId, CancellationToken cancellationToken)
    {
        await DeleteMetadataTokensAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await DeleteExtractionIssuesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await DeleteLinesAsync(db, fileId, cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(Sql.Format($"DELETE FROM files WHERE id = {fileId}"), cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecordFileOpenedAsync(Database db, string path, CancellationToken cancellationToken)
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

    private static Task DeleteMetadataTokensAsync(Database db, long fileId, CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM file_metadata_tokens WHERE file_id = {fileId}"), cancellationToken);

    private static Task DeleteExtractionIssuesAsync(Database db, long fileId, CancellationToken cancellationToken) =>
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
        CancellationToken cancellationToken)
    {
        try
        {
            await DeleteMetadataTokensAsync(db, fileId, cancellationToken).ConfigureAwait(false);

            var tokens = BuildMetadataTokens(path, directoryPath, fileName, extension, fileTypeCategory);
            if (tokens.Count == 0)
                return;

            foreach (var token in tokens)
            {
                var id = CreateMetadataTokenId(rootId, fileId, token);
                await db.ExecuteAsync(
                        Sql.Format($"INSERT INTO file_metadata_tokens VALUES ({id}, {rootId}, {fileId}, {token})"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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

    public static IReadOnlyList<string> BuildQueryMetadataTokens(IEnumerable<string> terms)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
            AddTextTokens(tokens, term, includePrefixes: false);

        return tokens.ToList();
    }

    private static List<string> BuildMetadataTokens(
        string path,
        string directoryPath,
        string fileName,
        string extension,
        string fileTypeCategory)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTextTokens(tokens, path, includePrefixes: false);
        AddTextTokens(tokens, directoryPath, includePrefixes: false);
        AddTextTokens(tokens, fileName, includePrefixes: true);
        AddTextTokens(tokens, Path.GetFileNameWithoutExtension(fileName), includePrefixes: true);
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

    private static long CreateMetadataTokenId(long rootId, long fileId, string token)
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
                row[4].IsNull ? 0 : row[4].AsInteger,
                row[5].AsInteger,
                row[6].AsText,
                row[7].IsNull ? string.Empty : row[7].AsText,
                row[8].IsNull ? string.Empty : row[8].AsText,
                checked((int)row[9].AsInteger),
                row[10].AsText,
                row[11].IsNull ? null : DeserializeAnchor(row[11].AsText));
        }
    }

    public static DbValue SerializeAnchor(SourceAnchor? anchor) =>
        anchor is null
            ? DbValue.Null
            : DbValue.FromText(JsonSerializer.Serialize(anchor, s_anchorJsonOptions));

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

    private static Task DeleteValidationDriftsForRootAsync(
        Database db,
        long rootId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(db, Sql.Format($"DELETE FROM validation_drifts WHERE root_id = {rootId}"), cancellationToken);

    public static async Task<long> GetNextIdAsync(Database db, string tableName, CancellationToken cancellationToken)
    {
        return await AllocateIdsAsync(db, tableName, 1, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> AllocateIdsAsync(Database db, string tableName, long count, CancellationToken cancellationToken)
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

    private static async Task ExecuteAsync(Database db, string sql, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private static int Bool(bool value) => value ? 1 : 0;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

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

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTokenRegex();
}
