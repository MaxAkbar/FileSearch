using System;

namespace FileSearch.Core.Indexing;

public sealed record IndexedLocationInfo(
    string Root,
    long FileCount,
    long LineCount,
    DateTime? IndexedUtc,
    string Profile,
    bool Exists,
    DateTime? LastFullScanUtc = null,
    string? VolumeKey = null);
