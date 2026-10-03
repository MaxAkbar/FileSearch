using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Volumes;

public enum VolumeIndexPhase
{
    NotIndexed,
    Loading,
    Building,
    Ready,
    NeedsRebuild,
    Unsupported,
    Failed,
}

/// <summary>A local drive that could carry a drive name index.</summary>
public sealed record VolumeIndexCandidate(
    string VolumeRoot,
    string Label,
    string FileSystem,
    long TotalBytes,
    bool IsSupported,
    string? UnsupportedReason);

public sealed record VolumeIndexStatus(
    string VolumeRoot,
    VolumeIndexPhase Phase,
    long EntryCount,
    VolumeBuildMethod? BuildMethod,
    DateTime? BuiltUtc,
    DateTime? UpdatedUtc,
    bool IsTracking,
    string Message,
    long? ProgressEntries = null)
{
    public bool IsReady => Phase == VolumeIndexPhase.Ready;
}

/// <summary>
/// Whole-drive file and folder name index for local NTFS volumes, built
/// from the master file table (or an unprivileged directory walk) and kept
/// current from the USN change journal.
/// </summary>
public interface IVolumeNameIndex : IAsyncDisposable
{
    event EventHandler<VolumeIndexStatus>? StatusChanged;

    /// <summary>Local fixed drives, with whether each can be indexed.</summary>
    IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes();

    /// <summary>Volumes that are tracked, loaded, or have a snapshot on disk.</summary>
    IReadOnlyList<VolumeIndexStatus> GetStatuses();

    /// <summary>
    /// Makes this process the live owner of <paramref name="volumeRoots"/>:
    /// loads their snapshots, follows the change journal, saves snapshots
    /// periodically, and builds missing indexes with
    /// <see cref="VolumeBuildMethod.Auto"/> (never prompting for elevation).
    /// Volumes left out of the set stop being tracked.
    /// </summary>
    Task StartTrackingAsync(IReadOnlyCollection<string> volumeRoots, CancellationToken cancellationToken);

    /// <summary>
    /// Builds (or rebuilds) a volume's index and saves its snapshot.
    /// <see cref="VolumeBuildMethod.MasterFileTable"/> from an unelevated
    /// process asks Windows for administrator permission; declining throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<VolumeIndexStatus> BuildAsync(string volumeRoot, VolumeBuildMethod method, CancellationToken cancellationToken);

    /// <summary>Stops tracking a volume and deletes its snapshot.</summary>
    Task RemoveAsync(string volumeRoot, CancellationToken cancellationToken);

    /// <summary>Ranked filename search over indexed volumes.</summary>
    Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="path"/> lies on a volume whose index is loaded and ready.</summary>
    bool IsReady(string path);

    /// <summary>Saves snapshots that changed since they were last written.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs the master file table scan in an elevated helper process for hosts
/// that are not elevated themselves.
/// </summary>
public interface IVolumeScanLauncher
{
    /// <summary>
    /// Scans <paramref name="volumeRoot"/> elevated and writes a snapshot to
    /// <paramref name="outputPath"/>. Throws
    /// <see cref="OperationCanceledException"/> when the user declines the
    /// elevation prompt.
    /// </summary>
    Task RunElevatedScanAsync(string volumeRoot, string outputPath, CancellationToken cancellationToken);
}

public sealed class VolumeNameIndexOptions
{
    public string SnapshotDirectory { get; init; } = GetDefaultSnapshotDirectory(null);

    /// <summary>How often a tracking owner reads the change journal.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often a tracking owner rewrites a changed snapshot.</summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Non-owners (CLI, MCP) catch up from the journal before a search when
    /// their copy is older than this.
    /// </summary>
    public TimeSpan OnDemandCatchUpInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Wait before retrying an automatic rebuild that failed.</summary>
    public TimeSpan RebuildRetryInterval { get; init; } = TimeSpan.FromMinutes(10);

    public static string GetDefaultSnapshotDirectory(string? databasePath)
    {
        var indexDirectory = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileSearch",
                "Index")
            : Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? string.Empty;
        return Path.Combine(indexDirectory, "Volumes");
    }
}
