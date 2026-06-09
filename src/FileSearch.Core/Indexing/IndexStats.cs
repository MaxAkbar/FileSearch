using System;

namespace FileSearch.Core.Indexing;

public sealed record IndexStats(
    string Root,
    long FileCount,
    long LineCount,
    DateTime? IndexedUtc,
    bool Exists);
