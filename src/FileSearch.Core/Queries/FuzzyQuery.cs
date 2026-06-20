using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Queries;

public sealed partial class FuzzyQuery : Query
{
    private readonly string _term;
    private readonly StringComparison _comparison;

    public FuzzyQuery(string term, int maxEdits = 1, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(term))
            throw new ArgumentException("Term must not be empty.", nameof(term));
        if (maxEdits < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEdits), "Edit distance must be zero or greater.");

        _term = term;
        MaxEdits = maxEdits;
        _comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    public string Term => _term;
    public int MaxEdits { get; }
    public bool CaseSensitive => _comparison == StringComparison.Ordinal;

    public override bool IsMatch(string line)
    {
        foreach (Match match in WordRegex().Matches(line))
            if (IsFuzzyMatch(match.Value))
                return true;
        return false;
    }

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        foreach (Match match in WordRegex().Matches(line))
            if (IsFuzzyMatch(match.Value))
                sink.Add(new MatchSpan(match.Index, match.Length));
    }

    private bool IsFuzzyMatch(string value)
    {
        if (string.Equals(value, _term, _comparison))
            return true;

        var left = CaseSensitive ? value : value.ToUpperInvariant();
        var right = CaseSensitive ? _term : _term.ToUpperInvariant();
        return BoundedDistance(left, right, MaxEdits) <= MaxEdits;
    }

    private static int BoundedDistance(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
