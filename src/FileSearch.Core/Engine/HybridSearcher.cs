using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FileSearch.Core.Engine;

public sealed class HybridSearcher : IHybridSearcher
{
    private readonly IHybridRetrievalPipeline _pipeline;

    public HybridSearcher(IHybridRetrievalPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await _pipeline.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = result.BestCandidate;
            if (candidate is null)
                continue;

            yield return ToHit(result, candidate);
        }
    }

    private static Hit ToHit(RankedSearchResult result, SearchCandidate candidate) =>
        new(
            result.Path,
            candidate.LineNumber ?? 0,
            candidate.DisplayText,
            candidate.Highlights,
            candidate.Kind,
            result.Score,
            candidate.SizeBytes,
            candidate.ModifiedUtc,
            candidate.Route ?? HitRoute.Live,
            candidate.Anchor,
            candidate.ContentUnitId,
            candidate.Locator,
            candidate.Snippet);
}
