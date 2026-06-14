using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Indexing;

public sealed class IndexingService : IIndexingService
{
    private static readonly TimeSpan MaxBurst = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BurstRest = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForegroundRefreshDelay = TimeSpan.FromSeconds(5);
    private const long StatusNotificationIntervalMs = 200;

    private readonly IFileIndex _index;
    private readonly IIndexQueue _queue;
    private readonly IIndexWatcherService _watchers;
    private readonly object _sync = new();
    private readonly Dictionary<string, IndexedLocation> _locations = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private CancellationTokenSource? _currentItemCts;
    private IndexQueueItem? _currentItem;
    private bool _paused;
    private bool _foregroundSearchActive;
    private bool _foregroundRefreshYieldRequested;
    private long _lastStatusNotificationTicks;
    private IndexingStatus _status = new(false, false, false, 0, "Indexing idle.");

    private readonly ILogger _logger;

    public IndexingService(
        IFileIndex index,
        IIndexQueue queue,
        IIndexWatcherService watchers,
        ILogger<IndexingService>? logger = null)
    {
        _index = index;
        _queue = queue;
        _watchers = watchers;
        _logger = logger ?? NullLogger<IndexingService>.Instance;
    }

    public event EventHandler<IndexingStatus>? StatusChanged;

    public IndexingStatus CurrentStatus => _status;

    public bool IsPaused => _paused;

