using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

public sealed class IndexedSearcher : ISearcher
{
    private readonly Searcher _liveSearcher;
    private readonly IFileIndex _index;
    private readonly IndexCoverageService _coverageService;
    private readonly IIndexingService? _indexingService;

    public IndexedSearcher(
        Searcher liveSearcher,
        IFileIndex index,
        IndexCoverageService coverageService,
        IIndexingService? indexingService = null)
    {
        _liveSearcher = liveSearcher ?? throw new ArgumentNullException(nameof(liveSearcher));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _coverageService = coverageService ?? throw new ArgumentNullException(nameof(coverageService));
        _indexingService = indexingService;
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _indexingService?.SetForegroundSearchActive(true);
        try
        {
            if (!request.UseIndex)
            {
                await foreach (var hit in _liveSearcher.SearchAsync(request, cancellationToken).ConfigureAwait(false))
                    yield return hit;
                yield break;
            }

            if (_indexingService?.CurrentStatus.IsProcessing == true)
            {
                request.Status?.Invoke("Index updating in background; using live scan");
                var liveRequest = request with
                {
                    UseIndex = false,
                    IndexCandidate = path => QueueFileChanged(request, path),
                };
                await foreach (var hit in _liveSearcher.SearchAsync(liveRequest, cancellationToken).ConfigureAwait(false))
                    yield return hit;
                yield break;
            }

            var coverage = await _coverageService.GetCoverageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!coverage.IsCovered)
            {
                request.Status?.Invoke($"{coverage.Message}; using live scan; indexing scheduled");
                QueueRootRefresh(request);

                var liveRequest = request with
                {
                    UseIndex = false,
                    IndexCandidate = path => QueueFileChanged(request, path),
                };
                await foreach (var hit in _liveSearcher.SearchAsync(liveRequest, cancellationToken).ConfigureAwait(false))
                    yield return hit;
                yield break;
            }

            request.Status?.Invoke(_indexingService?.CurrentStatus.IsProcessing == true
                ? "Index updating in background; using indexed search"
                : coverage.Message);
            await foreach (var hit in _index.SearchAsync(request, cancellationToken).ConfigureAwait(false))
                yield return hit;
        }
        finally
        {
            _indexingService?.SetForegroundSearchActive(false);
        }
    }

    private void QueueRootRefresh(SearchRequest request)
    {
        if (_indexingService is null || request.Roots.Count != 1)
            return;

        _ = _indexingService.EnqueueRootRefreshAsync(
            request.Roots[0],
            request.WalkerOptions,
            IndexQueuePriority.Low,
            CancellationToken.None);
    }

    private void QueueFileChanged(SearchRequest request, string path)
    {
        if (_indexingService is null || request.Roots.Count != 1)
            return;

        _ = _indexingService.EnqueueFileChangedAsync(
            request.Roots[0],
            path,
            request.WalkerOptions,
            CancellationToken.None);
    }
}
