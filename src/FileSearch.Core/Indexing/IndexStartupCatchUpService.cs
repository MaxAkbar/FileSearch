using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Indexing;

internal sealed class IndexStartupCatchUpService : IIndexStartupCatchUpService
{
    private const int MaxReplayBatchRecords = 512;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;

    private readonly IIndexWriter _indexWriter;
    private readonly IIndexCatchUpStore _store;
    private readonly IIndexVolumeResolver _volumeResolver;
    private readonly IUsnJournalReader _journalReader;
    private readonly ILogger _logger;

    public IndexStartupCatchUpService(
        IIndexWriter indexWriter,
        IIndexCatchUpStore store,
        IIndexVolumeResolver volumeResolver,
        IUsnJournalReader journalReader,
        ILogger<IndexStartupCatchUpService>? logger = null)
    {
        _indexWriter = indexWriter;
        _store = store;
        _volumeResolver = volumeResolver;
        _journalReader = journalReader;
        _logger = logger ?? NullLogger<IndexStartupCatchUpService>.Instance;
    }

    public async Task<IndexStartupCatchUpResult> CatchUpAsync(
        IReadOnlyCollection<IndexedLocation> locations,
        CancellationToken cancellationToken)
    {
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = GroupByVolume(locations, fallback);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanReplay(group, fallback))
                continue;

            try
            {
                var checkpoint = await _store.GetVolumeCheckpointAsync(group.Volume, cancellationToken).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    AddFallback(group, fallback, "No USN checkpoint has been established yet.");
                    continue;
                }

                if (checkpoint.JournalId is null || checkpoint.LastCommittedUsn <= 0)
                {
                    AddFallback(group, fallback, "USN checkpoint is incomplete.");
                    continue;
                }

                var journal = await _journalReader.QueryAsync(group.Volume, cancellationToken).ConfigureAwait(false);
                if (journal.JournalId != checkpoint.JournalId.Value)
                {
                    AddFallback(group, fallback, "USN journal changed or was recreated.");
                    continue;
                }

                if (checkpoint.LastCommittedUsn < journal.FirstUsn)
                {
                    AddFallback(group, fallback, "Saved USN checkpoint is older than the retained journal.");
                    continue;
                }

                if (checkpoint.LastCommittedUsn > journal.NextUsn)
                {
                    AddFallback(group, fallback, "Saved USN checkpoint is newer than the journal cursor.");
                    continue;
                }

                var references = await _store.GetReplayReferencesAsync(group.Volume, cancellationToken).ConfigureAwait(false);
                if (references.DirectoryReferences.Count == 0)
                {
                    AddFallback(group, fallback, "No directory identity checkpoint has been established yet.");
                    continue;
                }

                if (!await TryReplayGroupAsync(group, checkpoint, journal, references, fallback, cancellationToken).ConfigureAwait(false))
                    continue;

