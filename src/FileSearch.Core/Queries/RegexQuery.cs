using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Queries;

public sealed class RegexQuery : Query
{
    private readonly Regex _regex;

    public RegexQuery(string pattern, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("Pattern must not be empty.", nameof(pattern));
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        _regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    public string Pattern => _regex.ToString();

    public override bool IsMatch(string line) => _regex.IsMatch(line);

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        foreach (Match m in _regex.Matches(line))
        {
            if (m.Length > 0)
                sink.Add(new MatchSpan(m.Index, m.Length));
        }
    }
}
