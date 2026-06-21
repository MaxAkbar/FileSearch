using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed class IndexedLexicalCandidateProvider : IndexedCandidateProvider
{
    public IndexedLexicalCandidateProvider(
        IIndexSearch index,
        IndexCoverageService coverageService,
        ISnippetGenerator? snippetGenerator = null)
        : base(index, coverageService, CandidateProviderKind.Lexical, "indexed-lexical", snippetGenerator)
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        var expression = SearcherCandidateProvider.RemoveUnavailableSemantic(request.Expression);
        if (!SearcherCandidateProvider.QueryUsesProvider(expression, CandidateProviderKind.Lexical))
            return null;

        return CreateIndexedRequest(request, expression, SearchTarget.Content);
    }
}
