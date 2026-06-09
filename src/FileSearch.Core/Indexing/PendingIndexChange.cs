namespace FileSearch.Core.Indexing;

public sealed record PendingIndexChange(
    long Id,
    string Root,
    string Path,
    IndexChangeKind Kind);
