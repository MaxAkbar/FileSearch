using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class IndexingServiceTests
{
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

    private sealed class BlockingFileIndex : IFileIndex
    {
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public Task ClearAsync(string root, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SavePendingChangeAsync(string root, string path, IndexChangeKind kind, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

        public Task RemovePendingChangeAsync(string root, string path, IndexChangeKind kind, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
