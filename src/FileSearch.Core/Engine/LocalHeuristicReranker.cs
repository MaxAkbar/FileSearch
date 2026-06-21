using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed class LocalRerankerOptions
{
    public bool IsEnabled { get; set; } = true;

    public Func<bool>? IsEnabledProvider { get; set; }

    public bool GetIsEnabled() => IsEnabledProvider?.Invoke() ?? IsEnabled;
}

public sealed class LocalHeuristicReranker : IReranker
{
    private static readonly char[] s_termSeparators =
    [
        ' ', '\t', '\r', '\n',
        '.', ',', ';', ':', '!', '?',
        '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}',
        '"', '\''
    ];

    private readonly LocalRerankerOptions _options;

    public LocalHeuristicReranker(LocalRerankerOptions? options = null) =>
        _options = options ?? new LocalRerankerOptions();

    public Task<IReadOnlyList<RankedSearchResult>> RerankAsync(
        SearchPlan plan,
        IReadOnlyList<RankedSearchResult> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidates.Count == 0)
            return Task.FromResult(candidates);
        if (!_options.GetIsEnabled())
            return Task.FromResult(candidates);

        var terms = ExtractTerms(plan.Request.Expression)
            .SelectMany(ExpandTerm)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            return Task.FromResult(candidates);

        var reranked = candidates
            .Select(result => ApplyLocalSignals(result, terms))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Select((result, index) => result with { Rank = index + 1 })
            .ToArray();

        return Task.FromResult<IReadOnlyList<RankedSearchResult>>(reranked);
    }

    private static RankedSearchResult ApplyLocalSignals(
        RankedSearchResult result,
        IReadOnlyList<string> terms)
    {
        var bonus = CalculateBonus(result, terms);
        if (bonus <= 0)
            return result;

        var explanations = result.Explanations
            .Concat(new[]
            {
                new SearchResultExplanation(
                    "local-reranker",
                    "Local reranker boosted this result using filename, path, snippet, provider and hit-count signals.",
                    ScoreContribution: bonus),
            })
            .ToArray();

        return result with
        {
            Score = result.Score + bonus,
            Explanations = explanations,
        };
    }

    private static double CalculateBonus(
        RankedSearchResult result,
        IReadOnlyList<string> terms)
    {
        var fileName = Path.GetFileName(result.Path);
        var fileStem = Path.GetFileNameWithoutExtension(result.Path);
        var pathBonus = 0d;
        foreach (var term in terms)
        {
            if (fileStem.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                pathBonus += 0.30;
                continue;
            }

            if (fileName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                pathBonus += 0.18;
                continue;
            }

            if (result.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
                pathBonus += 0.06;
        }

        var textBonus = 0d;
        foreach (var candidate in result.Candidates)
        {
            var text = candidate.Snippet?.Text;
            if (string.IsNullOrWhiteSpace(text))
                text = candidate.DisplayText;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var term in terms)
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    textBonus += 0.03;
        }

        var providerBonus = Math.Min(0.15, Math.Max(0, result.Candidates.Select(candidate => candidate.Provider).Distinct().Count() - 1) * 0.05);
        var hitCountBonus = Math.Min(0.12, Math.Log(result.Candidates.Count + 1) * 0.03);
        var recencyBonus = CalculateRecencyBonus(result);

        return Math.Min(0.75, Math.Min(0.45, pathBonus) + Math.Min(0.18, textBonus) + providerBonus + hitCountBonus + recencyBonus);
    }

    private static double CalculateRecencyBonus(RankedSearchResult result)
    {
        var modified = result.Candidates
            .Select(candidate => candidate.ModifiedUtc)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        if (modified == default)
            return 0;

        var age = DateTime.UtcNow - modified;
        if (age < TimeSpan.Zero)
            return 0.08;
        if (age <= TimeSpan.FromDays(7))
            return 0.08;
        if (age <= TimeSpan.FromDays(30))
            return 0.05;
        return age <= TimeSpan.FromDays(365) ? 0.02 : 0;
    }

    private static IEnumerable<string> ExtractTerms(Query query)
    {
        switch (query)
        {
            case TermQuery term:
                yield return term.Term;
                break;

            case FuzzyQuery fuzzy:
                yield return fuzzy.Term;
                break;

            case UnifiedQuery unified:
                foreach (var term in ExtractTerms(unified.ContentQuery))
                    yield return term;
                foreach (var term in unified.MetadataTerms)
                    yield return term;
                foreach (var term in unified.Filters.SemanticTerms)
                    yield return term;
                break;

            case NearQuery near:
                foreach (var term in ExtractTerms(near.Left))
                    yield return term;
                foreach (var term in ExtractTerms(near.Right))
                    yield return term;
                break;

            case AndQuery and:
                foreach (var child in and.Children)
                    foreach (var term in ExtractTerms(child))
                        yield return term;
                break;

            case OrQuery or:
                foreach (var child in or.Children)
                    foreach (var term in ExtractTerms(child))
                        yield return term;
                break;
        }
    }

    private static IEnumerable<string> ExpandTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            yield break;

        var trimmed = term.Trim();
        if (trimmed.Length >= 2)
            yield return trimmed;

        foreach (var part in trimmed.Split(s_termSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length >= 2)
                yield return part;
    }
}
