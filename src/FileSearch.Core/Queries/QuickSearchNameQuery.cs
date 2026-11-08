using System;
using System.Collections.Generic;
using System.Linq;

namespace FileSearch.Core.Queries;

/// <summary>Quick Search's literal space-separated metadata terms, case-insensitive by default.</summary>
public sealed class QuickSearchNameQuery : Query
{
    public const int MaxInputCharacters = 8192;
    public const int MaxTerms = 256;
    private readonly TermQuery[] _terms;

    public QuickSearchNameQuery(string input, bool caseSensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.Length > MaxInputCharacters)
            throw new ArgumentOutOfRangeException(nameof(input), "Quick Search matching is limited to 8,192 characters.");
        var terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length > MaxTerms)
            throw new ArgumentOutOfRangeException(nameof(input), "Quick Search matching is limited to 256 different terms.");
        _terms = terms.Select(term => new TermQuery(term, caseSensitive)).ToArray();
    }

    public override bool IsMatch(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _terms.All(term => term.IsMatch(line));
    }

    public override bool TryCollectHighlights(string line, List<MatchSpan> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!IsMatch(line)) return false;
        foreach (var term in _terms) term.CollectHighlights(line, sink);
        return true;
    }

    public override void CollectHighlights(string line, List<MatchSpan> sink) => TryCollectHighlights(line, sink);
}
