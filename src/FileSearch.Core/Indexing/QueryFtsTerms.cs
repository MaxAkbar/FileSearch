using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Indexing;

internal static partial class QueryFtsTerms
{
    public static IReadOnlyList<string> BuildCandidateQueries(Query query)
    {
        var candidates = Build(query);
        return candidates
            .Where(candidate => candidate.Count > 0)
            .Select(candidate => string.Join(' ', candidate.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlySet<string>> Build(Query query)
    {
        switch (query)
        {
            case UnifiedQuery unified:
                return Build(unified.ContentQuery);

            case MatchAllQuery:
            case FuzzyQuery:
                return Array.Empty<IReadOnlySet<string>>();

            case NearQuery near:
                return Build(new AndQuery(new[] { near.Left, near.Right }));

            case TermQuery term:
                var tokens = ExtractTokens(term.Term);
                return tokens.Count == 0
                    ? Array.Empty<IReadOnlySet<string>>()
                    : new[] { tokens };

            case RegexQuery:
            case NotQuery:
                return Array.Empty<IReadOnlySet<string>>();

            case AndQuery and:
                var andTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in and.Children)
                {
                    if (child is NotQuery)
                        continue;

                    var childCandidates = Build(child);
                    if (childCandidates.Count == 0)
                        continue;

                    var requiredTerms = new HashSet<string>(childCandidates[0], StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in childCandidates.Skip(1))
                        requiredTerms.IntersectWith(candidate);

                    foreach (var token in requiredTerms)
                        andTerms.Add(token);
                }

                return andTerms.Count == 0
                    ? Array.Empty<IReadOnlySet<string>>()
                    : new[] { andTerms };

            case OrQuery or:
                var orCandidates = new List<IReadOnlySet<string>>();
                foreach (var child in or.Children)
                {
                    var childCandidates = Build(child);
                    if (childCandidates.Count == 0)
                        return Array.Empty<IReadOnlySet<string>>();

                    orCandidates.AddRange(childCandidates);
                }

                return orCandidates;

            default:
                return Array.Empty<IReadOnlySet<string>>();
        }
    }

    private static HashSet<string> ExtractTokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex().Matches(value))
            tokens.Add(match.Value);
        return tokens;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
