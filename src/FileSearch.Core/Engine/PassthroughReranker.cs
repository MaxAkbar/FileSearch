using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Engine;

public sealed class PassthroughReranker : IReranker
{
    public Task<IReadOnlyList<RankedSearchResult>> RerankAsync(
        SearchPlan plan,
        IReadOnlyList<RankedSearchResult> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(candidates);
    }
}
