using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

/// <summary>
/// Routing matrix for <see cref="IndexedSearcher"/>: which underlying
/// searcher serves the request under each coverage/index state.
/// </summary>
public sealed class IndexedSearcherTests
{
    private static readonly string s_root = Path.GetTempPath();

    [Fact]
    public async Task UsesIndexedResultsWhenCoveredAndIdle()
    {
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: true, "indexed.txt");
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index));

        var hits = await CollectAsync(searcher, BuildRequest(useIndex: true));

        var hit = Assert.Single(hits);
        Assert.Equal("indexed.txt", hit.Path);
        Assert.False(live.WasUsed);
    }

    [Fact]
    public async Task UsesLiveScanWhenIndexDisabled()
    {
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: true, "indexed.txt");
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index));

        var hits = await CollectAsync(searcher, BuildRequest(useIndex: false));

        var hit = Assert.Single(hits);
        Assert.Equal("live.txt", hit.Path);
        Assert.False(index.SearchWasUsed);
    }

    [Fact]
    public async Task UsesLiveScanWhenIndexDoesNotCover()
    {
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: false, "indexed.txt");
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index));

        var hits = await CollectAsync(searcher, BuildRequest(useIndex: true));

        var hit = Assert.Single(hits);
        Assert.Equal("live.txt", hit.Path);
        Assert.False(index.SearchWasUsed);
    }

    [Fact]
    public async Task UsesLiveScanWhenRequestRootHasQueuedIndexWork()
    {
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: true, "indexed.txt");
        var indexingService = new StubIndexingService(new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: false,
            QueueLength: 1,
            Message: "queued",
            QueuedRootCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [IndexPath.NormalizeRoot(s_root)] = 1,
            }));
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index), indexingService);

        var hits = await CollectAsync(searcher, BuildRequest(useIndex: true));

        var hit = Assert.Single(hits);
        Assert.Equal("live.txt", hit.Path);
        Assert.False(index.SearchWasUsed);
    }

    [Fact]
    public async Task SplitsCoveredMultiRootRequestAcrossIndexedRoots()
    {
        var root1 = Path.Combine(Path.GetTempPath(), "filesearch-index-root-1");
        var root2 = Path.Combine(Path.GetTempPath(), "filesearch-index-root-2");
        var live = new StubSearcher("live.txt");
        var index = new StubIndexSearch(covered: true, "indexed.txt");
        var searcher = new IndexedSearcher(live, index, new IndexCoverageService(index));

        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { root1, root2 },
            new WalkerOptions(),
            UseIndex: true);
        var hits = await CollectAsync(searcher, request);

        Assert.Equal(2, hits.Count);
        Assert.False(live.WasUsed);
        Assert.Equal(new[] { root1, root2 }, index.SearchRoots);
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

        public bool WasUsed { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WasUsed = true;
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

        public List<string> SearchRoots { get; } = new();

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchWasUsed = true;
            SearchRoots.Add(request.Roots[0]);
            await Task.CompletedTask;
            yield return new Hit(_path, 1, "needle", Array.Empty<MatchSpan>());
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_covered
                ? new IndexCoverage(IndexCoverageStatus.Covered, "stub covered")
                : new IndexCoverage(IndexCoverageStatus.Missing, "stub missing"));
    }

    private sealed class StubIndexingService : IIndexingService
    {
        public StubIndexingService(IndexingStatus status) => CurrentStatus = status;

        public event EventHandler<IndexingStatus>? StatusChanged;

        public IndexingStatus CurrentStatus { get; private set; }

        public bool IsPaused => CurrentStatus.IsPaused;

        public IndexerResourceProfile ResourceProfile { get; private set; } = IndexerResourceProfile.Balanced;

        public IndexerRuntimeOptions RuntimeOptions { get; private set; } = IndexerRuntimeOptions.Default;

        public Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddOrUpdateLocationAsync(IndexedLocation location, bool queueInitialRefresh, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveLocationAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnqueueRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken) => Task.CompletedTask;

        public void SetForegroundSearchActive(bool isActive) => StatusChanged?.Invoke(this, CurrentStatus);

        public void SetResourceProfile(IndexerResourceProfile profile) => ResourceProfile = profile;

        public void SetRuntimeOptions(IndexerRuntimeOptions options) => RuntimeOptions = options.Normalize();

        public void Pause()
        {
        }

        public void Resume()
        {
        }
    }
}
