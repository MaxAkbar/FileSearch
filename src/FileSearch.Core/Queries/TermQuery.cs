using System;
using System.Collections.Generic;

namespace FileSearch.Core.Queries;

public sealed class TermQuery : Query
{
    private readonly string _term;
    private readonly StringComparison _comparison;

    public TermQuery(string term, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(term))
            throw new ArgumentException("Term must not be empty.", nameof(term));
        _term = term;
        _comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    public string Term => _term;
    public bool CaseSensitive => _comparison == StringComparison.Ordinal;

    public override bool IsMatch(string line) =>
        line.Contains(_term, _comparison);

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        int index = 0;
        while (index <= line.Length - _term.Length)
        {
            int found = line.IndexOf(_term, index, _comparison);
            if (found < 0) break;
            sink.Add(new MatchSpan(found, _term.Length));
            index = found + _term.Length;
        }
    }
}
