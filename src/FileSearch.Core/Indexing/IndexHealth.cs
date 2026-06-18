using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

public enum IndexHealthStatus
{
    Healthy,
    CatchingUp,
    Watching,
    Paused,
    NeedsFullScan,
    JournalUnavailable,
    JournalExpired,
    AccessDenied,
    Offline,
    TooManyExtractorFailures,
}

public sealed record IndexWatcherDiagnosticInfo(
    string Root,
    bool IsWatching,
    DateTime? LastEventUtc,
    DateTime? LastErrorUtc,
    string? LastError);

public sealed record IndexRootHealthInfo(
    string Root,
    string DisplayName,
    IndexHealthStatus Status,
    string StatusText,
    long FilesIndexed,
    int FilesPending,
    long FilesFailed,
    DateTime? LastSuccessfulScanUtc,
    DateTime? LastUsnCheckpointUtc,
    long? LastCommittedUsn,
    DateTime? LastWatcherEventUtc,
    DateTime? LastFullValidationUtc,
    string ValidationSummary,
    string JournalStatus,
    string ExtractorFailures,
    int QueueDepth,
    string EstimatedCatchUpTime,
    string Strategy,
    string Detail);

public sealed record IndexHealthSnapshot(
    DateTime CheckedUtc,
    int QueueDepth,
    IReadOnlyList<IndexRootHealthInfo> Roots);

public interface IIndexHealthService
{
    Task<IndexHealthSnapshot> GetHealthAsync(
        IReadOnlyCollection<IndexedLocation> locations,
        IndexingStatus runtimeStatus,
        CancellationToken cancellationToken);
}

public sealed class IndexHealthService : IIndexHealthService
{
    private const long TooManyExtractorFailuresThreshold = 25;

    private readonly IFileIndex _index;

    public IndexHealthService(IFileIndex index) =>
        _index = index ?? throw new ArgumentNullException(nameof(index));

    public async Task<IndexHealthSnapshot> GetHealthAsync(
        IReadOnlyCollection<IndexedLocation> locations,
        IndexingStatus runtimeStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locations);

