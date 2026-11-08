using System.Diagnostics;

namespace FileSearch.Core.Databases;

/// <summary>Opt-in, per-open diagnostics for the benchmark; ordinary previews collect no timings.</summary>
internal sealed class DatabaseOpenTimings
{
    public long? SnapshotTicks { get; set; }
    public long? CopyTicks { get; set; }
    public long MainBytes { get; set; }
    public long WalBytes { get; set; }
    public long? EngineOpenTicks { get; set; }
    public long? EngineSetupTicks { get; set; }
    public long? SchemaTicks { get; set; }
    public long? ValidationTicks { get; set; }
    public long? TotalTicks { get; set; }
    public bool Completed { get; set; }

    internal static long Start(DatabaseOpenTimings? timings) => timings is null ? 0 : Stopwatch.GetTimestamp();
    internal static long Elapsed(long start) => Stopwatch.GetTimestamp() - start;
    public static double? Milliseconds(long? ticks) => ticks * 1000d / Stopwatch.Frequency;
}
