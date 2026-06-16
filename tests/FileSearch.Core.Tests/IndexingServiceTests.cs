using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class IndexingServiceTests
{
    [Fact]
    public async Task StartAsyncQueuesRootRefreshForEveryIndexedLocation()
    {
        var index = new BlockingFileIndex();
        var queue = new RecordingQueue();
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-start-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            var item = Assert.Single(queue.Enqueued);
            Assert.Equal(IndexPath.NormalizeRoot(root), item.Root);
            Assert.Equal(IndexChangeKind.RefreshRoot, item.Kind);
            Assert.True(item.Persisted);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ForegroundSearchDefersRunningRootRefresh()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-indexing-service-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            await service.EnqueueRootRefreshAsync(root, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);
            await index.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            service.SetForegroundSearchActive(true);

            await WaitUntilAsync(() => index.RefreshCanceled, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => queue.Count > 0, TestContext.Current.CancellationToken);
            Assert.True(index.RefreshCanceled);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RemoveLocationCancelsActiveWriterAndPrunesQueuedWorkForRemovedRoot()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var activeRoot = Path.Combine(Path.GetTempPath(), "filesearch-active-remove-" + Guid.NewGuid().ToString("N"));
        var removedRoot = Path.Combine(Path.GetTempPath(), "filesearch-queued-remove-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            await service.EnqueueRootRefreshAsync(activeRoot, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);
            await index.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await service.EnqueueRootRefreshAsync(removedRoot, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);

            await service.RemoveLocationAsync(removedRoot, TestContext.Current.CancellationToken);

            await WaitUntilAsync(() => index.RefreshCanceled, TestContext.Current.CancellationToken);
            Assert.True(index.RefreshCanceled);
            Assert.Contains(IndexPath.NormalizeRoot(removedRoot), index.ClearedRoots);
            Assert.False(queue.GetQueuedRootCounts().ContainsKey(IndexPath.NormalizeRoot(removedRoot)));
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PauseDefersProcessingUntilResume()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-pause-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            service.Pause();
            await service.EnqueueRootRefreshAsync(root, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);

            // The worker dequeues the item but must hold it in the pause loop.
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.False(index.RefreshStarted.Task.IsCompleted);

            service.Resume();
            await index.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PausedWorkerKeepsItemsInQueueInsteadOfHoldingThem()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-pause-hold-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            service.Pause();
            await service.EnqueueRootRefreshAsync(root, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);

            // The worker must cycle the item back into the queue rather than
            // hold it (a held item was lost on shutdown-while-paused).
            await WaitUntilAsync(() => queue.Count == 1, TestContext.Current.CancellationToken);
            Assert.False(index.RefreshStarted.Task.IsCompleted);

            await service.StopAsync(TestContext.Current.CancellationToken);

            // Shutdown while paused: the item survived in the queue.
            Assert.Equal(1, queue.Count);
            Assert.False(index.RefreshStarted.Task.IsCompleted);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!condition())
            await Task.Delay(25, linked.Token);
    }

    private sealed class RecordingQueue : IIndexQueue
    {
        public List<IndexQueueItem> Enqueued { get; } = new();

        public int Count => Enqueued.Count;

        public Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken)
        {
            Enqueued.Add(item with { Root = IndexPath.NormalizeRoot(item.Root) });
            return Task.CompletedTask;
        }

        public async Task<IndexQueueItem> DequeueAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public void RemoveRoot(string root)
        {
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            Enqueued.RemoveAll(item =>
                string.Equals(item.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyDictionary<string, int> GetQueuedRootCounts() =>
            Enqueued
                .GroupBy(item => item.Root, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        public Task LoadPendingAsync(
            IReadOnlyDictionary<string, IndexedLocation> locations,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class BlockingFileIndex : IFileIndex
    {
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> ClearedRoots { get; } = new();

        public bool RefreshCanceled { get; private set; }

        public string DatabasePath => string.Empty;

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) =>
            RefreshRootAsync(request, IndexRefreshMode.Full, cancellationToken);

        public async Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken)
        {
            RefreshStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RefreshCanceled = true;
                throw;
            }
        }

        public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexCoverage(IndexCoverageStatus.Missing, "missing"));

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new IndexDatabaseInfo(DatabasePath, false, false, "3", 0, 0, 0, 0, 0, 0, 0, null));

        public Task CompactAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                ClearedRoots.Add(IndexPath.NormalizeRoot(root));
            }, cancellationToken);

        public Task SavePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

        public Task RemovePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
