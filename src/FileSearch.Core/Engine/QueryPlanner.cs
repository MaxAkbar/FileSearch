using System;
using System.Collections.Generic;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class QueryPlanner : IQueryPlanner
{
    public SearchPlan CreatePlan(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providers = new Dictionary<CandidateProviderKind, SearchProviderPlan>();
        var explanations = new List<SearchPlanExplanation>();
        var expression = request.Expression;
        var contentExpression = expression;

        AddProvider(providers, CandidateProviderKind.Metadata, RetrievalLayer.Instant);

        if (expression is UnifiedQuery unified)
        {
            contentExpression = unified.ContentQuery;
            if (!unified.HasContentCriteria)
                contentExpression = MatchAllQuery.Instance;

            if (unified.HasSemantic)
            {
                AddProvider(
                    providers,
                    CandidateProviderKind.Semantic,
                    RetrievalLayer.Smart);
                explanations.Add(new SearchPlanExplanation(
                    "semantic-requested",
                    "Semantic provider requested by the query.",
                    SearchExplanationSeverity.Info,
                    CandidateProviderKind.Semantic,
                    RetrievalLayer.Smart));
            }
        }

        if (request.SearchTarget != SearchTarget.Content)
        {
            explanations.Add(new SearchPlanExplanation(
                "metadata-target",
                "The query targets file or folder metadata.",
                Provider: CandidateProviderKind.Metadata,
                Layer: RetrievalLayer.Instant));
            return new SearchPlan(request, providers.Values.ToArray(), explanations);
        }

        var contentProviders = CollectContentProviders(contentExpression);
        if (request.Mode == QueryMode.Regex)
            contentProviders |= CandidateProviderKind.Regex;

        if ((contentProviders & CandidateProviderKind.Lexical) != 0)
            AddProvider(providers, CandidateProviderKind.Lexical, RetrievalLayer.Deep);

        if ((contentProviders & CandidateProviderKind.Regex) != 0)
            AddProvider(providers, CandidateProviderKind.Regex, RetrievalLayer.Deep);

        if ((contentProviders & CandidateProviderKind.Fuzzy) != 0)
            AddProvider(providers, CandidateProviderKind.Fuzzy, RetrievalLayer.Deep);

        if (request.WalkerOptions.EnableOcr)
        {
            AddProvider(providers, CandidateProviderKind.Ocr, RetrievalLayer.Smart);
            explanations.Add(new SearchPlanExplanation(
                "ocr-enabled",
                "OCR-capable extractors may contribute late text candidates.",
                Provider: CandidateProviderKind.Ocr,
                Layer: RetrievalLayer.Smart));
        }

        return new SearchPlan(request, providers.Values.ToArray(), explanations);
    }

    private static void AddProvider(
        Dictionary<CandidateProviderKind, SearchProviderPlan> providers,
        CandidateProviderKind provider,
        RetrievalLayer layer,
        bool isEnabled = true,
        string? explanation = null,
        double weight = 1)
    {
        if (providers.TryGetValue(provider, out var existing))
        {
            providers[provider] = existing with
            {
                Layer = existing.Layer | layer,
                IsEnabled = existing.IsEnabled && isEnabled,
                Explanation = existing.Explanation ?? explanation,
                Weight = Math.Max(existing.Weight, weight),
            };
            return;
        }

        providers.Add(provider, new SearchProviderPlan(provider, layer, isEnabled, explanation, weight));
    }

    private static CandidateProviderKind CollectContentProviders(Query query)
    {
        switch (query)
        {
            case MatchAllQuery:
                return CandidateProviderKind.None;

            case UnifiedQuery unified:
                var providers = CollectContentProviders(unified.ContentQuery);
                if (unified.HasSemantic)
                    providers |= CandidateProviderKind.Semantic;
                return providers;

            case TermQuery:
                return CandidateProviderKind.Lexical;

            case RegexQuery:
                return CandidateProviderKind.Regex;

            case FuzzyQuery:
                return CandidateProviderKind.Fuzzy;

            case NearQuery near:
                return CandidateProviderKind.Lexical |
                    CollectContentProviders(near.Left) |
                    CollectContentProviders(near.Right);

            case NotQuery not:
                return CollectContentProviders(not.Child);

            case AndQuery and:
                return CollectChildren(and.Children);

            case OrQuery or:
                return CollectChildren(or.Children);

            default:
                return CandidateProviderKind.Lexical;
        }
    }

    private static CandidateProviderKind CollectChildren(IEnumerable<Query> children)
    {
        var providers = CandidateProviderKind.None;
        foreach (var child in children)
            providers |= CollectContentProviders(child);
        return providers;
    }
}
