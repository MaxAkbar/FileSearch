namespace FileSearch.Core.Indexing;

public enum IndexLocationKind
{
    Unknown,
    LocalUsn,
    LocalSnapshot,
    NetworkShare,
    CloudBacked,
    Removable,
}

public enum IndexUpdateStrategy
{
    Unknown,
    UsnJournalAndWatcher,
    SnapshotScanAndWatcher,
    ScheduledSnapshotScan,
    OfflineCacheAndReconnectScan,
}

public sealed record IndexRootStrategyInfo(
    string RootPath,
    IndexLocationKind LocationKind,
    IndexUpdateStrategy UpdateStrategy,
    string StrategyLabel,
    string Warning,
    bool UsnCatchUpEnabled,
    bool WatcherRecommended);

internal enum IndexVolumeDriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
}

internal sealed record IndexLocationStrategy(
    IndexLocationKind LocationKind,
    IndexUpdateStrategy UpdateStrategy,
    string StrategyLabel,
    string Warning,
    bool UsnCatchUpEnabled,
    bool WatcherRecommended)
{
    public string FallbackReason =>
        string.IsNullOrWhiteSpace(Warning)
            ? $"{StrategyLabel} requires snapshot refresh."
            : Warning;
}

internal static class IndexLocationStrategyResolver
{
    public static IndexLocationStrategy Classify(string root, IndexVolumeInfo volume)
    {
        if (IsCloudBackedRoot(root))
        {
            return new IndexLocationStrategy(
                IndexLocationKind.CloudBacked,
                IndexUpdateStrategy.SnapshotScanAndWatcher,
                "Cloud folder: snapshot scan + watcher",
                "Cloud-backed folders use snapshot scans because hydration and sync timing can delay file availability.",
                UsnCatchUpEnabled: false,
                WatcherRecommended: true);
        }

        if (volume.DriveKind == IndexVolumeDriveKind.Network || volume.IsRemote)
        {
            return new IndexLocationStrategy(
                IndexLocationKind.NetworkShare,
                IndexUpdateStrategy.ScheduledSnapshotScan,
                "Network share: scheduled snapshot scan",
                "Network shares use snapshot scans; watcher events are best effort and may be incomplete.",
                UsnCatchUpEnabled: false,
                WatcherRecommended: false);
        }

        if (volume.DriveKind == IndexVolumeDriveKind.Removable)
        {
            return new IndexLocationStrategy(
                IndexLocationKind.Removable,
                IndexUpdateStrategy.OfflineCacheAndReconnectScan,
                "Removable drive: offline cache + reconnect scan",
                "Removable drive indexes are retained while offline and refreshed when the drive is available.",
                UsnCatchUpEnabled: false,
                WatcherRecommended: true);
        }

        if (volume.UsnSupported &&
            volume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return new IndexLocationStrategy(
                IndexLocationKind.LocalUsn,
                IndexUpdateStrategy.UsnJournalAndWatcher,
                "Local NTFS: USN journal + watcher",
                string.Empty,
                UsnCatchUpEnabled: true,
                WatcherRecommended: true);
        }

        return new IndexLocationStrategy(
            IndexLocationKind.LocalSnapshot,
            IndexUpdateStrategy.SnapshotScanAndWatcher,
            $"Local {volume.FileSystemName}: snapshot scan + watcher",
            volume.FileSystemName.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
                ? "ReFS uses 128-bit file identifiers; FileSearch uses snapshot scans until ReFS USN replay is supported."
                : $"Filesystem {volume.FileSystemName} does not expose a supported local USN journal.",
            UsnCatchUpEnabled: false,
            WatcherRecommended: true);
    }

    public static IndexLocationStrategy FromStored(
        IndexLocationKind locationKind,
        IndexUpdateStrategy updateStrategy,
        string warning,
        bool usnCatchUpEnabled,
        bool watcherRecommended)
    {
        return new IndexLocationStrategy(
            locationKind,
            updateStrategy,
            LabelFor(locationKind, updateStrategy),
            warning,
            usnCatchUpEnabled,
            watcherRecommended);
    }

    private static string LabelFor(IndexLocationKind locationKind, IndexUpdateStrategy updateStrategy) =>
        locationKind switch
        {
            IndexLocationKind.LocalUsn => "Local NTFS: USN journal + watcher",
            IndexLocationKind.LocalSnapshot => "Local filesystem: snapshot scan + watcher",
            IndexLocationKind.NetworkShare => "Network share: scheduled snapshot scan",
            IndexLocationKind.CloudBacked => "Cloud folder: snapshot scan + watcher",
            IndexLocationKind.Removable => "Removable drive: offline cache + reconnect scan",
            _ => updateStrategy.ToString(),
        };

    private static bool IsCloudBackedRoot(string root)
    {
        var normalized = IndexPath.NormalizeRoot(root);
        foreach (var cloudRoot in EnumerateKnownCloudRoots())
        {
            if (IsSameOrUnderRoot(cloudRoot, normalized))
                return true;
        }

        return ContainsKnownCloudSegment(normalized);
    }

    private static IEnumerable<string> EnumerateKnownCloudRoots()
    {
        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer", "Dropbox", "Box" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                yield return IndexPath.NormalizeRoot(value);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            yield break;

        foreach (var folderName in new[] { "Dropbox", "Box", "Google Drive" })
        {
            var path = Path.Combine(userProfile, folderName);
            yield return IndexPath.NormalizeRoot(path);
        }
    }

    private static bool ContainsKnownCloudSegment(string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("Box", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrUnderRoot(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
            return true;

        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 &&
            relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
