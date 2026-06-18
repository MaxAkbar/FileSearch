namespace FileSearch.Core.Indexing;

public sealed class IndexingServiceOptions
{
    public TimeSpan SchedulerPollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan SnapshotScanInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan NetworkSnapshotScanInterval { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RemovableReconnectPollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan FullValidationInterval { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan FullValidationIdleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    public IndexingServiceOptions Normalize()
    {
        static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
            value > TimeSpan.Zero ? value : fallback;

        return new IndexingServiceOptions
        {
            SchedulerPollInterval = PositiveOrDefault(SchedulerPollInterval, TimeSpan.FromMinutes(1)),
            SnapshotScanInterval = PositiveOrDefault(SnapshotScanInterval, TimeSpan.FromHours(1)),
            NetworkSnapshotScanInterval = PositiveOrDefault(NetworkSnapshotScanInterval, TimeSpan.FromMinutes(30)),
            RemovableReconnectPollInterval = PositiveOrDefault(RemovableReconnectPollInterval, TimeSpan.FromMinutes(1)),
            FullValidationInterval = PositiveOrDefault(FullValidationInterval, TimeSpan.FromDays(1)),
            FullValidationIdleThreshold = PositiveOrDefault(FullValidationIdleThreshold, TimeSpan.FromMinutes(5)),
        };
    }
}
