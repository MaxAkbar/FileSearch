using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileSearch.Core.Databases;

namespace FileSearch.Benchmarks;

internal sealed record DatabaseProfileSlot(int Round, int Position, DatabaseKind Engine, bool Warmup);
internal sealed record DatabasePhaseTimes(double? Snapshot, double? Copy, double? SnapshotOther, double? EngineOpen,
    double? EngineSetup, double? Schema, double? Validation, double? OpenOther, double? OpenTotal,
    double? FirstPage, double? DeepPage, double? SequentialDeepPage, double? OpenAndFirstPage, double? Close);
internal sealed record DatabasePhaseSample(DatabaseProfileSlot Slot, bool Success, string? Error, DatabasePhaseTimes Ms,
    long MainBytes, long WalBytes, long AllocatedBytes, long WorkingSetBytes, bool SequentialDeepPageApplicable);
internal sealed record DatabasePhaseSummary(DatabaseKind Engine, int? Position, int Samples, int Successes,
    IReadOnlyDictionary<string, Percentiles> Ms);
internal sealed record DatabaseSourcePart(string Path, bool Exists, long Bytes, string? Sha256);
internal sealed record DatabaseProfileSource(DatabaseKind Engine, string Path, string Fingerprint, IReadOnlyList<DatabaseSourcePart> Parts);
internal sealed record DatabasePhaseReport(DateTime StartedUtc, DateTime FinishedUtc, string ManifestPath, string Profile,
    int Rows, int Rounds, int WarmupRounds, BaselineEnvironment Environment, string Method,
    IReadOnlyList<DatabaseProfileSource> Sources, bool SourcesUnchanged, IReadOnlyList<DatabasePhaseSample> Samples,
    IReadOnlyList<DatabasePhaseSummary> Summaries, IReadOnlyDictionary<string, Percentiles> PairedCSharpMinusSqliteMs);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DatabasePhaseReport))]
[JsonSerializable(typeof(DatabasePhaseSample))]
internal sealed partial class DatabasePhaseJsonContext : JsonSerializerContext;

internal sealed class DatabasePhaseProfileRunner
{
    private const int WarmupRounds = 6;
    private static readonly string[] SourceSuffixes = ["", "-wal", ".wal", "-shm", ".shm", "-journal"];
    private static readonly JsonSerializerOptions SampleJson = new(DatabasePhaseJsonContext.Default.Options) { WriteIndented = false };
    private static readonly (string Name, Func<DatabasePhaseTimes, double?> Value)[] Phases =
    [
        ("Snapshot", value => value.Snapshot), ("Copy", value => value.Copy), ("SnapshotOther", value => value.SnapshotOther),
        ("EngineOpen", value => value.EngineOpen), ("EngineSetup", value => value.EngineSetup), ("Schema", value => value.Schema),
        ("Validation", value => value.Validation), ("OpenOther", value => value.OpenOther), ("OpenTotal", value => value.OpenTotal),
        ("FirstPage", value => value.FirstPage), ("DeepPage", value => value.DeepPage), ("SequentialDeepPage", value => value.SequentialDeepPage),
        ("OpenAndFirstPage", value => value.OpenAndFirstPage), ("Close", value => value.Close),
    ];

