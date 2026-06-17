using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class BackgroundIndexerSearchCoordinatorTests
{
    private static readonly string s_root = Path.GetTempPath();

    [Fact]
    public async Task IndexedSearcherUsesWorkerQueueStatusWhenBackgroundIndexerModeIsEnabled()
    {
        var settings = new FakeSettingsService();
        settings.Current.KeepIndexUpdatedAfterClose = true;
        var localIndexingService = new FakeIndexingService
        {
            CurrentStatus = new IndexingStatus(
                IsRunning: true,
                IsPaused: false,
                IsProcessing: false,
                QueueLength: 0,
                Message: "local idle"),
        };
        var backgroundIndexer = new FakeBackgroundIndexerProcessService
        {
            Status = new IndexingStatus(
                IsRunning: true,
                IsPaused: false,
                IsProcessing: false,
                QueueLength: 1,
                Message: "worker queued",
                QueuedRootCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [IndexPath.NormalizeRoot(s_root)] = 1,
                }),
        };
        var coordinator = new BackgroundIndexerSearchCoordinator(
            localIndexingService,
            backgroundIndexer,
            settings);
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: true, "indexed.txt");
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index), coordinator);

        var hits = await CollectAsync(searcher, BuildRequest(useIndex: true));

        var hit = Assert.Single(hits);
        Assert.Equal("live.txt", hit.Path);
        Assert.False(index.SearchWasUsed);
        Assert.Equal(1, backgroundIndexer.GetStatusCallCount);
        Assert.Equal(2, backgroundIndexer.SetForegroundSearchActiveCallCount);
    }

    [Fact]
    public async Task QueueRootRefreshUsesWorkerAndFallsBackToLocalWhenWorkerFails()
    {
        var settings = new FakeSettingsService();
        settings.Current.KeepIndexUpdatedAfterClose = true;
        var localIndexingService = new FakeIndexingService();
        var backgroundIndexer = new FakeBackgroundIndexerProcessService();
        var coordinator = new BackgroundIndexerSearchCoordinator(
            localIndexingService,
            backgroundIndexer,
            settings);

        await coordinator.EnqueueRootRefreshAsync(
            s_root,
            new WalkerOptions(),
            IndexQueuePriority.Low,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, backgroundIndexer.QueueRootRefreshCallCount);

        backgroundIndexer.CommandResult = false;
        await coordinator.EnqueueRootRefreshAsync(
            s_root,
            new WalkerOptions(),
            IndexQueuePriority.Low,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, backgroundIndexer.QueueRootRefreshCallCount);
        Assert.Equal(1, localIndexingService.EnqueuedRootRefreshCount);
    }

    private static SearchRequest BuildRequest(bool useIndex) =>
        new(new TermQuery("needle"), new[] { s_root }, new WalkerOptions(), UseIndex: useIndex);

    private static async Task<List<Hit>> CollectAsync(IndexedSearcher searcher, SearchRequest request)
    {
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private sealed class StubSearcher : ISearcher
    {
        private readonly string _path;

        public StubSearcher(string path) => _path = path;

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new Hit(_path, 1, "needle", Array.Empty<MatchSpan>());
        }
    }

    private sealed class StubIndexSearch : IIndexSearch
    {
        private readonly bool _covered;
        private readonly string _path;

        public StubIndexSearch(bool covered, string path)
        {
            _covered = covered;
            _path = path;
        }

        public bool SearchWasUsed { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchWasUsed = true;
            await Task.CompletedTask;
            yield return new Hit(_path, 1, "needle", Array.Empty<MatchSpan>());
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_covered
                ? new IndexCoverage(IndexCoverageStatus.Covered, "stub covered")
                : new IndexCoverage(IndexCoverageStatus.Missing, "stub missing"));
    }
}
