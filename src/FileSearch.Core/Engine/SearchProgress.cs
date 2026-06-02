namespace FileSearch.Core.Engine;

public sealed record SearchProgress(
    long FilesEnumerated,
    long FilesProcessed,
    long FilesMatched,
    long FilesSkipped,
    long FilesFailed);
