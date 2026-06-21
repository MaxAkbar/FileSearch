using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class IndexedRegexCandidateProvider : IndexedCandidateProvider
{
    public IndexedRegexCandidateProvider(
        IIndexSearch index,
        IndexCoverageService coverageService,
        ISnippetGenerator? snippetGenerator = null)
        : base(index, coverageService, CandidateProviderKind.Regex, "indexed-regex", snippetGenerator)
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = SearcherCandidateProvider.RemoveUnavailableSemantic(request.Expression);
        if (!SearcherCandidateProvider.QueryUsesProvider(expression, CandidateProviderKind.Regex))
        {
            if (request.Mode != QueryMode.Regex || string.IsNullOrWhiteSpace(request.RawQuery))
                return null;

            expression = new RegexQuery(request.RawQuery);
        }

        return CreateIndexedRequest(request, expression, SearchTarget.Content);
    }
}
