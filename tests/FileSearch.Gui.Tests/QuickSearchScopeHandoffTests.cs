using System.Runtime.CompilerServices;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class QuickSearchScopeHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchQuickScope-" + Guid.NewGuid().ToString("N"));

    public QuickSearchScopeHandoffTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EmptySelectedScopeDoesNotBroadenToOtherRegisteredRoots()
    {
        string root = Folder("registered");
        File.WriteAllText(Path.Combine(root, "needle.txt"), "needle");
        Run(root, (pump, main, quick, settings, searcher) =>
        {
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.SelectedIndexedLocations);
            main.SelectedIndexedRoots.Clear();
            Assert.Same(main.SelectedIndexedRoots, quick.SelectedIndexedRoots);
            Assert.Same(main.LocationSelections, quick.LocationSelections);
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.StatusText != "Type to search" && !quick.IsSearching, TimeSpan.FromSeconds(5));
            Assert.Empty(searcher.Requests);
            Assert.Empty(quick.Results);
            Assert.Contains("select", quick.LocationScopeNotice, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedAndOfflineSelectionsRemainExplicitAndNeverSearchOtherLocations(bool registered)
    {
        string root = Folder("registered");
        string offline = Path.Combine(_root, "offline");
        File.WriteAllText(Path.Combine(root, "needle.txt"), "needle");
        Run(root, (pump, main, quick, settings, searcher) =>
        {
            if (registered)
            {
                settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = offline });
                main.RefreshLocationSelections();
            }
            main.SelectedIndexedRoots.Add(offline);
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.SelectedIndexedLocations);
            quick.SearchText = "needle";
            pump.PumpUntil(() => quick.StatusText != "Type to search" && !quick.IsSearching, TimeSpan.FromSeconds(5));
            Assert.Equal(offline, Assert.Single(quick.SelectedIndexedRoots));
            Assert.Empty(searcher.Requests);
            Assert.Empty(quick.Results);
            Assert.Contains(offline, quick.LocationScopeNotice, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(registered ? "Offline" : "No longer indexed", quick.LocationScopeNotice, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SharedSelectionChangesRestartQuickSearchWithoutRetainingOldResults()
    {
        string first = Folder("first"), second = Folder("second");
        var delayed = new DeferredSearcher(first, second);
        Run(first, (pump, main, quick, settings, _) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = second });
            main.RefreshLocationSelections();
            main.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [first]);
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.SelectedIndexedLocations);
            quick.SearchText = "needle";
            pump.PumpUntil(() => delayed.OldStarted, TimeSpan.FromSeconds(5));
            main.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [second]);
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(Path.Combine(second, "new.txt"), Assert.Single(quick.Results).FullPath);
            delayed.ReleaseOld.TrySetResult();
            pump.PumpUntil(() => delayed.OldFinished, TimeSpan.FromSeconds(5));
            Assert.Equal(Path.Combine(second, "new.txt"), Assert.Single(quick.Results).FullPath);
        }, quickSearcher: delayed);
    }

    [Fact]
    public void SameLineAndTextFromDifferentMailMessagesRemainDistinctInQuickSearch()
    {
        string root = Folder("mail"), path = Path.Combine(root, "mail.pst");
        File.WriteAllText(path, "fixture placeholder");
        var first = new MailMessageMetadata("first", "Same subject", "sender", "recipient", "", null, "Inbox", "stamp");
        var second = first with { Id = "second" };
        Hit[] hits =
        [
            new(path, 1, "needle", [], Anchor: new SourceAnchor(SourceAnchorKind.Email, "message", MailMessage: first)),
            new(path, 1, "needle", [], Locator: new SourceLocator(MailMessage: second)),
        ];
        Run(root, (pump, main, quick, settings, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "first", "second" }, quick.Results.Select(file => file.MailMessage!.Id).Order(StringComparer.Ordinal));
        }, quickSearcher: new RecordingSearcher(_ => hits));
    }

    [Fact]
    public void HandoffPreservesSelectedRootsLiteralMatchingAndExactMailIdentity()
    {
        string firstRoot = Folder("first"), selectedRoot = Folder("selected");
        string path = Path.Combine(selectedRoot, "mail.pst");
        File.WriteAllText(path, "fixture placeholder");
        var first = new MailMessageMetadata("first", "Same subject", "sender", "recipient", "", null, "Inbox", "stamp");
        var second = first with { Id = "second" };
        Hit[] hits =
        [
            new(path, 1, "john OR smith", [], Anchor: new SourceAnchor(SourceAnchorKind.Email, "message", MailMessage: first)),
            new(path, 1, "john OR smith", [], Snippet: new SearchSnippet("john OR smith", locator: new SourceLocator(MailMessage: second), contentUnitId: 42)),
        ];
        var mainSearcher = new RecordingSearcher(request => request.SearchTarget == SearchTarget.Content ? hits : []);
        Run(firstRoot, (pump, main, quick, settings, _) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = selectedRoot });
            main.RefreshLocationSelections();
            main.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [selectedRoot]);
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.SelectedIndexedLocations);
            quick.SearchText = "john OR smith";
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 2, TimeSpan.FromSeconds(5));
            var selected = quick.Results.Single(file => file.MailMessage?.Id == "second");
            quick.SelectedResult = selected;
            int shown = 0, hidden = 0;
            quick.RequestOpenMainWindow += (_, _) => shown++;
            quick.RequestHide += (_, _) => hidden++;
            var handoff = quick.OpenFullSearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => handoff.IsCompleted, TimeSpan.FromSeconds(5));
            handoff.GetAwaiter().GetResult();
            Assert.Equal(1, shown); Assert.Equal(1, hidden);
            Assert.Equal("john OR smith", main.QueryText);
            Assert.Equal(QueryMode.PlainText, main.SearchMode);
            Assert.True(main.UseQuickSearchMatching);
            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, main.SelectedLocationScope);
            Assert.Equal(selectedRoot, Assert.Single(main.SelectedIndexedRoots));
            Assert.All(mainSearcher.Requests, request => Assert.Equal(selectedRoot, Assert.Single(request.Roots)));
            var content = Assert.Single(mainSearcher.Requests, request => request.SearchTarget == SearchTarget.Content);
            Assert.True(content.Expression.IsMatch("john OR smith"));
            Assert.False(content.Expression.IsMatch("john smith"));
            var names = Assert.Single(mainSearcher.Requests, request => request.SearchTarget == SearchTarget.FileNames);
            Assert.True(names.Expression.IsMatch(@"C:\john\OR\smith.txt"));
            Assert.False(names.Expression.IsMatch(@"C:\john\smith.txt"));
            Assert.Equal(selected.ResultKey, main.SelectedFile?.ResultKey);
            Assert.Equal(second, main.CurrentPreviewHit?.MailMessage);
            Assert.Equal(42, main.CurrentPreviewHit?.Snippet?.ContentUnitId);
        }, quickSearcher: new RecordingSearcher(_ => hits), mainSearcher: mainSearcher);
    }

    [Fact]
    public void FolderMetadataOnlyHandoffKeepsFolderAndDoesNotEnableContent()
    {
        string root = Folder("folder");
        var mainSearcher = new RecordingSearcher();
        Run(root, (pump, main, quick, settings, _) =>
        {
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.CurrentFolder);
            quick.QuickFolderPath = root;
            quick.IncludeContentMatches = false;
            quick.SearchText = "literal words";
            var handoff = quick.OpenFullSearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => handoff.IsCompleted, TimeSpan.FromSeconds(5));
            handoff.GetAwaiter().GetResult();
            Assert.Equal(LocationScopeKind.SelectedFolder, main.SelectedLocationScope);
            Assert.Equal(root, main.SearchPath);
            Assert.Equal(QueryMode.PlainText, main.SearchMode);
            Assert.DoesNotContain(mainSearcher.Requests, request => request.SearchTarget == SearchTarget.Content);
            Assert.Contains(mainSearcher.Requests, request => request.SearchTarget == SearchTarget.FileNames);
            Assert.Contains(mainSearcher.Requests, request => request.SearchTarget == SearchTarget.FolderNames);
        }, mainSearcher: mainSearcher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandoffKeepsExactArchiveMemberOrDatabaseCellWhenFullSearchOrderChanges(bool database)
    {
        string root = Folder("locations"), path = Path.Combine(root, database ? "records.db" : "documents.zip");
        File.WriteAllText(path, "fixture placeholder");
        var firstLocation = database
            ? new SourceLocator(Database: new DatabaseLocation(DatabaseKind.Sqlite, "Records", 5, "Note", "5", "source-stamp"))
            : new SourceLocator(ArchiveMember: "folder/first.txt", StartLine: 12);
        var secondLocation = database
            ? new SourceLocator(Database: new DatabaseLocation(DatabaseKind.Sqlite, "Records", 9, "Note", "9", "source-stamp"))
            : new SourceLocator(ArchiveMember: "folder/second.txt", StartLine: 12);
        var preferred = new Hit(path, 1, "needle", [], Locator: firstLocation);
        var other = new Hit(path, 1, "needle", [], Locator: secondLocation);
        var mainSearcher = new RecordingSearcher(request => request.SearchTarget == SearchTarget.Content ? [other, preferred] : []);
        Run(root, (pump, main, quick, settings, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(2, Assert.Single(quick.Results).Hits.Count);
            quick.SelectedResult = quick.Results[0];
            var handoff = quick.OpenFullSearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => handoff.IsCompleted, TimeSpan.FromSeconds(5));
            handoff.GetAwaiter().GetResult();
            Assert.Equal(firstLocation, main.CurrentPreviewHit?.Locator);
            Assert.Equal(path, main.SelectedFile?.FullPath);
        }, quickSearcher: new RecordingSearcher(_ => [preferred, other]), mainSearcher: mainSearcher);
    }

    [Fact]
    public void EntireMachineAndBusyMainHandoffsAreExplicitlyUnavailable()
    {
        string root = Folder("folder");
        Run(root, (pump, main, quick, settings, _) =>
        {
            main.QueryText = "keep main query";
            quick.SearchText = "incoming";
            quick.SelectedScope = Option(quick, QuickSearchScopeKind.EntireMachineMetadata);
            Assert.False(quick.OpenFullSearchCommand.CanExecute(null));
            Assert.Contains("Entire machine", quick.FullSearchHandoffSummary, StringComparison.Ordinal);
            quick.OpenFullSearchCommand.Execute(null);
            Assert.Equal("keep main query", main.QueryText);

            quick.SelectedScope = Option(quick, QuickSearchScopeKind.CurrentFolder);
            main.IsSearching = true;
            Assert.False(quick.OpenFullSearchCommand.CanExecute(null));
            quick.OpenFullSearchCommand.Execute(null);
            Assert.Equal("keep main query", main.QueryText);
            main.IsSearching = false;
            main.IsReplacementBusy = true;
            Assert.False(quick.OpenFullSearchCommand.CanExecute(null));
            main.IsReplacementBusy = false;
        });
    }

    [Fact]
    public void OverlappingRegisteredRootsDoNotDuplicateTheSameContentResult()
    {
        string parent = Folder("parent"), child = Folder(Path.Combine("parent", "child"));
        string path = Path.Combine(child, "document.txt");
        File.WriteAllText(path, "needle");
        var hit = new Hit(path, 1, "needle", [], ContentUnitId: 10);
        var searcher = new RecordingSearcher(_ => [hit, hit with { }]);
        Run(parent, (pump, main, quick, settings, _) =>
        {
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = child });
            main.RefreshLocationSelections();
            quick.SearchText = "needle";
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(2, Assert.Single(searcher.Requests).Roots.Count);
            Assert.Equal(path, Assert.Single(quick.Results).FullPath);
            Assert.Single(quick.Results[0].Hits);
        }, quickSearcher: searcher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentCoverageLabelsReflectLiveFallbackAndMixedRoutes(bool mixed)
    {
        string root = Folder("coverage"), path = Path.Combine(root, "document.txt");
        File.WriteAllText(path, "needle");
        var live = new Hit(path, 1, "needle", [], Route: HitRoute.Live);
        Hit[] hits = mixed ? [live, live with { Route = HitRoute.Indexed }] : [live];
        Run(root, (pump, main, quick, settings, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => !quick.IsSearching && quick.Results.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(mixed ? "Indexed and live content matches" : "Live content fallback matches", quick.StageText);
            Assert.Single(quick.Results[0].Hits);
        }, quickSearcher: new RecordingSearcher(_ => hits));
    }

    [Fact]
    public void ZeroContentMatchesRetainTheActualFallbackExplanation()
    {
        string root = Folder("no-hits");
        const string explanation = "Index unavailable; searching only the selected roots live.";
        var searcher = new RecordingSearcher(request => { request.Status?.Invoke(explanation); return []; });
        Run(root, (pump, main, quick, settings, _) =>
        {
            quick.SearchText = "needle";
            pump.PumpUntil(() => searcher.Requests.Count > 0 && !quick.IsSearching, TimeSpan.FromSeconds(5));
            Assert.Empty(quick.Results);
            Assert.StartsWith("No matches", quick.StatusText, StringComparison.Ordinal);
            Assert.Contains(explanation, quick.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain(explanation, quick.LocationScopeNotice, StringComparison.Ordinal);
        }, quickSearcher: searcher);
    }

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static QuickSearchScopeOption Option(QuickSearchViewModel quick, QuickSearchScopeKind kind) =>
        quick.ScopeOptions.Single(option => option.Value == kind);

    private static void Run(string root,
        Action<PumpingSynchronizationContext, SearchViewModel, QuickSearchViewModel, FakeSettingsService, RecordingSearcher> body,
        ISearcher? quickSearcher = null, RecordingSearcher? mainSearcher = null)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.IndexedLocations.Add(new IndexedLocationSettings { Root = root });
            settings.Current.QuickSearchFolderPath = root;
            settings.Current.QuickSearchDefaultScope = settings.Current.QuickSearchLastScope = QuickSearchScopeKind.AllIndexedLocations;
            settings.Current.QuickSearchIncludeContent = true;
            var status = new StatusBarViewModel();
            var appSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, appSettings, status);
            var recorder = quickSearcher as RecordingSearcher ?? new RecordingSearcher();
            using var main = new SearchViewModel(mainSearcher ?? new RecordingSearcher(), new ExtractorRegistry([]), new QueryFactory(),
                new FakePreviewService(), new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status)
            { SearchPath = root };
            using var quick = new QuickSearchViewModel(quickSearcher ?? recorder, new QueryFactory(), new FakePreviewService(),
                new FakeFileLauncher(), settings, main, new FakeFolderPicker());
            body(pump, main, quick, settings, recorder);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class RecordingSearcher(Func<SearchRequest, IReadOnlyList<Hit>>? hits = null) : ISearcher
    {
        public List<SearchRequest> Requests { get; } = [];
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (var hit in hits?.Invoke(request) ?? [])
            {
                token.ThrowIfCancellationRequested();
                yield return hit;
            }
        }
    }

    private sealed class DeferredSearcher(string first, string second) : ISearcher
    {
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OldStarted { get; private set; }
        public bool OldFinished { get; private set; }
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            if (request.Roots.Contains(first, StringComparer.OrdinalIgnoreCase))
            {
                OldStarted = true;
                try { await ReleaseOld.Task; yield return new Hit(Path.Combine(first, "old.txt"), 1, "needle", []); }
                finally { OldFinished = true; }
            }
            else yield return new Hit(Path.Combine(second, "new.txt"), 1, "needle", []);
        }
    }
}
