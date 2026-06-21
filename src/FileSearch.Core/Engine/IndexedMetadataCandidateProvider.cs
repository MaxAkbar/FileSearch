using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class IndexedMetadataCandidateProvider : IndexedCandidateProvider
{
    public IndexedMetadataCandidateProvider(
        IIndexSearch index,
        IndexCoverageService coverageService)
        : base(index, coverageService, CandidateProviderKind.Metadata, "indexed-metadata")
    {
    }

    protected override SearchRequest? CreateRequest(SearchPlan plan)
    {
        var request = plan.Request;
        if (request.SearchTarget is SearchTarget.FolderNames or SearchTarget.FileAndFolderNames)
            return null;

        if (request.SearchTarget == SearchTarget.FileNames)
            return CreateIndexedRequest(request, request.Expression, SearchTarget.FileNames);

        var expression = SearcherCandidateProvider.CreateMetadataExpression(request.Expression);
        if (expression is null)
            return null;

        var target = request.Expression is UnifiedQuery { HasContentCriteria: false }
            ? SearchTarget.Content
            : SearchTarget.FileNames;
        return CreateIndexedRequest(request, expression, target);
    }
}
