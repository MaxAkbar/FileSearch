using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Queries;

public sealed partial class NearQuery : Query
{
    public NearQuery(Query left, Query right, int maxDistance)
    {
        if (maxDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Distance must be zero or greater.");

        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        MaxDistance = maxDistance;
    }

    public Query Left { get; }
    public Query Right { get; }
    public int MaxDistance { get; }

    public override bool IsMatch(string line)
    {
        if (!Left.IsMatch(line) || !Right.IsMatch(line))
            return false;

        var leftSpans = CollectSpans(Left, line);
        var rightSpans = CollectSpans(Right, line);
        if (leftSpans.Count == 0 || rightSpans.Count == 0)
            return true;

        var tokens = Tokenize(line);
        if (tokens.Count == 0)
            return false;

        foreach (var left in leftSpans)
        {
            var leftTokenRange = GetTokenRange(tokens, left);
            if (leftTokenRange is null)
                continue;

            foreach (var right in rightSpans)
            {
                var rightTokenRange = GetTokenRange(tokens, right);
                if (rightTokenRange is null)
                    continue;

                if (Distance(leftTokenRange.Value, rightTokenRange.Value) <= MaxDistance)
                    return true;
            }
        }

        return false;
    }

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        if (!IsMatch(line))
            return;

        Left.CollectHighlights(line, sink);
        Right.CollectHighlights(line, sink);
    }

    private static List<MatchSpan> CollectSpans(Query query, string line)
    {
        var spans = new List<MatchSpan>();
        query.CollectHighlights(line, spans);
        return spans;
    }

    private static int Distance((int Start, int End) left, (int Start, int End) right)
    {
        if (left.End < right.Start)
            return right.Start - left.End - 1;

        if (right.End < left.Start)
            return left.Start - right.End - 1;

        return 0;
    }

    private static (int Start, int End)? GetTokenRange(IReadOnlyList<TokenSpan> tokens, MatchSpan span)
    {
        var spanStart = span.Start;
        var spanEnd = span.Start + span.Length;
        int? first = null;
        int? last = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.End <= spanStart)
                continue;
            if (token.Start >= spanEnd)
                break;

            first ??= i;
            last = i;
        }

        return first is null || last is null ? null : (first.Value, last.Value);
    }

    private static List<TokenSpan> Tokenize(string line)
    {
        var tokens = new List<TokenSpan>();
        foreach (Match match in WordRegex().Matches(line))
            tokens.Add(new TokenSpan(match.Index, match.Index + match.Length));
        return tokens;
    }

    private readonly record struct TokenSpan(int Start, int End);

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
