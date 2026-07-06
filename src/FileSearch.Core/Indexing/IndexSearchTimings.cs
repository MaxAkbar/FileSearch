using System;
using System.Diagnostics;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Phase breakdown for one indexed <see cref="CSharpDbFileIndex.SearchAsync"/>
/// call, used to attribute fixed query overhead (database open, FTS lookup,
/// row fetch, recheck) in benchmarks. Populated only while
/// <see cref="CSharpDbFileIndex.SearchTimingsCallback"/> is set; ordinary
/// searches skip all bookkeeping. Tick values are <see cref="Stopwatch"/>
/// timestamp deltas.
/// </summary>
internal sealed class IndexSearchTimings
{
    /// <summary>Opening the database connection.</summary>
    public long OpenTicks { get; set; }

    /// <summary>Resolving the requested root to a root id.</summary>
    public long RootResolveTicks { get; set; }

    /// <summary>Metadata token lookup, scoring, and ordering.</summary>
    public long MetadataTicks { get; set; }

    /// <summary>Full-text index candidate lookups.</summary>
    public long FtsLookupTicks { get; set; }

    /// <summary>Trigram postings lookups used to bound substring candidates.</summary>
    public long TrigramLookupTicks { get; set; }

    /// <summary>
    /// Reading line rows from the database (FTS id batches or the
    /// full-scan fallback), excluding recheck time. Includes consumer dwell
    /// time between yielded hits, which is negligible for counting consumers.
    /// </summary>
    public long LineFetchTicks { get; set; }

    /// <summary>Re-verifying fetched lines against the query expression.</summary>
    public long RecheckTicks { get; set; }

    /// <summary>Whole SearchAsync call.</summary>
    public long TotalTicks { get; set; }

    /// <summary>Line rows fetched from the database and rechecked.</summary>
    public int LinesExamined { get; set; }

    /// <summary>Hits that survived the recheck.</summary>
    public int HitCount { get; set; }

    /// <summary>True when no FTS candidate query existed and the search scanned every line row.</summary>
    public bool UsedFullScan { get; set; }

    /// <summary>True when substring candidates came from the trigram postings table.</summary>
    public bool UsedTrigramIndex { get; set; }

    public static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
