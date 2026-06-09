namespace FileSearch.Core.Indexing;

public enum IndexCoverageStatus
{
    Covered,
    Disabled,
    Missing,
    Incompatible,
    Unsupported,
    Error,
}

public sealed record IndexCoverage(IndexCoverageStatus Status, string Message)
{
    public bool IsCovered => Status == IndexCoverageStatus.Covered;
}
