namespace FileSearch.Core.Engine;

public sealed class MetadataCandidateProvider : SearcherCandidateProvider
{
    public MetadataCandidateProvider(Searcher searcher)
        : base(searcher, CandidateProviderKind.Metadata, "live-metadata")
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        if (request.SearchTarget != SearchTarget.Content)
            return CreateLiveRequest(request, RemoveUnavailableSemantic(request.Expression), request.SearchTarget);

        var expression = CreateMetadataExpression(request.Expression);
        if (expression is null)
            return null;

        var target = request.Expression is FileSearch.Core.Queries.UnifiedQuery { HasContentCriteria: false }
            ? SearchTarget.Content
            : SearchTarget.FileAndFolderNames;
        return CreateLiveRequest(request, expression, target);
    }
}
