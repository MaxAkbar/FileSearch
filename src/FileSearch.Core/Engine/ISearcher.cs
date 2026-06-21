using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Engine;

/// <summary>
/// Streams <see cref="Hit"/>s for a given <see cref="SearchRequest"/>.
/// Async-on-the-outside, parallel-on-the-inside: callers can <c>await foreach</c>
/// without blocking while many files are processed in parallel internally.
/// </summary>
public interface ISearcher
{
    IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

public interface IHybridSearcher : ISearcher
{
}
