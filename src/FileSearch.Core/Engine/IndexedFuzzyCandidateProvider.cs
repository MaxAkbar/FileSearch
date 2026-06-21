using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed class IndexedFuzzyCandidateProvider : IndexedCandidateProvider
{
    public IndexedFuzzyCandidateProvider(
        IIndexSearch index,
        IndexCoverageService coverageService,
        ISnippetGenerator? snippetGenerator = null)
        : base(index, coverageService, CandidateProviderKind.Fuzzy, "indexed-fuzzy", snippetGenerator)
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = SearcherCandidateProvider.RemoveUnavailableSemantic(request.Expression);
        if (!SearcherCandidateProvider.QueryUsesProvider(expression, CandidateProviderKind.Fuzzy))
            return null;

        return CreateIndexedRequest(request, expression, SearchTarget.Content);
    }
}
