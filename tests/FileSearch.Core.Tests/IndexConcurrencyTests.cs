using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

/// <summary>
/// Regression tests for the read/write concurrency defects found during the
/// 2026-07 Phase 0 benchmarking: silent write loss when a reader overlapped
/// the write session's checkpoint, reader crashes racing writes, subfolder
/// watcher events bypassing the per-file upsert path, and queued refreshes
/// starved by due-time sliding.
/// </summary>
public sealed class IndexConcurrencyTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _root;
    private readonly CSharpDbFileIndex _index;

    public IndexConcurrencyTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), "filesearch-concurrency-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_basePath, "root");
        Directory.CreateDirectory(_root);

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        _index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(_basePath, "index", "filesearch.db") },
            new FileWalker(),
            registry);
    }

    public void Dispose()
    {
        _index.Dispose();
        try
        {
            Directory.Delete(_basePath, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task UpsertProceedsDuringActiveReaderAndSurvives()
    {
        // Enough matching lines that the search streams instead of finishing
        // in one pull; a regex query forces the full-scan path. 600 keeps the
        // test fast — hot-token postings currently cost O(blob) per inserted
        // line in the engine, so large uniform corpora are slow to index.
        File.WriteAllText(
            Path.Combine(_root, "many.txt"),
            string.Concat(Enumerable.Range(0, 600).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"needle_{i:D4} line {i}\n"))));
        await BuildAsync();

        var request = new SearchRequest(
            new RegexQuery(@"needle_\d+"),
            new[] { _root },
            new WalkerOptions(),
            UseIndex: true);

        var enumerator = _index.SearchAsync(request, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync()); // snapshot session now held mid-stream

        var newFile = Path.Combine(_root, "fresh.txt");
        await File.WriteAllTextAsync(newFile, "fresh_marker content\n", TestContext.Current.CancellationToken);
        var upsert = _index.UpsertFileAsync(_root, newFile, new WalkerOptions(), TestContext.Current.CancellationToken);

        // Snapshot reader sessions must not block the writer: the write
        // completes while the search streams, and neither side loses data.
        var completed = await Task.WhenAny(upsert, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == upsert;
        Assert.True(completed, "Upsert blocked behind a snapshot reader session.");
        await upsert;

        Assert.True(await enumerator.MoveNextAsync()); // reader stream unaffected
        await enumerator.DisposeAsync();

        var hits = await SearchAllAsync(new TermQuery("fresh_marker"));
        var hit = Assert.Single(hits);
        Assert.EndsWith("fresh.txt", hit.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSearchPollingDoesNotLoseWrites()
    {
        // The original repro: a search polling the database around an upsert
        // made the writer's checkpoint fail and CSharpDB discarded the whole
        // session while the upsert reported success.
        File.WriteAllText(Path.Combine(_root, "seed.txt"), "seed content\n");
        await BuildAsync();

        var newFile = Path.Combine(_root, "polled.txt");
        await File.WriteAllTextAsync(newFile, "polled_marker content\n", TestContext.Current.CancellationToken);

        var stop = new CancellationTokenSource();
        var poller = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = await SearchAllAsync(new TermQuery("seed"));
                await Task.Delay(10, CancellationToken.None);
            }
        }, CancellationToken.None);

        try
        {
            await _index.UpsertFileAsync(_root, newFile, new WalkerOptions(), TestContext.Current.CancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await poller;
            stop.Dispose();
        }

        var hits = await SearchAllAsync(new TermQuery("polled_marker"));
        var hit = Assert.Single(hits);
        Assert.EndsWith("polled.txt", hit.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletingDirectoryPathRemovesIndexedChildren()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "child.txt"), "subtree_needle content\n");
        await BuildAsync();
        Assert.Single(await SearchAllAsync(new TermQuery("subtree_needle")));

        Directory.Delete(sub, recursive: true);

        // Watchers report one Deleted event for the folder, not its children;
        // a directory-path delete must sweep the subtree rows.
        await _index.DeleteFileAsync(_root, sub, TestContext.Current.CancellationToken);

        Assert.Empty(await SearchAllAsync(new TermQuery("subtree_needle")));
    }

    [Fact]
    public async Task WatcherRoutesSubfolderFileChangeToUpsertNotRefresh()
    {
        var sub = Path.Combine(_root, "watched-sub");
        Directory.CreateDirectory(sub);

        var queue = new RecordingQueue();
        var watchers = new IndexWatcherService(queue);
        watchers.StartWatching(new IndexedLocation(_root, new WalkerOptions(), WatchEnabled: true));
        try
        {
            var file = Path.Combine(sub, "inside.txt");
            await File.WriteAllTextAsync(file, "hello\n", TestContext.Current.CancellationToken);

            var upsert = await queue.WaitForAsync(
                item => item.Kind == IndexChangeKind.UpsertFile &&
                    item.Path is not null &&
                    item.Path.EndsWith("inside.txt", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));
            Assert.NotNull(upsert);
            Assert.True(
                upsert.DueUtc <= DateTime.UtcNow.AddSeconds(1),
                $"File watcher debounce should stay below the freshness target; due at {upsert.DueUtc:O}.");

            // Give trailing directory-change echoes time to arrive, then make
            // sure none of them became a full root refresh.
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(queue.Items, item => item.Kind == IndexChangeKind.RefreshRoot);
        }
        finally
        {
            watchers.StopAll();
        }
    }

    [Fact]
    public async Task WatcherRoutesNewDirectoryToRootRefresh()
    {
        var queue = new RecordingQueue();
        var watchers = new IndexWatcherService(queue);
        watchers.StartWatching(new IndexedLocation(_root, new WalkerOptions(), WatchEnabled: true));
        try
        {
            // A created directory may be a tree moved in from outside the
            // root; its children raise no events, so a subtree walk is needed.
            Directory.CreateDirectory(Path.Combine(_root, "moved-in"));

            var refresh = await queue.WaitForAsync(
                item => item.Kind == IndexChangeKind.RefreshRoot,
                TimeSpan.FromSeconds(10));
            Assert.NotNull(refresh);
            Assert.True(
                refresh.DueUtc <= DateTime.UtcNow.AddSeconds(3),
                $"Root watcher debounce should stay near the freshness target; due at {refresh.DueUtc:O}.");
        }
        finally
        {
            watchers.StopAll();
        }
    }

    [Fact]
    public async Task CoalesceKeepsEarliestDueForQueuedRefresh()
    {
        var queue = new IndexQueue(_index);
        var firstDue = DateTime.UtcNow.AddSeconds(5);

        await queue.EnqueueAsync(
            new IndexQueueItem(_root, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, firstDue, Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(_root, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow.AddSeconds(60), Persisted: false),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(queue.SnapshotItems());
        Assert.Equal(firstDue, item.DueUtc);
    }

    [Fact]
    public async Task CoalesceCapsFileDebounceSliding()
    {
        var queue = new IndexQueue(_index);
        var file = Path.Combine(_root, "busy.log");
        var start = DateTime.UtcNow;

        await queue.EnqueueAsync(
            new IndexQueueItem(_root, file, new WalkerOptions(), IndexChangeKind.UpsertFile, IndexQueuePriority.Normal, start.AddSeconds(2), Persisted: false),
            TestContext.Current.CancellationToken);

        // A file under constant rewrite keeps arriving with later due times;
        // the debounce may slide, but never past the cap from first enqueue.
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(
                new IndexQueueItem(_root, file, new WalkerOptions(), IndexChangeKind.UpsertFile, IndexQueuePriority.Normal, start.AddSeconds(60 + i), Persisted: false),
                TestContext.Current.CancellationToken);
        }

        var item = Assert.Single(queue.SnapshotItems());
        Assert.True(
            item.DueUtc <= start.AddSeconds(31),
            $"Debounce slid to {item.DueUtc:O}, past the 30s cap from first enqueue at {start:O}.");
        Assert.True(item.DueUtc >= start.AddSeconds(2), "Cap must not pull the due time before the original debounce.");
    }

    [Fact]
    public void StorageFailureClassifierExcludesCancellationAndForeignExceptions()
    {
        Assert.False(IndexStorageFailure.IsStorageEngineFailure(new OperationCanceledException()));
        Assert.False(IndexStorageFailure.IsStorageEngineFailure(new InvalidOperationException("not the engine")));
        Assert.False(IndexStorageFailure.IsStorageEngineFailure(
            new InvalidOperationException("outer", new ArgumentOutOfRangeException("x"))));
    }

    private Task BuildAsync() =>
        _index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

    private async Task<List<Hit>> SearchAllAsync(Query query)
    {
        var request = new SearchRequest(query, new[] { _root }, new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in _index.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    /// <summary>In-memory queue capturing watcher enqueues; no persistence.</summary>
    private sealed class RecordingQueue : IIndexQueue
    {
        private readonly ConcurrentQueue<IndexQueueItem> _items = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public IReadOnlyCollection<IndexQueueItem> Items => _items;

        public int Count => _items.Count;

        public Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken)
        {
            _items.Enqueue(item);
            _arrived.Release();
            return Task.CompletedTask;
        }

        public Task<IndexQueueItem> DequeueAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Recording queue does not dequeue.");

        public void RemoveRoot(string root)
        {
        }

        public IReadOnlyDictionary<string, int> GetQueuedRootCounts() =>
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Task LoadPendingAsync(
            IReadOnlyDictionary<string, IndexedLocation> locations,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<IndexQueueItem?> WaitForAsync(Func<IndexQueueItem, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var match = _items.FirstOrDefault(predicate);
                if (match is not null)
                    return match;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                await _arrived.WaitAsync(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
            }

            return _items.FirstOrDefault(predicate);
        }
    }
}
