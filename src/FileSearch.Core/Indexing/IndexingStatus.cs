namespace FileSearch.Core.Indexing;

public sealed record IndexingStatus(
    bool IsRunning,
    bool IsPaused,
    bool IsProcessing,
    int QueueLength,
    string Message,
    string? ActiveRoot = null,
    IndexChangeKind? ActiveKind = null,
    IReadOnlyDictionary<string, int>? QueuedRootCounts = null,
    IndexProgress? ActiveProgress = null,
    IReadOnlyDictionary<string, string>? RootStatusDetails = null,
    IReadOnlyDictionary<string, IndexWatcherDiagnosticInfo>? WatcherDiagnostics = null);
