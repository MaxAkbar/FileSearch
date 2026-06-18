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
    public async Task StartAsyncSkipsRootRefreshForCatchUpHandledLocation()
    {
        var index = new BlockingFileIndex();
        var queue = new RecordingQueue();
        var root = Path.Combine(Path.GetTempPath(), "filesearch-start-catchup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catchUp = new StaticStartupCatchUp(new IndexStartupCatchUpResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IndexPath.NormalizeRoot(root) },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var service = new IndexingService(
            index,
            queue,
            new IndexWatcherService(queue),
            startupCatchUp: catchUp);

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(0, queue.Count);
            Assert.NotNull(service.CurrentStatus.RootStatusDetails);
            var details = service.CurrentStatus.RootStatusDetails!;
            Assert.Equal(
                "Caught up via USN journal",
                details[IndexPath.NormalizeRoot(root)]);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncQueuesRootRefreshForCatchUpFallbackLocation()
    {
        var index = new BlockingFileIndex();
        var queue = new RecordingQueue();
        var root = Path.Combine(Path.GetTempPath(), "filesearch-start-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catchUp = new StaticStartupCatchUp(new IndexStartupCatchUpResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [IndexPath.NormalizeRoot(root)] = "No checkpoint.",
            }));
        var service = new IndexingService(
            index,
            queue,
            new IndexWatcherService(queue),
            startupCatchUp: catchUp);

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            var item = Assert.Single(queue.Enqueued);
            Assert.Equal(IndexPath.NormalizeRoot(root), item.Root);
            Assert.Equal(IndexChangeKind.RefreshRoot, item.Kind);
            Assert.NotNull(service.CurrentStatus.RootStatusDetails);
            var details = service.CurrentStatus.RootStatusDetails!;
            Assert.Equal(
                "Snapshot scan queued: No checkpoint.",
                details[IndexPath.NormalizeRoot(root)]);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SchedulerQueuesPeriodicSnapshotForNetworkRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-network-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var index = new BlockingFileIndex
        {
            DatabaseInfo = DatabaseInfoWithStrategy(new IndexRootStrategyInfo(
                normalizedRoot,
                IndexLocationKind.NetworkShare,
                IndexUpdateStrategy.ScheduledSnapshotScan,
                "Network share: scheduled snapshot scan",
                "Network shares use snapshot scans.",
                UsnCatchUpEnabled: false,
                WatcherRecommended: false)),
        };
        var queue = new RecordingQueue();
        var catchUp = new StaticStartupCatchUp(new IndexStartupCatchUpResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedRoot },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var service = new IndexingService(
            index,
            queue,
            new IndexWatcherService(queue),
            startupCatchUp: catchUp,
            options: FastSchedulerOptions());

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            await WaitUntilAsync(() => queue.Count > 0, TestContext.Current.CancellationToken);
            var item = queue.SnapshotEnqueued().First();
            Assert.Equal(normalizedRoot, item.Root);
            Assert.Equal(IndexChangeKind.RefreshRoot, item.Kind);
            Assert.Equal(IndexQueuePriority.Low, item.Priority);
            Assert.NotNull(service.CurrentStatus.RootStatusDetails);
            Assert.StartsWith(
                "Snapshot scan queued: scheduled refresh",
                service.CurrentStatus.RootStatusDetails![normalizedRoot],
                StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SchedulerQueuesSnapshotWhenRemovableRootReconnects()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-removable-reconnect-" + Guid.NewGuid().ToString("N"));
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var index = new BlockingFileIndex
        {
            DatabaseInfo = DatabaseInfoWithStrategy(new IndexRootStrategyInfo(
                normalizedRoot,
                IndexLocationKind.Removable,
                IndexUpdateStrategy.OfflineCacheAndReconnectScan,
                "Removable drive: offline cache + reconnect scan",
                "Removable drive indexes are retained while offline.",
                UsnCatchUpEnabled: false,
                WatcherRecommended: true)),
        };
        var queue = new RecordingQueue();
        var service = new IndexingService(
            index,
            queue,
            new IndexWatcherService(queue),
            options: FastSchedulerOptions());

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Empty(queue.Enqueued);
            await WaitUntilAsync(
                () => service.CurrentStatus.RootStatusDetails is { } details &&
                    details.TryGetValue(normalizedRoot, out var detail) &&
                    detail.Contains("cached index retained", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);

            Directory.CreateDirectory(root);

            await WaitUntilAsync(() => queue.Count > 0, TestContext.Current.CancellationToken);
            var item = Assert.Single(queue.SnapshotEnqueued());
            Assert.Equal(normalizedRoot, item.Root);
            Assert.Equal(IndexChangeKind.RefreshRoot, item.Kind);
            Assert.NotNull(service.CurrentStatus.RootStatusDetails);
            Assert.Contains(
                "removable drive reconnected",
                service.CurrentStatus.RootStatusDetails![normalizedRoot],
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SchedulerRunsFullValidationWhenIdleAndQueuesRefreshOnDrift()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-validation-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var strategy = new IndexRootStrategyInfo(
            normalizedRoot,
            IndexLocationKind.LocalUsn,
            IndexUpdateStrategy.UsnJournalAndWatcher,
            "Local NTFS/ReFS: USN journal + watcher",
            string.Empty,
            UsnCatchUpEnabled: true,
            WatcherRecommended: true);
        var index = new BlockingFileIndex
        {
            DatabaseInfo = DatabaseInfoWithStrategy(strategy),
            Locations =
            [
                new IndexedLocationInfo(
                    normalizedRoot,
                    FileCount: 1,
                    LineCount: 1,
                    IndexedUtc: DateTime.UtcNow.AddDays(-2),
                    Profile: "profile",
                    Exists: true,
                    LastFullValidationUtc: DateTime.UtcNow.AddDays(-2)),
            ],
            ValidationResult = IndexValidationResult.Create(
                normalizedRoot,
                DateTime.UtcNow,
                filesChecked: 2,
                filesMatched: 1,
                missingFromIndex: 1,
                changedSinceIndex: 0,
                missingFromDisk: 0,
                failedChecks: 0),
        };
        var queue = new RecordingQueue();
        var options = FastSchedulerOptions();
        options.FullValidationInterval = TimeSpan.FromMilliseconds(25);
        options.FullValidationIdleThreshold = TimeSpan.FromMilliseconds(1);
        var catchUp = new StaticStartupCatchUp(new IndexStartupCatchUpResult(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedRoot },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var service = new IndexingService(
            index,
            queue,
            new IndexWatcherService(queue),
            startupCatchUp: catchUp,
            runtimeCondition: new StaticRuntimeCondition(isIdle: true),
            options: options);

        await service.StartAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        try
        {
            await WaitUntilAsync(
                () => index.ValidateCallCount > 0 &&
                    queue.SnapshotEnqueued().Any(item => item.Kind == IndexChangeKind.RefreshRoot),
                TestContext.Current.CancellationToken);

            Assert.True(index.ValidateCallCount > 0);
            Assert.Contains(
                queue.SnapshotEnqueued(),
                item => string.Equals(item.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                    item.Priority == IndexQueuePriority.Low);
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
    public async Task ResourceProfileAddsThrottleToRootRefreshRequests()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-throttle-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            service.SetResourceProfile(IndexerResourceProfile.Low);
            await service.EnqueueRootRefreshAsync(root, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);

            await index.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(IndexerResourceProfile.Low, service.ResourceProfile);
            Assert.NotNull(index.RefreshRequest);
            Assert.NotNull(index.RefreshRequest.Throttle);
            var throttle = index.RefreshRequest.Throttle!;
            Assert.True(throttle.IsEnabled);
            Assert.Equal(1, throttle.FilesPerPause);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RuntimeOptionsAddThrottleToRootRefreshRequests()
    {
        var index = new BlockingFileIndex();
        var queue = new IndexQueue(index);
        var service = new IndexingService(index, queue, new IndexWatcherService(queue));
        var root = Path.Combine(Path.GetTempPath(), "filesearch-runtime-throttle-" + Guid.NewGuid().ToString("N"));

        await service.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);
        try
        {
            service.SetRuntimeOptions(new IndexerRuntimeOptions(CpuLimitPercent: 25, DiskPauseMilliseconds: 100));
            await service.EnqueueRootRefreshAsync(root, new WalkerOptions(), IndexQueuePriority.High, TestContext.Current.CancellationToken);

            await index.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(25, service.RuntimeOptions.CpuLimitPercent);
            Assert.NotNull(index.RefreshRequest);
            Assert.NotNull(index.RefreshRequest.Throttle);
            var throttle = index.RefreshRequest.Throttle!;
            Assert.True(throttle.IsEnabled);
            Assert.Equal(1, throttle.FilesPerPause);
            Assert.True(throttle.Pause >= TimeSpan.FromMilliseconds(100));
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

    private static IndexingServiceOptions FastSchedulerOptions() =>
        new()
        {
            SchedulerPollInterval = TimeSpan.FromMilliseconds(25),
            SnapshotScanInterval = TimeSpan.FromMilliseconds(25),
            NetworkSnapshotScanInterval = TimeSpan.FromMilliseconds(25),
            RemovableReconnectPollInterval = TimeSpan.FromMilliseconds(25),
        };

    private static IndexDatabaseInfo DatabaseInfoWithStrategy(IndexRootStrategyInfo strategy) =>
        new(
            DatabasePath: string.Empty,
            Exists: true,
            IsCompatible: true,
            SchemaVersion: IndexDatabase.CurrentSchemaVersion,
            DatabaseBytes: 0,
            WalBytes: 0,
            ShmBytes: 0,
            LocationCount: 1,
            TotalFileCount: 0,
            TotalLineCount: 0,
            PendingChangeCount: 0,
            LastIndexedUtc: null,
            RootStrategies: new[] { strategy });

    private sealed class RecordingQueue : IIndexQueue
    {
        private readonly object _sync = new();

        public List<IndexQueueItem> Enqueued { get; } = new();

        public int Count
        {
            get
            {
                lock (_sync)
                    return Enqueued.Count;
            }
        }

        public Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken)
        {
            lock (_sync)
                Enqueued.Add(item with { Root = IndexPath.NormalizeRoot(item.Root) });
            return Task.CompletedTask;
        }

        public IReadOnlyList<IndexQueueItem> SnapshotEnqueued()
        {
            lock (_sync)
                return Enqueued.ToArray();
        }

        public async Task<IndexQueueItem> DequeueAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public void RemoveRoot(string root)
        {
            var normalizedRoot = IndexPath.NormalizeRoot(root);
            lock (_sync)
            {
                Enqueued.RemoveAll(item =>
                    string.Equals(item.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyDictionary<string, int> GetQueuedRootCounts()
        {
            lock (_sync)
            {
                return Enqueued
                    .GroupBy(item => item.Root, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            }
        }

        public Task LoadPendingAsync(
            IReadOnlyDictionary<string, IndexedLocation> locations,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StaticStartupCatchUp : IIndexStartupCatchUpService
    {
        private readonly IndexStartupCatchUpResult _result;

        public StaticStartupCatchUp(IndexStartupCatchUpResult result) => _result = result;

        public Task<IndexStartupCatchUpResult> CatchUpAsync(
            IReadOnlyCollection<IndexedLocation> locations,
            CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    private sealed class StaticRuntimeCondition(bool isIdle) : IIndexerRuntimeCondition
    {
        public bool IsOnBattery => false;

        public bool IsUserIdle(TimeSpan idleThreshold) => isIdle;
    }

    private sealed class BlockingFileIndex : IFileIndex
    {
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IndexRequest? RefreshRequest { get; private set; }

        public List<string> ClearedRoots { get; } = new();

        public bool RefreshCanceled { get; private set; }

        public int ValidateCallCount { get; private set; }

        public IReadOnlyList<IndexedLocationInfo> Locations { get; init; } = Array.Empty<IndexedLocationInfo>();

        public IndexValidationResult ValidationResult { get; init; } =
            IndexValidationResult.Create(
                string.Empty,
                DateTime.UtcNow,
                filesChecked: 0,
                filesMatched: 0,
                missingFromIndex: 0,
                changedSinceIndex: 0,
                missingFromDisk: 0,
                failedChecks: 0);

        public string DatabasePath => string.Empty;

        public IndexDatabaseInfo DatabaseInfo { get; init; } = new(
            DatabasePath: string.Empty,
            Exists: false,
            IsCompatible: false,
            SchemaVersion: IndexDatabase.CurrentSchemaVersion,
            DatabaseBytes: 0,
            WalBytes: 0,
            ShmBytes: 0,
            LocationCount: 0,
            TotalFileCount: 0,
            TotalLineCount: 0,
            PendingChangeCount: 0,
            LastIndexedUtc: null);

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) =>
            RefreshRootAsync(request, IndexRefreshMode.Full, cancellationToken);

        public async Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken)
        {
            RefreshRequest = request;
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
            Task.FromResult(Locations);

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(DatabaseInfo);

        public Task<IndexValidationResult> ValidateRootAsync(IndexRequest request, CancellationToken cancellationToken)
        {
            ValidateCallCount++;
            request.ValidationProgress?.Invoke(new IndexValidationProgress(
                ValidationResult.FilesChecked,
                ValidationResult.FilesMatched,
                ValidationResult.MissingFromIndex,
                ValidationResult.ChangedSinceIndex,
                ValidationResult.MissingFromDisk,
                ValidationResult.FailedChecks));
            return Task.FromResult(ValidationResult with { Root = IndexPath.NormalizeRoot(request.Root) });
        }

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
