using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Engine;

public interface IQueryPlanner
{
    SearchPlan CreatePlan(SearchRequest request);
}

public interface IHybridRetrievalPipeline
{
    Task<IReadOnlyList<RankedSearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken);
}

public interface ICandidateProvider
{
    CandidateProviderKind Provider { get; }

    IAsyncEnumerable<SearchCandidate> FindAsync(
        SearchPlan plan,
        CancellationToken cancellationToken);
}

public interface IRoutedCandidateProvider : ICandidateProvider
{
    CandidateProviderRoute Route { get; }

    Task<CandidateProviderAvailability> GetAvailabilityAsync(
        SearchPlan plan,
        CancellationToken cancellationToken);
}

public interface IResultFusion
{
    IReadOnlyList<RankedSearchResult> Fuse(
        SearchPlan plan,
        IReadOnlyCollection<SearchCandidate> candidates);
}

public interface IReranker
{
    Task<IReadOnlyList<RankedSearchResult>> RerankAsync(
        SearchPlan plan,
        IReadOnlyList<RankedSearchResult> candidates,
        CancellationToken cancellationToken);
}
