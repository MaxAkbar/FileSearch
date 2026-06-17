using System;

namespace FileSearch.Core.Indexing;

internal sealed record IndexingResourcePolicy(
    TimeSpan MaxBurst,
    TimeSpan BurstRest,
    IndexingThrottle Throttle)
{
    public static IndexingResourcePolicy For(IndexerResourceProfile profile) =>
        For(profile, IndexerRuntimeOptions.Default);

    public static IndexingResourcePolicy For(IndexerResourceProfile profile, IndexerRuntimeOptions runtimeOptions)
    {
        var normalizedOptions = runtimeOptions.Normalize();
        IndexingResourcePolicy policy = Normalize(profile) switch
        {
            IndexerResourceProfile.Low => new(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(6),
                new IndexingThrottle(1, TimeSpan.FromMilliseconds(25))),
            IndexerResourceProfile.High => new(
                TimeSpan.FromMinutes(2),
                TimeSpan.Zero,
                IndexingThrottle.None),
            _ => new(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                new IndexingThrottle(20, TimeSpan.FromMilliseconds(5))),
        };

        return policy with
        {
            Throttle = Combine(policy.Throttle, BuildRuntimeThrottle(normalizedOptions)),
        };
    }

    public static IndexerResourceProfile Normalize(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;

    private static IndexingThrottle BuildRuntimeThrottle(IndexerRuntimeOptions options)
    {
        IndexingThrottle throttle = IndexingThrottle.None;

        if (options.CpuLimitPercent > 0)
        {
            var filesPerPause = options.CpuLimitPercent <= 25
                ? 1
                : options.CpuLimitPercent <= 50 ? 5 : 10;
            var pause = TimeSpan.FromMilliseconds(Math.Max(5, 100 - options.CpuLimitPercent));
            throttle = Combine(throttle, new IndexingThrottle(filesPerPause, pause));
        }

        if (options.DiskPauseMilliseconds > 0)
            throttle = Combine(throttle, new IndexingThrottle(1, TimeSpan.FromMilliseconds(options.DiskPauseMilliseconds)));

        return throttle;
    }

    private static IndexingThrottle Combine(IndexingThrottle left, IndexingThrottle right)
    {
        if (!left.IsEnabled)
            return right;
        if (!right.IsEnabled)
            return left;

        return new IndexingThrottle(
            Math.Min(left.FilesPerPause, right.FilesPerPause),
            left.Pause >= right.Pause ? left.Pause : right.Pause);
    }
}
