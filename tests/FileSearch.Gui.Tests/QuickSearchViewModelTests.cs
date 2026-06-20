using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
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

    private static void RunWithPump(
        Action<PumpingSynchronizationContext, QuickSearchViewModel, FakeSettingsService> body,
        ISearcher? quickSearcher = null,
        FakeFolderPicker? folderPicker = null)
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
            var mainSearch = new SearchViewModel(
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
            var vm = new QuickSearchViewModel(
                quickSearcher ?? new EmptySearcher(),
                new QueryFactory(),
                new FakePreviewService(),
                new FakeFileLauncher(),
                settings,
                mainSearch,
                folderPicker);

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
