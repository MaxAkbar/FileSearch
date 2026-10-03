using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class QuickSearchViewModelTests
{
    [Fact]
    public void SelectedIndexedLocationScopeUsesConfiguredRootsAndPersistsLastScope()
    {
        var root1 = CreateTempDirectory();
        var root2 = CreateTempDirectory();
        var searcher = new RecordingSearcher();

        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root1 });
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root2 });
            settings.Current.QuickSearchSelectedIndexedRoots.Add(root2);
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.SelectedIndexedLocations);

            vm.SearchText = "needle";
            pump.PumpUntil(() => searcher.Request is not null && !vm.IsSearching, TimeSpan.FromSeconds(10));

            Assert.NotNull(searcher.Request);
            Assert.Equal(root2, Assert.Single(searcher.Request.Roots));
            Assert.True(searcher.Request.UseIndex);
            Assert.Equal(QuickSearchScopeKind.SelectedIndexedLocations, settings.Current.QuickSearchLastScope);
        }, searcher);
    }

    [Fact]
    public void PinResultPersistsPathAtTopOfPinnedList()
    {
        var file = Path.Combine(CreateTempDirectory(), "Pinned.txt");
        File.WriteAllText(file, "needle");

        RunWithPump((pump, vm, settings) =>
        {
            var hideRequests = 0;
            vm.RequestHide += (_, _) => hideRequests++;
            var result = new FileResultViewModel(file, new FakeFileLauncher());
            result.AddHit(new Hit(file, 1, "needle", Array.Empty<MatchSpan>()));
            vm.SelectedResult = result;

            Assert.True(vm.PinResultCommand.CanExecute(null));
            vm.PinResultCommand.Execute(null);

            Assert.Equal(file, Assert.Single(settings.Current.QuickSearchPinnedPaths));
            Assert.True(result.IsPinned);
            Assert.Equal(1, hideRequests);
        });
    }

    [Fact]
    public void PinResultUnpinsPinnedResultWithoutHiding()
    {
        var file = Path.Combine(CreateTempDirectory(), "Pinned.txt");
        File.WriteAllText(file, "needle");

        RunWithPump((pump, vm, settings) =>
        {
            var hideRequests = 0;
            vm.RequestHide += (_, _) => hideRequests++;
            settings.Current.QuickSearchPinnedPaths.Add(file);

            vm.PrepareForShow();
            var result = Assert.Single(vm.Results);
            Assert.True(result.IsPinned);

            Assert.True(vm.PinResultCommand.CanExecute(null));
            vm.PinResultCommand.Execute(null);

            Assert.Empty(settings.Current.QuickSearchPinnedPaths);
            Assert.Empty(vm.Results);
            Assert.Equal("Unpinned result.", vm.StatusText);
            Assert.Equal(0, hideRequests);
        });
    }

    [Fact]
    public void ContentToggleCanReturnIndexedContentMatches()
    {
        var root = CreateTempDirectory();
        var file = Path.Combine(root, "document.txt");
        File.WriteAllText(file, "body text");
        var searcher = new ContentHitSearcher(new Hit(file, 7, "needle inside content", Array.Empty<MatchSpan>(), HitKind.Content));

        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root });
            settings.Current.QuickSearchIncludeContent = true;
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.AllIndexedLocations);

            vm.SearchText = "needle";
            pump.PumpUntil(() => !vm.IsSearching && vm.Results.Count == 1, TimeSpan.FromSeconds(10));

            var result = Assert.Single(vm.Results);
            Assert.Equal(file, result.FullPath);
            Assert.NotNull(searcher.Request);
            Assert.True(searcher.Request.UseIndex);
        }, searcher);
    }

    [Fact]
    public void ContentToggleOffSearchesOnlyNamesAndPaths()
    {
        var root = CreateTempDirectory();
        var file = Path.Combine(root, "needle-file.txt");
        File.WriteAllText(file, "content");
        var searcher = new RecordingSearcher();

        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root });
            settings.Current.QuickSearchIncludeContent = false;
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.AllIndexedLocations);

            vm.SearchText = "needle";
            pump.PumpUntil(() => !vm.IsSearching && vm.Results.Count == 1, TimeSpan.FromSeconds(10));

            Assert.False(vm.IncludeContentMatches);
            Assert.Null(searcher.Request);
            Assert.Equal(file, Assert.Single(vm.Results).FullPath);
        }, searcher);
    }

    [Fact]
    public void SelectedFolderScopeUsesQuickFolderPath()
    {
        var root = CreateTempDirectory();
        var searcher = new RecordingSearcher();

        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.QuickSearchFolderPath = root;
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.CurrentFolder);

            vm.SearchText = "needle";
            pump.PumpUntil(() => searcher.Request is not null && !vm.IsSearching, TimeSpan.FromSeconds(10));

            Assert.True(vm.IsFolderScope);
            Assert.Equal(root, vm.QuickFolderPath);
            Assert.NotNull(searcher.Request);
            Assert.Equal(root, Assert.Single(searcher.Request.Roots));
        }, searcher);
    }

    [Fact]
    public void ChooseQuickFolderPersistsSelectedFolder()
    {
        var root = CreateTempDirectory();
        var picker = new FakeFolderPicker { PathToReturn = root };

        RunWithPump((pump, vm, settings) =>
        {
            var events = new List<string>();
            vm.ExternalDialogOpened += (_, _) => events.Add("open");
            vm.ExternalDialogClosed += (_, _) => events.Add("close");

            vm.ChooseQuickFolderCommand.Execute(null);

            Assert.Equal("Select Quick Search folder", picker.LastTitle);
            Assert.Equal(root, vm.QuickFolderPath);
            Assert.Equal(root, settings.Current.QuickSearchFolderPath);
            Assert.Equal(new[] { "open", "close" }, events);
        }, folderPicker: picker);
    }

    [Fact]
    public void EntireMachineScopeShowsDriveNameIndexMatchesInRankOrder()
    {
        var root = CreateTempDirectory();
        var best = Path.Combine(root, "needle.txt");
        var folder = Path.Combine(root, "needle-folder");
        File.WriteAllText(best, "x");
        Directory.CreateDirectory(folder);
        var volumeIndex = new FakeVolumeNameIndex
        {
            Matches = [new VolumeTermMatch(best, false, 1500), new VolumeTermMatch(folder, true, 1200)],
        };

        RunWithPump((pump, vm, settings) =>
        {
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.EntireMachineMetadata);

            vm.SearchText = "needle";
            pump.PumpUntil(() => !vm.IsSearching && vm.Results.Count == 2, TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { best, folder }, vm.Results.Select(result => result.FullPath));
            Assert.True(vm.Results[1].IsDirectory);
            Assert.All(vm.Results.SelectMany(result => result.Hits), hit => Assert.Equal(HitRoute.Indexed, hit.Route));
            var request = Assert.Single(volumeIndex.Requests);
            Assert.Equal("needle", request.Text);
            Assert.NotNull(request.ScopeRoots);
            Assert.Equal("Drive name index matches", vm.StageText);
        }, volumeIndex: volumeIndex);
    }

    [Fact]
    public void RootsWithoutADriveIndexFallBackToTheFolderWalk()
    {
        var root = CreateTempDirectory();
        var file = Path.Combine(root, "needle-walk.txt");
        File.WriteAllText(file, "x");
        var volumeIndex = new FakeVolumeNameIndex { UncoverAll = true };

        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.QuickSearchFolderPath = root;
            settings.Current.QuickSearchIncludeContent = false;
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.CurrentFolder);

            vm.SearchText = "needle";
            pump.PumpUntil(() => !vm.IsSearching && vm.Results.Count == 1, TimeSpan.FromSeconds(10));

            Assert.Equal(file, Assert.Single(vm.Results).FullPath);
            Assert.Equal(root, Assert.Single(Assert.Single(volumeIndex.Requests).ScopeRoots!));
        }, volumeIndex: volumeIndex);
    }

    [Fact]
    public void GetShortcutReadsConfiguredQuickSearchShortcut()
    {
        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.QuickSearchShortcuts.PreviewSelectedResult = AppShortcutGesture.CtrlI;

            Assert.Equal(
                AppShortcutGesture.CtrlI,
                vm.GetShortcut(QuickSearchShortcutAction.PreviewSelectedResult));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObsoleteVolumeCompletionCannotOverwriteAnActiveQuery(bool failOldQuery)
    {
        var oldResult = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumeIndex = new FakeVolumeNameIndex
        {
            OnSearch = request => request.Text == "old" ? oldResult.Task : newResult.Task,
        };
        RunWithPump((pump, vm, settings) =>
        {
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.EntireMachineMetadata);
            vm.SearchText = "old";
            pump.PumpUntil(() => volumeIndex.Requests.Count == 1, TimeSpan.FromSeconds(10));
            vm.SearchText = "new";
            pump.PumpUntil(() => volumeIndex.Requests.Count == 2, TimeSpan.FromSeconds(10));
            if (failOldQuery)
                oldResult.SetException(new IOException("obsolete failure"));
            else
                oldResult.SetResult(VolumeResult(@"C:\synthetic\old.txt"));
            var settle = Task.Delay(150);
            pump.PumpUntil(() => settle.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.True(vm.IsSearching);
            Assert.Empty(vm.Results);
            Assert.Equal("Searching...", vm.StatusText);

            newResult.SetResult(VolumeResult(@"C:\synthetic\new.txt"));
            pump.PumpUntil(() => !vm.IsSearching, TimeSpan.FromSeconds(10));
            Assert.Equal(@"C:\synthetic\new.txt", Assert.Single(vm.Results).FullPath);
        }, volumeIndex: volumeIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingOrDismissingPreventsLateVolumeResults(bool dismiss)
    {
        var result = new TaskCompletionSource<VolumeTermSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumeIndex = new FakeVolumeNameIndex { OnSearch = _ => result.Task };
        RunWithPump((pump, vm, settings) =>
        {
            vm.PrepareForShow();
            vm.SelectedScope = vm.ScopeOptions.Single(option => option.Value == QuickSearchScopeKind.EntireMachineMetadata);
            vm.SearchText = "old";
            pump.PumpUntil(() => volumeIndex.Requests.Count == 1, TimeSpan.FromSeconds(10));
            if (dismiss) vm.Dismiss();
            else vm.SearchText = string.Empty;
            var status = vm.StatusText;
            result.SetResult(VolumeResult(@"C:\synthetic\old.txt"));
            var settle = Task.Delay(150);
            pump.PumpUntil(() => settle.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.Empty(vm.Results);
            Assert.False(vm.IsSearching);
            Assert.Equal(status, vm.StatusText);
        }, volumeIndex: volumeIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultCapsCancelABackpressuredContentProducer(bool singleFile)
    {
        var searcher = new EndlessSearcher(singleFile);
        var root = CreateTempDirectory();
        RunWithPump((pump, vm, settings) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root });
            settings.Current.QuickSearchIncludeContent = true;
            vm.PrepareForShow();
            vm.SearchText = "needle";
            pump.PumpUntil(() => searcher.Stopped.Task.IsCompleted && !vm.IsSearching, TimeSpan.FromSeconds(10));
            Assert.Equal(singleFile ? 1 : 80, vm.Results.Count);
            Assert.Equal(singleFile ? 500 : 80, vm.Results.Sum(file => file.HitCount));
            Assert.Contains("Showing first", vm.StatusText);
            Assert.InRange(searcher.Produced, 1, (singleFile ? 500 : 80) + 512 + 1);
        }, searcher, volumeIndex: new FakeVolumeNameIndex());
    }

    private static VolumeTermSearchResult VolumeResult(string path) =>
        new([new VolumeTermMatch(path, false, 1000)], 1, [@"C:\"], [], TimeSpan.Zero);

    private sealed class EndlessSearcher(bool singleFile) : ISearcher
    {
        public int Produced;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Yield();
                for (int i = 0; ; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref Produced);
                    yield return new Hit($@"C:\synthetic\file{(singleFile ? 0 : i)}.txt", i + 1, "needle", [],
                        SizeBytes: 100, ModifiedUtc: DateTime.UtcNow);
                }
            }
            finally { Stopped.TrySetResult(); }
        }
    }

    private static void RunWithPump(
        Action<PumpingSynchronizationContext, QuickSearchViewModel, FakeSettingsService> body,
        ISearcher? quickSearcher = null,
        FakeFolderPicker? folderPicker = null,
        IVolumeNameIndex? volumeIndex = null)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var status = new StatusBarViewModel();
            var settings = new FakeSettingsService();
            folderPicker ??= new FakeFolderPicker();
            var appSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, appSettings, status);
            using var mainSearch = new SearchViewModel(
                new EmptySearcher(),
                new ExtractorRegistry(Array.Empty<ITextExtractor>()),
                new QueryFactory(),
                new FakePreviewService(),
                new FakeFileLauncher(),
                settings,
                new FakeFileTypeOptionsStore(),
                new FakeFolderPicker(),
                history,
                status);
            using var vm = new QuickSearchViewModel(
                quickSearcher ?? new EmptySearcher(),
                new QueryFactory(),
                new FakePreviewService(),
                new FakeFileLauncher(),
                settings,
                mainSearch,
                folderPicker,
                volumeNameIndex: volumeIndex);

            body(pump, vm, settings);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "filesearch-quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EmptySearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class RecordingSearcher : ISearcher
    {
        public SearchRequest? Request { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FakeVolumeNameIndex : IVolumeNameIndex
    {
        public List<VolumeTermMatch> Matches { get; init; } = new();

        public bool UncoverAll { get; init; }

        public System.Collections.Concurrent.ConcurrentQueue<VolumeTermSearchRequest> Requests { get; } = new();

        public Func<VolumeTermSearchRequest, Task<VolumeTermSearchResult>>? OnSearch { get; init; }

        public event EventHandler<VolumeIndexStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (OnSearch is not null)
                return OnSearch(request);
            var roots = request.ScopeRoots ?? Array.Empty<string>();
            return Task.FromResult(UncoverAll
                ? VolumeTermSearchResult.Empty(roots)
                : new VolumeTermSearchResult(Matches, Matches.Count, roots, Array.Empty<string>(), TimeSpan.FromMilliseconds(3)));
        }

        public IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes() => Array.Empty<VolumeIndexCandidate>();

        public IReadOnlyList<VolumeIndexStatus> GetStatuses() => Array.Empty<VolumeIndexStatus>();

        public Task StartTrackingAsync(IReadOnlyCollection<string> volumeRoots, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<VolumeIndexStatus> BuildAsync(string volumeRoot, VolumeBuildMethod method, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string volumeRoot, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool IsReady(string path) => !UncoverAll;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContentHitSearcher : ISearcher
    {
        private readonly Hit _hit;

        public ContentHitSearcher(Hit hit) => _hit = hit;

        public SearchRequest? Request { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            await Task.Yield();
            yield return _hit;
        }
    }
}
