using System;

namespace FileSearch.Core.Indexing;

internal sealed record IndexingResourcePolicy(
    TimeSpan MaxBurst,
    TimeSpan BurstRest,
    IndexingThrottle Throttle)
{
    public static IndexingResourcePolicy For(IndexerResourceProfile profile) =>
        Normalize(profile) switch
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

    public static IndexerResourceProfile Normalize(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;
}