    public async Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (var location in locations)
                _locations[IndexPath.NormalizeRoot(location.Root)] = location with { Root = IndexPath.NormalizeRoot(location.Root) };
        }

        foreach (var location in SnapshotLocations())
        {
            if (location.WatchEnabled)
                _watchers.StartWatching(location);
        }

        await _queue.LoadPendingAsync(SnapshotLocationMap(), cancellationToken).ConfigureAwait(false);

        foreach (var location in SnapshotLocations())
        {
            if (Directory.Exists(location.Root))
                await EnqueueRootRefreshAsync(location.Root, location.WalkerOptions, IndexQueuePriority.Low, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (_worker is not null)
                return;

            _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => RunAsync(_workerCts.Token), CancellationToken.None);
        }

        Publish(false, "Background indexing ready.", force: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _watchers.StopAll();

        var cts = _workerCts;
        if (cts is not null)
            await cts.CancelAsync().ConfigureAwait(false);

        var worker = _worker;
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Indexing worker did not stop within the grace period.");
            }
        }

        _worker = null;
        _workerCts?.Dispose();
        _workerCts = null;
        Publish(false, "Background indexing stopped.", force: true);
    }

    public async Task AddOrUpdateLocationAsync(
        IndexedLocation location,
        bool queueInitialRefresh,
        CancellationToken cancellationToken)
    {
        var normalized = location with { Root = IndexPath.NormalizeRoot(location.Root) };
        lock (_sync)
            _locations[normalized.Root] = normalized;

        if (normalized.WatchEnabled)
            _watchers.StartWatching(normalized);
        else
            _watchers.StopWatching(normalized.Root);

        if (queueInitialRefresh)
            await EnqueueRootRefreshAsync(normalized.Root, normalized.WalkerOptions, IndexQueuePriority.Low, cancellationToken)
                .ConfigureAwait(false);

        Publish(false, $"Indexed location ready: {normalized.Root}", force: true);
    }

    public async Task RemoveLocationAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        lock (_sync)
            _locations.Remove(normalizedRoot);

        _watchers.StopWatching(normalizedRoot);
        await _index.ClearAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        Publish(false, $"Removed indexed location: {normalizedRoot}", force: true);
    }

    public Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken) =>
        EnqueueAsync(new IndexQueueItem(
            root,
            null,
            options,
            IndexChangeKind.RefreshRoot,
            priority,
            DateTime.UtcNow.AddSeconds(priority == IndexQueuePriority.High ? 0 : 3),
            Persisted: true),
            cancellationToken);

    public void SetForegroundSearchActive(bool isActive)
    {
        _foregroundSearchActive = isActive;
        if (isActive)
        {
            CancelCurrentRootRefreshForSearch();
            Publish(CurrentStatus.IsProcessing, "Search active; background root indexing will yield.", force: true);
        }
    }

    public void Pause()
    {
        _paused = true;
        CancelCurrentRootRefreshForSearch();
        Publish(CurrentStatus.IsProcessing, "Background indexing paused.", force: true);
    }

    public void Resume()
    {
        _paused = false;
        Publish(CurrentStatus.IsProcessing, "Background indexing resumed.", force: true);
    }

    private async Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken)
    {
        await _queue.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        Publish(CurrentStatus.IsProcessing, $"Indexing {_queue.Count:n0} files queued.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var burst = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(burst, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The worker is the only consumer of the queue — an unhandled
                // exception here used to kill background indexing silently.
                _logger.LogError(ex, "Indexing worker iteration failed; continuing.");
            }
        }
    }

    private async Task RunIterationAsync(Stopwatch burst, CancellationToken cancellationToken)
    {
        // Rest BEFORE dequeuing — resting after used to hold a dequeued item
        // across a 5s delay, where a shutdown silently dropped it (the same
        // loss pattern the pause branch below guards against).
        if (burst.Elapsed >= MaxBurst)
        {
            Publish(false, "Background indexing resting.");
            await Task.Delay(BurstRest, cancellationToken).ConfigureAwait(false);
            burst.Restart();
        }

        var item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);

        if (_paused)
        {
            // Put the item straight back instead of holding it: a shutdown
            // mid-pause can't lose work and queue counts stay truthful.
            // CancellationToken.None so the requeue itself can't be torn by
            // shutdown; Persisted=false because the pending-change row from
            // the original enqueue is still in place. Pause() already told
            // the user — no per-poll status spam here.
            await _queue.EnqueueAsync(
                item with { DueUtc = DateTime.UtcNow.AddSeconds(1), Persisted = false },
                CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_foregroundSearchActive && item.Kind == IndexChangeKind.RefreshRoot)
        {
            await EnqueueAsync(
                item with { DueUtc = DateTime.UtcNow.Add(ForegroundRefreshDelay), Persisted = false },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _currentItem = item;
            _currentItemCts = itemCts;
            _foregroundRefreshYieldRequested = false;
        }

        try
        {
            await ProcessAsync(item, itemCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && WasRefreshYieldRequested(item))
        {
            await EnqueueAsync(
                item with { DueUtc = DateTime.UtcNow.Add(ForegroundRefreshDelay), Persisted = false },
                cancellationToken).ConfigureAwait(false);
            Publish(false, "Index update deferred while search is active.", force: true);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentItemCts, itemCts))
                {
                    _currentItem = null;
                    _currentItemCts = null;
                    _foregroundRefreshYieldRequested = false;
                }
            }
        }
    }

    private async Task ProcessAsync(IndexQueueItem item, CancellationToken cancellationToken)
    {
        Publish(true, Describe(item));

        try
        {
            switch (item.Kind)
            {
                case IndexChangeKind.RefreshRoot:
                    await _index.RefreshRootAsync(
                        new IndexRequest(
                            item.Root,
                            item.WalkerOptions,
                            progress => Publish(true, FormatProgress(progress), progress: progress)),
                        IndexRefreshMode.Incremental,
                        cancellationToken).ConfigureAwait(false);
                    await _index.RemovePendingChangeAsync(item.Root, null, item.Kind, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case IndexChangeKind.UpsertFile:
                    if (item.Path is not null)
                    {
                        await _index.UpsertFileAsync(item.Root, item.Path, item.WalkerOptions, cancellationToken)
                            .ConfigureAwait(false);
                        await _index.RemovePendingChangeAsync(item.Root, item.Path, item.Kind, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                case IndexChangeKind.DeleteFile:
                    if (item.Path is not null)
                    {
                        await _index.DeleteFileAsync(item.Root, item.Path, cancellationToken).ConfigureAwait(false);
                        await _index.RemovePendingChangeAsync(item.Root, item.Path, item.Kind, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
            }

            var queueCount = _queue.Count;
            Publish(false, queueCount == 0 ? "Index ready." : $"Indexing {queueCount:n0} files queued.", force: queueCount == 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing {Kind} failed for {Root}.", item.Kind, item.Root);
            Publish(false, $"Indexing error: {ex.Message}", force: true);
        }
    }

    private List<IndexedLocation> SnapshotLocations()
    {
        lock (_sync)
            return _locations.Values.ToList();
    }

    private void CancelCurrentRootRefreshForSearch()
    {
        lock (_sync)
        {
            if (_currentItem is not { Kind: IndexChangeKind.RefreshRoot } || _currentItemCts is null)
                return;

            _foregroundRefreshYieldRequested = true;
            _currentItemCts.Cancel();
        }
    }

    private bool WasRefreshYieldRequested(IndexQueueItem item)
    {
        lock (_sync)
            return item.Kind == IndexChangeKind.RefreshRoot && _foregroundRefreshYieldRequested;
    }

    private Dictionary<string, IndexedLocation> SnapshotLocationMap()
    {
        lock (_sync)
            return new Dictionary<string, IndexedLocation>(_locations, StringComparer.OrdinalIgnoreCase);
    }

    private void Publish(
        bool isProcessing,
        string message,
        bool force = false,
        IndexProgress? progress = null)
    {
        var activeItem = isProcessing ? _currentItem : null;
        var previous = _status;
        var next = new IndexingStatus(
            IsRunning: _worker is not null,
            IsPaused: _paused,
            IsProcessing: isProcessing,
            QueueLength: _queue.Count,
            Message: message,
            ActiveRoot: activeItem?.Root,
            ActiveKind: activeItem?.Kind,
            QueuedRootCounts: _queue.GetQueuedRootCounts(),
            ActiveProgress: progress);

        if (StatusesEquivalent(previous, next))
            return;

        _status = next;

        var now = Environment.TickCount64;
        if (!force && !ShouldPublishNow(previous, next, now))
            return;

        Interlocked.Exchange(ref _lastStatusNotificationTicks, now);
        StatusChanged?.Invoke(this, next);
    }

    private bool ShouldPublishNow(IndexingStatus previous, IndexingStatus next, long now)
    {
        if (previous.QueueLength == 0 && next.QueueLength > 0)
            return true;

        var last = Interlocked.Read(ref _lastStatusNotificationTicks);
        return now - last >= StatusNotificationIntervalMs;
    }

    private static bool StatusesEquivalent(IndexingStatus left, IndexingStatus right) =>
        left.IsRunning == right.IsRunning &&
        left.IsPaused == right.IsPaused &&
        left.IsProcessing == right.IsProcessing &&
        left.QueueLength == right.QueueLength &&
        string.Equals(left.Message, right.Message, StringComparison.Ordinal) &&
        string.Equals(left.ActiveRoot, right.ActiveRoot, StringComparison.OrdinalIgnoreCase) &&
        left.ActiveKind == right.ActiveKind &&
        QueuedRootCountsEqual(left.QueuedRootCounts, right.QueuedRootCounts) &&
        Equals(left.ActiveProgress, right.ActiveProgress);

    private static bool QueuedRootCountsEqual(
        IReadOnlyDictionary<string, int>? left,
        IReadOnlyDictionary<string, int>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        foreach (var (root, count) in left)
            if (!right.TryGetValue(root, out var otherCount) || otherCount != count)
                return false;

        return true;
    }

    private static string Describe(IndexQueueItem item) =>
        item.Kind switch
        {
            IndexChangeKind.RefreshRoot => $"Index updating in background: {item.Root}",
            IndexChangeKind.UpsertFile => $"Indexing changed file: {Path.GetFileName(item.Path)}",
            IndexChangeKind.DeleteFile => $"Removing deleted file from index: {Path.GetFileName(item.Path)}",
            _ => "Indexing...",
        };

    private static string FormatProgress(IndexProgress progress)
    {
        var changed = progress.FilesIndexed + progress.FilesRemoved;
        var failed = progress.FilesFailed > 0 ? $", {progress.FilesFailed:n0} failed" : string.Empty;
        return $"Scanning {progress.FilesEnumerated:n0}; {changed:n0} changed, {progress.FilesSkippedUnchanged:n0} unchanged{failed}";
    }
}
