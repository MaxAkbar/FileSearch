using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed class IndexQueue : IIndexQueue
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly Dictionary<string, IndexQueueItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly IFileIndex _index;

    public IndexQueue(IFileIndex index)
    {
        _index = index;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _items.Count;
        }
    }

    public async Task EnqueueAsync(IndexQueueItem item, CancellationToken cancellationToken)
    {
        var due = item.DueUtc == default
            ? DateTime.UtcNow.Add(DefaultDebounce)
            : item.DueUtc;
        var normalized = item with
        {
            Root = IndexPath.NormalizeRoot(item.Root),
            Path = item.Path is null ? null : IndexPath.NormalizeFile(item.Path),
            DueUtc = due,
        };

        lock (_sync)
        {
            var key = BuildKey(normalized);
            if (_items.TryGetValue(key, out var existing))
                normalized = Coalesce(existing, normalized);

            _items[key] = normalized;
        }

        if (normalized.Persisted && normalized.Path is not null)
            await _index.SavePendingChangeAsync(normalized.Root, normalized.Path, normalized.Kind, cancellationToken)
                .ConfigureAwait(false);

        _signal.Release();
    }

    public async Task<IndexQueueItem> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            IndexQueueItem? ready = null;
            TimeSpan? delay = null;

            lock (_sync)
            {
                if (_items.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    var next = _items.Values
                        .OrderBy(item => item.DueUtc)
                        .ThenBy(item => item.Priority)
                        .First();

                    if (next.DueUtc <= now)
                    {
                        _items.Remove(BuildKey(next));
                        ready = next;
                    }
                    else
                    {
                        delay = next.DueUtc - now;
                    }
                }
            }

            if (ready is not null)
                return ready;

            if (delay is { } dueDelay)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waitTask = _signal.WaitAsync(waitCts.Token);
                var delayTask = Task.Delay(dueDelay, cancellationToken);
                var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (waitTask.IsCompleted)
                    await waitTask.ConfigureAwait(false);
                else if (completed == delayTask)
                {
                    await waitCts.CancelAsync().ConfigureAwait(false);
                    await delayTask.ConfigureAwait(false);
                }
            }
            else
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyDictionary<string, int> GetQueuedRootCounts()
    {
        lock (_sync)
        {
            return _items.Values
                .GroupBy(item => item.Root, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task LoadPendingAsync(
        IReadOnlyDictionary<string, IndexedLocation> locations,
        CancellationToken cancellationToken)
    {
        var pending = await _index.GetPendingChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in pending)
        {
            if (!locations.TryGetValue(IndexPath.NormalizeRoot(change.Root), out var location))
                continue;

            await EnqueueAsync(
                new IndexQueueItem(
                    location.Root,
                    change.Path,
                    location.WalkerOptions,
                    change.Kind,
                    IndexQueuePriority.Normal,
                    DateTime.UtcNow.Add(DefaultDebounce),
                    Persisted: false),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static IndexQueueItem Coalesce(IndexQueueItem existing, IndexQueueItem incoming)
    {
        if (incoming.Kind == IndexChangeKind.RefreshRoot || existing.Kind == IndexChangeKind.RefreshRoot)
            return incoming.Kind == IndexChangeKind.RefreshRoot ? incoming : existing;

        return incoming with
        {
            DueUtc = incoming.DueUtc > existing.DueUtc ? incoming.DueUtc : existing.DueUtc,
            Priority = incoming.Priority < existing.Priority ? incoming.Priority : existing.Priority,
        };
    }

    private static string BuildKey(IndexQueueItem item) =>
        item.Kind == IndexChangeKind.RefreshRoot
            ? $"R|{item.Root}"
            : $"F|{item.Root}|{item.Path}";
}