                foreach (var location in group.Locations)
                    handled.Add(IndexPath.NormalizeRoot(location.Root));
            }
            catch (Exception ex) when (IsReplayFallbackException(ex))
            {
                _logger.LogWarning(ex, "USN startup catch-up failed for volume {VolumeKey}.", group.Volume.VolumeKey);
                AddFallback(group, fallback, ex.Message);
            }
        }

        return new IndexStartupCatchUpResult(handled, fallback);
    }

    private async Task<bool> TryReplayGroupAsync(
        VolumeLocationGroup group,
        IndexVolumeCheckpoint checkpoint,
        UsnJournalSnapshot journal,
        IndexReplayReferenceSet references,
        Dictionary<string, string> fallback,
        CancellationToken cancellationToken)
    {
        var fileReferences = new HashSet<string>(references.FileReferences, StringComparer.Ordinal);
        var directoryReferences = new HashSet<string>(references.DirectoryReferences, StringComparer.Ordinal);
        var changesByFile = new Dictionary<string, List<IndexReplayChange>>(StringComparer.Ordinal);
        var recordsInBatch = 0;
        var batchCheckpointUsn = checkpoint.LastCommittedUsn;
        void SetDelete(string fileReferenceNumber) =>
            changesByFile[fileReferenceNumber] = new List<IndexReplayChange>
            {
                new(
                    IndexReplayChangeKind.DeleteByIdentity,
                    Root: null,
                    Path: null,
                    fileReferenceNumber),
            };

        void SetUpserts(string fileReferenceNumber, IEnumerable<IndexedLocation> matchingRoots, string normalizedPath)
        {
            var changes = new List<IndexReplayChange>();
            foreach (var location in matchingRoots)
            {
                changes.Add(new IndexReplayChange(
                    IndexReplayChangeKind.Upsert,
                    IndexPath.NormalizeRoot(location.Root),
                    normalizedPath,
                    fileReferenceNumber));
            }

            changesByFile[fileReferenceNumber] = changes;
            fileReferences.Add(fileReferenceNumber);
        }

        void SetDirectory(string directoryReferenceNumber, IEnumerable<IndexedLocation> matchingRoots, string normalizedPath)
        {
            var changes = new List<IndexReplayChange>();
            foreach (var location in matchingRoots)
            {
                changes.Add(new IndexReplayChange(
                    IndexReplayChangeKind.EnsureDirectory,
                    IndexPath.NormalizeRoot(location.Root),
                    normalizedPath,
                    directoryReferenceNumber));
            }

            changesByFile[directoryReferenceNumber] = changes;
            directoryReferences.Add(directoryReferenceNumber);
        }

        async Task CommitBatchAsync(long committedUsn)
        {
            var changes = changesByFile.Values.SelectMany(static x => x).ToArray();
            if (_indexWriter is IIndexReplayWriter replayWriter)
            {
                await replayWriter.ApplyReplayBatchAsync(
                    group.Volume,
                    group.Locations,
                    changes,
                    journal.JournalId,
                    committedUsn,
                    "healthy",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (changes.Any(static change => change.Kind == IndexReplayChangeKind.EnsureDirectory))
                    throw new InvalidOperationException("USN replay requires directory identity persistence.");

                foreach (var change in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (change.Kind == IndexReplayChangeKind.DeleteByIdentity)
                    {
                        await _store.DeleteFileByIdentityAsync(
                            group.Volume.VolumeKey,
                            change.FileReferenceNumber,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (change is { Root: not null, Path: not null })
                    {
                        var location = group.Locations.First(x =>
                            string.Equals(IndexPath.NormalizeRoot(x.Root), change.Root, StringComparison.OrdinalIgnoreCase));
                        await _indexWriter.UpsertFileAsync(
                            change.Root,
                            change.Path,
                            location.WalkerOptions,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await _store.UpdateVolumeCheckpointAsync(
                    group.Volume,
                    journal.JournalId,
                    committedUsn,
                    "healthy",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            changesByFile.Clear();
            recordsInBatch = 0;
            batchCheckpointUsn = committedUsn;
        }

        await foreach (var record in _journalReader.ReadChangesAsync(
                           group.Volume,
                           checkpoint.LastCommittedUsn,
                           journal.NextUsn,
                           journal.JournalId,
                           cancellationToken).ConfigureAwait(false))
        {
            if (recordsInBatch >= MaxReplayBatchRecords)
                await CommitBatchAsync(batchCheckpointUsn).ConfigureAwait(false);

            recordsInBatch++;
            batchCheckpointUsn = Math.Max(batchCheckpointUsn, NextCheckpointUsn(record.Usn, journal.NextUsn));

            var fileIsKnown = fileReferences.Contains(record.FileReferenceNumber);
            var directoryIsKnown = directoryReferences.Contains(record.FileReferenceNumber);
            var parentDirectoryIsKnown = directoryReferences.Contains(record.ParentFileReferenceNumber);

            if (record.FileAttributes.HasFlag(FileAttributes.Directory))
            {
                if (RequiresDirectoryFallback(record))
                {
                    if (directoryIsKnown || parentDirectoryIsKnown)
                    {
                        AddFallback(group, fallback, "Directory change requires root validation scan in V1.");
                        return false;
                    }

                    continue;
                }

                if (directoryIsKnown)
                    continue;

                if (!parentDirectoryIsKnown)
                    continue;

                if (!_volumeResolver.TryResolvePathFromFileId(
                        group.Volume,
                        record.FileReferenceNumber,
                        out var resolvedDirectory,
                        out var resolveReason))
                {
                    AddFallback(group, fallback, $"Could not resolve changed directory path: {resolveReason}");
                    return false;
                }

                var normalizedDirectory = IndexPath.NormalizeFile(resolvedDirectory);
                var matchingDirectoryRoots = MatchingRoots(group.Locations, normalizedDirectory).ToArray();
                if (matchingDirectoryRoots.Length > 0)
                    SetDirectory(record.FileReferenceNumber, matchingDirectoryRoots, normalizedDirectory);

                continue;
            }

            if (IsDelete(record))
            {
                if (fileIsKnown)
                    SetDelete(record.FileReferenceNumber);

                continue;
            }

            if (!fileIsKnown && !parentDirectoryIsKnown)
                continue;

            if (!_volumeResolver.TryResolvePathFromFileId(
                    group.Volume,
                    record.FileReferenceNumber,
                    out var resolvedPath,
                    out _))
            {
                SetDelete(record.FileReferenceNumber);
                continue;
            }

            var normalizedPath = IndexPath.NormalizeFile(resolvedPath);
            var matchingFileRoots = MatchingRoots(group.Locations, normalizedPath).ToArray();
            if (matchingFileRoots.Length == 0)
            {
                SetDelete(record.FileReferenceNumber);
                continue;
            }

            SetUpserts(record.FileReferenceNumber, matchingFileRoots, normalizedPath);
        }

        if (recordsInBatch > 0 || batchCheckpointUsn < journal.NextUsn)
            await CommitBatchAsync(journal.NextUsn).ConfigureAwait(false);

        return true;
    }

    private List<VolumeLocationGroup> GroupByVolume(
        IEnumerable<IndexedLocation> locations,
        Dictionary<string, string> fallback)
    {
        var groups = new Dictionary<string, VolumeLocationGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            var normalizedRoot = IndexPath.NormalizeRoot(location.Root);
            if (!Directory.Exists(normalizedRoot))
            {
                fallback[normalizedRoot] = "Indexed root no longer exists.";
                continue;
            }

            if (!_volumeResolver.TryResolveVolume(normalizedRoot, out var volume, out var reason))
            {
                fallback[normalizedRoot] = reason;
                continue;
            }

            if (!groups.TryGetValue(volume.VolumeKey, out var group))
            {
                group = new VolumeLocationGroup(volume);
                groups[volume.VolumeKey] = group;
            }

            group.Locations.Add(location with { Root = normalizedRoot });
        }

        return groups.Values.ToList();
    }

    private static bool CanReplay(
        VolumeLocationGroup group,
        Dictionary<string, string> fallback)
    {
        if (group.Volume.IsRemote)
        {
            AddFallback(group, fallback, "USN replay is unavailable for remote roots.");
            return false;
        }

        if (!group.Volume.UsnSupported)
        {
            AddFallback(group, fallback, $"Filesystem {group.Volume.FileSystemName} does not expose a supported USN journal.");
            return false;
        }

        return true;
    }

    private static IEnumerable<IndexedLocation> MatchingRoots(
        IEnumerable<IndexedLocation> locations,
        string path)
    {
        foreach (var location in locations)
        {
            var root = IndexPath.NormalizeRoot(location.Root);
            if (!IsUnderRoot(root, path))
                continue;

            if (!location.WalkerOptions.Recursive && !IsDirectChild(root, path))
                continue;

            yield return location;
        }
    }

    private static void AddFallback(
        VolumeLocationGroup group,
        Dictionary<string, string> fallback,
        string reason)
    {
        foreach (var location in group.Locations)
            fallback[IndexPath.NormalizeRoot(location.Root)] = reason;
    }

    private static bool RequiresDirectoryFallback(UsnChangeRecord record) =>
        record.FileAttributes.HasFlag(FileAttributes.Directory) &&
        (HasReason(record, UsnReasonFileDelete) ||
         HasReason(record, UsnReasonRenameOldName) ||
         HasReason(record, UsnReasonRenameNewName));

    private static bool IsDelete(UsnChangeRecord record) =>
        HasReason(record, UsnReasonFileDelete);

    private static bool HasReason(UsnChangeRecord record, uint reason) =>
        (record.Reason & reason) != 0;

    private static long NextCheckpointUsn(long recordUsn, long journalNextUsn)
    {
        if (recordUsn >= journalNextUsn)
            return journalNextUsn;

        return recordUsn == long.MaxValue
            ? journalNextUsn
            : Math.Min(recordUsn + 1, journalNextUsn);
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 &&
            relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static bool IsDirectChild(string root, string path)
    {
        var directory = Path.GetDirectoryName(path);
        return directory is not null && string.Equals(
            IndexPath.NormalizeRoot(directory),
            root,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplayFallbackException(Exception ex) =>
        ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException;

    private sealed class VolumeLocationGroup
    {
        public VolumeLocationGroup(IndexVolumeInfo volume) => Volume = volume;

        public IndexVolumeInfo Volume { get; }

        public List<IndexedLocation> Locations { get; } = new();
    }
}
