using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class RegexCandidateProvider : SearcherCandidateProvider
{
    public RegexCandidateProvider(Searcher searcher)
        : base(searcher, CandidateProviderKind.Regex, "live-regex")
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = RemoveUnavailableSemantic(request.Expression);
        if (!QueryUsesProvider(expression, CandidateProviderKind.Regex))
        {
            if (request.Mode != QueryMode.Regex || string.IsNullOrWhiteSpace(request.RawQuery))
                return null;

            expression = new RegexQuery(request.RawQuery);
        }

        return CreateLiveRequest(request, expression, SearchTarget.Content);
    }
}
