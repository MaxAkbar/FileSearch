using System;
using System.Collections.Generic;

namespace FileSearch.Core.Indexing;

public sealed record IndexVolumeHealthInfo(
    string VolumeKey,
    string FileSystemName,
    bool IsRemote,
    bool UsnSupported,
    ulong? JournalId,
    long LastCommittedUsn,
    string Health,
    string? LastError,
    DateTime? LastCheckedUtc);

public sealed record IndexDatabaseInfo(
    string DatabasePath,
    bool Exists,
    bool IsCompatible,
    string SchemaVersion,
    long DatabaseBytes,
    long WalBytes,
    long ShmBytes,
    int LocationCount,
    long TotalFileCount,
    long TotalLineCount,
    int PendingChangeCount,
    DateTime? LastIndexedUtc,
    IReadOnlyList<IndexVolumeHealthInfo>? VolumeHealth = null)
{
    public long TotalBytes => DatabaseBytes + WalBytes + ShmBytes;
}
