using System;

namespace FileSearch.Core.Engine;

public enum SearchEngineMode
{
    Legacy,
    Hybrid,
}

public sealed record SearchOptions
{
    /// <summary>Max worker threads processing files concurrently.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Bounded channel capacity for streaming hits back to the caller.</summary>
    public int ChannelCapacity { get; init; } = 1024;

    /// <summary>Maximum hits per single file (caps runaway matches).</summary>
    public int MaxHitsPerFile { get; init; } = 1000;

    /// <summary>
    /// Selects the search engine exposed through <see cref="ISearcher"/>.
    /// The legacy engine remains the default until the hybrid path has more
    /// production mileage.
    /// </summary>
    public SearchEngineMode EngineMode { get; init; } = SearchEngineMode.Legacy;
}
