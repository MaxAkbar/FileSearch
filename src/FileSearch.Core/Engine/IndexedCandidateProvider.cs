using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public abstract class IndexedCandidateProvider : IRoutedCandidateProvider
{
    private readonly IIndexSearch _index;
    private readonly IndexCoverageService _coverageService;
    private readonly string _providerId;

    protected IndexedCandidateProvider(
        IIndexSearch index,
        IndexCoverageService coverageService,
        CandidateProviderKind provider,
        string providerId)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _coverageService = coverageService ?? throw new ArgumentNullException(nameof(coverageService));
        Provider = provider;
        _providerId = string.IsNullOrWhiteSpace(providerId) ? provider.ToString() : providerId;
    }

    public CandidateProviderKind Provider { get; }

    public CandidateProviderRoute Route => CandidateProviderRoute.Indexed;

    public async Task<CandidateProviderAvailability> GetAvailabilityAsync(
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.HasEnabledProvider(Provider))
            return CandidateProviderAvailability.Unavailable("Provider is not enabled by the search plan.");

        var request = CreateRequest(plan);
        if (request is null)
            return CandidateProviderAvailability.Unavailable("Provider does not support this query.");

        if (!request.UseIndex)
            return CandidateProviderAvailability.Unavailable("Index is disabled for this request.");

        var coverage = await _coverageService.GetCoverageAsync(request, cancellationToken).ConfigureAwait(false);
        return coverage.IsCovered
            ? CandidateProviderAvailability.Available
            : CandidateProviderAvailability.Unavailable(coverage.Message);
    }

    public async IAsyncEnumerable<SearchCandidate> FindAsync(
        SearchPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var availability = await GetAvailabilityAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
            yield break;

        var request = CreateRequest(plan);
        if (request is null)
            yield break;

        await foreach (var hit in _index.SearchAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return SearchCandidate.FromHit(
                hit,
                Provider,
                _providerId,
                CreateExplanations(hit));
        }
    }

    protected abstract SearchRequest? CreateRequest(SearchPlan plan);

    protected virtual IReadOnlyList<SearchResultExplanation> CreateExplanations(Hit hit) =>
        new[]
        {
            new SearchResultExplanation(
                "indexed-candidate",
                "Candidate came from the file index.",
                Provider,
                hit.Score),
        };

    protected static SearchRequest CreateIndexedRequest(
        SearchRequest request,
        Query expression,
        SearchTarget target) =>
        request with
        {
            Expression = SearcherCandidateProvider.RemoveUnavailableSemantic(expression),
            SearchTarget = target,
            UseIndex = true,
        };
}
