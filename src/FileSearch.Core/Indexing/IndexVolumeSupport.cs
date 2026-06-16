using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

internal sealed record IndexVolumeInfo(
    string VolumeKey,
    string VolumeRoot,
    string DevicePath,
    string? VolumeSerial,
    string FileSystemName,
    bool IsRemote,
    bool UsnSupported);

internal sealed record IndexVolumeCheckpoint(
    long Id,
    string VolumeKey,
    ulong? JournalId,
    long LastCommittedUsn,
    string Health,
    string? LastError);

internal readonly record struct ResolvedFileIdentity(
    string FileReferenceNumber,
    string? ParentFileReferenceNumber);

internal sealed record IndexedFileIdentity(
    long VolumeId,
    string FileReferenceNumber,
    string? ParentFileReferenceNumber,
    long? LastObservedUsn);

internal sealed record IndexReplayReferenceSet(
    IReadOnlySet<string> FileReferences,
    IReadOnlySet<string> DirectoryReferences)
{
    public static IndexReplayReferenceSet Empty { get; } =
        new(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
}

internal sealed record UsnJournalSnapshot(
    ulong JournalId,
    long FirstUsn,
    long NextUsn);

internal sealed record UsnChangeRecord(
    string FileReferenceNumber,
    string ParentFileReferenceNumber,
    long Usn,
    DateTime TimestampUtc,
    uint Reason,
    FileAttributes FileAttributes,
    string Name);

internal enum IndexReplayChangeKind
{
    Upsert,
    DeleteByIdentity,
    EnsureDirectory,
}

internal sealed record IndexReplayChange(
    IndexReplayChangeKind Kind,
    string? Root,
    string? Path,
    string FileReferenceNumber);

internal interface IIndexVolumeResolver
{
    bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason);

    bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity);

    bool TryResolvePathFromFileId(
        IndexVolumeInfo volume,
        string fileReferenceNumber,
        out string path,
        out string fallbackReason);
}

internal interface IUsnJournalReader
{
    Task<UsnJournalSnapshot> QueryAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken);

    IAsyncEnumerable<UsnChangeRecord> ReadChangesAsync(
        IndexVolumeInfo volume,
        long startUsn,
        long stopAtUsn,
        ulong journalId,
        CancellationToken cancellationToken);
}

internal interface IIndexCatchUpStore
{
    Task<IndexVolumeCheckpoint?> GetVolumeCheckpointAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken);

    Task<IndexReplayReferenceSet> GetReplayReferencesAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken);

    Task DeleteFileByIdentityAsync(
        string volumeKey,
        string fileReferenceNumber,
        CancellationToken cancellationToken);

    Task UpdateVolumeCheckpointAsync(
        IndexVolumeInfo volume,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken);
}

internal interface IIndexReplayWriter
{
    Task ApplyReplayBatchAsync(
        IndexVolumeInfo volume,
        IReadOnlyCollection<IndexedLocation> locations,
        IReadOnlyList<IndexReplayChange> changes,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken);
}
