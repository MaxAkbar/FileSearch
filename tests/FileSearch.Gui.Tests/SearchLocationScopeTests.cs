using System.Runtime.CompilerServices;
using System.Text.Json;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class SearchLocationScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchScopeFlow-" + Guid.NewGuid().ToString("N"));

    public SearchLocationScopeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(LocationScopeKind.SelectedIndexedLocations)]
    [InlineData(LocationScopeKind.AllIndexedLocations)]
    public void IndexedScopesRunWithoutAFolderAndRetainTheirDeliberateRootSet(LocationScopeKind scope)
    {
        string first = Folder("first"), second = Folder("second");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, first, second), (pump, vm, history, status, settings) =>
        {
            vm.SearchPath = "";
            vm.QueryText = "needle";
            vm.ApplyLocationScope(scope, [first]);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            var request = Assert.Single(searcher.Requests);
            Assert.Equal(scope == LocationScopeKind.SelectedIndexedLocations ? [first] : new[] { first, second }, request.Roots);
            var saved = Assert.Single(history.SavedSearches);
            Assert.Equal(scope, saved.LocationScope);
            Assert.Empty(saved.SearchPath);
            Assert.Equal([first], saved.SelectedIndexedRoots);
            Assert.False(vm.IsSearching);
            Assert.Contains("done", status.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AnEmptySelectedScopeNeverBecomesAllIndexedLocations()
    {
        string registered = Folder("registered");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, registered), (pump, vm, history, status, settings) =>
        {
            vm.SearchPath = registered;
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, []);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            Assert.Empty(searcher.Requests);
            Assert.Empty(vm.Files);
            Assert.Equal(0, vm.TotalHits);
            Assert.Contains("No indexed locations selected", vm.LocationScopeNotice);
            Assert.Contains("No indexed locations selected", status.Text);
            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, Assert.Single(history.SavedSearches).LocationScope);
            Assert.Empty(settings.Current.QuickSearchSelectedIndexedRoots);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedAndOfflineSelectionsAreReportedWithoutSearchingOtherRegisteredRoots(bool includeAvailable)
    {
        string available = Folder("available"), other = Folder("other"), removed = Folder("removed");
        string offline = Path.Combine(_root, "offline");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, available, other, offline), (pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations,
                includeAvailable ? [removed, offline, available] : [removed, offline]);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            if (includeAvailable) Assert.Equal([available], Assert.Single(searcher.Requests).Roots);
            else Assert.Empty(searcher.Requests);
            Assert.Contains("No longer indexed: " + removed, vm.LocationScopeNotice);
            Assert.Contains("Offline or inaccessible: " + offline, vm.LocationScopeNotice);
            Assert.Contains(removed, status.Text);
            Assert.Contains(offline, status.Text);
            Assert.True(Assert.Single(vm.LocationSelections, row => row.Root == removed).IsSelected);
            Assert.Contains(removed, settings.Current.QuickSearchSelectedIndexedRoots);
            Assert.DoesNotContain(searcher.Requests.SelectMany(request => request.Roots), root => root == other);
        });
    }

    [Fact]
    public void OverlappingRootsRemoveRepeatedHitsAndRetainDifferentMessageRecordMemberAndPageLocations()
    {
        string parent = Folder("parent"), child = Folder("parent", "child");
        string plain = Path.Combine(child, "plain.txt"), store = Path.Combine(child, "messages.pst");
        string database = Path.Combine(child, "records.db"), archive = Path.Combine(child, "members.zip"), pdf = Path.Combine(child, "pages.pdf");
        var firstMail = new MailMessageMetadata("message-1", "Same subject", "sender", "recipient", "", null, "Inbox", "store-stamp");
        var secondMail = firstMail with { Id = "message-2" };
        var firstRow = new DatabaseLocation(DatabaseKind.Sqlite, "Ledger", 1, "Description", "row-1", "db-stamp");
        var secondRow = firstRow with { RowNumber = 2, RowKey = "row-2" };
        Hit[] unique =
        [
            new(plain, 1, "needle", []),
            new(store, 1, "needle", [], Anchor: new SourceAnchor(SourceAnchorKind.Email, "Message", MailMessage: firstMail)),
            new(store, 1, "needle", [], Anchor: new SourceAnchor(SourceAnchorKind.Email, "Message", MailMessage: secondMail)),
            new(database, 1, "needle", [], Locator: new SourceLocator(Database: firstRow)),
            new(database, 1, "needle", [], Locator: new SourceLocator(Database: secondRow)),
            new(archive, 1, "needle", [], Locator: new SourceLocator(ArchiveMember: "one.txt")),
            new(archive, 1, "needle", [], Locator: new SourceLocator(ArchiveMember: "two.txt")),
            new(pdf, 1, "needle", [], Locator: new SourceLocator(Page: 1)),
            new(pdf, 1, "needle", [], Locator: new SourceLocator(Page: 2)),
        ];
        var searcher = new RecordingSearcher(request => request.Roots.SelectMany(_ => unique));
        Run(searcher, settings => Register(settings, parent, child), (pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, []);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            Assert.Equal([parent, child], Assert.Single(searcher.Requests).Roots);
            Assert.Equal(9, vm.TotalHits);
            Assert.Equal(6, vm.Files.Count);
            Assert.Equal(6, vm.FilesMatched);
            Assert.Equal(9, vm.Files.Sum(file => file.HitCount));
            var messages = vm.Files.Where(file => file.FullPath == store).ToArray();
            Assert.Equal(2, messages.Length);
            Assert.All(messages, message => Assert.True(message.IsStoreMessage));
            Assert.Equal(["message-1", "message-2"], messages.Select(message => message.MailMessage!.Id).Order());
            Assert.Equal(["row-1", "row-2"], vm.Files.Single(file => file.FullPath == database).Hits.Select(hit => hit.Database!.RowKey).Order());
            Assert.Equal(["one.txt", "two.txt"], vm.Files.Single(file => file.FullPath == archive).Hits.Select(hit => hit.Locator!.ArchiveMember).Order());
            Assert.Equal([1, 2], vm.Files.Single(file => file.FullPath == pdf).Hits.Select(hit => hit.Locator!.Page).Order());
        });
    }

    [Fact]
    public void APhysicalLineFromLiveAndIndexedOverlappingRootsHasOneIdentity()
    {
        string parent = Folder("parent"), child = Folder("parent", "child");
        string file = Path.Combine(child, "one.txt");
        var searcher = new RecordingSearcher(_ =>
        [
            new Hit(file, 1, "needle", [], Route: HitRoute.Live),
            new Hit(file, 1, "needle", [], Route: HitRoute.Indexed, ContentUnitId: 42,
                Locator: new SourceLocator(StartLine: 1, EndLine: 1)),
        ]);
        Run(searcher, settings => Register(settings, parent, child), (pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, []);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            Assert.Equal(1, vm.TotalHits);
            Assert.Equal(1, vm.FilesMatched);
            Assert.Equal(1, Assert.Single(vm.Files).HitCount);
        });
    }

    [Fact]
    public void MultiLocationDeduplicationStopsAtItsBudgetAndLabelsTheRetainedResultsPartial()
    {
        string first = Folder("first"), second = Folder("second");
        string file = Path.Combine(first, "many.txt");
        var searcher = new RecordingSearcher(_ => Enumerable.Range(1, SearchViewModel.MaxRetainedHits + 2)
            .Select(number => new Hit(file, number, "needle " + number, [])));
        Run(searcher, settings => Register(settings, first, second), (pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, []);
            var pending = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(90));
            pending.GetAwaiter().GetResult();

            Assert.Equal(SearchViewModel.MaxRetainedHits + 1, searcher.EmittedHits);
            Assert.Equal(SearchViewModel.MaxRetainedHits, vm.TotalHits);
            Assert.Equal(SearchViewModel.MaxRetainedHits, Assert.Single(vm.Files).HitCount);
            Assert.Equal(0, vm.HiddenHits);
            Assert.Contains("Partial", status.Text);
            Assert.Contains("Partial", vm.SearchCoverageText);
            Assert.Contains("unique-hit limit", status.Text);
        });
    }

    [Fact]
    public void InternalFolderRemappingRetainsIndexedScopeAndExplicitlyReportsUnregisteredNewRoots()
    {
        string first = Folder("first"), child = Folder("first", "child"), renamed = Path.Combine(_root, "renamed");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, first, child), (pump, vm, history, status, settings) =>
        {
            vm.SearchPath = first;
            vm.QueryText = "needle";
            vm.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [first, child]);
            Directory.Move(first, renamed);
            vm.RemapReplacementPaths(first, renamed, directory: true);

            string[] remapped = [renamed, Path.Combine(renamed, "child")];
            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, vm.SelectedLocationScope);
            Assert.Equal(renamed, vm.SearchPath);
            Assert.Equal(remapped, vm.SelectedIndexedRoots);
            Assert.Equal(remapped, settings.Current.QuickSearchSelectedIndexedRoots);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));
            Assert.Empty(searcher.Requests);
            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, vm.SelectedLocationScope);
            Assert.Contains("No longer indexed: " + renamed, vm.LocationScopeNotice);
            Assert.Contains("No longer indexed: " + Path.Combine(renamed, "child"), vm.LocationScopeNotice);
        });
    }

    [Fact]
    public void RunningSearchUsesCapturedScopeRootsQueryTargetsAndWalkerOptionsWhenInputsChange()
    {
        string first = Folder("first"), second = Folder("second");
        var searcher = new RecordingSearcher(waitForRelease: true);
        Run(searcher, settings => Register(settings, first), (pump, vm, history, status, settings) =>
        {
            vm.SearchPath = "";
            vm.QueryText = "Alpha budget";
            vm.SearchMode = QueryMode.PlainText;
            vm.UseQuickSearchMatching = true;
            vm.MatchCase = false;
            vm.IncludeSubfolders = true;
            vm.FileNamePattern = "*.txt";
            vm.EnableImageOcr = true;
            SelectTargets(vm, SearchTarget.Content, SearchTarget.FileNames);
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, [first]);
            var pending = vm.SearchCommand.ExecuteAsync(null);
            try
            {
                vm.QueryText = "changed";
                vm.SearchPath = second;
                settings.Current.IndexedLocations.Clear();
                Register(settings.Current, second);
                vm.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [second]);
                vm.MatchCase = true;
                vm.IncludeSubfolders = false;
                vm.FileNamePattern = "*.md";
                vm.EnableImageOcr = false;
                SelectTargets(vm, SearchTarget.FolderNames);
                pump.PumpUntil(() => searcher.Started.Task.IsCompleted, TimeSpan.FromSeconds(10));
            }
            finally
            {
                searcher.Release();
                Complete(pump, pending);
            }

            Assert.Equal(2, searcher.Requests.Count);
            Assert.Equal([SearchTarget.Content, SearchTarget.FileNames], searcher.Requests.Select(request => request.SearchTarget));
            Assert.All(searcher.Requests, request =>
            {
                Assert.Equal([first], request.Roots);
                Assert.True(request.WalkerOptions.Recursive);
                Assert.True(request.WalkerOptions.EnableOcr);
                Assert.Equal(["*.txt"], request.WalkerOptions.IncludeGlobs);
            });
            var content = Assert.IsType<TermQuery>(searcher.Requests[0].Expression);
            Assert.Equal("Alpha budget", content.Term);
            Assert.False(content.CaseSensitive);
            var names = Assert.IsType<QuickSearchNameQuery>(searcher.Requests[1].Expression);
            Assert.True(names.IsMatch(@"C:\alpha\BUDGET.txt"));
            Assert.True(searcher.Requests[1].MatchFilePaths);
            Assert.False(searcher.Requests[0].MatchFilePaths);
            var saved = Assert.Single(history.SavedSearches);
            Assert.Equal(LocationScopeKind.AllIndexedLocations, saved.LocationScope);
            Assert.Equal([first], saved.SelectedIndexedRoots);
            Assert.Equal("Alpha budget", saved.QueryText);
            Assert.Equal([second], vm.SelectedIndexedRoots);
        });
    }

    [Fact]
    public void ApplyingSavedSearchRestoresLocationSelectionAndQuickMatchingForBothTargets()
    {
        string first = Folder("first"), second = Folder("second");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, first, second), (pump, vm, history, status, settings) =>
        {
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, [first]);
            vm.SelectedSavedSearch = Profile(second);
            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, vm.SelectedLocationScope);
            Assert.Equal([second], vm.SelectedIndexedRoots);
            Assert.Equal([second], settings.Current.QuickSearchSelectedIndexedRoots);
            Assert.True(vm.UseQuickSearchMatching);
            Assert.True(vm.MatchCase);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));

            Assert.Equal(2, searcher.Requests.Count);
            Assert.All(searcher.Requests, request => Assert.Equal([second], request.Roots));
            Assert.IsType<TermQuery>(searcher.Requests[0].Expression);
            var names = Assert.IsType<QuickSearchNameQuery>(searcher.Requests[1].Expression);
            Assert.True(names.IsMatch(@"C:\Alpha\budget.txt"));
            Assert.False(names.IsMatch(@"C:\alpha\budget.txt"));
            var saved = Assert.Single(history.SavedSearches);
            Assert.True(saved.UseQuickSearchMatching);
            Assert.Equal([second], saved.SelectedIndexedRoots);
        });
    }

    [Fact]
    public void WorkspaceRunOnLoadUsesItsExplicitSearchSelectionInsteadOfLegacyQuickChoices()
    {
        string first = Folder("first"), second = Folder("second");
        var searcher = new RecordingSearcher();
        Run(searcher, settings => Register(settings, first, second), (pump, vm, history, status, settings) =>
        {
            vm.SelectedWorkspace = new WorkspaceSettings
            {
                Name = "Review",
                Search = Profile(second),
                QuickSearchSelectedIndexedRoots = [first],
                RunOnLoad = true,
            };
            pump.PumpUntil(() => searcher.Requests.Count == 2 && !vm.IsSearching, TimeSpan.FromSeconds(10));

            Assert.Equal(LocationScopeKind.SelectedIndexedLocations, vm.SelectedLocationScope);
            Assert.True(vm.UseQuickSearchMatching);
            Assert.Equal([second], vm.SelectedIndexedRoots);
            Assert.Equal([second], settings.Current.QuickSearchSelectedIndexedRoots);
            Assert.All(searcher.Requests, request => Assert.Equal([second], request.Roots));
            Assert.IsType<QuickSearchNameQuery>(searcher.Requests[1].Expression);
            Assert.DoesNotContain("needs search text and a location", status.Text);
        });
    }

    [Fact]
    public void LegacyFolderStartupKeepsExistingQuickSearchSelections()
    {
        string first = Folder("first"), legacyFolder = Folder("legacy");
        Run(new RecordingSearcher(), settings =>
        {
            Register(settings, first);
            settings.QuickSearchSelectedIndexedRoots = [first];
            settings.SavedSearches = [new SavedSearchSettings { QueryText = "legacy", SearchPath = legacyFolder }];
        }, (pump, vm, history, status, settings) =>
        {
            Assert.Equal(LocationScopeKind.SelectedFolder, vm.SelectedLocationScope);
            Assert.Equal(legacyFolder, vm.SearchPath);
            Assert.False(vm.UseQuickSearchMatching);
            Assert.Equal([first], vm.SelectedIndexedRoots);
            Assert.Equal([first], settings.Current.QuickSearchSelectedIndexedRoots);
        });
    }

    [Fact]
    public void JsonExportReportsTheCompletedSearchScopeRatherThanLaterEditedInputs()
    {
        string first = Folder("first"), second = Folder("second"), exportPath = Path.Combine(_root, "results.json");
        string file = Path.Combine(first, "one.txt");
        var searcher = new RecordingSearcher(_ => [new Hit(file, 1, "needle phrase", [])]);
        Run(searcher, settings => Register(settings, first, second), (pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle phrase";
            vm.SearchMode = QueryMode.PlainText;
            vm.UseQuickSearchMatching = true;
            vm.ApplyLocationScope(LocationScopeKind.SelectedIndexedLocations, [first]);
            Complete(pump, vm.SearchCommand.ExecuteAsync(null));
            Assert.Single(vm.Files);
            vm.SearchPath = second;
            vm.ApplyLocationScope(LocationScopeKind.AllIndexedLocations, [second]);
            vm.QueryText = "changed";
            vm.UseQuickSearchMatching = false;
            Complete(pump, vm.ExportResultsCommand.ExecuteAsync("json"));

            using var document = JsonDocument.Parse(File.ReadAllText(exportPath));
            var json = document.RootElement;
            Assert.Equal("needle phrase", json.GetProperty("Query").GetString());
            Assert.Equal("SelectedIndexedLocations", json.GetProperty("LocationScope").GetString());
            Assert.True(json.GetProperty("UseQuickSearchMatching").GetBoolean());
            Assert.Empty(json.GetProperty("SearchPath").GetString()!);
            Assert.Equal([first], json.GetProperty("Roots").EnumerateArray().Select(root => root.GetString()));
        }, exportPath);
    }

    private static SavedSearchSettings Profile(string root) => new()
    {
        QueryText = "Alpha budget",
        SearchPath = "",
        SearchMode = QueryMode.PlainText,
        MatchCase = true,
        LocationScope = LocationScopeKind.SelectedIndexedLocations,
        SelectedIndexedRoots = [root],
        UseQuickSearchMatching = true,
        SearchTargets = [SearchTarget.Content, SearchTarget.FileNames],
    };

    private string Folder(params string[] names)
    {
        string path = names.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Register(AppSettings settings, params string[] roots) =>
        settings.IndexedLocations.AddRange(roots.Select(root => new IndexedLocationSettings { Root = root }));

    private static void SelectTargets(SearchViewModel vm, params SearchTarget[] targets)
    {
        foreach (var option in vm.SearchTargetOptions.Where(option => targets.Contains(option.Value))) option.IsSelected = true;
        foreach (var option in vm.SearchTargetOptions.Where(option => !targets.Contains(option.Value))) option.IsSelected = false;
    }

    private static void Complete(PumpingSynchronizationContext pump, Task task)
    {
        pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));
        task.GetAwaiter().GetResult();
    }

    private static void Run(RecordingSearcher searcher, Action<AppSettings> configure,
        Action<PumpingSynchronizationContext, SearchViewModel, HistoryViewModel, StatusBarViewModel, FakeSettingsService> action,
        string? exportPath = null) => StaTestDispatcher.Run(() =>
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var settings = new FakeSettingsService(); configure(settings.Current);
            var status = new StatusBarViewModel();
            var history = new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status);
            using var vm = new SearchViewModel(searcher, new ExtractorRegistry([]), new QueryFactory(), new FakePreviewService(),
                new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status,
                fileSavePicker: exportPath is null ? null : new FakeFileSavePicker { PathToReturn = exportPath });
            action(pump, vm, history, status, settings);
        }
        finally { searcher.Release(); SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class RecordingSearcher(Func<SearchRequest, IEnumerable<Hit>>? hits = null, bool waitForRelease = false) : ISearcher
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<SearchRequest> Requests { get; } = [];
        public int EmittedHits { get; private set; }
        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request); Started.TrySetResult();
            if (waitForRelease) await _gate.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            foreach (var hit in hits?.Invoke(request) ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                EmittedHits++;
                yield return hit;
            }
        }
    }
}
