using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FileSearch.Core.Indexing;

public sealed class IndexWatcherService : IIndexWatcherService
{
    private readonly IIndexQueue _queue;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public IndexWatcherService(IIndexQueue queue)
    {
        _queue = queue;
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

        watcher.Created += (_, e) => QueueUpsertOrRootRefresh(location, e.FullPath);
        watcher.Changed += (_, e) => QueueUpsertOrRootRefresh(location, e.FullPath);
        watcher.Deleted += (_, e) => QueueDelete(location, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            QueueDelete(location, e.OldFullPath);
            QueueUpsertOrRootRefresh(location, e.FullPath);
        };
        watcher.Error += (_, _) => QueueRootRefresh(location);

        _watchers[root] = watcher;
    }

    public void StopWatching(string root)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        if (!_watchers.Remove(normalizedRoot, out var watcher))
            return;

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void StopAll()
    {
        foreach (var root in _watchers.Keys.ToArray())
            StopWatching(root);
    }

    private void QueueUpsertOrRootRefresh(IndexedLocation location, string path)
    {
        if (Directory.Exists(path))
        {
            QueueRootRefresh(location);
            return;
        }

        _ = _queue.EnqueueAsync(
            new IndexQueueItem(
                location.Root,
                path,
                location.WalkerOptions,
                IndexChangeKind.UpsertFile,
                IndexQueuePriority.Normal,
                DateTime.UtcNow.AddSeconds(2),
                Persisted: true),
            CancellationToken.None);
    }

    private void QueueDelete(IndexedLocation location, string path)
    {
        _ = _queue.EnqueueAsync(
            new IndexQueueItem(
                location.Root,
                path,
                location.WalkerOptions,
                IndexChangeKind.DeleteFile,
                IndexQueuePriority.Normal,
                DateTime.UtcNow.AddSeconds(2),
                Persisted: true),
            CancellationToken.None);
    }

    private void QueueRootRefresh(IndexedLocation location)
    {
        _ = _queue.EnqueueAsync(
            new IndexQueueItem(
                location.Root,
                null,
                location.WalkerOptions,
                IndexChangeKind.RefreshRoot,
                IndexQueuePriority.Low,
                DateTime.UtcNow.AddSeconds(5),
                Persisted: false),
            CancellationToken.None);
    }
}
