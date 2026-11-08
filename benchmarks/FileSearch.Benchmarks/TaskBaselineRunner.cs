using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Benchmarks;

internal sealed class TaskBaselineRunner
{
    private static readonly JsonSerializerOptions SampleJson = new() { TypeInfoResolver = TaskBaselineJsonContext.Default };

    public async Task<int> RunAsync(BenchmarkPaths paths, int repeats, CancellationToken token, string? resumeManifest = null)
    {
        if (repeats is < 3 or > 1_000) throw new ArgumentOutOfRangeException(nameof(repeats), "Use 3 to 1000 measured repetitions.");
        var started = DateTime.UtcNow;
        // Each run owns a new child directory. Never overwrite/delete an existing corpus or user index.
        string run = Path.Combine(paths.RootDirectory, "tasks", $"{started:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var environment = CaptureEnvironment(run);
        Console.WriteLine($"Preparing task baseline: {run}");
        var manifest = resumeManifest is null
            ? await TaskCorpus.CreateAsync(Path.Combine(run, "content"), paths.Profile, token).ConfigureAwait(false)
            : ReadManifest(resumeManifest);
        string manifestPath = resumeManifest is null ? Path.Combine(run, "task-manifest.json") : Path.GetFullPath(resumeManifest);
        var runPaths = new BenchmarkPaths(Path.GetDirectoryName(manifestPath)!, BenchmarkProfile.Resolve(manifest.Profile));
        var clock = Stopwatch.StartNew();
        double? indexMs = null;
        if (resumeManifest is null)
        {
            Console.WriteLine($"Indexing {manifest.Files.Count} physical files; {manifest.DatabaseRows} rows per source database.");
            using var buildIndex = BenchmarkIndexFactory.Create(runPaths);
            clock.Restart();
            await buildIndex.BuildOrRefreshAsync(new IndexRequest(manifest.ContentRoot, BenchmarkIndexFactory.IndexOptions), token).ConfigureAwait(false);
            indexMs = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Initial index completed in {indexMs:0.00} ms."));
        }
        else if (!File.Exists(runPaths.DatabasePath)) throw new FileNotFoundException("The generated task index is missing.", runPaths.DatabasePath);
        var samples = new List<TaskSample>();
        var workers = new List<TaskWorker>();
        foreach (string route in new[] { "indexed", "live" })
        {
            foreach (var task in manifest.Tasks)
            {
                Console.WriteLine($"{route}: {task.Id}");
                var worker = await RunIsolatedAsync(run, manifestPath, task, route, repeats, samples, token).ConfigureAwait(false);
                workers.Add(worker);
                Console.WriteLine($"  {worker.CompletedSamples}/{worker.RequestedSamples} samples; success={worker.Success}; timeout={worker.TimedOut}");
            }
        }
        var databaseSamples = await MeasureDatabasesAsync(manifest, Math.Min(repeats, 20), token).ConfigureAwait(false);
        using var index = BenchmarkIndexFactory.Create(runPaths);
        var live = BenchmarkIndexFactory.CreateLiveSearcher();
        var cancellation = new List<CancellationSample>();
        foreach (string route in new[] { "indexed", "live" })
            cancellation.Add(await MeasureCancellationAsync(index, live, manifest.ContentRoot, route, token).ConfigureAwait(false));

        Console.WriteLine("Measuring explicit incremental refresh (watcher latency is a separate existing benchmark).");
        string freshnessFile = Path.Combine(manifest.ContentRoot, "freshness.txt");
        clock.Restart();
        await File.WriteAllTextAsync(freshnessFile, "freshness-5587b29a", token).ConfigureAwait(false);
        await index.UpsertFileAsync(manifest.ContentRoot, freshnessFile, BenchmarkIndexFactory.IndexOptions, token).ConfigureAwait(false);
        var fresh = await MeasureSearchAsync(index, live, manifest.ContentRoot, new("freshness", "freshness-5587b29a", "freshness.txt"), "indexed", 1, token).ConfigureAwait(false);
        double freshnessMs = clock.Elapsed.TotalMilliseconds;
        var info = await index.GetDatabaseInfoAsync(token).ConfigureAwait(false);
        bool unchanged = manifest.Files.All(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(manifest.ContentRoot, file.Path)))) == file.Sha256);
        using var process = Process.GetCurrentProcess();
        var report = new TaskBaselineReport(started, DateTime.UtcNow, environment,
            "Sequential isolated worker per route/task; first invocation is a fresh worker with warm OS caches, then same-worker warm repetitions. Each worker has a 60-second hard budget including startup and repetitions; timed-out workers are terminated and missing samples are explicit failures, never zero latency. Warm percentiles use nearest rank, include measured failures, and exclude iteration zero. Allocation/working-set samples cover the worker process; PeakWorkingSetBytes is the coordinator only, not an aggregate. IncludeHidden=true, no excluded directories, no max file size, MaxHitsPerFile=5; query mode/target in manifest. No model, OCR, PowerToys, watcher, or user settings are loaded. " + (indexMs.HasValue ? "New private index." : "Resumed generated index; initial index time is unmeasured."),
            manifestPath, indexMs, info.TotalBytes, info.TotalFileCount, info.FailedFileCount,
            freshnessMs, fresh.Success, unchanged, process.PeakWorkingSet64, samples, Summarize(samples, workers), workers, databaseSamples, cancellation,
            ["Installed competitors and end-to-end human tasks", "Held-out human relevance judgments and semantic/model retrieval", "Scan/OCR retrieval (optional Windows OCR qualification required)", "Hybrid provider-fusion and main/Quick end-to-end delivery; preview rendering: run the separate WPF probes", "OS-cold/cache-flushed measurements, network/removable roots, watcher freshness", "Separate database copy/schema phases (open timing combines both)"]);
        await File.WriteAllTextAsync(Path.Combine(run, "task-baseline.json"), JsonSerializer.Serialize(report, TaskBaselineJsonContext.Default.TaskBaselineReport), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(run, "task-baseline.md"), WriteMarkdown(report), token).ConfigureAwait(false);
        Console.WriteLine($"Wrote {run}");
        return workers.All(worker => worker.Success) && samples.All(sample => sample.Success) && databaseSamples.All(sample => sample.Success) &&
            cancellation.All(sample => sample.Observed) && fresh.Success && unchanged && info.FailedFileCount == 0 ? 0 : 5;
    }

    internal static TaskManifest ReadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), TaskBaselineJsonContext.Default.TaskManifest)
            ?? throw new InvalidDataException("Missing generated task manifest.");
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "content"));
        if (manifest.Version != 1 || Path.GetFullPath(manifest.ContentRoot) != root || !manifest.Provenance.StartsWith("Generated controlled fixtures,", StringComparison.Ordinal))
            throw new InvalidDataException("Resume requires an unchanged generated task corpus in its original run directory.");
        foreach (var file in manifest.Files)
        {
            string full = Path.GetFullPath(Path.Combine(root, file.Path));
            if (Path.IsPathRooted(file.Path) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) != file.Sha256)
                throw new InvalidDataException("The generated corpus path or hash changed: " + file.Path);
        }
        return manifest;
    }

    public static async Task<int> RunWorkerAsync(string manifestPath, string route, string taskId, int repeats, string output, CancellationToken token)
    {
        // Coordinator validated every corpus hash once; workers read the same frozen manifest.
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), TaskBaselineJsonContext.Default.TaskManifest)!;
        var task = manifest.Tasks.Single(task => task.Id == taskId);
        var paths = new BenchmarkPaths(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, BenchmarkProfile.Resolve(manifest.Profile));
        using var index = BenchmarkIndexFactory.Create(paths);
        var live = BenchmarkIndexFactory.CreateLiveSearcher();
        await using var checkpoint = new StreamWriter(output) { AutoFlush = true };
        bool passed = true;
        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            var sample = await MeasureSearchAsync(index, live, manifest.ContentRoot, task, route, iteration, token).ConfigureAwait(false);
            await checkpoint.WriteLineAsync(JsonSerializer.Serialize(sample, SampleJson)).ConfigureAwait(false);
            passed &= sample.Success;
        }
        return passed ? 0 : 5;
    }

    private static async Task<TaskWorker> RunIsolatedAsync(string run, string manifestPath, BaselineTask task, string route,
        int repeats, List<TaskSample> samples, CancellationToken token)
    {
        string output = Path.Combine(run, $"{route}-{task.Id}.jsonl");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { typeof(TaskBaselineRunner).Assembly.Location, "task-worker", manifestPath, route, task.Id, repeats.ToString(CultureInfo.InvariantCulture), output }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(token);
        var stderr = child.StandardError.ReadToEndAsync(token);
        bool timedOut = false;
        try
        {
            try { await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false); }
            catch (TimeoutException) { timedOut = true; child.Kill(entireProcessTree: true); await child.WaitForExitAsync(token).ConfigureAwait(false); }
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        await File.WriteAllTextAsync(output + ".stdout.txt", await stdout.ConfigureAwait(false), token).ConfigureAwait(false);
        string errors = await stderr.ConfigureAwait(false);
        await File.WriteAllTextAsync(output + ".stderr.txt", errors, token).ConfigureAwait(false);
        var completed = new List<TaskSample>();
        if (File.Exists(output))
        {
            foreach (string line in File.ReadLines(output))
            {
                try { var sample = JsonSerializer.Deserialize(line, TaskBaselineJsonContext.Default.TaskSample); if (sample is not null) completed.Add(sample); }
                catch (JsonException) { errors += " Incomplete checkpoint line."; }
            }
        }
        samples.AddRange(completed);
        bool success = !timedOut && child.ExitCode == 0 && completed.Count == repeats + 1 && completed.All(sample => sample.Success);
        return new(task.Id, route, repeats + 1, completed.Count, success, timedOut,
            timedOut ? "Worker exceeded the 60-second total budget; missing samples are unmeasured." : success ? null : $"Worker exit {child.ExitCode}; {errors}");
    }

    internal static bool IsUseful(Hit hit, BaselineTask task, string root)
    {
        if (task.ExpectedFile is null || !string.Equals(hit.Path, Path.Combine(root, task.ExpectedFile), StringComparison.OrdinalIgnoreCase)) return false;
        if (task.Page is { } page && (hit.Anchor?.Page ?? hit.Locator?.Page) != page) return false;
        if (task.Member is { } member && (hit.Anchor?.MemberPath ?? hit.Locator?.ArchiveMember) != member) return false;
        if (task.DatabaseRow is { } row && (hit.Database?.Table != TaskCorpus.Table || hit.Database.RowNumber != row || hit.Database.Column != task.DatabaseColumn)) return false;
        return true;
    }

    private static SearchRequest Request(string root, BaselineTask task, string route, Action<string>? status = null) =>
        new(new QueryFactory().Build(task.Query, task.Mode, false), [root], BenchmarkIndexFactory.IndexOptions,
            UseIndex: route == "indexed", Status: status, RawQuery: task.Query, Mode: task.Mode, SearchTarget: task.Target);

    private static async Task<TaskSample> MeasureSearchAsync(CSharpDbFileIndex index, Searcher live, string root,
        BaselineTask task, string route, int iteration, CancellationToken token)
    {
        var status = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var request = Request(root, task, route, message => { lock (status) status.Add(message); });
        long allocated = GC.GetTotalAllocatedBytes(false);
        var clock = Stopwatch.StartNew();
        int count = 0;
        double? first = null, useful = null;
        string? error = null;
        try
        {
            var hits = route == "indexed" ? index.SearchAsync(request, deadline.Token) : live.SearchAsync(request, deadline.Token);
            await foreach (var hit in hits.ConfigureAwait(false))
            {
                count++;
                first ??= clock.Elapsed.TotalMilliseconds;
                if (IsUseful(hit, task, root)) useful ??= clock.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { error = ex.GetType().Name + ": " + ex.Message; }
        double elapsed = clock.Elapsed.TotalMilliseconds;
        if (error is null && (task.ExpectedFile is null ? count != 0 : !useful.HasValue))
            error = task.ExpectedFile is null ? "Expected no results." : "The expected source location was not returned; see the task manifest.";
        return new(task.Id, route, iteration, iteration == 0 ? "first invocation" : "warm",
            error is null && (task.ExpectedFile is null ? count == 0 : useful.HasValue), count, first, useful,
            elapsed, GC.GetTotalAllocatedBytes(false) - allocated, Environment.WorkingSet, error, status);
    }

    internal static IReadOnlyList<TaskSummary> Summarize(IEnumerable<TaskSample> samples, IReadOnlyList<TaskWorker>? workers = null) =>
        samples.Where(sample => sample.Iteration > 0).GroupBy(sample => (sample.Task, sample.Route)).Select(group =>
        {
            var completion = Percentiles.From(group.Select(sample => (double?)sample.CompletionMs));
            double? goal = group.Key.Route == "indexed" ? group.Key.Task == "known-file" ? 50 : 150 : null;
            return new TaskSummary(group.Key.Task, group.Key.Route, group.Count(), group.Count(sample => sample.Success),
                Percentiles.From(group.Select(sample => sample.Success ? sample.FirstUsefulMs : null)), completion, goal,
                goal.HasValue ? group.All(sample => sample.Success) && completion.P95 < goal &&
                    (workers?.Where(worker => worker.Task == group.Key.Task && worker.Route == group.Key.Route).All(worker => worker.Success) ?? true) : null);
        }).ToArray();

    private static async Task<IReadOnlyList<DatabaseSample>> MeasureDatabasesAsync(TaskManifest manifest, int repeats, CancellationToken token)
    {
        var samples = new List<DatabaseSample>();
        foreach (var task in manifest.Tasks.Where(task => task.DatabaseRow.HasValue))
        {
            for (int iteration = 0; iteration <= repeats; iteration++)
            {
                double open = 0, first = 0, deep = 0;
                bool success = false;
                string? error = null;
                long deepRow = ((manifest.DatabaseRows - 1) / 250) * 250L + 1;
                try
                {
                    var clock = Stopwatch.StartNew();
                    await using var session = await DatabaseReadSession.OpenAsync(Path.Combine(manifest.ContentRoot, task.ExpectedFile!), null, token).ConfigureAwait(false);
                    open = clock.Elapsed.TotalMilliseconds;
                    clock.Restart();
                    var page = await session.ReadPageAsync(TaskCorpus.Table, 1, 250, token).ConfigureAwait(false);
                    first = clock.Elapsed.TotalMilliseconds;
                    clock.Restart();
                    var final = await session.ReadPageAsync(TaskCorpus.Table, deepRow, 250, token).ConfigureAwait(false);
                    deep = clock.Elapsed.TotalMilliseconds;
                    success = page.Rows.Count == 250 && final.Rows[^1].Number == manifest.DatabaseRows && final.Rows[^1].Values[1] == TaskCorpus.DatabaseMarker;
                }
                catch (Exception ex) when (!token.IsCancellationRequested) { error = ex.GetType().Name + ": " + ex.Message; }
                samples.Add(new(task.Id, iteration, open, first, deep, open + first, deepRow, success, error));
            }
        }
        return samples;
    }

    private static async Task<CancellationSample> MeasureCancellationAsync(CSharpDbFileIndex index, Searcher live, string root, string route, CancellationToken token)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        var request = Request(root, new("cancellation", "Routine", null), route);
        // Cancel after the first streamed result, so this checks disposal of active work rather than an already-cancelled call.
        var stream = route == "indexed" ? index.SearchAsync(request, source.Token) : live.SearchAsync(request, source.Token);
        var reader = stream.GetAsyncEnumerator(source.Token);
        bool disposed = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!await reader.MoveNextAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false)) return new(route, "after first result", false, 0, "No active result to cancel.");
            var clock = Stopwatch.StartNew();
            source.Cancel();
            bool ended = false;
            try { while (await reader.MoveNextAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false)) { } ended = true; }
            catch (OperationCanceledException) when (source.IsCancellationRequested && !timeout.IsCancellationRequested) { ended = true; }
            await reader.DisposeAsync().ConfigureAwait(false);
            disposed = true;
            return new(route, "after first result; includes enumerator disposal", ended, clock.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { return new(route, "after first result", false, 0, ex.Message); }
        finally
        {
            source.Cancel();
            if (!disposed) await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static BaselineEnvironment CaptureEnvironment(string path)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        string Version(Type type) => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return new(RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "not reported", Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, drive.DriveType.ToString(), drive.DriveFormat,
            Version(typeof(Searcher)), Version(typeof(CSharpDB.Engine.Database)), Version(typeof(Microsoft.Data.Sqlite.SqliteConnection)),
            Git("rev-parse", "HEAD"), Git("status", "--porcelain"), configuration);
    }

    private static string Git(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? result.Trim() : "unavailable";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "unavailable"; }
    }

    private static string WriteMarkdown(TaskBaselineReport report)
    {
        var text = new StringBuilder("# Controlled real-file task baseline\n\n");
        text.AppendLine(CultureInfo.InvariantCulture, $"Measured {report.StartedUtc:O}; HEAD `{report.Environment.GitHead}`; working tree changes are recorded in JSON.");
        text.AppendLine(report.Condition);
        text.AppendLine("\nCorpus: generated documents, mail, ZIP, and two SQL engines. No direct-seeded rows. These tasks check deterministic correctness, not held-out search relevance or competitive performance.");
        text.AppendLine("\n| Task | Route | Completed / requested samples (includes first invocation) | Passed | Timed out |\n| --- | --- | ---: | --- | --- |");
        foreach (var row in report.Workers)
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Task} | {row.Route} | {row.CompletedSamples}/{row.RequestedSamples} | {row.Success} | {row.TimedOut} |");
        text.AppendLine("\n| Task | Route | Success | Useful P95 ms | Complete P50/P95/P99 ms | P95 goal | Met |\n| --- | --- | ---: | ---: | ---: | ---: | --- |");
        string Number(double? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a";
        foreach (var row in report.Summaries)
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Task} | {row.Route} | {row.Successes}/{row.Samples} | {Number(row.FirstUsefulMs.P95)} | {Number(row.CompletionMs.P50)} / {Number(row.CompletionMs.P95)} / {Number(row.CompletionMs.P99)} | {Number(row.CompletionP95GoalMs)} | {row.MeetsGoal?.ToString() ?? "n/a"} |");
        text.AppendLine("\n| Database | Warm samples | Open + first page P95 ms | Open snapshot/schema P95 ms | First page P95 ms | Deep page P95 ms |\n| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in report.DatabaseSamples.Where(row => row.Iteration > 0).GroupBy(row => row.Engine))
        {
            double? P95(Func<DatabaseSample, double> selector) => Percentiles.From(group.Select(row => (double?)selector(row))).P95;
            text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {Number(P95(row => row.FirstPageIncludingOpenMs))} | {Number(P95(row => row.OpenSnapshotAndSchemaMs))} | {Number(P95(row => row.FirstPageMs))} | {Number(P95(row => row.DeepPageMs))} |");
        }
        text.AppendLine(CultureInfo.InvariantCulture, $"\nInitial index: {Number(report.InitialIndexMs)} ms; {report.IndexedFiles} files; {report.FailedFiles} failures; {report.IndexBytes} bytes. Coordinator peak working set: {report.PeakWorkingSetBytes} bytes; per-worker working-set samples are in JSON. Explicit write/upsert/search freshness: {report.RefreshToSearchableMs:0.00} ms; passed: {report.RefreshSuccess}. Original corpus hashes unchanged: {report.SourceBytesUnchanged}.");
        foreach (var row in report.Cancellation) text.AppendLine(CultureInfo.InvariantCulture, $"\nCancellation ({row.Route}): observed={row.Observed}; acknowledgement plus disposal={row.AcknowledgeMs:0.00} ms; {row.Error}");
        text.AppendLine("\n## Unmeasured\n");
        foreach (string item in report.Unmeasured) text.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
        text.AppendLine("\nRaw first-invocation samples, errors/status, allocations, memory, versions/settings, corpus hashes and exact source expectations are in the adjacent JSON files. Failures are retained. A small sample count gives coarse tail percentiles; use at least 100 repetitions for a meaningful P99 estimate. First useful result is null for no-match/failing tasks, never substituted with completion time. UI idle, human task time, and component results are distinct measurements.");
        return text.ToString();
    }
}