    public async Task<int> RunAsync(BenchmarkPaths paths, string manifestPath, int rounds, CancellationToken token)
    {
        var schedule = BuildSchedule(rounds);
        var manifest = TaskBaselineRunner.ReadManifest(manifestPath);
        var started = DateTime.UtcNow;
        string output = Path.Combine(paths.RootDirectory, "database-phases", $"{started:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Console.WriteLine($"Profiling database phases: {output}");
        var environment = TaskBaselineRunner.CaptureEnvironment(output);
        var sources = Enum.GetValues<DatabaseKind>().Select(engine =>
        {
            string name = engine == DatabaseKind.Sqlite ? "records.sqlite" : "records.csdb";
            return Source(engine, Path.Combine(manifest.ContentRoot, name));
        }).ToArray();
        var samples = new List<DatabasePhaseSample>();
        await using (var checkpoint = new StreamWriter(Path.Combine(output, "database-samples.jsonl")) { AutoFlush = true })
        {
            foreach (var slot in schedule)
            {
                var source = sources.Single(source => source.Engine == slot.Engine);
                var sample = await MeasureAsync(source, slot, manifest.DatabaseRows, token).ConfigureAwait(false);
                samples.Add(sample);
                await checkpoint.WriteLineAsync(JsonSerializer.Serialize(sample, SampleJson)).ConfigureAwait(false);
                if (slot.Position == 2 && (slot.Round < 0 || (slot.Round + 1) % 20 == 0))
                    Console.WriteLine($"  round {slot.Round + 1}: {slot.Engine}; success={sample.Success}");
            }
        }
        bool unchanged = sources.All(before =>
        {
            var after = Source(before.Engine, before.Path);
            return before.Fingerprint == after.Fingerprint && before.Parts.SequenceEqual(after.Parts);
        });
        var report = new DatabasePhaseReport(started, DateTime.UtcNow, Path.GetFullPath(manifestPath), manifest.Profile,
            manifest.DatabaseRows, rounds, WarmupRounds, environment,
            "Actual DatabaseReadSession path, opt-in phase clocks; fresh private main/WAL snapshot for every session. Six warmup pairs retained but excluded from statistics. Measured pairs alternate SQLite/CSharpDB then CSharpDB/SQLite, with equal first/second positions. Same process, warm OS caches primed by hash validation; no cache flush or user/index settings changed. Copy includes output-file creation, reads/writes and disposal; SnapshotOther includes source validation, metadata, directory setup and source-handle disposal. EngineOpen includes the engine's connection/open API and internal initialization; SQLite PRAGMAs are EngineSetup, CSharpDB has none. Schema is FileSearch's schema-discovery work, although CSharpDB may already load its catalog during Open. FirstPage and DeepPage retain their original first/last-page requests. SequentialDeepPage is measured afterward: an untimed 250-row penultimate-page request prepares a boundary, then the next request reads the last page. Setup is excluded from the SequentialDeepPage clock; its row mapping/source checks are included. This phase is not applicable for databases with at most 250 rows and stays null if setup or the measured read fails validation. Setup remains part of cumulative session allocation. Close includes engine and snapshot disposal. Phase and paired-difference percentiles are independent and must not be added. These are file-preview timings, not an intrinsic storage-engine or indexed-search comparison.",
            sources, unchanged, samples, Summarize(samples), PairedDifferences(samples));
        await File.WriteAllTextAsync(Path.Combine(output, "database-phases.json"), JsonSerializer.Serialize(report, DatabasePhaseJsonContext.Default.DatabasePhaseReport), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "database-phases.md"), WriteMarkdown(report), token).ConfigureAwait(false);
        Console.WriteLine($"Wrote {output}");
        return unchanged && samples.All(sample => sample.Success) ? 0 : 5;
    }

    internal static IReadOnlyList<DatabaseProfileSlot> BuildSchedule(int rounds)
    {
        if (rounds is < 10 or > 1000 || rounds % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(rounds), "Use an even number of measured pairs from 10 to 1000.");
        return Enumerable.Range(-WarmupRounds, rounds + WarmupRounds).SelectMany(round =>
        {
            DatabaseKind first = round % 2 == 0 ? DatabaseKind.Sqlite : DatabaseKind.CSharpDb;
            return new[] { new DatabaseProfileSlot(round, 1, first, round < 0),
                new DatabaseProfileSlot(round, 2, first == DatabaseKind.Sqlite ? DatabaseKind.CSharpDb : DatabaseKind.Sqlite, round < 0) };
        }).ToArray();
    }

