using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// View-model flow tests enabled by the sub-viewmodel split and the pumping
/// synchronization context (the drain loop resumes on the captured context,
/// which the pump executes on the test thread).
/// </summary>
public sealed class SearchViewModelTests
{
    [Fact]
    public void SearchPopulatesResultsRecordsHistoryAndPersists()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(3, vm.TotalHits);
            Assert.Equal(2, vm.FilesMatched);
            Assert.False(vm.IsSearching);
            Assert.NotEqual("—", vm.ElapsedText);
            Assert.StartsWith("Done", status.Text);
            Assert.Equal("needle", history.RecentQueries[0]);

            // History persists its slice immediately (crash safety).
            Assert.Contains("needle", settings.Current.RecentQueries);
        });
    }

    [Fact]
    public void LargeResultSetDrainsCompletelyAcrossBatches()
    {
        // 2,500 hits force more than one capped drain (2,000/tick) plus the
        // consumer-finished-but-queue-nonempty branch of the drain loop.
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(2500, vm.TotalHits);
            Assert.Single(vm.Files);
            Assert.StartsWith("Done", status.Text);
        }, new BulkSearcher(2500));
    }

    [Fact]
    public void InvalidQueryReportsAndStillRecordsHistory()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Regex;
            vm.QueryText = "(";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Invalid query", status.Text);
            Assert.Empty(vm.Files);
            Assert.False(vm.IsSearching);

            // Documented contract: failing searches still land in history.
            Assert.Equal("(", history.RecentQueries[0]);
        });
    }

    [Fact]
    public void SearcherFailureReportsErrorAndResets()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Error:", status.Text);
            Assert.Contains("boom", status.Text);
            Assert.False(vm.IsSearching);
        }, new FaultingSearcher());
    }

    [Fact]
    public void SecondSearchResetsPreviousResults()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var first = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => first.IsCompleted, TimeSpan.FromSeconds(10));

            var second = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => second.IsCompleted, TimeSpan.FromSeconds(10));

            // Counts equal a single run — stale results were cleared.
            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(3, vm.TotalHits);
        });
    }

    [Fact]
    public void CancelStopsAnInfiniteStreamAndCommandsTrackState()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => vm.TotalHits > 0, TimeSpan.FromSeconds(10));

            Assert.False(vm.SearchCommand.CanExecute(null));
            Assert.True(vm.CancelCommand.CanExecute(null));

            vm.CancelCommand.Execute(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Canceled", status.Text);
            Assert.False(vm.IsSearching);
            Assert.Equal("needle", history.RecentQueries[0]);
        }, new EndlessSearcher());
    }

    [Fact]
    public void OptionTogglesPersistTheirSettingsSliceEagerly()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.UseIndex = true;
            Assert.True(settings.Current.UseIndex);

            vm.SkipUnknownFileTypes = true;
            Assert.True(settings.Current.SkipUnknownFileTypes);

            // Another slice's save must not clobber these values.
            history.RecordSearch("query", Path.GetTempPath());
            Assert.True(settings.Current.UseIndex);
            Assert.True(settings.Current.SkipUnknownFileTypes);
            Assert.Contains("query", settings.Current.RecentQueries);
        });
    }

    private static void RunWithPump(
        Action<PumpingSynchronizationContext, SearchViewModel, HistoryViewModel, StatusBarViewModel, FakeSettingsService> body,
        ISearcher? searcher = null)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var status = new StatusBarViewModel();
            var settings = new FakeSettingsService();
            var history = new HistoryViewModel(settings, status);
            var vm = new SearchViewModel(
                searcher ?? new StubSearcher(),
                new ExtractorRegistry(Array.Empty<ITextExtractor>()),
                new QueryFactory(),
                new FakePreviewService(),
                new FakeFileLauncher(),
                settings,
                new FakeFileTypeOptionsStore(),
                new FakeFolderPicker(),
                history,
                status);

            body(pump, vm, history, status, settings);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Yields three hits across two files, then completes.</summary>
    private sealed class StubSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(@"C:\results\a.txt", 1, "needle one", Array.Empty<MatchSpan>());
            yield return new Hit(@"C:\results\a.txt", 2, "needle two", Array.Empty<MatchSpan>());
            yield return new Hit(@"C:\results\b.txt", 1, "needle three", Array.Empty<MatchSpan>());
        }
    }

    /// <summary>Yields N hits for one file as fast as possible.</summary>
    private sealed class BulkSearcher : ISearcher
    {
        private readonly int _count;

        public BulkSearcher(int count) => _count = count;

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (var i = 1; i <= _count; i++)
                yield return new Hit(@"C:\results\bulk.txt", i, "needle", Array.Empty<MatchSpan>());
        }
    }

    /// <summary>Yields one hit, then throws.</summary>
    private sealed class FaultingSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(@"C:\results\a.txt", 1, "needle", Array.Empty<MatchSpan>());
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Streams hits forever until the token cancels.</summary>
    private sealed class EndlessSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var line = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Hit(@"C:\results\stream.txt", ++line, "needle", Array.Empty<MatchSpan>());
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
