using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Volumes;
using FileSearch.Core.Walker;

namespace FileSearch.Benchmarks;

/// <summary>Real directory-walk scanner and filename kernel over generated empty files.</summary>
internal sealed class QuickNameTaskRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string EmptySha256 = Convert.ToHexString(SHA256.HashData([]));
    private const int QueryDeadlineSeconds = 15;
    private const int ScanDeadlineSeconds = 120;

    public async Task<int> RunAsync(BenchmarkPaths paths, int distractors, int repetitions, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real directory-walk scanner requires Windows.");
        if (distractors is < 1000 or > 100_000) throw new ArgumentOutOfRangeException(nameof(distractors), "Use 1,000 through 100,000 generated distractor files.");
        if (repetitions is < 3 or > 200) throw new ArgumentOutOfRangeException(nameof(repetitions), "Use 3 through 200 warm query repetitions, plus the separate first sample.");
        var definition = QuickNameTaskCorpus.Create(distractors);
        string parent = Path.Combine(paths.RootDirectory, "quick-name-tasks");
        string corpusParent = Path.Combine(parent, definition.Version, "distractors-" + distractors);
        string corpus = Path.Combine(corpusParent, "corpus");
        string run = Path.Combine(parent, "reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var report = new QuickNameReport
        {
            StartedUtc = DateTime.UtcNow,
            Definition = definition,
            DefinitionSha256 = HashJson(definition),
            TaskSha256 = HashJson(definition.Tasks),
            CorpusDirectory = corpus,
            RunDirectory = run,
            WarmRepetitions = repetitions,
            SourceBefore = SourceHashes(),
            Environment = new
            {
                runtime = System.Environment.Version.ToString(),
                os = System.Environment.OSVersion.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = System.Environment.ProcessorCount,
                stopwatchFrequency = Stopwatch.Frequency,
                processId = System.Environment.ProcessId,
                workingSetBefore = System.Environment.WorkingSet,
                machineName = System.Environment.MachineName,
            },
            Binaries = new[] { typeof(VolumeTermSearchRequest).Assembly.Location, typeof(QuickNameTaskRunner).Assembly.Location }.ToDictionary(path => path, FileHash),
        };
        string output = Path.Combine(run, "quick-name-report.json");
        string samplesPath = Path.Combine(run, "query-samples.jsonl");
        try
        {
            var generation = Stopwatch.StartNew();
            string[] files = QuickNameTaskCorpus.FileNames(definition).Order(StringComparer.Ordinal).ToArray();
            var expected = ExpectedEntries(files, definition.Directories);
            string expectedFingerprint = HashJson(expected);
            PrepareCorpus(corpusParent, corpus, definition, report.DefinitionSha256, expected, token);
            report.GenerationMilliseconds = generation.Elapsed.TotalMilliseconds;
            report.ExpectedEntryManifestSha256 = expectedFingerprint;
            report.FixtureBeforeSha256 = ValidateCorpus(corpus, expected, token);
            report.PhysicalFileCount = files.Length;
            report.DirectoryCount = expected.Count(entry => entry.IsDirectory);

            using var scanDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            scanDeadline.CancelAfter(TimeSpan.FromSeconds(ScanDeadlineSeconds));
            long scanAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var scan = Stopwatch.StartNew();
            var table = DirectoryWalkScanner.Scan(corpus, null, scanDeadline.Token);
            report.ScanMilliseconds = scan.Elapsed.TotalMilliseconds;
            report.ScanAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - scanAllocated;
            report.Table = new
            {
                table.Count,
                table.RootRecord,
                table.HighWater,
                table.EstimatedBytes,
                table.NameBytesInUse,
                expectedCountIncludingRoot = expected.Length + 1,
                scanner = "DirectoryWalkScanner.Scan; generated subtree only; native NTFS file references, no MFT scan/USN/service/UI",
            };
            if (table.Count != expected.Length + 1) report.Errors.Add($"Scanner produced {table.Count} entries; expected {expected.Length + 1}. Partial/inaccessible scanning is not accepted as complete.");

            await using var journal = new StreamWriter(samplesPath, append: false, Encoding.UTF8);
            foreach (var task in definition.Tasks)
            {
                token.ThrowIfCancellationRequested();
                var taskSamples = new List<QuerySample>();
                var compileWatch = Stopwatch.StartNew();
                var terms = VolumeTermSearch.Compile(task.Query) ?? throw new InvalidOperationException("A frozen query could not compile: " + task.Id);
                double compileMilliseconds = compileWatch.Elapsed.TotalMilliseconds;
                var roots = task.ScopeRoots.Length == 0 ? new[] { corpus } : task.ScopeRoots.Select(relative => SafeFullPath(corpus, relative)).ToArray();
                var scopeRecords = new HashSet<int>();
                foreach (string root in roots)
                {
                    if (!VolumeNativeMethods.TryGetFileReference(root, out ulong reference, out _) || !table.IsInUse(VolumeNameTable.RecordOf(reference)))
                        throw new IOException("Generated scope is absent from scanned table: " + root);
                    scopeRecords.Add(VolumeNameTable.RecordOf(reference));
                }
                var request = new VolumeTermSearchRequest(task.Query, definition.MaxResults, ScopeRoots: roots, IncludeHiddenFolders: false);
                for (int iteration = 0; iteration <= repetitions; iteration++)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(QueryDeadlineSeconds));
                    var sample = new QuerySample { TaskId = task.Id, Partition = task.Partition, Iteration = iteration, FirstSample = iteration == 0, CompileMilliseconds = compileMilliseconds };
                    long managedBefore = GC.GetTotalMemory(forceFullCollection: false);
                    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    List<VolumeTermMatch>? matches = null;
                    long total = 0;
                    long allocation = GC.GetTotalAllocatedBytes(precise: true);
                    long queryStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        (matches, total) = VolumeTermSearch.Search(table, corpus, terms, request, scopeRecords, deadline.Token);
                    }
                    catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                    {
                        sample.TimedOut = true;
                        sample.Error = ex.ToString();
                    }
                    catch (Exception ex)
                    {
                        sample.Error = ex.ToString();
                    }
                    finally
                    {
                        // Capture kernel costs before mapping paths into report rows.
                        sample.CompletionMilliseconds = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
                        sample.AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocation;
                        sample.ManagedBytesDelta = GC.GetTotalMemory(forceFullCollection: false) - managedBefore;
                        sample.GenCollections = new[] { GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2] };
                        using var process = Process.GetCurrentProcess();
                        process.Refresh();
                        sample.WorkingSetAfterBytes = process.WorkingSet64;
                    }
                    if (matches is not null)
                    {
                        sample.TotalEligibleMatches = total;
                        sample.Results = matches.Select(match => new RankedName(Relative(corpus, match.Path), match.Score, match.IsDirectory)).ToArray();
                        int targetIndex = task.TargetRelativePath is null ? -1 : matches.FindIndex(match => string.Equals(Relative(corpus, match.Path), task.TargetRelativePath, StringComparison.OrdinalIgnoreCase));
                        sample.TargetRank = targetIndex < 0 ? null : targetIndex + 1;
                        sample.TargetReturnedAtCompletionMilliseconds = sample.TargetRank is null ? null : sample.CompletionMilliseconds;
                        sample.StrictControlPassed = task.ExpectNoResults && total == 0 && matches.Count == 0;
                    }
                    taskSamples.Add(sample);
                    await journal.WriteLineAsync(JsonSerializer.Serialize(sample)).ConfigureAwait(false);
                    await journal.FlushAsync(token).ConfigureAwait(false);
                    if (sample.Error is not null) break;
                }
                var summary = SummarizeTask(task, taskSamples, repetitions);
                report.Tasks.Add(summary);
                Console.WriteLine($"{task.Id} {task.Query}: target rank {summary.FirstTargetRank?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, control={summary.StrictControlPassed}, warm P50={summary.WarmP50Milliseconds?.ToString("F2", CultureInfo.InvariantCulture) ?? "unavailable"}ms, errors={summary.ErrorCount}");
                await SaveAsync(output, report, token).ConfigureAwait(false);
            }
            report.FixtureAfterSha256 = ValidateCorpus(corpus, expected, token);
            report.FixtureBytesAndNamesUnchanged = report.FixtureBeforeSha256 == report.FixtureAfterSha256 && report.FixtureAfterSha256 == expectedFingerprint;
            report.Completed = true;
        }
        catch (Exception ex) { report.FatalError = ex.ToString(); }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            report.SourceAfter = SourceHashes();
            report.ObservedSourcesUnchanged = DictionariesEqual(report.SourceBefore, report.SourceAfter);
            report.Summaries = new[] { SummarizePartition("calibration", report), SummarizePartition("held-out", report), SummarizePartition("all", report) };
            await SaveAsync(output, report, CancellationToken.None).ConfigureAwait(false);
        }
        Console.WriteLine("Retained report: " + output);
        return report.Completed && report.FatalError is null && report.Errors.Count == 0 && report.ObservedSourcesUnchanged && report.FixtureBytesAndNamesUnchanged && report.Tasks.All(task => task.ErrorCount == 0 && (!task.ExpectNoResults || task.StrictControlPassed)) ? 0 : 5;
    }

    private static void PrepareCorpus(string parent, string root, QuickNameDefinition definition, string definitionHash, CorpusEntry[] entries, CancellationToken token)
    {
        string manifest = Path.Combine(parent, "quick-name-manifest.json");
        string manifestText = JsonSerializer.Serialize(new { definitionHash, definition, emptyFileSha256 = EmptySha256, entryManifestSha256 = HashJson(entries), entries }, JsonOptions);
        if (File.Exists(manifest))
        {
            if (File.ReadAllText(manifest) != manifestText) throw new InvalidDataException("The retained corpus manifest differs. Choose another benchmark root; do not overwrite a frozen corpus.");
            _ = ValidateCorpus(root, entries, token);
            return;
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("A nonempty corpus directory has no benchmark manifest. Choose another benchmark root.");
        Directory.CreateDirectory(root);
        foreach (var entry in entries.OrderBy(entry => entry.RelativePath.Length))
        {
            token.ThrowIfCancellationRequested();
            string full = SafeFullPath(root, entry.RelativePath);
            if (entry.IsDirectory) Directory.CreateDirectory(full);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
        }
        Directory.CreateDirectory(parent);
        File.WriteAllText(manifest, manifestText, new UTF8Encoding(false));
    }

    private static CorpusEntry[] ExpectedEntries(IEnumerable<string> files, IEnumerable<string> directories)
    {
        var entries = new Dictionary<string, CorpusEntry>(StringComparer.OrdinalIgnoreCase);
        void AddParents(string relative)
        {
            int separator = relative.LastIndexOf('/');
            while (separator > 0)
            {
                string parent = relative[..separator];
                entries.TryAdd(parent, new(parent, true, 0, null));
                separator = parent.LastIndexOf('/');
            }
        }
        foreach (string path in files)
        {
            if (!entries.TryAdd(path, new(path, false, 0, EmptySha256))) throw new InvalidDataException("Duplicate frozen filename: " + path);
            AddParents(path);
        }
        foreach (string path in directories) { entries[path] = new(path, true, 0, null); AddParents(path); }
        return entries.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ValidateCorpus(string root, CorpusEntry[] expected, CancellationToken token)
    {
        var actual = new List<CorpusEntry>(expected.Length);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not allowed in generated fixtures: " + path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            long length = directory ? 0 : new FileInfo(path).Length;
            if (length != 0) throw new InvalidDataException("A generated filename-only fixture is no longer empty: " + path);
            actual.Add(new(Relative(root, path), directory, length, directory ? null : EmptySha256));
        }
        string fingerprint = HashJson(actual.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());
        if (fingerprint != HashJson(expected)) throw new InvalidDataException("Generated fixture paths/types/bytes changed or are incomplete.");
        return fingerprint;
    }

    private static TaskSummary SummarizeTask(QuickNameTask task, List<QuerySample> samples, int repetitions)
    {
        var first = samples.FirstOrDefault();
        var warm = samples.Where(sample => !sample.FirstSample && sample.Error is null).ToArray();
        return new TaskSummary
        {
            Id = task.Id,
            Partition = task.Partition,
            Query = task.Query,
            TargetRelativePath = task.TargetRelativePath,
            ExpectNoResults = task.ExpectNoResults,
            FirstTargetRank = first?.TargetRank,
            FirstCompletionMilliseconds = first?.CompletionMilliseconds,
            FirstUsefulMilliseconds = null,
            FirstUsefulObservation = "Not separately observable: VolumeTermSearch returns a completed batch. TargetReturnedAtCompletionMilliseconds is separate, and remains null for absent targets.",
            FirstTargetReturnedAtCompletionMilliseconds = first?.TargetReturnedAtCompletionMilliseconds,
            Top1 = first?.TargetRank == 1,
            Top5 = first?.TargetRank is > 0 and <= 5,
            ReciprocalRank = first?.TargetRank is int rank ? 1d / rank : 0,
            StrictControlPassed = task.ExpectNoResults && samples.All(sample => sample.StrictControlPassed && sample.Error is null),
            ErrorCount = samples.Count(sample => sample.Error is not null),
            TimeoutCount = samples.Count(sample => sample.TimedOut),
            WarmP50Milliseconds = Percentile(warm.Select(sample => sample.CompletionMilliseconds), .50),
            WarmP95Milliseconds = Percentile(warm.Select(sample => sample.CompletionMilliseconds), .95),
            WarmP99Milliseconds = Percentile(warm.Select(sample => sample.CompletionMilliseconds), .99),
            WarmAllocationP50Bytes = Percentile(warm.Select(sample => (double)sample.AllocatedBytes), .50),
            MaximumSampledWorkingSetBytes = samples.Count == 0 ? null : samples.Max(sample => sample.WorkingSetAfterBytes),
            TargetRanksStable = samples.All(sample => sample.Error is null && sample.TargetRank == first?.TargetRank),
            RequestedWarmRepetitions = repetitions,
            CompletedWarmRepetitions = warm.Length,
            Samples = samples,
        };
    }

    private static object SummarizePartition(string partition, QuickNameReport report)
    {
        var definitions = report.Definition.Tasks.Where(task => partition == "all" || task.Partition == partition).ToArray();
        var rows = report.Tasks.Where(task => partition == "all" || task.Partition == partition).ToArray();
        int targets = definitions.Count(task => !task.ExpectNoResults);
        return new
        {
            partition,
            declaredTaskCount = definitions.Length,
            observedTaskCount = rows.Length,
            targetCount = targets,
            top1Count = rows.Count(task => !task.ExpectNoResults && task.Top1),
            top5Count = rows.Count(task => !task.ExpectNoResults && task.Top5),
            meanReciprocalRank = targets == 0 ? 0 : rows.Where(task => !task.ExpectNoResults).Sum(task => task.ReciprocalRank) / targets,
            targetMissingOrUnobserved = targets - rows.Count(task => !task.ExpectNoResults && task.FirstTargetRank is not null),
            expectedStrictControls = definitions.Count(task => task.ExpectNoResults),
            strictControlsPassed = rows.Count(task => task.StrictControlPassed),
            errorCount = rows.Sum(task => task.ErrorCount),
            timeoutCount = rows.Sum(task => task.TimeoutCount),
            incompleteTaskCount = definitions.Length - rows.Count(task => task.CompletedWarmRepetitions == report.WarmRepetitions),
        };
    }

    private static double? Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string SafeFullPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Fixture path escaped its root: " + relative);
        return full;
    }
    private static string HashJson<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool DictionariesEqual(Dictionary<string, string> left, Dictionary<string, string> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? hash) && pair.Value == hash);
    private static Task SaveAsync(string path, QuickNameReport report, CancellationToken token) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), token);

    private static Dictionary<string, string> SourceHashes()
    {
        for (var root = new DirectoryInfo(System.Environment.CurrentDirectory); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "FileSearch.slnx"))) continue;
            string[] paths =
            [
                "benchmarks/FileSearch.Benchmarks/QuickNameTaskRunner.cs", "benchmarks/FileSearch.Benchmarks/QuickNameTaskCorpus.cs", "benchmarks/FileSearch.Benchmarks/Program.cs",
                "src/FileSearch.Core/Volumes/VolumeTermSearch.cs", "src/FileSearch.Core/Volumes/VolumeScanners.cs", "src/FileSearch.Core/Volumes/VolumeNameTable.cs",
                "src/FileSearch.Core/Queries/QuickSearchNameRanking.cs", "src/FileSearch.Core/Queries/QuickSearchNameQuery.cs",
            ];
            return paths.ToDictionary(path => path, path => FileHash(Path.Combine(root.FullName, path)), StringComparer.Ordinal);
        }
        throw new DirectoryNotFoundException("Run quick-name-tasks from this checkout or a child directory.");
    }

    private sealed record CorpusEntry(string RelativePath, bool IsDirectory, long Length, string? ByteSha256);
    private sealed record RankedName(string RelativePath, double Score, bool IsDirectory);
    private sealed class QuerySample
    {
        public string TaskId { get; init; } = "";
        public string Partition { get; init; } = "";
        public int Iteration { get; init; }
        public bool FirstSample { get; init; }
        public double CompileMilliseconds { get; init; }
        public double CompletionMilliseconds { get; set; }
        public double? FirstUsefulMilliseconds => null;
        public double? TargetReturnedAtCompletionMilliseconds { get; set; }
        public long TotalEligibleMatches { get; set; }
        public int? TargetRank { get; set; }
        public bool StrictControlPassed { get; set; }
        public bool TimedOut { get; set; }
        public string? Error { get; set; }
        public long AllocatedBytes { get; set; }
        public long ManagedBytesDelta { get; set; }
        public int[] GenCollections { get; set; } = [];
        public long WorkingSetAfterBytes { get; set; }
        public RankedName[] Results { get; set; } = [];
    }
    private sealed class TaskSummary
    {
        public string Id { get; init; } = "";
        public string Partition { get; init; } = "";
        public string Query { get; init; } = "";
        public string? TargetRelativePath { get; init; }
        public bool ExpectNoResults { get; init; }
        public int? FirstTargetRank { get; init; }
        public bool Top1 { get; init; }
        public bool Top5 { get; init; }
        public double ReciprocalRank { get; init; }
        public bool StrictControlPassed { get; init; }
        public double? FirstCompletionMilliseconds { get; init; }
        public double? FirstUsefulMilliseconds { get; init; }
        public string FirstUsefulObservation { get; init; } = "";
        public double? FirstTargetReturnedAtCompletionMilliseconds { get; init; }
        public double? WarmP50Milliseconds { get; init; }
        public double? WarmP95Milliseconds { get; init; }
        public double? WarmP99Milliseconds { get; init; }
        public double? WarmAllocationP50Bytes { get; init; }
        public long? MaximumSampledWorkingSetBytes { get; init; }
        public bool TargetRanksStable { get; init; }
        public int RequestedWarmRepetitions { get; init; }
        public int CompletedWarmRepetitions { get; init; }
        public int ErrorCount { get; init; }
        public int TimeoutCount { get; init; }
        public List<QuerySample> Samples { get; init; } = [];
    }
    private sealed class QuickNameReport
    {
        public int SchemaVersion => 1;
        public required QuickNameDefinition Definition { get; init; }
        public string Method => "Generated empty files only; actual DirectoryWalkScanner and VolumeTermSearch. No copied repository/user content, fake providers, user index, full-drive scan, USN/service, UI, file launch or automatic learning.";
        public string FirstUsefulClock => "Not separately observable from this batch API; null is not zero. TargetReturnedAtCompletionMilliseconds only records successful target inclusion in the completed list.";
        public string ResourceCaveat => "NTFS file IDs can make subtree tables sparse. Table HighWater/RootRecord and allocation reflect volume history. Query allocation is process-total cumulative delta; sampled working set is not a peak; managed delta is not retained memory. First is first invocation in this worker, not OS-cold; warm percentiles from small counts are diagnostic only.";
        public string DepthCaveat => "Kernel paths/depth are relative to this generated subtree root, not a production drive root; fixture generation/scanning are outside query clocks.";
        public int QueryDeadlineSeconds => QuickNameTaskRunner.QueryDeadlineSeconds;
        public int ScanDeadlineSeconds => QuickNameTaskRunner.ScanDeadlineSeconds;
        public object SearchSettings => new { IncludeFiles = true, IncludeFolders = true, IncludeHiddenFolders = false, ExcludeDirectoryNames = WalkerOptions.DefaultExcludeDirectories.Order(StringComparer.Ordinal).ToArray(), CompileBeforeKernelClock = true, MaxResults = Definition.MaxResults, NearestRankPercentiles = true };
        public bool Completed { get; set; }
        public DateTime StartedUtc { get; init; }
        public DateTime FinishedUtc { get; set; }
        public required string DefinitionSha256 { get; init; }
        public required string TaskSha256 { get; init; }
        public required string CorpusDirectory { get; init; }
        public required string RunDirectory { get; init; }
        public int WarmRepetitions { get; init; }
        public object? Environment { get; init; }
        public required Dictionary<string, string> SourceBefore { get; init; }
        public Dictionary<string, string> SourceAfter { get; set; } = [];
        public required Dictionary<string, string> Binaries { get; init; }
        public bool ObservedSourcesUnchanged { get; set; }
        public string? ExpectedEntryManifestSha256 { get; set; }
        public string? FixtureBeforeSha256 { get; set; }
        public string? FixtureAfterSha256 { get; set; }
        public bool FixtureBytesAndNamesUnchanged { get; set; }
        public int PhysicalFileCount { get; set; }
        public int DirectoryCount { get; set; }
        public double GenerationMilliseconds { get; set; }
        public double ScanMilliseconds { get; set; }
        public long ScanAllocatedBytes { get; set; }
        public object? Table { get; set; }
        public string? FatalError { get; set; }
        public List<string> Errors { get; } = [];
        public List<TaskSummary> Tasks { get; } = [];
        public object[] Summaries { get; set; } = [];
    }
}
