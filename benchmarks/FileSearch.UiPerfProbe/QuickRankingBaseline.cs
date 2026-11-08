using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Gui;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

/// <summary>
/// Frozen, synthetic-provider replay through the real Quick Search VM and offscreen WPF list.
/// This measures host streaming/fusion, not native-provider ranking or installed-product speed.
/// </summary>
internal static class QuickRankingBaseline
{
    private const int TimeoutSeconds = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly DateTime FixtureTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string fixtureRoot = Path.Combine(Path.GetDirectoryName(output)!, "quick-ranking-fixtures-v1");
        var tasks = DeclareTasks();
        var fixtureHashes = PrepareFixtures(fixtureRoot, tasks);
        string repository = FindRepository();
        var sourceBefore = SourceHashes(repository);
        var rows = new List<CaseResult>();
        var startedUtc = DateTime.UtcNow;
        foreach (var task in tasks)
        {
            CaseResult row;
            try { row = await RunCaseAsync(fixtureRoot, task); }
            catch (Exception ex) { row = new CaseResult { Id = task.Id, Split = task.Split, Query = task.Query, ExpectNoResults = task.ExpectNoResults, ExpectedPath = task.Target is null ? null : Path.Combine(fixtureRoot, task.Id, task.Target), Error = ex.ToString() }; }
            rows.Add(row);
            Console.WriteLine($"quick ranking {task.Id}: rank={row.FinalRank?.ToString() ?? "miss"}, visible={row.FirstUsefulVisibleMs?.ToString("F1") ?? "never"}ms, complete={row.CompletionMs:F1}ms, error={row.Error ?? "none"}");
            await File.WriteAllTextAsync(output + ".partial.json", JsonSerializer.Serialize(new { corpusVersion = "quick-ranking-v1", tasks, rows }, JsonOptions));
        }