    private static DatabaseProfileSource Source(DatabaseKind engine, string path) => new(engine, path, DatabaseFiles.Fingerprint(path),
        SourceSuffixes.Select(suffix =>
        {
            string member = path + suffix;
            var info = new FileInfo(member);
            return new DatabaseSourcePart(member, info.Exists, info.Exists ? info.Length : 0,
                info.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(member))) : null);
        }).ToArray());

    internal static async Task<DatabasePhaseSample> MeasureAsync(DatabaseProfileSource source, DatabaseProfileSlot slot, int rows, CancellationToken token)
    {
        var timings = new DatabaseOpenTimings();
        DatabaseReadSession? session = null;
        double? first = null, deep = null, sequentialDeep = null, openAndFirst = null, close = null;
        bool sequentialApplicable = rows > 250;
        bool success = false;
        string? error = null;
        long allocated = GC.GetTotalAllocatedBytes(false);
        var total = Stopwatch.StartNew();
        try
        {
            session = await DatabaseReadSession.OpenAsync(source.Path, source.Fingerprint, timings, token).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var page = await session.ReadPageAsync(TaskCorpus.Table, 1, 250, token).ConfigureAwait(false);
            first = clock.Elapsed.TotalMilliseconds;
            openAndFirst = total.Elapsed.TotalMilliseconds;
            clock.Restart();
            long deepRow = ((rows - 1) / 250) * 250L + 1;
            var last = await session.ReadPageAsync(TaskCorpus.Table, deepRow, 250, token).ConfigureAwait(false);
            deep = clock.Elapsed.TotalMilliseconds;
            success = timings.Completed && session.Kind == source.Engine && IsExpectedPage(page, 1, rows) && IsExpectedPage(last, deepRow, rows);
            if (!success) error = "Unexpected engine, page, or matching record.";
            if (success && sequentialApplicable)
            {
                // Keep the original DeepPage phase above intact. Only this later
                // operation has a deliberately primed adjacent-page boundary.
                var previous = await session.ReadPageAsync(TaskCorpus.Table, deepRow - 250, 250, token).ConfigureAwait(false);
                if (!IsExpectedPage(previous, deepRow - 250, rows))
                    throw new InvalidDataException("Unexpected sequential deep-page setup records.");
                clock.Restart();
                var next = await session.ReadPageAsync(TaskCorpus.Table, deepRow, 250, token).ConfigureAwait(false);
                double elapsed = clock.Elapsed.TotalMilliseconds;
                if (!IsExpectedPage(next, deepRow, rows))
                    throw new InvalidDataException("Unexpected sequential deep-page records.");
                sequentialDeep = elapsed;
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { success = false; error = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            if (session is not null)
            {
                var clock = Stopwatch.StartNew();
                await session.DisposeAsync().ConfigureAwait(false);
                close = clock.Elapsed.TotalMilliseconds;
            }
        }
        double? Ms(long? ticks) => DatabaseOpenTimings.Milliseconds(ticks);
        long? openOther = timings.TotalTicks - timings.SnapshotTicks - timings.EngineOpenTicks - timings.EngineSetupTicks - timings.SchemaTicks - timings.ValidationTicks;
        var phases = new DatabasePhaseTimes(Ms(timings.SnapshotTicks), Ms(timings.CopyTicks), Ms(timings.SnapshotTicks - timings.CopyTicks),
            Ms(timings.EngineOpenTicks), Ms(timings.EngineSetupTicks), Ms(timings.SchemaTicks), Ms(timings.ValidationTicks), Ms(openOther),
            Ms(timings.TotalTicks), first, deep, sequentialDeep, openAndFirst, close);
        return new(slot, success, error, phases, timings.MainBytes, timings.WalBytes,
            GC.GetTotalAllocatedBytes(false) - allocated, Environment.WorkingSet, sequentialApplicable);
    }

    internal static bool IsExpectedPage(DatabasePage page, long firstRow, int totalRows)
    {
        if (firstRow < 1 || firstRow > totalRows) return false;
        int count = (int)Math.Min(250, totalRows - firstRow + 1);
        return page.Rows.Count == count && page.HasMore == (firstRow + count <= totalRows) && !page.ValuesTruncated &&
            page.Rows.Select((row, index) => row.Number == firstRow + index && row.Values.Count == 3 &&
                row.Values[0] == (firstRow + index).ToString(CultureInfo.InvariantCulture) &&
                row.Values[1] == (row.Number == totalRows ? TaskCorpus.DatabaseMarker : "Ordinary record") && row.Values[2] is null).All(value => value);
    }

    internal static IReadOnlyList<DatabasePhaseSummary> Summarize(IEnumerable<DatabasePhaseSample> samples)
    {
        var rows = samples.Where(sample => !sample.Slot.Warmup).ToArray();
        var result = new List<DatabasePhaseSummary>();
        foreach (var engine in Enum.GetValues<DatabaseKind>())
            foreach (int? position in new int?[] { null, 1, 2 })
            {
                var selected = rows.Where(sample => sample.Slot.Engine == engine && (position is null || sample.Slot.Position == position)).ToArray();
                result.Add(new(engine, position, selected.Length, selected.Count(sample => sample.Success),
                    Phases.ToDictionary(phase => phase.Name, phase => Percentiles.From(selected.Select(sample => phase.Value(sample.Ms))))));
            }
        return result;
    }

    internal static IReadOnlyDictionary<string, Percentiles> PairedDifferences(IEnumerable<DatabasePhaseSample> samples)
    {
        var pairs = samples.Where(sample => !sample.Slot.Warmup).GroupBy(sample => sample.Slot.Round).Select(group =>
            (Sqlite: group.SingleOrDefault(sample => sample.Slot.Engine == DatabaseKind.Sqlite),
                CSharp: group.SingleOrDefault(sample => sample.Slot.Engine == DatabaseKind.CSharpDb)))
            .Where(pair => pair.Sqlite?.Success == true && pair.CSharp?.Success == true).ToArray();
        return Phases.ToDictionary(phase => phase.Name,
            phase => Percentiles.From(pairs.Select(pair => phase.Value(pair.CSharp!.Ms) - phase.Value(pair.Sqlite!.Ms))));
    }

    private static string WriteMarkdown(DatabasePhaseReport report)
    {
        var text = new StringBuilder("# Database preview phase profile\n\n");
        text.AppendLine(CultureInfo.InvariantCulture, $"Measured {report.StartedUtc:O}; {report.Rows} rows/database; {report.Rounds} measured pairs plus {report.WarmupRounds} warmup pairs. Build: {report.Environment.BuildConfiguration}; CSharpDB {report.Environment.CSharpDbVersion}; SQLite provider {report.Environment.SqliteVersion}.");
        text.AppendLine(report.Method);
        var sqlite = report.Summaries.Single(row => row.Engine == DatabaseKind.Sqlite && row.Position is null);
        var csharp = report.Summaries.Single(row => row.Engine == DatabaseKind.CSharpDb && row.Position is null);
        text.AppendLine(CultureInfo.InvariantCulture, $"\nSuccessful measured sessions: SQLite {sqlite.Successes}/{sqlite.Samples}; CSharpDB {csharp.Successes}/{csharp.Samples}. Sources/main/WAL/sidecars unchanged: {report.SourcesUnchanged}.");
        if (report.Rows <= 250) text.AppendLine("SequentialDeepPage is not applicable: this database fits in a single 250-row page. Its null samples do not represent measured failures.");
        text.AppendLine("\n| Phase, ms | SQLite P50 / P95 | CSharpDB P50 / P95 | Paired CSharpDB minus SQLite P50 |\n| --- | ---: | ---: | ---: |");
        string Number(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unmeasured";
        foreach (var phase in Phases)
        {
            var left = sqlite.Ms[phase.Name]; var right = csharp.Ms[phase.Name];
            text.AppendLine(CultureInfo.InvariantCulture, $"| {phase.Name} | {Number(left.P50)} / {Number(left.P95)} | {Number(right.P50)} / {Number(right.P95)} | {Number(report.PairedCSharpMinusSqliteMs[phase.Name].P50)} |");
        }
        text.AppendLine("\n| Engine | Pair position | Samples | Open P50 / P95 ms | Open + first page P50 / P95 ms |\n| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in report.Summaries.Where(row => row.Position.HasValue))
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Engine} | {row.Position} | {row.Samples} | {Number(row.Ms["OpenTotal"].P50)} / {Number(row.Ms["OpenTotal"].P95)} | {Number(row.Ms["OpenAndFirstPage"].P50)} / {Number(row.Ms["OpenAndFirstPage"].P95)} |");
        text.AppendLine("\nSnapshot includes Copy; do not add both. OpenTotal includes Snapshot, EngineOpen, EngineSetup, Schema, Validation and OpenOther. OpenAndFirstPage is an independently timed complete operation. Different APIs perform different amounts of catalog/setup work during Open. Component percentiles do not add; paired differences use completed successful pairs only. All failures, warmups, per-position summaries, source hashes/sizes and environment/Git state are retained in JSON. Cumulative allocations are process churn, not peak memory. Full-file indexing/search and rendered preview grids are outside this measurement.");
        return text.ToString();
    }
}
