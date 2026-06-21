using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Engine;

public sealed class HybridRetrievalPipeline : IHybridRetrievalPipeline
{
    private readonly IQueryPlanner _planner;
    private readonly IReadOnlyList<ICandidateProvider> _providers;
    private readonly IResultFusion _fusion;
    private readonly IReranker _reranker;

    public HybridRetrievalPipeline(
        IQueryPlanner planner,
        IEnumerable<ICandidateProvider> providers,
        IResultFusion fusion,
        IReranker reranker)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        _fusion = fusion ?? throw new ArgumentNullException(nameof(fusion));
        _reranker = reranker ?? throw new ArgumentNullException(nameof(reranker));
    }

    public async Task<IReadOnlyList<RankedSearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = _planner.CreatePlan(request);
        var activeProviders = await SelectProvidersAsync(plan, cancellationToken).ConfigureAwait(false);
        if (activeProviders.Length == 0)
            return Array.Empty<RankedSearchResult>();

        var providerTasks = activeProviders
            .Select(provider => CollectAsync(provider, plan, cancellationToken))
            .ToArray();
        await Task.WhenAll(providerTasks).ConfigureAwait(false);

        var candidates = providerTasks
            .SelectMany(task => task.Result)
            .ToArray();
        var fused = _fusion.Fuse(plan, candidates);
        return await _reranker.RerankAsync(plan, fused, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SearchCandidate>> CollectAsync(
        ICandidateProvider provider,
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SearchCandidate>();
        await foreach (var candidate in provider.FindAsync(plan, cancellationToken).ConfigureAwait(false))
            candidates.Add(candidate);
        return candidates;
    }

    private async Task<ICandidateProvider[]> SelectProvidersAsync(
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        var availabilityTasks = _providers
            .Where(provider => plan.HasEnabledProvider(provider.Provider))
            .Select(provider => GetAvailabilityAsync(provider, plan, cancellationToken))
            .ToArray();
        await Task.WhenAll(availabilityTasks).ConfigureAwait(false);

        var availableProviders = availabilityTasks
            .Select(task => task.Result)
            .Where(selection => selection.Availability.IsAvailable)
            .ToArray();
        if (availableProviders.Length == 0)
            return Array.Empty<ICandidateProvider>();

        var selected = new List<ICandidateProvider>();
        foreach (var group in availableProviders.GroupBy(selection => selection.Provider.Provider))
        {
            var candidates = group.ToArray();
            var indexed = candidates
                .Where(selection => selection.Route == CandidateProviderRoute.Indexed)
                .ToArray();
            if (plan.Request.UseIndex && indexed.Length > 0)
            {
                selected.AddRange(indexed.Select(selection => selection.Provider));
                continue;
            }

            selected.AddRange(candidates
                .Where(selection => selection.Route != CandidateProviderRoute.Indexed)
                .Select(selection => selection.Provider));
        }

        return selected.ToArray();
    }

    private static async Task<ProviderSelection> GetAvailabilityAsync(
        ICandidateProvider provider,
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        if (provider is IRoutedCandidateProvider routedProvider)
        {
            return new ProviderSelection(
                provider,
                routedProvider.Route,
                await routedProvider.GetAvailabilityAsync(plan, cancellationToken).ConfigureAwait(false));
        }

        return new ProviderSelection(
            provider,
            CandidateProviderRoute.Live,
            CandidateProviderAvailability.Available);
    }

    private sealed record ProviderSelection(
        ICandidateProvider Provider,
        CandidateProviderRoute Route,
        CandidateProviderAvailability Availability);
}
