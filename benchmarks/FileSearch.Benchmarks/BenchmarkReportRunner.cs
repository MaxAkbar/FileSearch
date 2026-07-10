using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;

namespace FileSearch.Benchmarks;

internal sealed class BenchmarkReportRunner
{
    /// <summary>Mid-token substring of the corpus token "critical_latency_event".</summary>
    private const string MidTokenSubstringQuery = "ritical_latency";

    /// <summary>Regex matching the same corpus token; required literals can use trigram candidates.</summary>
    private const string RegexQueryPattern = @"critical_latency_\w+";

    /// <summary>Below this indexed-file count, per-million memory normalization is meaningless.</summary>
    private const int MemoryPerMillionFileFloor = 100_000;

    private const int FreshnessSampleCount = 3;
    private static readonly TimeSpan FreshnessTimeout = TimeSpan.FromSeconds(30);

    private readonly BenchmarkCorpusGenerator _corpusGenerator = new();
    private readonly MetadataIndexSeeder _metadataSeeder = new();
    private readonly RelevanceEvaluator _relevanceEvaluator = new();

    public async Task<BenchmarkReport> RunAsync(
        BenchmarkPaths paths,
        bool forceCorpus,
        bool forceIndex,
        CancellationToken cancellationToken)
    {
        LogPhase("Preparing corpus");
        var manifest = _corpusGenerator.EnsureCorpus(paths, forceCorpus);
        if (forceIndex)
            DeleteDatabaseFiles(paths);

        using var index = BenchmarkIndexFactory.Create(paths);
        LogPhase("Seeding metadata index");
        await _metadataSeeder.EnsureSeededAsync(index, paths, manifest, cancellationToken).ConfigureAwait(false);

        var metrics = new List<BenchmarkMetric>();

        LogPhase("Indexing physical content");
        var initialIndex = await TimeInitialContentIndexAsync(index, paths, manifest, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "initial_index_throughput",
            initialIndex.FilesPerSecond,
            "files/second",
            $"Indexed {manifest.PhysicalFileCount:n0} physical files in {initialIndex.Elapsed.TotalSeconds:n2}s."));