        var sourceAfter = SourceHashes(repository);
        var fixtureAfter = fixtureHashes.Keys.ToDictionary(path => path, FileHash, StringComparer.OrdinalIgnoreCase);
        bool sourceUnchanged = HashesEqual(sourceBefore, sourceAfter);
        bool fixtureUnchanged = HashesEqual(fixtureHashes, fixtureAfter);
        var report = new
        {
            corpusVersion = "quick-ranking-v1",
            startedUtc,
            finishedUtc = DateTime.UtcNow,
            provenance = "Scripted fake IVolumeNameIndex scores/order and fake ISearcher content schedules; actual QuickSearchViewModel streaming/fusion, actual offscreen WPF ResultsList, actual filesystem metadata fallback only in the two mixed-root tasks. No native provider ranking claim.",
            limitations = new[]
            {
                "Frozen 12 calibration + 12 held-out declared tasks; do not tune against held-out results.",
                "Target-visible means a realized intersecting list-item container in an offscreen WPF viewport, not physical screen visibility or user input.",
                "Timings include 35ms query debounce, dispatcher scheduling and this harness's sampling/render work; they are local diagnostic measurements, not installed-product/performance qualification.",
                "A missing first-useful timestamp is retained as a failure, never changed to zero. Strict acronym/typo misses are scored separately from target tasks.",
                "No usage-history store, file launch, network, whole-drive index, OS-cold cache control, or automatic learning is exercised."
            },
            environment = new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), processorCount = Environment.ProcessorCount, stopwatchFrequency = Stopwatch.Frequency, fixtureRoot, timeoutSeconds = TimeoutSeconds, viewport = new { width = 900, height = 650 } },
            binaries = new[] { typeof(QuickSearchViewModel).Assembly.Location, typeof(Hit).Assembly.Location, typeof(QuickRankingBaseline).Assembly.Location }.Distinct().ToDictionary(path => path, FileHash),
            sourceFilesUnchanged = sourceUnchanged,
            fixtureBytesUnchanged = fixtureUnchanged,
            sourceBefore,
            sourceAfter,
            fixtureHashes,
            tasks,
            summary = new[] { Summarize("calibration", rows), Summarize("held-out", rows), Summarize("all", rows) },
            rows
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));
        if (!sourceUnchanged || !fixtureUnchanged) throw new InvalidOperationException("Source or fixture bytes changed during ranking replay; see the retained report.");
    }

    private static async Task<CaseResult> RunCaseAsync(string fixtureRoot, RankingTask task)
    {
        string root = Path.Combine(fixtureRoot, task.Id);
        string Full(string relative) => Path.GetFullPath(Path.Combine(root, relative));
        var state = new ReplayState(task, root);
        var settings = new FakeSettingsService();
        settings.Current.QuickSearchDefaultScope = task.MixedRoots ? QuickSearchScopeKind.SelectedIndexedLocations : QuickSearchScopeKind.CurrentFolder;
        settings.Current.QuickSearchLastScope = settings.Current.QuickSearchDefaultScope;
        settings.Current.QuickSearchIncludeContent = task.Content.Length != 0;
        var roots = task.MixedRoots ? new[] { Full("covered"), Full("live") } : new[] { root };
        settings.Current.IndexedLocations = roots.Select(path => new IndexedLocationSettings { Root = path }).ToList();
        settings.Current.QuickSearchSelectedIndexedRoots = roots.ToList();
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher();
        using var main = new SearchViewModel(new EmptySearcher(), new ExtractorRegistry([]), new QueryFactory(), new FakePreviewService(), launcher, settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var quick = new QuickSearchViewModel(new ReplaySearcher(state), new QueryFactory(), new FakePreviewService(), launcher, settings, main, new FakeFolderPicker(), volumeNameIndex: new ReplayVolumeIndex(state));
        main.ApplyLocationScope(task.MixedRoots ? LocationScopeKind.SelectedIndexedLocations : LocationScopeKind.SelectedFolder, roots);
        quick.PrepareForShow();
        quick.QuickFolderPath = root;
        quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == settings.Current.QuickSearchDefaultScope);
        quick.IncludeContentMatches = task.Content.Length != 0;
        var window = new QuickSearchWindow { DataContext = quick, Width = 900, Height = 650, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var result = new CaseResult { Id = task.Id, Split = task.Split, Query = task.Query, ExpectNoResults = task.ExpectNoResults, ExpectedPath = task.Target is null ? null : Full(task.Target) };
        var clock = new Stopwatch();
        string Relative(string path) => Path.GetRelativePath(root, path);
        int? Rank() => result.ExpectedPath is null ? null : quick.Results.Select((item, index) => (item, index)).Where(pair => string.Equals(pair.item.FullPath, result.ExpectedPath, StringComparison.OrdinalIgnoreCase)).Select(pair => (int?)(pair.index + 1)).FirstOrDefault();
        void Capture(string reason)
        {
            if (!clock.IsRunning) return;
            double ms = clock.Elapsed.TotalMilliseconds;
            if (quick.Results.Count != 0) result.FirstAnyCollectionMs ??= ms;
            int? rank = Rank();
            if (rank is not null) result.FirstUsefulCollectionMs ??= ms;
            if (result.Timeline.Count < 256) result.Timeline.Add(new TimelineEntry(ms, reason, rank, quick.SelectedResult is null ? null : Relative(quick.SelectedResult.FullPath), quick.Results.Take(5).Select(item => Relative(item.FullPath)).ToArray()));
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => Capture("collection");
        quick.Results.CollectionChanged += changed;
        System.ComponentModel.PropertyChangedEventHandler selected = (_, args) => { if (args.PropertyName == nameof(quick.SelectedResult)) Capture("selection"); };
        quick.PropertyChanged += selected;
        try
        {
            window.Show();
            await SettleAsync(window);
            var list = (ListBox)window.FindName("ResultsList");
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            clock.Start();
            quick.SearchText = task.Query;
            while (true)
            {
                await Task.Delay(5);
                window.UpdateLayout();
                if (result.FirstUsefulVisibleMs is null && result.ExpectedPath is not null)
                {
                    var target = quick.Results.FirstOrDefault(item => string.Equals(item.FullPath, result.ExpectedPath, StringComparison.OrdinalIgnoreCase));
                    if (target is not null && list.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container && container.IsVisible)
                    {
                        var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
                        if (bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(new Rect(list.RenderSize))) result.FirstUsefulVisibleMs = clock.Elapsed.TotalMilliseconds;
                    }
                }
                if (state.MetadataStarted && !quick.IsSearching) break;
                if (clock.Elapsed.TotalSeconds > TimeoutSeconds) { result.TimedOut = true; break; }
            }
            result.CompletionMs = clock.Elapsed.TotalMilliseconds;
            await SettleAsync(window);
            result.AllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore;
            Capture("completion");
            result.FinalOrder = quick.Results.Select(item => Relative(item.FullPath)).ToArray();
            result.FinalRank = Rank();
            result.Top1Correct = result.FinalRank == 1;
            result.Top5Correct = result.FinalRank is > 0 and <= 5;
            result.ReciprocalRank = result.FinalRank is int rank ? 1d / rank : 0;
            result.StrictMissPassed = task.ExpectNoResults && quick.Results.Count == 0 && !result.TimedOut;
            result.StatusText = quick.StatusText;
            result.StageText = quick.StageText;
            result.MetadataStarted = state.MetadataStarted;
            result.MetadataCancelled = state.MetadataCancelled;
            result.ContentStarted = state.ContentStarted;
            result.ContentCancelled = state.ContentCancelled;
            result.SelectedPath = quick.SelectedResult is null ? null : Relative(quick.SelectedResult.FullPath);
        }
        catch (Exception ex) { result.Error = ex.ToString(); result.CompletionMs = clock.Elapsed.TotalMilliseconds; }
        finally
        {
            quick.Dismiss();
            quick.Results.CollectionChanged -= changed;
            quick.PropertyChanged -= selected;
            window.Close();
        }
        return result;
    }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(15);
    }

    private static object Summarize(string split, IReadOnlyList<CaseResult> rows)
    {
        var selected = rows.Where(row => split == "all" || row.Split == split).ToArray();
        var targets = selected.Where(row => !row.ExpectNoResults).ToArray();
        return new
        {
            split, count = selected.Length, targetCount = targets.Length,
            top1Accuracy = targets.Length == 0 ? 0 : targets.Count(row => row.Top1Correct) / (double)targets.Length,
            top5Accuracy = targets.Length == 0 ? 0 : targets.Count(row => row.Top5Correct) / (double)targets.Length,
            meanReciprocalRank = targets.Length == 0 ? 0 : targets.Average(row => row.ReciprocalRank),
            usefulVisibleCount = targets.Count(row => row.FirstUsefulVisibleMs is not null),
            usefulNeverVisibleCount = targets.Count(row => row.FirstUsefulVisibleMs is null),
            firstUsefulVisibleMs = targets.Select(row => new { row.Id, milliseconds = row.FirstUsefulVisibleMs }).ToArray(),
            strictMissCount = selected.Count(row => row.ExpectNoResults), strictMissPassed = selected.Count(row => row.StrictMissPassed),
            timeoutCount = selected.Count(row => row.TimedOut), errorCount = selected.Count(row => row.Error is not null)
        };
    }

    // The entire declared task set and provider schedule are serialized into every report.
    // Held-out cases are fixed before product ranking changes and are not a tuning surface.
    private static RankingTask[] DeclareTasks()
    {
        var tasks = new List<RankingTask>();
        void Add(string id, string split, string query, string? target, Entry[] metadata, Entry[]? content = null, int delay = 10, bool mixed = false, bool strictMiss = false, Entry[]? other = null) => tasks.Add(new(id, split, query, target, metadata, content ?? [], other ?? [], delay, mixed, strictMiss));
        void Family(string split, int offset, string exact, string deep, string brand, string own, string first, string mixed, string cap)
        {
            string Id(int n) => $"task-{offset + n:00}";
            Add(Id(1), split, exact, exact + ".xlsx", [new(exact + ".xlsx", 1500), new(exact + "-draft.xlsx", 1100)], [new(exact + "-draft.xlsx", 100, Text: exact)], 80);
            string deepPath = Path.Combine(Enumerable.Repeat("d", 45).Append(deep + ".txt").ToArray());
            Add(Id(2), split, deep, deepPath, [new(deep + "-draft.txt", 1100), new(deepPath, 1000)]);
            Add(Id(3), split, brand + " " + own, brand + "-" + own + ".txt", [new(Path.Combine(brand, own + "-notes.txt"), 500), new(brand + "-" + own + ".txt", 1400)]);
            Add(Id(4), split, "memo", "alpha-memo.txt", [new("bravo-memo.txt", 700), new("alpha-memo.txt", 700)]);
            Add(Id(5), split, first, first + ".docx", [new(first + ".docx", 1500)], [new("summary.txt", 10, Text: first)], 100);
            Add(Id(6), split, mixed, Path.Combine("live", mixed + ".txt"), [new(Path.Combine("covered", mixed + "-notes.txt"), 1200)], delay: 15, mixed: true, other: [new(Path.Combine("live", mixed + ".txt"), 1)]);
            Add(Id(7), split, cap, cap + ".txt", [new(cap + ".txt", 1500)], Enumerable.Range(0, 80).Select(i => new Entry(Path.Combine("content", $"loose-{i:000}.txt"), 1, Text: cap)).ToArray(), 150);
        }
        Family("calibration", 0, "budget", "report", "atlas", "report", "invoice", "needle", "capexact");
        Add("task-08", "calibration", "a[b].txt", "a[b].txt", [new("a[b].txt", 1500)]);
        Add("task-09", "calibration", "REPORT", "report.txt", [new("report.txt", 1500), new("report-notes.txt", 1100)]);
        Add("task-10", "calibration", "arpx", null, [], strictMiss: true, other: [new("AnnualReportPortfolioX.txt", 0)]);
        Add("task-11", "calibration", "budgte", null, [], strictMiss: true, other: [new("Budget.xlsx", 0)]);
        Add("task-12", "calibration", "john OR smith", "john OR smith.txt", [new("john OR smith.txt", 1500)], [new("operators.txt", 10, Text: "john OR smith")], 80);
        Family("held-out", 12, "forecast", "receipt", "lumen", "notes", "contract", "receipt", "targetfile");
        Add("task-20", "held-out", "réSUMé", "Résumé.txt", [new("Résumé.txt", 1500), new("Résumé notes.txt", 1100)]);
        Add("task-21", "held-out", @"Projects\Orion", Path.Combine("Projects", "Orion"), [new(Path.Combine("Projects", "Orion"), 1500, IsDirectory: true)]);
        Add("task-22", "held-out", "renewal 東京", "notes.txt", [], [new("notes.txt", 10, Text: "renewal 東京")]);
        Add("task-23", "held-out", "qspx", null, [], strictMiss: true, other: [new("QuerySolutionPlanX.docx", 0)]);
        Add("task-24", "held-out", "forecats", null, [], strictMiss: true, other: [new("Forecast.xlsx", 0)]);
        return tasks.ToArray();
    }

    private static Dictionary<string, string> PrepareFixtures(string fixtureRoot, IReadOnlyList<RankingTask> tasks)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            string root = Path.Combine(fixtureRoot, task.Id);
            Directory.CreateDirectory(root);
            if (task.MixedRoots) { Directory.CreateDirectory(Path.Combine(root, "covered")); Directory.CreateDirectory(Path.Combine(root, "live")); }
            foreach (var group in task.Metadata.Concat(task.Content).Concat(task.OtherFiles).GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(Path.Combine(root, group.Key));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped its declared root.");
                if (group.Any(entry => entry.IsDirectory)) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = Encoding.UTF8.GetBytes(group.Select(entry => entry.Text).FirstOrDefault(text => text is not null) ?? "Frozen Quick ranking fixture.\n");
                if (File.Exists(path))
                {
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException($"Existing fixture content differs: {path}");
                }
                else { File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, FixtureTime); }
                hashes[path] = FileHash(path);
            }
        }
        return hashes;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "FileSearch.Gui", "ViewModels", "QuickSearchViewModel.cs"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the ranking probe from this repository or a child directory.");
    }

    private static Dictionary<string, string> SourceHashes(string root)
    {
        var paths = Directory.EnumerateFiles(Path.Combine(root, "src", "FileSearch.Gui", "ViewModels"), "QuickSearchViewModel*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "FileSearch.Core", "Volumes"), "*TermSearch*.cs"))
            .Concat(new[] { Path.Combine(root, "benchmarks", "FileSearch.UiPerfProbe", "QuickRankingBaseline.cs") });
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToDictionary(path => Path.GetRelativePath(root, path), FileHash, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HashesEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Entry(string RelativePath, double Score, bool IsDirectory = false, string? Text = null);
    private sealed record RankingTask(string Id, string Split, string Query, string? Target, Entry[] Metadata, Entry[] Content, Entry[] OtherFiles, int MetadataDelayMs, bool MixedRoots, bool ExpectNoResults);
    private sealed record TimelineEntry(double Ms, string Reason, int? TargetRank, string? Selected, string[] Top5);
    private sealed class CaseResult
    {
        public string Id { get; init; } = "";
        public string Split { get; init; } = "";
        public string Query { get; init; } = "";
        public string? ExpectedPath { get; init; }
        public bool ExpectNoResults { get; init; }
        public double? FirstAnyCollectionMs { get; set; }
        public double? FirstUsefulCollectionMs { get; set; }
        public double? FirstUsefulVisibleMs { get; set; }
        public double CompletionMs { get; set; }
        public int? FinalRank { get; set; }
        public bool Top1Correct { get; set; }
        public bool Top5Correct { get; set; }
        public double ReciprocalRank { get; set; }
        public bool StrictMissPassed { get; set; }
        public bool TimedOut { get; set; }
        public string? Error { get; set; }
        public bool MetadataStarted { get; set; }
        public bool MetadataCancelled { get; set; }
        public bool ContentStarted { get; set; }
        public bool ContentCancelled { get; set; }
        public long AllocatedBytes { get; set; }
        public string? SelectedPath { get; set; }
        public string StatusText { get; set; } = "";
        public string StageText { get; set; } = "";
        public string[] FinalOrder { get; set; } = [];
        public List<TimelineEntry> Timeline { get; } = [];
    }

    private sealed class ReplayState(RankingTask task, string root)
    {
        public RankingTask Task { get; } = task;
        public string Root { get; } = root;
        public volatile bool MetadataStarted;
        public volatile bool MetadataCancelled;
        public volatile bool ContentStarted;
        public volatile bool ContentCancelled;
        public string Full(Entry entry) => Path.GetFullPath(Path.Combine(Root, entry.RelativePath));
    }

    private sealed class ReplayVolumeIndex(ReplayState state) : IVolumeNameIndex
    {
        public event EventHandler<VolumeIndexStatus>? StatusChanged { add { } remove { } }
        public IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes() => [];
        public IReadOnlyList<VolumeIndexStatus> GetStatuses() => [];
        public Task StartTrackingAsync(IReadOnlyCollection<string> roots, CancellationToken token) => Task.CompletedTask;
        public Task<VolumeIndexStatus> BuildAsync(string root, VolumeBuildMethod method, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveAsync(string root, CancellationToken token) => Task.CompletedTask;
        public bool IsReady(string path) => true;
        public Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken token)
        {
            state.MetadataStarted = true;
            try
            {
                await Task.Delay(state.Task.MetadataDelayMs, token).ConfigureAwait(false);
                var matches = state.Task.Metadata.Select(entry => new VolumeTermMatch(state.Full(entry), entry.IsDirectory, entry.Score)).ToArray();
                IReadOnlyList<string> covered = state.Task.MixedRoots ? [Path.Combine(state.Root, "covered")] : request.ScopeRoots ?? [];
                IReadOnlyList<string> uncovered = state.Task.MixedRoots ? [Path.Combine(state.Root, "live")] : [];
                return new VolumeTermSearchResult(matches, matches.Length, covered, uncovered, TimeSpan.FromMilliseconds(state.Task.MetadataDelayMs));
            }
            catch (OperationCanceledException) { state.MetadataCancelled = true; throw; }
        }
    }

    private sealed class ReplaySearcher(ReplayState state) : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            state.ContentStarted = true;
            await Task.Yield();
            foreach (var entry in state.Task.Content)
            {
                if (token.IsCancellationRequested) { state.ContentCancelled = true; token.ThrowIfCancellationRequested(); }
                yield return new Hit(state.Full(entry), 1, entry.Text ?? state.Task.Query, [], Score: entry.Score, Route: HitRoute.Indexed);
            }
        }
    }

    private sealed class EmptySearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken token) { await Task.CompletedTask; yield break; }
    }
}
