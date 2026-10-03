global using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Gui;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class Program
{
    private static readonly List<object> Rows = new();
    private static readonly Stopwatch Global = Stopwatch.StartNew();
    private static string _output = "";
    private static int _folders = 20;
    private static bool _verify;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = args[0];
        _folders = args.Length > 3 ? int.Parse(args[3]) : 20;
        _verify = args.Contains("--verify");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ModernWpf.ThemeResources());
        app.Resources.MergedDictionaries.Add(new ModernWpf.Controls.FluentControlsResources());
        app.Resources.MergedDictionaries.Add(Dictionary("Styles/AppStyles.xaml"));
        app.Resources.MergedDictionaries.Add(Dictionary("Themes/Atlas.xaml"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        int exitCode = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                int[] sizes = args.Length > 1 ? args[1] == "quick" ? [] : args[1].Split(',').Select(int.Parse).ToArray() : [500, 2000];
                foreach (int count in sizes)
                {
                    foreach (string mode in args.Length > 2 ? args[2].Split(',') : new[] { "default", "flat", "folder" })
                    {
                        for (int repeat = 0; repeat < (args.Length > 4 ? int.Parse(args[4]) : 4); repeat++)
                            await RunCase(count, mode, repeat);
                    }
                }
                for (int repeat = 0; repeat < 4; repeat++)
                    await RunQuickCase(repeat);
                File.WriteAllText(_output, JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount, os = Environment.OSVersion.ToString(), wallSeconds = Global.Elapsed.TotalSeconds, rows = Rows }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(_output + ".error.txt", ex.ToString());
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally { app.Shutdown(); }
        });
        app.Run();
        return exitCode;
    }

    private static ResourceDictionary Dictionary(string path) => new() { Source = new Uri($"pack://application:,,,/FileSearch.Gui;component/{path}") };

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task RunCase(int count, string mode, int repeat)
    {
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher();
        using var search = new SearchViewModel(new SyntheticSearcher(count), new ExtractorRegistry(Array.Empty<ITextExtractor>()), new QueryFactory(), new FakePreviewService(), launcher, settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        if (mode != "default")
            search.SelectedGroupOption = search.ResultGroupOptions.Single(x => x.Value == (mode == "flat" ? ResultGroupMode.File : ResultGroupMode.Folder));
        var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher, new InlineDispatcher(), search, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService());
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 860, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show();
        await Settle(window);
        var list = (ListBox)window.FindName("ResultsList");
        search.SearchPath = Path.GetTempPath();
        search.QueryText = "needle";
        search.UseIndex = false;
        await Settle(window);
        double maxGap = 0;
        string phase = "search", maxGapPhase = "search";
        var gaps = new List<double>();
        long lastTick = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); double gap = Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds; gaps.Add(gap); if (gap > maxGap) { maxGap = gap; maxGapPhase = phase; } lastTick = now; };
        timer.Start();
        long memoryBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        await search.SearchCommand.ExecuteAsync(null);
        await Settle(window);
        double displayMs = sw.Elapsed.TotalMilliseconds;
        double displayGap = maxGap;
        if (search.Files.Count != count || search.TotalHits != count * 3 || !status.Text.Contains("done", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected results: {search.Files.Count}, hits: {search.TotalHits}, status: {status.Text}");
        int realized = Descendants(list).OfType<ListBoxItem>().Count();
        double allocMb = (GC.GetTotalAllocatedBytes() - memoryBefore) / 1048576d;
        var typing = new List<double>();
        for (int i = 1; i <= 6; i++)
        {
            phase = "typing";
            sw.Restart();
            search.QueryText = "needle"[..i];
            await Settle(window);
            typing.Add(sw.Elapsed.TotalMilliseconds);
        }
        search.QueryText = "needle";
        await Settle(window);
        var filter = new List<double>();
        var refresh = new List<double>();
        var clear = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            phase = "filter";
            search.RefinementQuery = "even";
            await Task.Delay(250);
            await Settle(window);
            if (search.FilesVisible != (count + 1) / 2) throw new InvalidOperationException("Filter count mismatch");
            // Measure an already-filtered refresh separately from the fixed 200ms debounce.
            sw.Restart();
            search.FilesView.Refresh();
            double refreshMs = sw.Elapsed.TotalMilliseconds;
            await Settle(window);
            filter.Add(sw.Elapsed.TotalMilliseconds);
            refresh.Add(refreshMs);
            sw.Restart();
            search.RefinementQuery = "";
            await Settle(window);
            clear.Add(sw.Elapsed.TotalMilliseconds);
        }
        int scrolledCards = realized;
        int automationCards = 0;
        if (_verify)
        {
            if (realized > 50 || !ScrollViewer.GetCanContentScroll(list))
                throw new InvalidOperationException($"Virtualization failed: {realized} cards, content scrolling {ScrollViewer.GetCanContentScroll(list)}");
            var expander = Descendants(list).OfType<Expander>().FirstOrDefault();
            string? groupName = (expander?.DataContext as CollectionViewGroup)?.Name?.ToString();
            if (expander is not null)
            {
                phase = "collapse";
                if (expander.Tag is null) throw new InvalidOperationException("Group has no persistent state");
                ToggleGroup(expander);
                if (expander.IsExpanded || expander.Tag.GetType().GetProperty("IsExpanded")!.GetValue(expander.Tag) is not false)
                    throw new InvalidOperationException("Header toggle did not update the bound group state");
                await Settle(window);
            }
            phase = "scroll-end";
            list.ScrollIntoView(search.Files[^1]);
            await Settle(window);
            scrolledCards = Descendants(list).OfType<ListBoxItem>().Count();
            phase = "scroll-top";
            Descendants(list).OfType<ScrollViewer>().First().ScrollToTop();
            await Settle(window);
            if (groupName is not null)
            {
                expander = Descendants(list).OfType<Expander>().First(x => (x.DataContext as CollectionViewGroup)?.Name?.ToString() == groupName);
                if (expander.IsExpanded) throw new InvalidOperationException("Recycling lost the collapsed group state");
                search.RefinementQuery = "even";
                phase = "collapsed-filter";
                await Task.Delay(250);
                await Settle(window);
                search.RefinementQuery = "";
                phase = "collapsed-clear";
                await Settle(window);
                expander = Descendants(list).OfType<Expander>().First(x => (x.DataContext as CollectionViewGroup)?.Name?.ToString() == groupName);
                if (expander.IsExpanded) throw new InvalidOperationException("Filtering lost the collapsed group state");
                ToggleGroup(expander);
                phase = "expand";
                await Settle(window);
            }
            if (scrolledCards > 50) throw new InvalidOperationException($"Scrolling realized {scrolledCards} cards");
            phase = "automation";
            var peer = UIElementAutomationPeer.CreatePeerForElement(list)!;
            var children = peer.GetChildren();
            automationCards = children?.Count ?? 0;
            if (automationCards == 0 || automationCards > 50 || children!.Any(x => string.IsNullOrWhiteSpace(x.GetName())))
                throw new InvalidOperationException("Realized automation cards need accessible names");
            list.SelectedItem = list.Items[0];
            if (!ReferenceEquals(list.SelectedItem, search.SelectedFile))
                throw new InvalidOperationException("Selection binding failed");
            await Settle(window);
        }
        timer.Stop();
        var orderedGaps = gaps.Order().ToArray();
        double p95Gap = orderedGaps.Length == 0 ? 0 : orderedGaps[(int)Math.Ceiling(orderedGaps.Length * .95) - 1];
        var row = new { count, folders = _folders, mode, actualGrouping = search.SelectedGroupOption?.Value.ToString(), repeat, warmup = repeat == 0, displayMs, maxDisplayDispatcherGapMs = displayGap, maxDispatcherGapMs = maxGap, maxGapPhase, p95DispatcherGapMs = p95Gap, dispatcherGapsMs = gaps, realizedCards = realized, scrolledCards, automationCards, allocatedMb = allocMb, queryTypingMs = typing, filterRefreshAndLayoutMs = filter, filterRefreshMs = refresh, clearFilterAndLayoutMs = clear, windowWidth = window.ActualWidth, windowHeight = window.ActualHeight, listWidth = list.ActualWidth, listHeight = list.ActualHeight };
        Rows.Add(row);
        File.WriteAllText(_output + ".partial.json", JsonSerializer.Serialize(Rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{count} {mode} #{repeat}: display={displayMs:F1}ms gap={maxGap:F1}ms cards={realized} filter={filter.Average():F1}ms clear={clear.Average():F1}ms");
        window.Close();
        index.Dispose();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void ToggleGroup(Expander expander)
    {
        var toggle = Descendants(expander).OfType<System.Windows.Controls.Primitives.ToggleButton>().First();
        var peer = UIElementAutomationPeer.CreatePeerForElement(toggle)!;
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
    }

    private static async Task RunQuickCase(int repeat)
    {
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher();
        using var search = new SearchViewModel(new SyntheticSearcher(0), new ExtractorRegistry([]), new QueryFactory(), new FakePreviewService(), launcher, settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        var backend = new ProbeVolumeIndex();
        using var quick = new QuickSearchViewModel(new SyntheticSearcher(0), new QueryFactory(), new FakePreviewService(), launcher, settings, search, new FakeFolderPicker(), volumeNameIndex: backend);
        quick.SelectedScope = quick.ScopeOptions.Single(x => x.Value == QuickSearchScopeKind.EntireMachineMetadata);
        var window = new QuickSearchWindow { DataContext = quick, Width = 900, Height = 650, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show();
        await Settle(window);
        var started = Stopwatch.StartNew();
        quick.SearchText = "needle";
        while (quick.Results.Count != 80 || quick.IsSearching)
        {
            if (started.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(quick.StatusText);
            await Task.Delay(5);
        }
        await Settle(window);
        double backendToDisplayMs = Stopwatch.GetElapsedTime(backend.CompletedTimestamp).TotalMilliseconds;
        int realized = Descendants((ListBox)window.FindName("ResultsList")).OfType<ListBoxItem>().Count();
        if (_verify && (!quick.Results[0].IsDirectory || quick.Results.Any(x => x.Hits.Single().Route != HitRoute.Indexed)))
            throw new InvalidOperationException("Quick Search lost indexed metadata or directory classification");
        Rows.Add(new { mode = "quick", count = 80, repeat, warmup = repeat == 0, backendToDisplayMs, realizedCards = realized });
        Console.WriteLine($"quick #{repeat}: backendToDisplay={backendToDisplayMs:F1}ms cards={realized}");
        window.Close();
    }

    private sealed class ProbeVolumeIndex : IVolumeNameIndex
    {
        public long CompletedTimestamp;
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
            await Task.Delay(10, token).ConfigureAwait(false);
            var matches = Enumerable.Range(0, 80).Select(i => new VolumeTermMatch($@"C:\FileSearchPerfSynthetic\needle{i:000}.txt", i == 0, 1500 - i)).ToArray();
            CompletedTimestamp = Stopwatch.GetTimestamp();
            return new VolumeTermSearchResult(matches, matches.Length, request.ScopeRoots ?? [], [], TimeSpan.FromMilliseconds(10));
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class SyntheticSearcher(int count) : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (int i = 0; i < count; i++)
                for (int line = 1; line <= 3; line++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new Hit($@"C:\FileSearchPerfSynthetic\folder{i % _folders:00}\file{i:000000}.cs", line, $"var needle = {(i % 2 == 0 ? "even" : "odd")}; // deterministic match {line}", [new MatchSpan(4, 6)], SizeBytes: 2048, ModifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Route: HitRoute.Indexed);
                }
        }
    }
}
