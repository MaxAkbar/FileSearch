using System;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

/// <summary>
/// One unit of queued indexing work. <paramref name="FirstQueuedUtc"/> is
/// stamped by the queue on first enqueue and preserved across coalescing so
/// debounce extensions can be capped; default means "not yet stamped".
/// </summary>
public sealed record IndexQueueItem(
    string Root,
    string? Path,
    WalkerOptions WalkerOptions,
    IndexChangeKind Kind,
    IndexQueuePriority Priority,
    DateTime DueUtc,
    bool Persisted,
    DateTime FirstQueuedUtc = default,
    IndexRefreshMode RefreshMode = IndexRefreshMode.Incremental);
