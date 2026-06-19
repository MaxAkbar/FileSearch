using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FileSearch.Core.Indexing;

namespace FileSearch.Benchmarks;

internal sealed class BenchmarkReportRunner
{
    private readonly BenchmarkCorpusGenerator _corpusGenerator = new();
    private readonly MetadataIndexSeeder _metadataSeeder = new();
    private readonly RelevanceEvaluator _relevanceEvaluator = new();

    public async Task<BenchmarkReport> RunAsync(
        BenchmarkPaths paths,
        bool forceCorpus,
        bool forceIndex,
        CancellationToken cancellationToken)
    {
        var manifest = _corpusGenerator.EnsureCorpus(paths, forceCorpus);
        if (forceIndex)
            DeleteDatabaseFiles(paths);

        await _metadataSeeder.EnsureSeededAsync(paths, manifest, cancellationToken).ConfigureAwait(false);

        using var index = BenchmarkIndexFactory.Create(paths);
        var metrics = new List<BenchmarkMetric>();

        var initialIndex = await TimeInitialContentIndexAsync(index, paths, manifest, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "initial_index_throughput",
            initialIndex.FilesPerSecond,
            "files/second",
            $"Indexed {manifest.PhysicalFileCount:n0} physical files in {initialIndex.Elapsed.TotalSeconds:n2}s."));

        var metadataLatency = await MeasureQueryLatencyAsync(
                index,
                manifest.MetadataRoot,
                "metadata_target_000042",
                paths.Profile.QueryIterations,
                cancellationToken)
            .ConfigureAwait(false);
        AddLatencyMetrics(metrics, "metadata_query", metadataLatency, "Warm metadata filename query.");

        var contentQuery = manifest.Queries.First(query => query.RootKind == "content").Query;
        var contentLatency = await MeasureQueryLatencyAsync(
                index,
                manifest.ContentRoot,
                contentQuery,
                paths.Profile.QueryIterations,
                cancellationToken)
            .ConfigureAwait(false);
        AddLatencyMetrics(metrics, "indexed_content_query", contentLatency, "Warm indexed content query.");

        var firstResultMs = await BenchmarkSearch.TimeToFirstResultAsync(
                index,
                manifest.ContentRoot,
                contentQuery,
                cancellationToken)
            .ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric("time_to_first_result", firstResultMs, "ms", "Warm indexed content query."));

        var incremental = await MeasureIncrementalCatchUpAsync(index, paths, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "incremental_catch_up_throughput",
            incremental.FilesPerSecond,
            "files/second",
            $"Upserted {incremental.FileCount:n0} changed files in {incremental.Elapsed.TotalSeconds:n2}s."));

        var restartCorrectness = await MeasureRestartCorrectnessAsync(paths, cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "crash_restart_correctness",
            restartCorrectness.PercentRecovered,
            "percent",
            $"{restartCorrectness.Recovered:n0}/{restartCorrectness.Expected:n0} stopped-indexer changes were found after restart recovery."));

        var stats = await index.GetDatabaseInfoAsync(cancellationToken).ConfigureAwait(false);
        metrics.Add(new BenchmarkMetric(
            "memory_per_million_files",
            NormalizePerMillion(Environment.WorkingSet, Math.Max(1, stats.TotalFileCount)),
            "bytes/million files",
            "Current benchmark process working set normalized by indexed file count."));
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

        var relevance = await _relevanceEvaluator.EvaluateAsync(index, manifest, cancellationToken).ConfigureAwait(false);
        var report = new BenchmarkReport(
            paths.Profile.Name,
            DateTime.UtcNow,
            metrics,
            relevance,
            manifest.ExternalRoots);

        WriteReports(paths, report);
        return report;
    }

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
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var (milliseconds, _) = await BenchmarkSearch.TimeSearchAsync(index, root, query, cancellationToken)
                .ConfigureAwait(false);
            samples.Add(milliseconds);
        }

        return LatencySummary.From(samples);
    }

    private static async Task<(int FileCount, TimeSpan Elapsed, double FilesPerSecond)> MeasureIncrementalCatchUpAsync(
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
                    string.Create(CultureInfo.InvariantCulture, $"incremental_catch_up_marker {i:D6}"),
                    cancellationToken)
                .ConfigureAwait(false);
            changed.Add(path);
        }

        var stopwatch = Stopwatch.StartNew();
        foreach (var path in changed)
        {
            await index.UpsertFileAsync(paths.ContentRoot, path, BenchmarkIndexFactory.IndexOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        stopwatch.Stop();
        return (
            count,
            stopwatch.Elapsed,
            stopwatch.Elapsed.TotalSeconds <= 0 ? count : count / stopwatch.Elapsed.TotalSeconds);
    }

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
}