        var configuredRoots = locations
            .Where(static location => !string.IsNullOrWhiteSpace(location.Root))
            .Select(static location => IndexPath.NormalizeRoot(location.Root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dbLocations = await _index.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        var dbByRoot = dbLocations.ToDictionary(
            location => IndexPath.NormalizeRoot(location.Root),
            StringComparer.OrdinalIgnoreCase);

        var allRoots = configuredRoots
            .Concat(dbByRoot.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var databaseInfo = await _index.GetDatabaseInfoAsync(cancellationToken).ConfigureAwait(false);
        var failures = await _index.GetFailedFilesAsync(cancellationToken).ConfigureAwait(false);
        var pendingChanges = await _index.GetPendingChangesAsync(cancellationToken).ConfigureAwait(false);

        var pendingByRoot = pendingChanges
            .GroupBy(change => IndexPath.NormalizeRoot(change.Root), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var actionableFailuresByRoot = failures
            .Where(IsActionableFailure)
            .GroupBy(failure => IndexPath.NormalizeRoot(failure.Root), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.OrdinalIgnoreCase);

        var extractionIssuesByRoot = failures
            .Where(static failure => string.Equals(failure.FailureKind, "extraction_issue", StringComparison.Ordinal))
            .GroupBy(failure => IndexPath.NormalizeRoot(failure.Root), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var strategiesByRoot = (databaseInfo.RootStrategies ?? Array.Empty<IndexRootStrategyInfo>())
            .ToDictionary(strategy => IndexPath.NormalizeRoot(strategy.RootPath), StringComparer.OrdinalIgnoreCase);

        var volumesByKey = (databaseInfo.VolumeHealth ?? Array.Empty<IndexVolumeHealthInfo>())
            .Where(static volume => !string.IsNullOrWhiteSpace(volume.VolumeKey))
            .ToDictionary(volume => volume.VolumeKey, StringComparer.OrdinalIgnoreCase);

        var queued = runtimeStatus.QueuedRootCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var watcherDiagnostics = runtimeStatus.WatcherDiagnostics ?? new Dictionary<string, IndexWatcherDiagnosticInfo>(StringComparer.OrdinalIgnoreCase);
        var rootDetails = runtimeStatus.RootStatusDetails ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<IndexRootHealthInfo>(allRoots.Count);
        foreach (var root in allRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            dbByRoot.TryGetValue(root, out var dbLocation);
            strategiesByRoot.TryGetValue(root, out var strategy);
            watcherDiagnostics.TryGetValue(root, out var watcher);
            rootDetails.TryGetValue(root, out var runtimeDetail);

            var pending = pendingByRoot.TryGetValue(root, out var persistedPending) ? persistedPending : 0;
            var queuedCount = queued.TryGetValue(root, out var runtimeQueued) ? runtimeQueued : 0;
            var queueDepth = Math.Max(pending, queuedCount);
            var failed = actionableFailuresByRoot.TryGetValue(root, out var failedCount) ? failedCount : 0;
            var volume = dbLocation?.VolumeKey is { Length: > 0 } volumeKey &&
                volumesByKey.TryGetValue(volumeKey, out var matchedVolume)
                    ? matchedVolume
                    : null;
            var status = DeriveStatus(root, runtimeStatus, queueDepth, failed, volume, runtimeDetail, strategy, watcher, dbLocation);
            var detail = BuildDetail(status, runtimeDetail, strategy, volume, watcher);
            var checkpointUtc = volume is { LastCommittedUsn: > 0 } ? volume.LastCheckedUtc : null;
            rows.Add(new IndexRootHealthInfo(
                root,
                GetDisplayName(root),
                status,
                FormatStatus(status),
                dbLocation?.FileCount ?? 0,
                queueDepth,
                failed,
                dbLocation?.IndexedUtc,
                checkpointUtc,
                volume is { LastCommittedUsn: > 0 } ? volume.LastCommittedUsn : null,
                watcher?.LastEventUtc,
                dbLocation?.LastFullValidationUtc,
                FormatValidationSummary(dbLocation),
                FormatJournalStatus(volume, runtimeDetail, strategy),
                FormatExtractorFailures(failed, extractionIssuesByRoot.TryGetValue(root, out var issues) ? issues : Array.Empty<IndexFailureInfo>()),
                queueDepth,
                EstimateCatchUpTime(runtimeStatus, root, queueDepth),
                strategy?.StrategyLabel ?? "Strategy pending",
                detail));
        }

        return new IndexHealthSnapshot(DateTime.UtcNow, runtimeStatus.QueueLength, rows);
    }

    private static IndexHealthStatus DeriveStatus(
        string root,
        IndexingStatus runtimeStatus,
        int queueDepth,
        long failed,
        IndexVolumeHealthInfo? volume,
        string? runtimeDetail,
        IndexRootStrategyInfo? strategy,
        IndexWatcherDiagnosticInfo? watcher,
        IndexedLocationInfo? location)
    {
        if (IsAccessDenied(runtimeDetail) || IsAccessDenied(volume?.LastError) || IsAccessDenied(watcher?.LastError))
            return IndexHealthStatus.AccessDenied;

        if (!Directory.Exists(root))
            return IndexHealthStatus.Offline;

        var isActive = !string.IsNullOrWhiteSpace(runtimeStatus.ActiveRoot) &&
            string.Equals(runtimeStatus.ActiveRoot, root, StringComparison.OrdinalIgnoreCase);

        if (runtimeStatus.IsPaused && (isActive || queueDepth > 0 || runtimeStatus.QueueLength > 0))
            return IndexHealthStatus.Paused;

        if (isActive || queueDepth > 0)
            return IndexHealthStatus.CatchingUp;

        if (IsJournalExpired(runtimeDetail) || IsJournalExpired(volume?.LastError) || IsJournalExpired(volume?.Health))
            return IndexHealthStatus.JournalExpired;

        if (IsJournalUnavailable(runtimeDetail) || IsJournalUnavailable(volume?.LastError) || IsJournalUnavailable(volume?.Health))
            return IndexHealthStatus.JournalUnavailable;

        if (string.Equals(location?.LastValidationStatus, IndexValidationStatus.DriftDetected.ToString(), StringComparison.Ordinal))
            return IndexHealthStatus.NeedsFullScan;

        if (failed >= TooManyExtractorFailuresThreshold)
            return IndexHealthStatus.TooManyExtractorFailures;

        if (NeedsFullScan(runtimeDetail, strategy, volume))
            return IndexHealthStatus.NeedsFullScan;

        if (watcher?.IsWatching == true)
            return IndexHealthStatus.Watching;

        return IndexHealthStatus.Healthy;
    }

    private static bool NeedsFullScan(
        string? runtimeDetail,
        IndexRootStrategyInfo? strategy,
        IndexVolumeHealthInfo? volume)
    {
        if (Contains(runtimeDetail, "snapshot scan queued") ||
            Contains(runtimeDetail, "root validation scan") ||
            Contains(runtimeDetail, "No USN checkpoint") ||
            Contains(runtimeDetail, "checkpoint is incomplete"))
        {
            return true;
        }

        if (strategy?.UsnCatchUpEnabled == true &&
            (volume is null || volume.JournalId is null || volume.LastCommittedUsn <= 0))
        {
            return true;
        }

        return false;
    }

    private static string BuildDetail(
        IndexHealthStatus status,
        string? runtimeDetail,
        IndexRootStrategyInfo? strategy,
        IndexVolumeHealthInfo? volume,
        IndexWatcherDiagnosticInfo? watcher)
    {
        if (!string.IsNullOrWhiteSpace(runtimeDetail))
            return runtimeDetail;

        if (!string.IsNullOrWhiteSpace(watcher?.LastError))
            return watcher.LastError!;

        if (!string.IsNullOrWhiteSpace(volume?.LastError))
            return volume.LastError!;

        if (!string.IsNullOrWhiteSpace(strategy?.Warning))
            return strategy.Warning;

        return status switch
        {
            IndexHealthStatus.Healthy => "Index is current based on the latest persisted diagnostics.",
            IndexHealthStatus.Watching => "Watcher is active and no queued catch-up work is pending.",
            IndexHealthStatus.Offline => "Folder is not reachable; cached index entries are retained.",
            IndexHealthStatus.TooManyExtractorFailures => "Extractor failures exceed the warning threshold.",
            _ => FormatStatus(status),
        };
    }

    private static string FormatJournalStatus(
        IndexVolumeHealthInfo? volume,
        string? runtimeDetail,
        IndexRootStrategyInfo? strategy)
    {
        if (IsJournalExpired(runtimeDetail))
            return "Journal expired";

        if (IsJournalUnavailable(runtimeDetail))
            return "Journal unavailable";

        if (volume is null)
            return strategy?.UsnCatchUpEnabled == true ? "No volume checkpoint" : "Not used";

        var health = string.IsNullOrWhiteSpace(volume.Health) ? "unknown" : volume.Health;
        if (!volume.UsnSupported)
            return $"{volume.FileSystemName} without USN";

        return volume.LastCommittedUsn > 0
            ? $"{health}, USN {volume.LastCommittedUsn:n0}"
            : $"{health}, no checkpoint";
    }

    private static string FormatExtractorFailures(long failures, IndexFailureInfo[] issues)
    {
        if (failures <= 0 && issues.Length == 0)
            return "None";

        var issueCodes = issues
            .Where(static issue => !string.IsNullOrWhiteSpace(issue.IssueCode))
            .Select(static issue => issue.IssueCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var codes = issueCodes.Length == 0 ? string.Empty : $" ({string.Join(", ", issueCodes)})";
        return failures == 1
            ? $"1 failed file{codes}"
            : $"{failures:n0} failed files{codes}";
    }

    private static string FormatValidationSummary(IndexedLocationInfo? location)
    {
        if (location is null || location.LastFullValidationUtc is null)
            return "Never validated";

        if (string.Equals(location.LastValidationStatus, IndexValidationStatus.Passed.ToString(), StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(location.LastValidationMessage)
                ? $"Validated {location.LastValidationFilesChecked:n0} files with no drift."
                : location.LastValidationMessage;

        if (!string.IsNullOrWhiteSpace(location.LastValidationMessage))
            return location.LastValidationMessage;

        return $"Validation {location.LastValidationStatus}: " +
            $"{location.LastValidationMissingFromIndexCount:n0} missing, " +
            $"{location.LastValidationChangedCount:n0} changed, " +
            $"{location.LastValidationMissingFromDiskCount:n0} removed, " +
            $"{location.LastValidationFailedCount:n0} failed checks.";
    }

    private static string EstimateCatchUpTime(IndexingStatus runtimeStatus, string root, int queueDepth)
    {
        if (queueDepth <= 0)
            return "None";

        if (runtimeStatus.ActiveProgress is not { } progress ||
            string.IsNullOrWhiteSpace(runtimeStatus.ActiveRoot) ||
            !string.Equals(runtimeStatus.ActiveRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        var processed = progress.FilesIndexed + progress.FilesSkippedUnchanged + progress.FilesRemoved + progress.FilesFailed;
        return processed > 0
            ? "Estimating from current scan"
            : "Unknown";
    }

    private static bool IsActionableFailure(IndexFailureInfo failure) =>
        !string.Equals(failure.FailureKind, "extraction_issue", StringComparison.Ordinal) ||
        !string.Equals(failure.Severity, "info", StringComparison.OrdinalIgnoreCase);

    private static string FormatStatus(IndexHealthStatus status) =>
        status switch
        {
            IndexHealthStatus.Healthy => "Healthy",
            IndexHealthStatus.CatchingUp => "Catching up",
            IndexHealthStatus.Watching => "Watching",
            IndexHealthStatus.Paused => "Paused",
            IndexHealthStatus.NeedsFullScan => "Needs full scan",
            IndexHealthStatus.JournalUnavailable => "Journal unavailable",
            IndexHealthStatus.JournalExpired => "Journal expired",
            IndexHealthStatus.AccessDenied => "Access denied",
            IndexHealthStatus.Offline => "Offline",
            IndexHealthStatus.TooManyExtractorFailures => "Too many extractor failures",
            _ => status.ToString(),
        };

    private static bool IsAccessDenied(string? value) =>
        Contains(value, "access denied") ||
        Contains(value, "unauthorized");

    private static bool IsJournalExpired(string? value) =>
        Contains(value, "older than the retained journal") ||
        Contains(value, "journal expired") ||
        Contains(value, "expired");

    private static bool IsJournalUnavailable(string? value) =>
        Contains(value, "journal unavailable") ||
        Contains(value, "journal changed") ||
        Contains(value, "journal was recreated") ||
        Contains(value, "newer than the journal cursor") ||
        Contains(value, "does not expose a supported") ||
        Contains(value, "USN replay is unavailable");

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string GetDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }
}
