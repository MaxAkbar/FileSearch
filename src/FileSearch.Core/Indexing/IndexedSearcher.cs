using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

public sealed class IndexedSearcher : ISearcher
{
    private readonly ISearcher _liveSearcher;
    private readonly IIndexSearch _index;
    private readonly IndexCoverageService _coverageService;
    private readonly IIndexingSearchCoordinator? _indexingCoordinator;

    public IndexedSearcher(
        ISearcher liveSearcher,
        IIndexSearch index,
        IndexCoverageService coverageService,
        IIndexingService? indexingService = null)
        : this(
            liveSearcher,
            index,
            coverageService,
            indexingService is null ? null : new IndexingServiceSearchCoordinator(indexingService))
    {
    }

    public IndexedSearcher(
        ISearcher liveSearcher,
        IIndexSearch index,
        IndexCoverageService coverageService,
        IIndexingSearchCoordinator? indexingCoordinator)
    {
        _liveSearcher = liveSearcher ?? throw new ArgumentNullException(nameof(liveSearcher));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _coverageService = coverageService ?? throw new ArgumentNullException(nameof(coverageService));
        _indexingCoordinator = indexingCoordinator;
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_indexingCoordinator is not null)
            await _indexingCoordinator.SetForegroundSearchActiveAsync(true, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!request.UseIndex)
            {
                await foreach (var hit in _liveSearcher.SearchAsync(request, cancellationToken).ConfigureAwait(false))
                    yield return hit;
                yield break;
            }

            var indexingStatus = _indexingCoordinator is null
                ? null
                : await _indexingCoordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (indexingStatus?.IsProcessing == true)
            {
                // The writer is actively rebuilding; reads could see a
                // half-built index, so fall back to a live scan and let the
                // in-flight indexing finish on its own.
                request.Status?.Invoke("Index updating in background; using live scan");
                await foreach (var hit in _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken).ConfigureAwait(false))
                    yield return hit;
                yield break;
            }

            if (request.Roots.Count > 1)
            {
                foreach (var root in request.Roots)
                {
                    var rootRequest = request with { Roots = new[] { root } };
                    await foreach (var hit in SearchSingleRootAsync(rootRequest, indexingStatus, cancellationToken).ConfigureAwait(false))
                        yield return hit;
                }

                yield break;
            }

            await foreach (var hit in SearchSingleRootAsync(request, indexingStatus, cancellationToken).ConfigureAwait(false))
                yield return hit;
        }
        finally
        {
            if (_indexingCoordinator is not null)
                await _indexingCoordinator.SetForegroundSearchActiveAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<Hit> SearchSingleRootAsync(
        SearchRequest request,
        IndexingStatus? indexingStatus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.Roots.Count != 1)
        {
            await foreach (var hit in _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        if (HasQueuedWorkForRequestRoot(request, indexingStatus))
        {
            request.Status?.Invoke("Index has pending updates; using live scan");
            await foreach (var hit in _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        var coverage = await _coverageService.GetCoverageAsync(request, cancellationToken).ConfigureAwait(false);
        if (!coverage.IsCovered)
        {
            // One queued root refresh brings the index up to date; it
            // re-walks the tree and diffs by size/mtime, so feeding it
            // per-file candidates from the live scan would only duplicate
            // that work (and hammer the database with per-file writes).
            request.Status?.Invoke($"{coverage.Message}; using live scan; indexing scheduled");
            QueueRootRefresh(request);

            await foreach (var hit in _liveSearcher.SearchAsync(request with { UseIndex = false }, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        request.Status?.Invoke(coverage.Message);
        await foreach (var hit in _index.SearchAsync(request, cancellationToken).ConfigureAwait(false))
            yield return hit;
    }

    private bool HasQueuedWorkForRequestRoot(SearchRequest request, IndexingStatus? indexingStatus)
    {
        if (indexingStatus is null || request.Roots.Count != 1)
            return false;

        var queued = indexingStatus.QueuedRootCounts;
        if (queued is null || queued.Count == 0)
            return false;

        string root;
        try
        {
            root = IndexPath.NormalizeRoot(request.Roots[0]);
        }
        catch
        {
            return false;
        }

        return queued.ContainsKey(root);
    }

    private void QueueRootRefresh(SearchRequest request)
    {
        if (_indexingCoordinator is null || request.Roots.Count != 1)
            return;

        _ = _indexingCoordinator.EnqueueRootRefreshAsync(
            request.Roots[0],
            request.WalkerOptions,
            IndexQueuePriority.Low,
            CancellationToken.None);
    }
}
