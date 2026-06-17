namespace FileSearch.Core.Indexing;

public sealed record IndexerRuntimeOptions(
    bool PauseOnBattery = false,
    bool IndexOnlyWhenIdle = false,
    int CpuLimitPercent = 0,
    int DiskPauseMilliseconds = 0)
{
    public static IndexerRuntimeOptions Default { get; } = new();

    public IndexerRuntimeOptions Normalize() =>
        this with
        {
            CpuLimitPercent = CpuLimitPercent is <= 0 or >= 100 ? 0 : Math.Clamp(CpuLimitPercent, 1, 99),
            DiskPauseMilliseconds = Math.Clamp(DiskPauseMilliseconds, 0, 1_000),
        };
}
