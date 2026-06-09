using System;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed record IndexQueueItem(
    string Root,
    string? Path,
    WalkerOptions WalkerOptions,
    IndexChangeKind Kind,
    IndexQueuePriority Priority,
    DateTime DueUtc,
    bool Persisted);
