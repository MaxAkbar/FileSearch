using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed class IndexQueue : IIndexQueue, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling on how far coalescing may push an item past its first enqueue.
    /// Without it, steady events (a log file under append, a busy folder)
    /// slide the due time forever and the item is never processed.
    /// </summary>
    private static readonly TimeSpan MaxDebounceWindow = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, IndexQueueItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly IPendingChangeStore _pendingChanges;

    public IndexQueue(IPendingChangeStore pendingChanges)
    {
        _pendingChanges = pendingChanges;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _items.Count;
        }
    }

    public void Dispose() => _signal.Dispose();

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
            FirstQueuedUtc = item.FirstQueuedUtc == default ? DateTime.UtcNow : item.FirstQueuedUtc,
        };

        bool persist;
        lock (_sync)
        {
            // A queued root refresh re-walks the tree and diffs every file by
            // size/mtime when it runs, so per-file changes under that root are
            // redundant: drop incoming ones and purge queued ones when a
            // refresh arrives. Persisting the root refresh also clears older
            // pending rows for that root.
            if (IsContentRootRefresh(normalized.Kind))
            {
                RemoveFileItemsForRootLocked(normalized.Root);
            }
            else if (_items.ContainsKey(RefreshKey(normalized.Root)))
            {
                return;
            }

            var key = BuildKey(normalized);
            var alreadyPersisted = false;
            if (_items.TryGetValue(key, out var existing))
            {
                alreadyPersisted = existing.Persisted;
                normalized = Coalesce(existing, normalized);
            }

            _items[key] = normalized;
            persist = normalized.Persisted &&
                !alreadyPersisted &&
                (IsRootLevelChange(normalized.Kind) || normalized.Path is not null);
        }

        if (persist)
            await _pendingChanges.SavePendingChangeAsync(normalized.Root, normalized.Path, normalized.Kind, cancellationToken)
                .ConfigureAwait(false);

        _signal.Release();
    }

    private void RemoveFileItemsForRootLocked(string root)
    {
        List<string>? stale = null;
        foreach (var (key, queued) in _items)
        {
            if (!IsContentRootRefresh(queued.Kind) &&
                string.Equals(queued.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                (stale ??= new List<string>()).Add(key);
            }
        }

        if (stale is null)
            return;

        foreach (var key in stale)
            _items.Remove(key);
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

                    // Single pass: among items already due, pick the highest
                    // priority first (then the oldest) — sorting by due time
                    // first made priority a mere timestamp tie-break.
                    // Otherwise sleep until the earliest item comes due.
                    IndexQueueItem? bestDue = null;
                    var earliest = DateTime.MaxValue;

                    foreach (var item in _items.Values)
                    {
                        if (item.DueUtc <= now &&
                            (bestDue is null ||
                             item.Priority < bestDue.Priority ||
                             (item.Priority == bestDue.Priority && item.DueUtc < bestDue.DueUtc)))
                        {
                            bestDue = item;
                        }

                        if (item.DueUtc < earliest)
                            earliest = item.DueUtc;
                    }

                    if (bestDue is not null)
                    {
                        _items.Remove(BuildKey(bestDue));
                        ready = bestDue;
                    }
                    else
                    {
                        delay = earliest - now;
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

    public void RemoveRoot(string root)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var removed = 0;

        lock (_sync)
        {
            List<string>? stale = null;
            foreach (var (key, queued) in _items)
            {
                if (string.Equals(queued.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    (stale ??= new List<string>()).Add(key);
            }

            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    if (_items.Remove(key))
                        removed++;
                }
            }
        }

        if (removed > 0)
            _signal.Release();
    }

    /// <summary>Test hook: point-in-time copy of the queued items.</summary>
    internal IReadOnlyList<IndexQueueItem> SnapshotItems()
    {
        lock (_sync)
            return _items.Values.ToList();
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
        var pending = await _pendingChanges.GetPendingChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in pending)
        {
            if (!locations.TryGetValue(IndexPath.NormalizeRoot(change.Root), out var location))
                continue;
            if (!IsRootLevelChange(change.Kind) && change.Path is null)
                continue;

            await EnqueueAsync(
                new IndexQueueItem(
                    location.Root,
                    IsRootLevelChange(change.Kind) ? null : change.Path,
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
        var firstQueued = existing.FirstQueuedUtc <= incoming.FirstQueuedUtc
            ? existing.FirstQueuedUtc
            : incoming.FirstQueuedUtc;

        if (IsContentRootRefresh(incoming.Kind) || IsContentRootRefresh(existing.Kind))
        {
            // A queued refresh keeps its EARLIEST due time. Taking the later
            // one let every new filesystem event push the refresh further
            // away, starving it forever under steady activity — while the
            // per-file changes it supersedes were being dropped.
            var refresh = incoming.RefreshMode == IndexRefreshMode.Full
                ? incoming
                : existing.RefreshMode == IndexRefreshMode.Full
                    ? existing
                    : IsContentRootRefresh(incoming.Kind)
                        ? incoming
                        : existing;
            return refresh with
            {
                DueUtc = existing.DueUtc <= incoming.DueUtc ? existing.DueUtc : incoming.DueUtc,
                Priority = incoming.Priority < existing.Priority ? incoming.Priority : existing.Priority,
                FirstQueuedUtc = firstQueued,
            };
        }

        // File items debounce: rapid rewrites extend the due time so one
        // upsert covers them — but never past MaxDebounceWindow from the
        // first enqueue, or a file under constant append is never indexed.
        var slidDue = incoming.DueUtc > existing.DueUtc ? incoming.DueUtc : existing.DueUtc;
        var dueCeiling = firstQueued.Add(MaxDebounceWindow);
        return incoming with
        {
            DueUtc = slidDue <= dueCeiling ? slidDue : dueCeiling,
            Priority = incoming.Priority < existing.Priority ? incoming.Priority : existing.Priority,
            FirstQueuedUtc = firstQueued,
        };
    }

    private static string BuildKey(IndexQueueItem item) =>
        item.Kind switch
        {
            IndexChangeKind.RefreshRoot => RefreshKey(item.Root),
            IndexChangeKind.RefreshSemanticRoot => $"S|{item.Root}",
            _ => $"F|{item.Root}|{item.Path}",
        };

    private static string RefreshKey(string root) => $"R|{root}";

    private static bool IsContentRootRefresh(IndexChangeKind kind) =>
        kind is IndexChangeKind.RefreshRoot;

    private static bool IsRootLevelChange(IndexChangeKind kind) =>
        IsContentRootRefresh(kind) || kind is IndexChangeKind.RefreshSemanticRoot;
}
