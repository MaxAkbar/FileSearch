namespace FileSearch.Core.Engine;

public sealed class FuzzyCandidateProvider : SearcherCandidateProvider
{
    public FuzzyCandidateProvider(Searcher searcher)
        : base(searcher, CandidateProviderKind.Fuzzy, "live-fuzzy")
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = RemoveUnavailableSemantic(request.Expression);
        if (!QueryUsesProvider(expression, CandidateProviderKind.Fuzzy))
            return null;

        return CreateLiveRequest(request, expression, SearchTarget.Content);
    }
}
