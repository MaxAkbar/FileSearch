namespace FileSearch.Core.Indexing;

public sealed record IndexProgress(
    long FilesEnumerated,
    long FilesIndexed,
    long FilesSkippedUnchanged,
    long FilesRemoved,
    long FilesFailed,
    long LinesIndexed);
