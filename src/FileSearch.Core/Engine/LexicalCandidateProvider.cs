namespace FileSearch.Core.Engine;

public sealed class LexicalCandidateProvider : SearcherCandidateProvider
{
    public LexicalCandidateProvider(Searcher searcher)
        : base(searcher, CandidateProviderKind.Lexical, "live-lexical")
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = RemoveUnavailableSemantic(request.Expression);
        if (!QueryUsesProvider(expression, CandidateProviderKind.Lexical))
            return null;

        return CreateLiveRequest(request, expression, SearchTarget.Content);
    }
}
