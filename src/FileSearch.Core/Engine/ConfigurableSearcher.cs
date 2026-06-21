using System;
using System.Collections.Generic;
using System.Threading;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class ConfigurableSearcher : ISearcher
{
    private readonly ISearcher _legacySearcher;
    private readonly IHybridSearcher _hybridSearcher;
    private readonly SearchOptions _options;

    public ConfigurableSearcher(
        ISearcher legacySearcher,
        IHybridSearcher hybridSearcher,
        SearchOptions? options = null)
    {
        _legacySearcher = legacySearcher ?? throw new ArgumentNullException(nameof(legacySearcher));
        _hybridSearcher = hybridSearcher ?? throw new ArgumentNullException(nameof(hybridSearcher));
        _options = options ?? new SearchOptions();
    }

    public IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var selected = _options.EngineMode == SearchEngineMode.Hybrid ||
            request.Expression is UnifiedQuery { HasSemantic: true }
            ? _hybridSearcher
            : _legacySearcher;
        return selected.SearchAsync(request, cancellationToken);
    }
}