        LogPhase("Measuring metadata latency");
        var metadataLatency = await MeasureQueryLatencyWithWarmupAsync(
                index,
                manifest.MetadataRoot,
                "metadata_target_000042",
                paths.Profile.QueryIterations,
                cancellationToken)
            .ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "metadata_query_cold",
            metadataLatency.WarmupMilliseconds,
            "ms",
            $"First metadata filename query before candidate-cache warm-up; {metadataLatency.WarmupHitCount:n0} hits."));
        AddLatencyMetrics(metrics, "metadata_query", metadataLatency.Warm, "Warm metadata filename query.");

        LogPhase("Measuring indexed content latency");
        var contentQuery = manifest.Queries.First(query => query.RootKind == "content").Query;
        var contentLatency = await MeasureQueryLatencyWithWarmupAsync(
                index,
                manifest.ContentRoot,
                contentQuery,
                paths.Profile.QueryIterations,
                cancellationToken)
            .ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "indexed_content_query_cold",
            contentLatency.WarmupMilliseconds,
            "ms",
            $"First indexed content query before candidate-cache warm-up; {contentLatency.WarmupHitCount:n0} hits."));
        AddLatencyMetrics(metrics, "indexed_content_query", contentLatency.Warm, "Warm indexed content query.");

        LogPhase("Measuring time to first indexed result");
        var firstResultMs = await BenchmarkSearch.TimeToFirstResultAsync(
                index,
                manifest.ContentRoot,
                contentQuery,
                cancellationToken)
            .ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric("time_to_first_result", firstResultMs, "ms", "Warm indexed content query."));

        LogPhase("Measuring indexed query phase breakdown");
        metrics.AddRange(await MeasureQueryPhaseBreakdownAsync(
                index,
                manifest.ContentRoot,
                contentQuery,
                Math.Min(paths.Profile.QueryIterations, 20),
                cancellationToken)
            .ConfigureAwait(false));

        LogPhase("Measuring regex and substring latency");
        var scanIterations = ScanIterations(paths.Profile.QueryIterations);
        var regexLatency = await MeasureRequestLatencyAsync(
                index,
                BenchmarkSearch.CreateRegexRequest(manifest.ContentRoot, RegexQueryPattern, BenchmarkIndexFactory.IndexOptions),
                scanIterations,
                cancellationToken)
            .ConfigureAwait(false);
        AddLatencyMetrics(
            metrics,
            "indexed_regex_query",
            regexLatency,
            "Warm indexed regex query using required-literal trigram candidates when available; patterns without required literals fall back to a lines-table scan.");

        var substringLatency = await MeasureRequestLatencyAsync(
                index,
                BenchmarkSearch.CreateRequest(manifest.ContentRoot, MidTokenSubstringQuery, BenchmarkIndexFactory.IndexOptions),
                scanIterations,
                cancellationToken)
            .ConfigureAwait(false);
        AddLatencyMetrics(
            metrics,
            "indexed_substring_query",
            substringLatency,
            "Warm indexed mid-token substring query (substring of critical_latency_event).");

        LogPhase("Measuring substring parity");
        var liveSearcher = BenchmarkIndexFactory.CreateLiveSearcher();
        var parity = await MeasureSubstringParityAsync(index, liveSearcher, manifest.ContentRoot, cancellationToken)
            .ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "substring_index_live_parity",
            parity.ParityPercent,
            "percent",
            $"Distinct files with mid-token substring hits: indexed={parity.IndexedFiles:n0}, live={parity.LiveFiles:n0}. Below 100 means the indexed path misses substring matches the live scanner finds."));

        LogPhase("Measuring live scan");
        metrics.AddRange(await MeasureLiveScanAsync(
                liveSearcher,
                paths,
                manifest,
                contentQuery,
                cancellationToken)
            .ConfigureAwait(false));

        LogPhase("Measuring incremental updates and deletes");
        var incremental = await MeasureIncrementalCatchUpAsync(index, paths, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "incremental_catch_up_throughput",
            incremental.UpdateFilesPerSecond,
            "files/second",
            $"Re-indexed {incremental.FileCount:n0} existing changed files in {incremental.UpdateElapsed.TotalSeconds:n2}s."));
        metrics.Add(new BenchmarkMetric(
            "incremental_delete_throughput",
            incremental.DeleteFilesPerSecond,
            "files/second",
            $"Removed {incremental.FileCount:n0} indexed files and their owned rows in {incremental.DeleteElapsed.TotalSeconds:n2}s."));

        LogPhase("Measuring restart recovery correctness");
        var restartCorrectness = await MeasureRestartCorrectnessAsync(paths, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "crash_restart_correctness",
            restartCorrectness.PercentRecovered,
            "percent",
            $"{restartCorrectness.Recovered:n0}/{restartCorrectness.Expected:n0} stopped-indexer changes were found after restart recovery."));

        LogPhase("Measuring watcher freshness");
        var freshness = await MeasureFreshnessAsync(index, paths, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "index_freshness_after_event",
            freshness.MedianMs,
            "ms",
            $"Median of {FreshnessSampleCount} watcher events (root-level file write to first indexed hit via the per-file upsert path, including watcher debounce and queue dispatch; {freshness.TimedOut} timed out at {FreshnessTimeout.TotalSeconds:n0}s)."));

        LogPhase("Collecting database stats");
        var stats = await index.GetDatabaseInfoAsync(cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "benchmark_process_working_set",
            Environment.WorkingSet,
            "bytes",
            "Whole benchmark process (corpus generation + harness + index); not index-attributable. memory_per_million_files is only reported at 100k+ indexed files."));
        if (stats.TotalFileCount >= MemoryPerMillionFileFloor)
        {
            metrics.Add(new BenchmarkMetric(
                "memory_per_million_files",
                NormalizePerMillion(Environment.WorkingSet, stats.TotalFileCount),
                "bytes/million files",
                "Benchmark process working set normalized by indexed file count."));
        }

        metrics.Add(new BenchmarkMetric(
            "index_disk_size",
            stats.TotalBytes,
            "bytes",
            "CSharpDB main file plus WAL/SHM sidecars."));
        metrics.Add(new BenchmarkMetric(
            "extraction_success_rate",
            CalculateExtractionSuccessRate(stats.FailedFileCount, manifest.PhysicalFileCount),
            "percent",
            $"{stats.FailedFileCount:n0} failed/issue rows reported by extraction diagnostics."));

        LogPhase("Measuring relevance");
        var relevance = await _relevanceEvaluator.EvaluateAsync(index, manifest, cancellationToken).ConfigureAwait(false);
        var report = new BenchmarkReport(
            paths.Profile.Name,
            DateTime.UtcNow,
            metrics,
            relevance,
            manifest.ExternalRoots);

        LogPhase("Writing benchmark report");
        WriteReports(paths, report);
        LogPhase("Benchmark report complete");
        return report;
    }

    private static void LogPhase(string message) =>
        Console.Error.WriteLine($"{DateTime.UtcNow:O} {message}");

    private static async Task<(TimeSpan Elapsed, double FilesPerSecond)> TimeInitialContentIndexAsync(
        CSharpDbFileIndex index,
        BenchmarkPaths paths,
        CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await index.BuildOrRefreshAsync(
                new IndexRequest(paths.ContentRoot, BenchmarkIndexFactory.IndexOptions),
                cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        var filesPerSecond = stopwatch.Elapsed.TotalSeconds <= 0
            ? manifest.PhysicalFileCount
            : manifest.PhysicalFileCount / stopwatch.Elapsed.TotalSeconds;
        return (stopwatch.Elapsed, filesPerSecond);
    }

    private static async Task<LatencySummary> MeasureQueryLatencyAsync(
        CSharpDbFileIndex index,
        string root,
        string query,
        int iterations,
        CancellationToken cancellationToken)
    {
        var measured = await MeasureQueryLatencyWithWarmupAsync(
                index,
                root,
                query,
                iterations,
                cancellationToken)
            .ConfigureAwait(false);
        return measured.Warm;
    }

    private static async Task<QueryLatencyMeasurement> MeasureQueryLatencyWithWarmupAsync(
        CSharpDbFileIndex index,
        string root,
        string query,
        int iterations,
        CancellationToken cancellationToken)
    {
        var (warmupMilliseconds, warmupHitCount) = await BenchmarkSearch.TimeSearchAsync(index, root, query, cancellationToken)
            .ConfigureAwait(false);

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var (milliseconds, _) = await BenchmarkSearch.TimeSearchAsync(index, root, query, cancellationToken)
                .ConfigureAwait(false);
            samples.Add(milliseconds);
        }

        return new QueryLatencyMeasurement(warmupMilliseconds, warmupHitCount, LatencySummary.From(samples));
    }

    private static async Task<IncrementalMutationMeasurement> MeasureIncrementalCatchUpAsync(
        CSharpDbFileIndex index,
        BenchmarkPaths paths,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(paths.ContentRoot, "incremental");
        Directory.CreateDirectory(folder);
        var count = Math.Max(1, Math.Min(paths.Profile.StoppedIndexerChangeCount, paths.Profile.SmallTextFileCount));
        var changed = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, $"incremental_catch_up_{i:D6}.txt");
            await File.WriteAllTextAsync(
                    path,
                    string.Create(CultureInfo.InvariantCulture, $"incremental_original_marker {i:D6}"),
                    cancellationToken)
                .ConfigureAwait(false);
            await index.UpsertFileAsync(paths.ContentRoot, path, BenchmarkIndexFactory.IndexOptions, cancellationToken)
                .ConfigureAwait(false);
            changed.Add(path);
        }

        try
        {
            for (var i = 0; i < changed.Count; i++)
            {
                await File.WriteAllTextAsync(
                        changed[i],
                        string.Create(CultureInfo.InvariantCulture, $"incremental_updated_marker with changed content {i:D6}"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var updateStopwatch = Stopwatch.StartNew();
            foreach (var path in changed)
            {
                await index.UpsertFileAsync(paths.ContentRoot, path, BenchmarkIndexFactory.IndexOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            updateStopwatch.Stop();
            foreach (var path in changed)
                File.Delete(path);

            var deleteStopwatch = Stopwatch.StartNew();
            foreach (var path in changed)
                await index.DeleteFileAsync(paths.ContentRoot, path, cancellationToken).ConfigureAwait(false);

            deleteStopwatch.Stop();
            return new IncrementalMutationMeasurement(
                count,
                updateStopwatch.Elapsed,
                Rate(count, updateStopwatch.Elapsed),
                deleteStopwatch.Elapsed,
                Rate(count, deleteStopwatch.Elapsed));
        }
        finally
        {
            foreach (var path in changed)
                DeleteIfExists(path);
        }
    }

    private static double Rate(int count, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? count : count / elapsed.TotalSeconds;

    private static async Task<(int Expected, int Recovered, double PercentRecovered)> MeasureRestartCorrectnessAsync(
        BenchmarkPaths paths,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(paths.ContentRoot, "stopped-indexer-changes");
        Directory.CreateDirectory(folder);
        var expected = Math.Max(1, paths.Profile.StoppedIndexerChangeCount);

        for (var i = 0; i < expected; i++)
        {
            await File.WriteAllTextAsync(
                    Path.Combine(folder, $"stopped_indexer_change_{i:D6}.txt"),
                    string.Create(CultureInfo.InvariantCulture, $"stopped_indexer_marker {i:D6}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        using var restarted = BenchmarkIndexFactory.Create(paths);
        await restarted.BuildOrRefreshAsync(
                new IndexRequest(paths.ContentRoot, BenchmarkIndexFactory.IndexOptions),
                cancellationToken)
            .ConfigureAwait(false);

        var hits = await BenchmarkSearch.SearchAllAsync(
                restarted,
                paths.ContentRoot,
                "stopped_indexer_marker",
                cancellationToken)
            .ConfigureAwait(false);
        var recovered = hits.Select(static hit => hit.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var percent = expected == 0 ? 100 : Math.Min(100, recovered * 100d / expected);
        return (expected, recovered, percent);
    }

    /// <summary>
    /// Fewer iterations for measurements that scan the whole corpus (regex,
    /// substring fallback, live scan) so large profiles stay tractable.
    /// </summary>
    private static int ScanIterations(int queryIterations) =>
        Math.Clamp(queryIterations / 10, 3, 10);

    private static async Task<LatencySummary> MeasureRequestLatencyAsync(
        CSharpDbFileIndex index,
        SearchRequest request,
        int iterations,
        CancellationToken cancellationToken)
    {
        _ = await BenchmarkSearch.TimeAsync(() => index.SearchAsync(request, cancellationToken))
            .ConfigureAwait(false);

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var (milliseconds, _) = await BenchmarkSearch.TimeAsync(() => index.SearchAsync(request, cancellationToken))
                .ConfigureAwait(false);
            samples.Add(milliseconds);
        }

        return LatencySummary.From(samples);
    }

    private static async Task<List<BenchmarkMetric>> MeasureQueryPhaseBreakdownAsync(
        CSharpDbFileIndex index,
        string root,
        string query,
        int iterations,
        CancellationToken cancellationToken)
    {
        var collected = new List<IndexSearchTimings>(iterations);
        index.SearchTimingsCallback = collected.Add;
        try
        {
            for (var i = 0; i < iterations; i++)
            {
                _ = await BenchmarkSearch.TimeSearchAsync(index, root, query, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            index.SearchTimingsCallback = null;
        }

        var metrics = new List<BenchmarkMetric>();
        if (collected.Count == 0)
            return metrics;

        var notes = string.Create(
            CultureInfo.InvariantCulture,
            $"Average per query over {collected.Count} instrumented warm indexed content queries.");
        metrics.Add(PhaseMetric("indexed_query_phase_open_avg", collected, static t => t.OpenTicks, notes));
        metrics.Add(PhaseMetric("indexed_query_phase_root_resolve_avg", collected, static t => t.RootResolveTicks, notes));
        metrics.Add(PhaseMetric("indexed_query_phase_metadata_avg", collected, static t => t.MetadataTicks, notes));
        metrics.Add(PhaseMetric("indexed_query_phase_trigram_lookup_avg", collected, static t => t.TrigramLookupTicks, notes));
        metrics.Add(PhaseMetric("indexed_query_phase_line_fetch_avg", collected, static t => t.LineFetchTicks, notes));
        metrics.Add(PhaseMetric("indexed_query_phase_recheck_avg", collected, static t => t.RecheckTicks, notes));
        metrics.Add(PhaseMetric(
            "indexed_query_phase_other_avg",
            collected,
            static t => t.TotalTicks - (t.OpenTicks + t.RootResolveTicks + t.MetadataTicks + t.TrigramLookupTicks + t.LineFetchTicks + t.RecheckTicks),
            notes + " Remainder not covered by the named phases (candidate iteration, streaming plumbing)."));
        metrics.Add(new BenchmarkMetric(
            "indexed_query_lines_examined_avg",
            collected.Average(static t => t.LinesExamined),
            "lines/query",
            "Average candidate line rows fetched and rechecked per instrumented query."));
        return metrics;
    }

    private static BenchmarkMetric PhaseMetric(
        string name,
        List<IndexSearchTimings> collected,
        Func<IndexSearchTimings, long> selector,
        string notes) =>
        new(name, collected.Average(t => IndexSearchTimings.ToMilliseconds(selector(t))), "ms", notes);

    private static async Task<(double ParityPercent, int IndexedFiles, int LiveFiles)> MeasureSubstringParityAsync(
        CSharpDbFileIndex index,
        Searcher liveSearcher,
        string root,
        CancellationToken cancellationToken)
    {
        var indexedRequest = BenchmarkSearch.CreateRequest(root, MidTokenSubstringQuery, BenchmarkIndexFactory.IndexOptions);
        var liveRequest = BenchmarkSearch.CreateRequest(root, MidTokenSubstringQuery, BenchmarkIndexFactory.IndexOptions, useIndex: false);
        var indexedFiles = await BenchmarkSearch.CountDistinctPathsAsync(index.SearchAsync(indexedRequest, cancellationToken))
            .ConfigureAwait(false);
        var liveFiles = await BenchmarkSearch.CountDistinctPathsAsync(liveSearcher.SearchAsync(liveRequest, cancellationToken))
            .ConfigureAwait(false);
        var parity = liveFiles == 0 ? 100 : Math.Min(100, indexedFiles * 100d / liveFiles);
        return (parity, indexedFiles, liveFiles);
    }

    private static async Task<List<BenchmarkMetric>> MeasureLiveScanAsync(
        Searcher liveSearcher,
        BenchmarkPaths paths,
        CorpusManifest manifest,
        string contentQuery,
        CancellationToken cancellationToken)
    {
        var corpusBytes = new DirectoryInfo(paths.ContentRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(static file => file.Length);

        var iterations = ScanIterations(paths.Profile.QueryIterations);
        var samples = new List<double>(iterations);
        var request = BenchmarkSearch.CreateRequest(paths.ContentRoot, contentQuery, BenchmarkIndexFactory.IndexOptions, useIndex: false);
        for (var i = 0; i < iterations; i++)
        {
            var (milliseconds, _) = await BenchmarkSearch.TimeAsync(() => liveSearcher.SearchAsync(request, cancellationToken))
                .ConfigureAwait(false);
            samples.Add(milliseconds);
        }

        var summary = LatencySummary.From(samples);
        var notes = string.Create(
            CultureInfo.InvariantCulture,
            $"Warm live scan (no index) over {manifest.PhysicalFileCount:n0} physical files; OS file cache warm.");
        var throughput = summary.P50Milliseconds <= 0
            ? 0
            : corpusBytes / (1024d * 1024d) / (summary.P50Milliseconds / 1000d);

        var firstResultMs = await BenchmarkSearch.TimeToFirstAsync(() => liveSearcher.SearchAsync(request, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            new BenchmarkMetric("live_content_scan_p50", summary.P50Milliseconds, "ms", notes),
            new BenchmarkMetric("live_content_scan_p95", summary.P95Milliseconds, "ms", notes),
            new BenchmarkMetric(
                "live_scan_throughput",
                throughput,
                "MB/second",
                string.Create(CultureInfo.InvariantCulture, $"Corpus bytes ({corpusBytes:n0}) divided by median warm live scan time.")),
            new BenchmarkMetric("live_time_to_first_result", firstResultMs, "ms", "Warm live content scan."),
        ];
    }

    private static async Task<(double MedianMs, int TimedOut)> MeasureFreshnessAsync(
        CSharpDbFileIndex index,
        BenchmarkPaths paths,
        CancellationToken cancellationToken)
    {
        var runId = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
        using var queue = new IndexQueue(index);
        var watchers = new IndexWatcherService(queue);
        var service = new IndexingService(index, queue, watchers);

        // Start with no registered locations (StartAsync would enqueue a
        // startup root refresh) and attach the watcher directly: this measures
        // steady-state freshness — watcher active, index already current.
        await service.StartAsync(Array.Empty<IndexedLocation>(), cancellationToken).ConfigureAwait(false);
        watchers.StartWatching(new IndexedLocation(paths.ContentRoot, BenchmarkIndexFactory.IndexOptions, WatchEnabled: true));

        var samples = new List<double>(FreshnessSampleCount);
        var timedOut = 0;
        try
        {
            for (var i = 0; i < FreshnessSampleCount; i++)
            {
                var marker = string.Create(CultureInfo.InvariantCulture, $"freshness_probe_{runId}_{i:D2}");

                // Root-level file on purpose: this keeps the benchmark on
                // the per-file upsert path and avoids measuring directory
                // subtree refresh behavior.
                var filePath = Path.Combine(
                    paths.ContentRoot,
                    string.Create(CultureInfo.InvariantCulture, $"freshness_{runId}_{i:D2}.txt"));
                var stopwatch = Stopwatch.StartNew();
                await File.WriteAllTextAsync(filePath, marker + " freshness fixture", cancellationToken).ConfigureAwait(false);

                // Wait for the pipeline to drain using in-memory state only.
                // Opening a read handle during the write session makes the
                // writer's checkpoint fail and CSharpDB silently discards the
                // session, so polling the database here would both distort
                // the number and suppress the very write being measured.
                var sawWork = false;
                var drained = false;
                while (stopwatch.Elapsed < FreshnessTimeout)
                {
                    var busy = queue.Count > 0 || service.CurrentStatus.IsProcessing;
                    if (busy)
                    {
                        sawWork = true;
                    }
                    else if (sawWork)
                    {
                        drained = true;
                        break;
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }

                var found = false;
                if (drained)
                {
                    for (var attempt = 0; attempt < 3 && !found; attempt++)
                    {
                        var hits = await BenchmarkSearch.SearchAllAsync(index, paths.ContentRoot, marker, cancellationToken)
                            .ConfigureAwait(false);
                        found = hits.Count > 0;
                        if (!found)
                            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    }
                }

                stopwatch.Stop();
                if (!found)
                    timedOut++;

                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            watchers.StopAll();
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        samples.Sort();
        return (samples[samples.Count / 2], timedOut);
    }

    private static void AddLatencyMetrics(
        List<BenchmarkMetric> metrics,
        string name,
        LatencySummary summary,
        string notes)
    {
        metrics.Add(new BenchmarkMetric($"{name}_p50", summary.P50Milliseconds, "ms", notes));
        metrics.Add(new BenchmarkMetric($"{name}_p95", summary.P95Milliseconds, "ms", notes));
        metrics.Add(new BenchmarkMetric($"{name}_p99", summary.P99Milliseconds, "ms", notes));
    }

    private static double NormalizePerMillion(long bytes, long fileCount) =>
        bytes * 1_000_000d / fileCount;

    private static double CalculateExtractionSuccessRate(long failedRows, long physicalFiles)
    {
        if (physicalFiles <= 0)
            return 100;

        return Math.Max(0, 100d - failedRows * 100d / physicalFiles);
    }

    private static void DeleteDatabaseFiles(BenchmarkPaths paths)
    {
        DeleteIfExists(paths.DatabasePath);
        DeleteIfExists(paths.DatabasePath + ".wal");
        DeleteIfExists(paths.DatabasePath + ".shm");
        DeleteIfExists(paths.DatabasePath + ".lock");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void WriteReports(BenchmarkPaths paths, BenchmarkReport report)
    {
        Directory.CreateDirectory(paths.ReportsDirectory);
        File.WriteAllText(
            Path.Combine(paths.ReportsDirectory, "benchmark-report.json"),
            JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.BenchmarkReport));
        File.WriteAllText(
            Path.Combine(paths.ReportsDirectory, "benchmark-report.md"),
            BenchmarkMarkdownWriter.Write(report));
    }

    private sealed record QueryLatencyMeasurement(
        double WarmupMilliseconds,
        int WarmupHitCount,
        LatencySummary Warm);

    private sealed record IncrementalMutationMeasurement(
        int FileCount,
        TimeSpan UpdateElapsed,
        double UpdateFilesPerSecond,
        TimeSpan DeleteElapsed,
        double DeleteFilesPerSecond);
}
