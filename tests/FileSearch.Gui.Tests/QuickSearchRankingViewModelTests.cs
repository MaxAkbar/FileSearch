using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class QuickSearchRankingViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.QuickRankingTests", Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public QuickSearchRankingViewModelTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LaterExactMetadataOutranksHighScoreContentAndUpdatesAutomaticSelection()
    {
        string content = FileAt("general.txt"), exact = FileAt("needle.txt");
        var lateMetadata = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new ControlledVolumeIndex(_ => lateMetadata.Task);
        WithQuick(new FixedSearcher(new Hit(content, 1, "needle", [], Score: 99_999)), volume, (pump, quick, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.Results.Count == 1 && volume.Requests.Count == 1, Timeout);
            var initial = Assert.Single(quick.Results);
            Assert.Same(initial, quick.SelectedResult);
            lateMetadata.SetResult(Matches(exact));
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 2, Timeout);
            Assert.Equal(exact, quick.Results[0].FullPath);
            Assert.Same(quick.Results[0], quick.SelectedResult);
            Assert.Contains(quick.Results, result => ReferenceEquals(result, initial));
        });
    }

    [Fact]
    public void ExplicitSelectionAndPreviewKeepTheirIdentityWhenExactMetadataArrivesAboveThem()
    {
        string first = FileAt("ordinary-first.txt"), second = FileAt("ordinary-second.txt"), exact = FileAt("needle.txt");
        var lateMetadata = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new ControlledVolumeIndex(_ => lateMetadata.Task);
        WithQuick(new FixedSearcher(new Hit(first, 1, "needle", [], Score: 99_999), new Hit(second, 1, "needle", [], Score: 50_000)),
            volume, (pump, quick, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.Results.Count == 2, Timeout);
            var chosen = quick.Results.Single(result => result.FullPath == second);
            quick.SelectedResult = chosen;
            quick.MarkSelectionExplicit();
            var preview = quick.PreviewResultCommand.ExecuteAsync(null);
            pump.PumpUntil(() => preview.IsCompleted && quick.PreviewContent.Contains("ordinary-second.txt", StringComparison.Ordinal), Timeout);
            string content = quick.PreviewContent;
            lateMetadata.SetResult(Matches(exact));
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 3, Timeout);
            Assert.Equal(exact, quick.Results[0].FullPath);
            Assert.Same(chosen, quick.SelectedResult);
            Assert.Same(chosen, quick.Results.Single(result => result.ResultKey == chosen.ResultKey));
            Assert.Equal(content, quick.PreviewContent);
            Assert.True(quick.IsPreviewVisible);
        }, new FilenamePreviewService());
    }

    [Fact]
    public void NewQuerySynchronouslyClearsOldResultsAndDisablesSelectedResultActions()
    {
        string old = FileAt("old.txt");
        var launcher = new FakeFileLauncher();
        WithQuick(new FixedSearcher(new Hit(old, 1, "old", [])), new ControlledVolumeIndex(_ => Task.FromResult(Matches())),
            (pump, quick, _) =>
        {
            quick.SearchText = "old";
            pump.PumpUntil(() => quick.Results.Count == 1 && !quick.IsSearching, Timeout);
            var oldResult = Assert.Single(quick.Results);
            Assert.True(quick.OpenResultCommand.CanExecute(null));
            quick.SearchText = "new";
            // No pumping: even the debounce interval must have no actionable old result.
            Assert.Empty(quick.Results);
            Assert.Null(quick.SelectedResult);
            Assert.False(quick.OpenResultCommand.CanExecute(null));
            Assert.False(quick.RevealResultCommand.CanExecute(null));
            Assert.False(quick.CopyResultPathCommand.CanExecute(null));
            Assert.False(quick.PreviewResultCommand.CanExecute(null));
            Assert.False(quick.OpenResultCommand.CanExecute(oldResult));
            Assert.False(quick.RevealResultCommand.CanExecute(oldResult));
            Assert.False(quick.CopyResultPathCommand.CanExecute(oldResult));
            Assert.False(quick.PinResultCommand.CanExecute(oldResult));
            Assert.False(quick.PreviewResultCommand.CanExecute(oldResult));
            var staleOpen = quick.OpenResultCommand.ExecuteAsync(oldResult);
            pump.PumpUntil(() => staleOpen.IsCompleted, Timeout);
            Assert.Null(launcher.LastOpenedPath);
            Assert.Null(launcher.LastOpenedAtPath);
        }, launcher: launcher);
    }

    [Fact]
    public void UnpinningAnActiveTiedResultReordersItWithoutLosingExplicitSelection()
    {
        string first = FileAt("a-needle.txt"), pinned = FileAt("b-needle.txt");
        var ranking = QuickSearchNameRanking.Compile("needle");
        Assert.Equal(ranking.Score(first), ranking.Score(pinned));
        WithQuick(new FixedSearcher(), new ControlledVolumeIndex(_ => Task.FromResult(Matches(first, pinned))),
            (pump, quick, settings) =>
        {
            settings.Current.QuickSearchPinnedPaths.Add(pinned);
            quick.PrepareForShow();
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.Results.Count == 2 && !quick.IsSearching, Timeout);
            var chosen = quick.Results[0];
            Assert.Equal(pinned, chosen.FullPath);
            Assert.True(chosen.IsPinned);
            quick.SelectedResult = chosen;
            quick.MarkSelectionExplicit();
            int hides = 0;
            quick.RequestHide += (_, _) => hides++;
            quick.PinResultCommand.Execute(chosen);
            Assert.Equal(first, quick.Results[0].FullPath);
            Assert.Same(chosen, quick.SelectedResult);
            Assert.False(chosen.IsPinned);
            Assert.DoesNotContain(pinned, settings.Current.QuickSearchPinnedPaths);
            Assert.Equal(0, hides);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentBudgetStopsOnlyContentAndStillAdmitsDelayedExactMetadata(bool singleContentFile)
    {
        string[] files = Enumerable.Range(0, singleContentFile ? 1 : 80).Select(index => FileAt($"ordinary-{index:00}.txt")).ToArray();
        string exact = FileAt("needle.txt");
        var content = new EndlessContentSearcher(files);
        var lateMetadata = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new ControlledVolumeIndex(_ => lateMetadata.Task);
        WithQuick(content, volume, (pump, quick, _) =>
        {
            int maximumVisibleFiles = 0;
            quick.Results.CollectionChanged += (_, _) => maximumVisibleFiles = Math.Max(maximumVisibleFiles, quick.Results.Count);
            quick.SearchText = "needle";
            pump.PumpUntil(() => content.Stopped.Task.IsCompleted && volume.Requests.Count == 1, Timeout);
            Assert.True(quick.IsSearching);
            Assert.Equal(singleContentFile ? 1 : 80, quick.Results.Count);
            Assert.Equal(singleContentFile ? 500 : 80, quick.Results.Sum(result => result.Hits.Count(hit => hit.Kind == HitKind.Content)));
            Assert.InRange(content.Produced, 1, (singleContentFile ? 500 : 80) + 512 + 1);
            lateMetadata.SetResult(Matches(exact));
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Any(result => result.FullPath == exact), Timeout);
            Assert.Equal(exact, quick.Results[0].FullPath);
            Assert.InRange(quick.Results.Count, 1, 80);
            Assert.InRange(maximumVisibleFiles, 1, 80);
            Assert.InRange(quick.Results.Sum(result => result.Hits.Count(hit => hit.Kind == HitKind.Content)), 0, 500);
            Assert.InRange(content.Produced, 1, (singleContentFile ? 500 : 80) + 512 + 1);
        });
    }

    [Fact]
    public void OverlappingLiveRootsRetainEightyDistinctRankedFiles()
    {
        string nested = Path.Combine(_root, "Nested");
        string exact = FileAt("Nested/needle.txt");
        foreach (int index in Enumerable.Range(0, 119)) FileAt($"Nested/needle-extra-{index:000}.txt");
        WithQuick(new FixedSearcher(), null, (pump, quick, settings) =>
        {
            settings.Current.IndexedLocations = [new() { Root = _root, Recursive = true }, new() { Root = nested, Recursive = true }];
            quick.PrepareForShow();
            quick.IncludeContentMatches = false;
            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.AllIndexedLocations);
            int maximumVisibleFiles = 0;
            quick.Results.CollectionChanged += (_, _) => maximumVisibleFiles = Math.Max(maximumVisibleFiles, quick.Results.Count);
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.Results.Count == 80 && !quick.IsSearching, Timeout);
            Assert.Equal(80, quick.Results.Select(result => result.ResultKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(exact, quick.Results[0].FullPath);
            Assert.InRange(maximumVisibleFiles, 1, 80);
            Assert.All(quick.Results, result => Assert.True(File.Exists(result.FullPath)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataKindHitsFromTheOrdinarySearcherStillUseItsOwnBudget(bool singleFile)
    {
        string[] files = Enumerable.Range(0, singleFile ? 1 : 80).Select(index => FileAt($"ordinary-{index:00}.txt")).ToArray();
        string exact = FileAt("needle.txt");
        var ordinarySearcher = new EndlessContentSearcher(files, HitKind.Metadata);
        var lateMetadata = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new ControlledVolumeIndex(_ => lateMetadata.Task);
        WithQuick(ordinarySearcher, volume, (pump, quick, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => ordinarySearcher.Stopped.Task.IsCompleted && volume.Requests.Count == 1, Timeout);
            Assert.True(quick.IsSearching);
            Assert.Equal(singleFile ? 1 : 80, quick.Results.Count);
            Assert.Equal(singleFile ? 500 : 80, quick.Results.Sum(result => result.HitCount));
            Assert.All(quick.Results.SelectMany(result => result.Hits), hit => Assert.Equal(HitKind.Metadata, hit.Kind));
            Assert.InRange(ordinarySearcher.Produced, 1, (singleFile ? 500 : 80) + 512 + 1);
            lateMetadata.SetResult(Matches(exact));
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Any(result => result.FullPath == exact), Timeout);
            Assert.Equal(exact, quick.Results[0].FullPath);
            Assert.InRange(quick.Results.Count, 1, 80);
            Assert.InRange(quick.Results.Where(result => result.FullPath != exact).Sum(result => result.HitCount), 0, 500);
            Assert.InRange(ordinarySearcher.Produced, 1, (singleFile ? 500 : 80) + 512 + 1);
        });
    }

    [Theory]
    [InlineData("\"alpha beta\"", null)]
    [InlineData("alpha OR beta", "alpha OR beta.txt")]
    [InlineData("needel", null)]
    public void FilenameEligibilityKeepsQuotesOperatorsAndTyposLiteral(string text, string? expectedName)
    {
        FileAt("alpha beta.txt"); FileAt("alpha OR beta.txt"); FileAt("needle.txt");
        WithQuick(new FixedSearcher(), null, (pump, quick, _) =>
        {
            quick.IncludeContentMatches = false;
            quick.SearchText = text;
            pump.PumpUntil(() => !quick.IsSearching && (quick.StatusText == "No matches" || quick.Results.Count > 0), Timeout);
            if (expectedName is null) Assert.Empty(quick.Results);
            else Assert.Equal(expectedName, Assert.Single(quick.Results).FileName);
        });
    }

    [Fact]
    public void ChangingScopeIgnoresTheOldVolumeProducerEvenIfItCompletesLast()
    {
        string firstRoot = Path.Combine(_root, "A"), secondRoot = Path.Combine(_root, "B");
        Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(secondRoot);
        string oldFile = FileAt("A/needle.txt"), newFile = FileAt("B/needle.txt");
        var oldResult = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new ControlledVolumeIndex(request => request.ScopeRoots?.Count == 1 ? oldResult.Task : newResult.Task);
        WithQuick(new FixedSearcher(), volume, (pump, quick, settings) =>
        {
            settings.Current.IndexedLocations = [new() { Root = firstRoot }, new() { Root = secondRoot }];
            quick.PrepareForShow();
            quick.QuickFolderPath = firstRoot;
            quick.SearchText = "needle";
            pump.PumpUntil(() => volume.Requests.Count == 1, Timeout);
            quick.SelectedScope = quick.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.AllIndexedLocations);
            pump.PumpUntil(() => volume.Requests.Count == 2, Timeout);
            newResult.SetResult(Matches(newFile));
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 1, Timeout);
            var selected = quick.SelectedResult;
            string status = quick.StatusText;
            oldResult.SetResult(Matches(oldFile));
            var settle = Task.Delay(100);
            pump.PumpUntil(() => settle.IsCompleted, Timeout);
            Assert.Equal(newFile, Assert.Single(quick.Results).FullPath);
            Assert.Same(selected, quick.SelectedResult);
            Assert.Equal(status, quick.StatusText);
            Assert.False(quick.IsSearching);
        });
    }

    private string FileAt(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "needle fixture content");
        return path;
    }

    private void WithQuick(ISearcher source, IVolumeNameIndex? volume,
        Action<PumpingSynchronizationContext, QuickSearchViewModel, FakeSettingsService> body, IFilePreviewService? preview = null,
        FakeFileLauncher? launcher = null) => StaTestDispatcher.Run(() =>
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.QuickSearchDefaultScope = QuickSearchScopeKind.CurrentFolder;
            settings.Current.QuickSearchLastScope = QuickSearchScopeKind.CurrentFolder;
            settings.Current.QuickSearchFolderPath = _root;
            settings.Current.QuickSearchIncludeContent = true;
            var status = new StatusBarViewModel();
            var applicationSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, applicationSettings, status);
            launcher ??= new FakeFileLauncher();
            using var main = new SearchViewModel(new FixedSearcher(), new ExtractorRegistry([]), new QueryFactory(),
                new FakePreviewService(), launcher, settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
            main.SearchPath = _root;
            using var quick = new QuickSearchViewModel(source, new QueryFactory(), preview ?? new FakePreviewService(),
                launcher, settings, main, new FakeFolderPicker(), volumeNameIndex: volume);
            quick.PrepareForShow();
            body(pump, quick, settings);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static VolumeTermSearchResult Matches(params string[] paths) =>
        new(paths.Select(path => new VolumeTermMatch(path, false, 1)).ToArray(), paths.Length, [], [], TimeSpan.Zero);

    private sealed class FilenamePreviewService : IFilePreviewService
    {
        public Task<string> LoadHitsPreviewAsync(string path, IReadOnlyList<int> hits, int contextLines, CancellationToken cancellationToken) =>
            Task.FromResult(Path.GetFileName(path) + " preview");
        public Task<string> LoadFullTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult(File.ReadAllText(path));
    }

    private sealed class FixedSearcher(params Hit[] hits) : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var hit in hits) { cancellationToken.ThrowIfCancellationRequested(); yield return hit; }
        }
    }

    private sealed class EndlessContentSearcher(string[] files, HitKind kind = HitKind.Content) : ISearcher
    {
        public int Produced;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                for (int index = 0; ; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref Produced);
                    yield return new Hit(files[index % files.Length], index + 1, "needle", [], kind);
                }
            }
            finally { Stopped.TrySetResult(); }
        }
    }

    private sealed class ControlledVolumeIndex(Func<VolumeTermSearchRequest, Task<VolumeTermSearchResult>> response) : IVolumeNameIndex
    {
        public ConcurrentQueue<VolumeTermSearchRequest> Requests { get; } = new();
        public event EventHandler<VolumeIndexStatus>? StatusChanged { add { } remove { } }
        public Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken cancellationToken)
        { Requests.Enqueue(request); return response(request); }
        public IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes() => [];
        public IReadOnlyList<VolumeIndexStatus> GetStatuses() => [];
        public Task StartTrackingAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VolumeIndexStatus> BuildAsync(string root, VolumeBuildMethod method, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsReady(string path) => true;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
