using System;
using System.Collections.Generic;
using System.Linq;

namespace FileSearch.Core.Engine;

public sealed record SearchPlan
{
    public SearchPlan(
        SearchRequest request,
        IReadOnlyList<SearchProviderPlan> providers,
        IReadOnlyList<SearchPlanExplanation>? explanations = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Providers = providers ?? throw new ArgumentNullException(nameof(providers));
        Explanations = explanations ?? Array.Empty<SearchPlanExplanation>();
    }

    public SearchRequest Request { get; init; }

    public IReadOnlyList<SearchProviderPlan> Providers { get; init; }

    public IReadOnlyList<SearchPlanExplanation> Explanations { get; init; }

    public CandidateProviderKind AllProviders => CombineProviders(Providers);

    public CandidateProviderKind EnabledProviders => CombineProviders(Providers.Where(provider => provider.IsEnabled));

    public RetrievalLayer AllLayers => CombineLayers(Providers);

    public RetrievalLayer EnabledLayers => CombineLayers(Providers.Where(provider => provider.IsEnabled));

    public bool HasProvider(CandidateProviderKind provider) =>
        (AllProviders & provider) != CandidateProviderKind.None;

    public bool HasEnabledProvider(CandidateProviderKind provider) =>
        (EnabledProviders & provider) != CandidateProviderKind.None;

    public SearchProviderPlan? GetProvider(CandidateProviderKind provider) =>
        Providers.FirstOrDefault(candidate => candidate.Provider == provider);

    private static CandidateProviderKind CombineProviders(IEnumerable<SearchProviderPlan> providers)
    {
        var combined = CandidateProviderKind.None;
        foreach (var provider in providers)
            combined |= provider.Provider;
        return combined;
    }

    private static RetrievalLayer CombineLayers(IEnumerable<SearchProviderPlan> providers)
    {
        var combined = RetrievalLayer.None;
        foreach (var provider in providers)
            combined |= provider.Layer;
        return combined;
    }
}

