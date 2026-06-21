using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public abstract class SearcherCandidateProvider : IRoutedCandidateProvider
{
    private readonly Searcher _searcher;
    private readonly string _providerId;

    protected SearcherCandidateProvider(
        Searcher searcher,
        CandidateProviderKind provider,
        string providerId)
    {
        _searcher = searcher ?? throw new ArgumentNullException(nameof(searcher));
        Provider = provider;
        _providerId = string.IsNullOrWhiteSpace(providerId) ? provider.ToString() : providerId;
    }

    public CandidateProviderKind Provider { get; }

    public CandidateProviderRoute Route => CandidateProviderRoute.Live;

    public Task<CandidateProviderAvailability> GetAvailabilityAsync(
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!plan.HasEnabledProvider(Provider))
            return Task.FromResult(CandidateProviderAvailability.Unavailable("Provider is not enabled by the search plan."));

        return Task.FromResult(
            CreateRequest(plan) is null
                ? CandidateProviderAvailability.Unavailable("Provider does not support this query.")
                : CandidateProviderAvailability.Available);
    }

    public async IAsyncEnumerable<SearchCandidate> FindAsync(
        SearchPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.HasEnabledProvider(Provider))
            yield break;

        var request = CreateRequest(plan);
        if (request is null)
            yield break;

        await foreach (var hit in _searcher.SearchAsync(request, cancellationToken).ConfigureAwait(false))
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
        Array.Empty<SearchResultExplanation>();

    protected static SearchRequest CreateLiveRequest(
        SearchRequest request,
        Query expression,
        SearchTarget target) =>
        request with
        {
            Expression = expression,
            SearchTarget = target,
            UseIndex = false,
        };

    internal static Query RemoveUnavailableSemantic(Query expression)
    {
        if (expression is not UnifiedQuery unified || !unified.HasUnavailableSemantic)
            return expression;

        return new UnifiedQuery(
            unified.ContentQuery,
            unified.Filters with { SemanticTerms = Array.Empty<string>() },
            unified.Chips);
    }

    internal static bool QueryUsesProvider(Query query, CandidateProviderKind provider)
    {
        query = RemoveUnavailableSemantic(query);

        switch (query)
        {
            case MatchAllQuery:
                return false;

            case UnifiedQuery unified:
                return QueryUsesProvider(unified.ContentQuery, provider);

            case TermQuery:
                return provider == CandidateProviderKind.Lexical;

            case RegexQuery:
                return provider == CandidateProviderKind.Regex;

            case FuzzyQuery:
                return provider == CandidateProviderKind.Fuzzy;

            case NearQuery near:
                return provider == CandidateProviderKind.Lexical ||
                    QueryUsesProvider(near.Left, provider) ||
                    QueryUsesProvider(near.Right, provider);

            case NotQuery not:
                return QueryUsesProvider(not.Child, provider);

            case AndQuery and:
                return and.Children.Any(child => QueryUsesProvider(child, provider));

            case OrQuery or:
                return or.Children.Any(child => QueryUsesProvider(child, provider));

            default:
                return provider == CandidateProviderKind.Lexical;
        }
    }

    internal static Query? CreateMetadataExpression(Query expression)
    {
        expression = RemoveUnavailableSemantic(expression);

        if (expression is not UnifiedQuery unified)
            return expression;

        if (!unified.HasContentCriteria)
            return expression;

        var terms = unified.MetadataTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();
        if (terms.Length > 0)
            return CreateTermExpression(terms);

        return unified.ContentQuery;
    }

    private static Query CreateTermExpression(string[] terms)
    {
        if (terms.Length == 1)
            return new TermQuery(terms[0]);

        return new AndQuery(terms.Select(term => new TermQuery(term)).ToArray());
    }
}
