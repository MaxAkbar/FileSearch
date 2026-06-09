using System.IO;
using CSharpDB.Engine;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class FileIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly IExtractorRegistry _registry;
    private readonly Searcher _liveSearcher;
    private readonly CSharpDbFileIndex _index;
    private readonly IndexedSearcher _indexedSearcher;

    public FileIndexTests()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "filesearch-index-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(basePath, "root");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(basePath, "index", "filesearch.db");

        var plain = new PlainTextExtractor();
        _registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        var walker = new FileWalker();
        _liveSearcher = new Searcher(walker, _registry);
        _index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = _dbPath }, walker, _registry);
        _indexedSearcher = new IndexedSearcher(_liveSearcher, _index, new IndexCoverageService(_index));
    }

    public void Dispose()
    {
        try
        {
            var basePath = Directory.GetParent(_root)?.FullName;
            if (basePath is not null)
                Directory.Delete(basePath, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task IndexedSearchMatchesLiveSearch_ForPlainRegexAndBooleanQueries()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\nbeta match\nfoo and bar\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "beta match\nbar only\n");

        await BuildAsync();

        await AssertSameResultsAsync(new TermQuery("beta"));
        await AssertSameResultsAsync(new RegexQuery("b.t.\\s+match"));
        await AssertSameResultsAsync(new AndQuery(new Query[] { new TermQuery("foo"), new TermQuery("bar") }));
        await AssertSameResultsAsync(new AndQuery(new Query[]
        {
            new OrQuery(new Query[] { new TermQuery("foo"), new TermQuery("beta") }),
            new TermQuery("match"),
        }));
    }

    [Fact]
    public async Task RefreshUpdatesChangedFilesAndRemovesDeletedFiles()
    {
        var changed = Path.Combine(_root, "changed.txt");
        var deleted = Path.Combine(_root, "deleted.txt");
        File.WriteAllText(changed, "old needle\n");
        File.WriteAllText(deleted, "delete needle\n");

        await BuildAsync();

        File.WriteAllText(changed, "new needle\n");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(2));
        File.Delete(deleted);

        await BuildAsync();

        Assert.Empty(await IndexedSearchAsync(new TermQuery("old")));
        Assert.Empty(await IndexedSearchAsync(new TermQuery("delete")));

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("new")));
        Assert.EndsWith("changed.txt", hit.Path);
    }

    [Fact]
    public async Task IncrementalUpsertAddsAndUpdatesFileHits()
    {
        var file = Path.Combine(_root, "live.txt");

        File.WriteAllText(file, "first needle\n");
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        var first = Assert.Single(await IndexedSearchAsync(new TermQuery("first")));
        Assert.EndsWith("live.txt", first.Path);

        File.WriteAllText(file, "second needle\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(await IndexedSearchAsync(new TermQuery("first")));
        var second = Assert.Single(await IndexedSearchAsync(new TermQuery("second")));
        Assert.EndsWith("live.txt", second.Path);
    }

    [Fact]
    public async Task IncrementalDeleteRemovesStaleHits()
    {
        var file = Path.Combine(_root, "gone.txt");
        File.WriteAllText(file, "delete needle\n");
        await BuildAsync();

        await _index.DeleteFileAsync(_root, file, TestContext.Current.CancellationToken);

        Assert.Empty(await IndexedSearchAsync(new TermQuery("delete")));
    }

    [Fact]
    public async Task MultiRootIndexReturnsOnlyMatchingRootResults()
    {
        var otherRoot = Path.Combine(Path.GetDirectoryName(_root)!, "other");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "shared needle\n");
        File.WriteAllText(Path.Combine(otherRoot, "b.txt"), "shared needle\n");

        await BuildAsync();
        await _index.BuildOrRefreshAsync(new IndexRequest(otherRoot, new WalkerOptions()), TestContext.Current.CancellationToken);

        var rootHits = await IndexedSearchAsync(new TermQuery("needle"));
        var hit = Assert.Single(rootHits);
        Assert.EndsWith("a.txt", hit.Path);
    }

    [Fact]
    public async Task IndexQueueCoalescesRepeatedFileChanges()
    {
        var queue = new IndexQueue(_index);
        var file = Path.Combine(_root, "queued.txt");
        var item = new IndexQueueItem(
            _root,
            file,
            new WalkerOptions(),
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow,
            Persisted: false);

        await queue.EnqueueAsync(item, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(item with { DueUtc = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        Assert.Equal(1, queue.Count);
        var dequeued = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IndexChangeKind.UpsertFile, dequeued.Kind);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task IndexedSearcherFallsBackToLiveScan_WhenIndexDoesNotCoverSearch()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "a.txt"), "recursive needle\n");

        await BuildAsync(new WalkerOptions { Recursive = false });

        var status = string.Empty;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions { Recursive = true },
            UseIndex: true,
            Status: message => status = message);

        var hits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("a.txt", found.Path);
        Assert.Contains("using live scan", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexedSearcherReturnsLiveHitsWhileBackgroundIndexingIsQueued()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "fallback needle\n");
        var queue = new IndexQueue(_index);
        var indexingService = new IndexingService(
            _index,
            queue,
            new IndexWatcherService(queue));
        await indexingService.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);

        try
        {
            var searcher = new IndexedSearcher(_liveSearcher, _index, new IndexCoverageService(_index), indexingService);
            var request = new SearchRequest(
                new TermQuery("needle"),
                new[] { _root },
                new WalkerOptions(),
                UseIndex: true);

            var hits = new List<Hit>();
            await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
                hits.Add(hit);

            Assert.Single(hits);
            Assert.True(queue.Count > 0 || indexingService.CurrentStatus.QueueLength > 0 || indexingService.CurrentStatus.IsProcessing);
        }
        finally
        {
            await indexingService.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task IndexedSearcherUsesLiveScanWhileBackgroundIndexingIsProcessing()
    {
        File.WriteAllText(Path.Combine(_root, "processing.txt"), "processing needle\n");
        var index = new ThrowIfUsedFileIndex();
        var indexingService = new ProcessingIndexingService();
        var searcher = new IndexedSearcher(_liveSearcher, index, new IndexCoverageService(index), indexingService);
        var status = string.Empty;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions(),
            UseIndex: true,
            Status: message => status = message);

        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("processing.txt", found.Path);
        Assert.Contains("using live scan", status, StringComparison.OrdinalIgnoreCase);
        Assert.True(indexingService.ForegroundSearchWasSet);
        Assert.True(indexingService.FileChangesQueued > 0);
    }

    [Fact]
    public async Task IncompatibleSchemaIsTreatedAsMissingUntilRebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using (var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken))
        {
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("INSERT INTO meta VALUES ('schema_version', '1')", TestContext.Current.CancellationToken);
            await db.CheckpointAsync(TestContext.Current.CancellationToken);
        }

        var stats = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        var pending = await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(stats.Exists);
        Assert.Empty(pending);

        File.WriteAllText(Path.Combine(_root, "rebuilt.txt"), "rebuilt needle\n");
        await BuildAsync();

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("rebuilt")));
        Assert.EndsWith("rebuilt.txt", hit.Path);
    }

    [Fact]
    public async Task IndexedSearchToleratesWalCleanupContention()
    {
        File.WriteAllText(Path.Combine(_root, "wal.txt"), "wal needle\n");
        await BuildAsync();

        var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken);
        try
        {
            var hits = await IndexedSearchAsync(new TermQuery("needle"));

            var hit = Assert.Single(hits);
            Assert.EndsWith("wal.txt", hit.Path);
        }
        finally
        {
            await SafeDisposeAsync(db);
        }
    }

    [Fact]
    public async Task IndexedSearchAppliesFileFiltersAtQueryTime()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "shared needle\n");
        File.WriteAllText(Path.Combine(_root, "b.md"), "shared needle\n");

        await BuildAsync();

        var hits = await IndexedSearchAsync(
            new TermQuery("needle"),
            new WalkerOptions { IncludeGlobs = new[] { "*.md" } });

        var hit = Assert.Single(hits);
        Assert.EndsWith("b.md", hit.Path);
    }

    [Fact]
    public async Task ClearRemovesCurrentRootFromIndex()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "needle\n");

        await BuildAsync();
        var before = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        Assert.True(before.Exists);
        Assert.Equal(1, before.FileCount);

        await _index.ClearAsync(_root, TestContext.Current.CancellationToken);

        var after = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        Assert.False(after.Exists);

        var coverage = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexCoverageStatus.Missing, coverage.Status);
    }

    private async Task BuildAsync(WalkerOptions? options = null)
    {
        await _index.BuildOrRefreshAsync(
            new IndexRequest(_root, options ?? new WalkerOptions()),
            TestContext.Current.CancellationToken);
    }

    private async Task AssertSameResultsAsync(Query query)
    {
        var live = await LiveSearchAsync(query);
        var indexed = await IndexedSearchAsync(query);

        Assert.Equal(
            Normalize(live),
            Normalize(indexed));
    }

    private async Task<List<Hit>> LiveSearchAsync(Query query, WalkerOptions? options = null)
    {
        var request = new SearchRequest(query, new[] { _root }, options ?? new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var hit in _liveSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private async Task<List<Hit>> IndexedSearchAsync(Query query, WalkerOptions? options = null)
    {
        var request = new SearchRequest(query, new[] { _root }, options ?? new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private static List<string> Normalize(IEnumerable<Hit> hits) =>
        hits
            .Select(hit => $"{Path.GetFileName(hit.Path)}:{hit.LineNumber}:{hit.LineContent}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async ValueTask SafeDisposeAsync(Database db)
    {
        try
        {
            await db.DisposeAsync();
        }
        catch (Exception ex) when (ex.Message.Contains("WAL file", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private sealed class ProcessingIndexingService : IIndexingService
    {
        public event EventHandler<IndexingStatus>? StatusChanged;

        public IndexingStatus CurrentStatus { get; private set; } = new(true, false, true, 1, "Indexing");

        public bool IsPaused => false;

        public bool ForegroundSearchWasSet { get; private set; }

        public int FileChangesQueued { get; private set; }

        public Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddOrUpdateLocationAsync(IndexedLocation location, bool queueInitialRefresh, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveLocationAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnqueueRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnqueueFileChangedAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken)
        {
            FileChangesQueued++;
            return Task.CompletedTask;
        }

        public Task EnqueueFileDeletedAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public void SetForegroundSearchActive(bool isActive)
        {
            ForegroundSearchWasSet |= isActive;
            CurrentStatus = isActive
                ? CurrentStatus
                : CurrentStatus with { IsProcessing = false };
            StatusChanged?.Invoke(this, CurrentStatus);
        }

        public void Pause() { }

        public void Resume() { }
    }

    private sealed class ThrowIfUsedFileIndex : IFileIndex
    {
        public string DatabasePath => string.Empty;

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Indexed search should not be used while background indexing is processing.");
            #pragma warning disable CS0162
            yield break;
            #pragma warning restore CS0162
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Coverage should not be checked while background indexing is processing.");

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePendingChangeAsync(string root, string path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

        public Task RemovePendingChangeAsync(string root, string path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
