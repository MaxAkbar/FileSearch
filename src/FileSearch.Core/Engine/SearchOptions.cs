using System;

namespace FileSearch.Core.Engine;

public sealed record SearchOptions
{
    /// <summary>Max worker threads processing files concurrently.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Bounded channel capacity for streaming hits back to the caller.</summary>
    public int ChannelCapacity { get; init; } = 1024;

    /// <summary>Maximum hits per single file (caps runaway matches).</summary>
    public int MaxHitsPerFile { get; init; } = 1000;
}
