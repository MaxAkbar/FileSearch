using System.Runtime.CompilerServices;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class SearchGuidanceViewModelTests
{
    [Fact]
    public void GuidanceUpdatesWithInputsAndPresetsKeepTheQueryAndOtherLimits()
    {
        Run((pump, vm) =>
        {
            var changes = new List<string?>();
            vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            vm.QueryText = "john smith";
            Assert.Contains("All terms (AND)", vm.QueryMeaningText);
            vm.SearchMode = QueryMode.PlainText;
            Assert.Equal("john smith", vm.QueryText);
            Assert.Contains("Literal text", vm.QueryMeaningText);
            vm.MatchCase = true;
            Assert.Contains("Case sensitive", vm.QueryMeaningText);
            vm.IncludeSubfolders = false;
            vm.ExcludeFileNamePattern = "secret*";
            vm.EnableDocumentExtraction = false;
            vm.FindDatabaseValuesCommand.Execute(null);
            Assert.Equal("john smith", vm.QueryText);
            Assert.Equal(QueryMode.PlainText, vm.SearchMode);
            Assert.Equal(new[] { SearchTarget.Content }, vm.CurrentSearchTargets);
            Assert.Equal(DatabaseFiles.Extensions.Select(extension => "*" + extension),
                vm.FileNamePattern.Split(';', StringSplitOptions.TrimEntries));
            Assert.True(vm.EnableDocumentExtraction);
            Assert.Contains("within one cell", vm.SearchScopeText);
            Assert.Contains("This folder only", vm.SearchScopeText);
            Assert.Contains("File pattern:", vm.SearchScopeText);
            Assert.True(vm.MatchCase);
            Assert.Equal("secret*", vm.ExcludeFileNamePattern);

            vm.FindFileCommand.Execute(null);
            Assert.Equal(new[] { SearchTarget.FileNames }, vm.CurrentSearchTargets);
            Assert.Empty(vm.FileNamePattern);
            Assert.DoesNotContain("Database values", vm.SearchScopeText);
            vm.FindContentCommand.Execute(null);
            Assert.Equal(new[] { SearchTarget.Content }, vm.CurrentSearchTargets);
            Assert.Equal("john smith", vm.QueryText);
            Assert.Contains(nameof(vm.QueryMeaningText), changes);
            Assert.Contains(nameof(vm.SearchScopeText), changes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroHitSearchKeepsActualCoverageReasonsAndOriginalScope(bool fail)
    {
        Run((pump, vm) =>
        {
            vm.QueryText = "unmatched";
            vm.SearchPath = Path.GetTempPath();
            vm.UseIndex = true;
            Assert.Contains("checked when you search", vm.SearchCoverageText);
            var search = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.True(search.IsCompletedSuccessfully);
            Assert.Empty(vm.Files);
            Assert.StartsWith("Last search:", vm.SearchCoverageText);
            Assert.Contains("Index refresh is incomplete; using live scan", vm.SearchCoverageText);
            Assert.Contains("Later route detail", vm.SearchCoverageText);
            Assert.Contains("No results", vm.SearchCoverageText);
            if (fail) Assert.Contains("Search failed: fixture failed", vm.SearchCoverageText);
            string previous = vm.SearchCoverageText;
            vm.QueryText = "next query";
            vm.SearchPath = @"C:\another-folder";
            Assert.Equal(previous, vm.SearchCoverageText);
            Assert.Contains(Path.GetTempPath(), vm.SearchCoverageDetails);
        }, new EmptyRouteSearcher(fail));
    }

    [Fact]
    public void SetupCommandsCannotChangeARunningSearchOrReplacementAndCancellationRemainsVisible()
    {
        Run((pump, vm) =>
        {
            vm.QueryText = "original";
            vm.SearchPath = Path.GetTempPath();
            var search = vm.SearchCommand.ExecuteAsync(null);
            Assert.True(vm.IsSearching);
            Assert.False(vm.FindDatabaseValuesCommand.CanExecute(null));
            vm.FindDatabaseValuesCommand.Execute(null);
            Assert.Equal("original", vm.QueryText);
            Assert.Empty(vm.FileNamePattern);
            vm.CancelCommand.Execute(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.Contains("Canceled; results may be incomplete", vm.SearchCoverageText);
            Assert.True(vm.FindDatabaseValuesCommand.CanExecute(null));
            vm.IsReplacementBusy = true;
            Assert.False(vm.FindFileCommand.CanExecute(null));
            Assert.False(vm.FindContentCommand.CanExecute(null));
            vm.IsReplacementBusy = false;
            Assert.True(vm.FindFileCommand.CanExecute(null));
        }, new WaitingSearcher());
    }

    [Fact]
    public void RoutingSeparatesTargetsPreservesPendingUpdatesAndReportsMixedResults()
    {
        var routes = new SearchRouteSummary(@"C:\docs", [SearchTarget.Content, SearchTarget.FileNames]);
        routes.ReportStatus(SearchTarget.Content, "Index updates pending");
        routes.ReportStatus(SearchTarget.Content, "Using metadata index");
        routes.ReportStatus(SearchTarget.Content, "Using metadata index");
        routes.ReportHit(SearchTarget.Content, HitRoute.Indexed);
        routes.ReportHit(SearchTarget.Content, HitRoute.Live);
        routes.ReportStatus(SearchTarget.FileNames, "Name index covers this drive");
        routes.ReportHit(SearchTarget.FileNames, HitRoute.Indexed);
        string summary = routes.Describe();
        Assert.Contains("Contents: Indexed and live results", summary);
        Assert.Contains("Index updates pending", summary);
        Assert.Equal(1, summary.Split("Using metadata index").Length - 1);
        Assert.Contains("File names: Indexed results; Name index covers this drive", summary);
    }

    private static void Run(Action<PumpingSynchronizationContext, SearchViewModel> body, ISearcher? searcher = null)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var status = new StatusBarViewModel();
            var settings = new FakeSettingsService();
            var appSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, appSettings, status);
            using var vm = new SearchViewModel(searcher ?? new EmptyRouteSearcher(false), new ExtractorRegistry([]), new QueryFactory(),
                new FakePreviewService(), new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
            body(pump, vm);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class EmptyRouteSearcher(bool fail) : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            request.Status?.Invoke("Index refresh is incomplete; using live scan");
            request.Status?.Invoke("Later route detail");
            if (fail) throw new IOException("fixture failed");
            yield break;
        }
    }

    private sealed class WaitingSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }
}
