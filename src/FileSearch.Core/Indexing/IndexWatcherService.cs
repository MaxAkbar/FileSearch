using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Indexing;

public sealed class IndexWatcherService : IIndexWatcherService
{
    private readonly IIndexQueue _queue;
    private readonly object _sync = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IndexWatcherDiagnosticInfo> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public IndexWatcherService(IIndexQueue queue, ILogger<IndexWatcherService>? logger = null)
    {
        _queue = queue;
        _logger = logger ?? NullLogger<IndexWatcherService>.Instance;
    }

    public void StartWatching(IndexedLocation location)
    {
        var root = IndexPath.NormalizeRoot(location.Root);
        StopWatching(root);

        if (!location.WatchEnabled || !Directory.Exists(root))
            return;

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = location.WalkerOptions.Recursive,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };

        watcher.Created += (_, e) =>
        {
            RecordEvent(root);
            QueueUpsertOrRootRefresh(location, e.FullPath);
        };
        watcher.Changed += (_, e) =>
        {
            RecordEvent(root);
            QueueUpsertOrRootRefresh(location, e.FullPath);
        };
        watcher.Deleted += (_, e) =>
        {
            RecordEvent(root);
            QueueDelete(location, e.FullPath);
        };
        watcher.Renamed += (_, e) =>
        {
            RecordEvent(root);
            QueueDelete(location, e.OldFullPath);
            QueueUpsertOrRootRefresh(location, e.FullPath);
        };
        watcher.Error += (_, e) =>
        {
            RecordError(root, e.GetException());
            QueueRootRefresh(location);
        };

        lock (_sync)
        {
            _watchers[root] = watcher;
            _diagnostics[root] = CurrentDiagnostics(root) with { IsWatching = true };
        }
    }

    public void StopWatching(string root)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        FileSystemWatcher? watcher;
        lock (_sync)
        {
            if (!_watchers.Remove(normalizedRoot, out watcher))
                return;

            _diagnostics[normalizedRoot] = CurrentDiagnostics(normalizedRoot) with { IsWatching = false };
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void StopAll()
    {
        string[] roots;
        lock (_sync)
            roots = _watchers.Keys.ToArray();

        foreach (var root in roots)
            StopWatching(root);
    }

    public IReadOnlyDictionary<string, IndexWatcherDiagnosticInfo> GetDiagnostics()
    {
        lock (_sync)
            return new Dictionary<string, IndexWatcherDiagnosticInfo>(_diagnostics, StringComparer.OrdinalIgnoreCase);
    }

    private void RecordEvent(string root)
    {
        lock (_sync)
            _diagnostics[root] = CurrentDiagnostics(root) with { LastEventUtc = DateTime.UtcNow, IsWatching = true };
    }

    private void RecordError(string root, Exception? error)
    {
        lock (_sync)
        {
            _diagnostics[root] = CurrentDiagnostics(root) with
            {
                LastErrorUtc = DateTime.UtcNow,
                LastError = error?.Message ?? "Watcher buffer overflow or change notification error.",
                IsWatching = _watchers.ContainsKey(root),
            };
        }
    }

    private IndexWatcherDiagnosticInfo CurrentDiagnostics(string root) =>
        _diagnostics.TryGetValue(root, out var diagnostics)
            ? diagnostics
            : new IndexWatcherDiagnosticInfo(root, IsWatching: false, LastEventUtc: null, LastErrorUtc: null, LastError: null);

    private void QueueUpsertOrRootRefresh(IndexedLocation location, string path)
    {
        if (Directory.Exists(path))
        {
            QueueRootRefresh(location);
            return;
        }

        Enqueue(new IndexQueueItem(
            location.Root,
            path,
            location.WalkerOptions,
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow.AddSeconds(2),
            Persisted: true));
    }

    private void QueueDelete(IndexedLocation location, string path)
    {
        Enqueue(new IndexQueueItem(
            location.Root,
            path,
            location.WalkerOptions,
            IndexChangeKind.DeleteFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow.AddSeconds(2),
            Persisted: true));
    }

    private void QueueRootRefresh(IndexedLocation location)
    {
        Enqueue(new IndexQueueItem(
            location.Root,
            null,
            location.WalkerOptions,
            IndexChangeKind.RefreshRoot,
            IndexQueuePriority.Low,
            DateTime.UtcNow.AddSeconds(5),
            Persisted: true));
    }

    /// <summary>
    /// Watcher callbacks can't await; observe the enqueue so a persistence
    /// failure is logged instead of vanishing as an unobserved task fault.
    /// </summary>
    private void Enqueue(IndexQueueItem item)
    {
        _queue.EnqueueAsync(item, CancellationToken.None).ContinueWith(
            task => _logger.LogWarning(
                task.Exception!.GetBaseException(),
                "Failed to queue index change for {Path}.",
                item.Path ?? item.Root),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
