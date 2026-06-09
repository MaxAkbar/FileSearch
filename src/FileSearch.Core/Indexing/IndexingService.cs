using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed class IndexingService : IIndexingService
{
    private static readonly TimeSpan MaxBurst = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BurstRest = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForegroundRefreshDelay = TimeSpan.FromSeconds(5);

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
    private IndexingStatus _status = new(false, false, false, 0, "Indexing idle.");

    public IndexingService(
        IFileIndex index,
        IIndexQueue queue,
        IIndexWatcherService watchers)
    {
        _index = index;
        _queue = queue;
        _watchers = watchers;
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
            var stats = await _index.GetStatsAsync(location.Root, cancellationToken).ConfigureAwait(false);
            if (!stats.Exists)
                await EnqueueRootRefreshAsync(location.Root, location.WalkerOptions, IndexQueuePriority.Low, cancellationToken).ConfigureAwait(false);
        }

        if (_worker is not null)
            return;

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_workerCts.Token), CancellationToken.None);
        Publish(false, "Background indexing ready.");
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
            catch
            {
            }
        }

        _worker = null;
        _workerCts?.Dispose();
        _workerCts = null;
        Publish(false, "Background indexing stopped.");
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

        Publish(false, $"Indexed location ready: {normalized.Root}");
    }

    public async Task RemoveLocationAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        lock (_sync)
            _locations.Remove(normalizedRoot);

        _watchers.StopWatching(normalizedRoot);
        await _index.ClearAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        Publish(false, $"Removed indexed location: {normalizedRoot}");
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
            Persisted: false),
            cancellationToken);

    public Task EnqueueFileChangedAsync(
        string root,
        string path,
        WalkerOptions options,
        CancellationToken cancellationToken) =>
        EnqueueAsync(new IndexQueueItem(
            root,
            path,
            options,
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow.AddSeconds(2),
            Persisted: true),
            cancellationToken);

    public Task EnqueueFileDeletedAsync(
        string root,
        string path,
        WalkerOptions options,
        CancellationToken cancellationToken) =>
        EnqueueAsync(new IndexQueueItem(
            root,
            path,
            options,
            IndexChangeKind.DeleteFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow.AddSeconds(2),
            Persisted: true),
            cancellationToken);

    public void SetForegroundSearchActive(bool isActive)
    {
        _foregroundSearchActive = isActive;
        if (isActive)
        {
            CancelCurrentRootRefreshForSearch();
            Publish(CurrentStatus.IsProcessing, "Search active; background root indexing will yield.");
        }
    }

    public void Pause()
    {
        _paused = true;
        CancelCurrentRootRefreshForSearch();
        Publish(CurrentStatus.IsProcessing, "Background indexing paused.");
    }

    public void Resume()
    {
        _paused = false;
        Publish(CurrentStatus.IsProcessing, "Background indexing resumed.");
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
            var item = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);

            while (_paused && !cancellationToken.IsCancellationRequested)
            {
                Publish(false, $"Background indexing paused; {_queue.Count + 1:n0} queued.");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            if (_foregroundSearchActive && item.Kind == IndexChangeKind.RefreshRoot)
            {
                await EnqueueAsync(item with { DueUtc = DateTime.UtcNow.Add(ForegroundRefreshDelay) }, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (burst.Elapsed >= MaxBurst)
            {
                Publish(false, "Background indexing resting.");
                await Task.Delay(BurstRest, cancellationToken).ConfigureAwait(false);
                burst.Restart();
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
                    item with { DueUtc = DateTime.UtcNow.Add(ForegroundRefreshDelay) },
                    cancellationToken).ConfigureAwait(false);
                Publish(false, "Index update deferred while search is active.");
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
                        new IndexRequest(item.Root, item.WalkerOptions),
                        IndexRefreshMode.Incremental,
                        cancellationToken).ConfigureAwait(false);
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

            Publish(false, _queue.Count == 0 ? "Index ready." : $"Indexing {_queue.Count:n0} files queued.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Publish(false, $"Indexing error: {ex.Message}");
        }
    }

    private IReadOnlyList<IndexedLocation> SnapshotLocations()
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

    private IReadOnlyDictionary<string, IndexedLocation> SnapshotLocationMap()
    {
        lock (_sync)
            return new Dictionary<string, IndexedLocation>(_locations, StringComparer.OrdinalIgnoreCase);
    }

    private void Publish(bool isProcessing, string message)
    {
        var activeItem = isProcessing ? _currentItem : null;
        _status = new IndexingStatus(
            IsRunning: _worker is not null,
            IsPaused: _paused,
            IsProcessing: isProcessing,
            QueueLength: _queue.Count,
            Message: message,
            ActiveRoot: activeItem?.Root,
            ActiveKind: activeItem?.Kind,
            QueuedRootCounts: _queue.GetQueuedRootCounts());
        StatusChanged?.Invoke(this, _status);
    }

    private static string Describe(IndexQueueItem item) =>
        item.Kind switch
        {
            IndexChangeKind.RefreshRoot => $"Index updating in background: {item.Root}",
            IndexChangeKind.UpsertFile => $"Indexing changed file: {Path.GetFileName(item.Path)}",
            IndexChangeKind.DeleteFile => $"Removing deleted file from index: {Path.GetFileName(item.Path)}",
            _ => "Indexing...",
        };
}
