using System;
using System.Collections.Generic;
using System.Linq;

namespace FileSearch.Core.Engine;

public sealed class WeightedResultFusion : IResultFusion
{
    public IReadOnlyList<RankedSearchResult> Fuse(
        SearchPlan plan,
        IReadOnlyCollection<SearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return Array.Empty<RankedSearchResult>();

        var providerWeights = plan.Providers.ToDictionary(
            provider => provider.Provider,
            provider => provider.Weight);
        var groups = candidates
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var orderedCandidates = group
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.ProviderId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var score = orderedCandidates.Sum(candidate => ScoreCandidate(providerWeights, candidate));
                var explanations = new[]
                {
                    new SearchResultExplanation(
                        "weighted-fusion",
                        $"Weighted fusion combined {orderedCandidates.Length} candidate(s).",
                        ScoreContribution: score),
                };

                return new
                {
                    Path = group.Key,
                    Score = score,
                    Candidates = orderedCandidates,
                    Explanations = explanations,
                };
            })
            .OrderByDescending(group => group.Score)
            .ThenBy(group => group.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<RankedSearchResult>(groups.Length);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            results.Add(new RankedSearchResult(
                i + 1,
                group.Path,
                group.Score,
                group.Candidates,
                group.Explanations));
        }

        return results;
    }

    private static double ScoreCandidate(
        Dictionary<CandidateProviderKind, double> providerWeights,
        SearchCandidate candidate)
    {
        var weight = providerWeights.TryGetValue(candidate.Provider, out var configuredWeight)
            ? configuredWeight
            : 1;
        var score = candidate.Score > 0 ? candidate.Score : 1;
        return score * weight;
    }
}
