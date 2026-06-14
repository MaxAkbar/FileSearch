using System;

namespace FileSearch.Core.Indexing;

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
    DateTime? LastIndexedUtc)
{
    public long TotalBytes => DatabaseBytes + WalBytes + ShmBytes;
}
