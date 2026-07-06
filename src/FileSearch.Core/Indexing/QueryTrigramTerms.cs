using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Indexing;

internal static class QueryTrigramTerms
{
    public const int TrigramLength = 3;

    public static IReadOnlyList<IReadOnlyList<string>> BuildCandidateClauses(Query query)
    {
        var clauses = Build(query);
        return clauses.Count == 0
            ? Array.Empty<IReadOnlyList<string>>()
            : clauses
                .Select(static clause => (IReadOnlyList<string>)clause.OrderBy(static x => x, StringComparer.Ordinal).ToArray())
                .Distinct(TrigramClauseComparer.Instance)
                .ToArray();
    }

    public static IReadOnlyList<string> BuildLineTrigrams(string content)
    {
        if (content.Length < TrigramLength)
            return Array.Empty<string>();

        var lowered = content.ToLowerInvariant();
        var trigrams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= lowered.Length - TrigramLength; i++)
            trigrams.Add(lowered.Substring(i, TrigramLength));

        return trigrams.Count == 0
            ? Array.Empty<string>()
            : trigrams.ToArray();
    }

    private static IReadOnlyList<HashSet<string>> Build(Query query)
    {
        switch (query)
        {
            case UnifiedQuery unified:
                return Build(unified.ContentQuery);

            case TermQuery term:
                var trigrams = BuildLineTrigrams(term.Term);
                return trigrams.Count == 0
                    ? Array.Empty<HashSet<string>>()
                    : new[] { new HashSet<string>(trigrams, StringComparer.Ordinal) };

            case NearQuery near:
                return BuildAnd(new[] { near.Left, near.Right });

            case AndQuery and:
                return BuildAnd(and.Children);

            case RegexQuery regex:
                return BuildRegex(regex);

            case OrQuery or:
                var orClauses = new List<HashSet<string>>();
                foreach (var child in or.Children)
                {
                    var childClauses = Build(child);
                    if (childClauses.Count == 0)
                        return Array.Empty<HashSet<string>>();

                    orClauses.AddRange(childClauses);
                }

                return orClauses;

            case MatchAllQuery:
            case FuzzyQuery:
            case NotQuery:
            default:
                return Array.Empty<HashSet<string>>();
        }
    }

    private static IReadOnlyList<HashSet<string>> BuildAnd(IReadOnlyList<Query> children)
    {
        List<HashSet<string>>? clauses = null;
        foreach (var child in children)
        {
            if (child is NotQuery)
                continue;

            var childClauses = Build(child);
            if (childClauses.Count == 0)
                continue;

            if (clauses is null)
            {
                clauses = childClauses.Select(static clause => new HashSet<string>(clause, StringComparer.Ordinal)).ToList();
                continue;
            }

            var combined = new List<HashSet<string>>(clauses.Count * childClauses.Count);
            foreach (var existing in clauses)
            foreach (var childClause in childClauses)
            {
                var merged = new HashSet<string>(existing, StringComparer.Ordinal);
                merged.UnionWith(childClause);
                combined.Add(merged);
            }

            clauses = combined;
        }

        return clauses is null
            ? Array.Empty<HashSet<string>>()
            : clauses;
    }

    private static HashSet<string>[] BuildRegex(RegexQuery regex)
    {
        var literals = ExtractRequiredRegexLiterals(regex.Pattern);
        if (literals.Count == 0)
            return Array.Empty<HashSet<string>>();

        var trigrams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var literal in literals)
        {
            foreach (var trigram in BuildLineTrigrams(literal))
                trigrams.Add(trigram);
        }

        return trigrams.Count == 0
            ? Array.Empty<HashSet<string>>()
            : new[] { trigrams };
    }

    private static IReadOnlyList<string> ExtractRequiredRegexLiterals(string pattern)
    {
        var literals = new List<string>();
        var builder = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (!TryReadEscapedLiteral(pattern, ref i, out var literal))
                {
                    FlushLiteral(literals, builder);
                    ConsumeTrailingQuantifier(pattern, ref i);
                    continue;
                }

                AppendLiteralWithOptionalQuantifier(pattern, ref i, literal, literals, builder);
                continue;
            }

            if (ch == '|')
                return Array.Empty<string>();

            if (ch is '(' or ')')
                return Array.Empty<string>();

            if (ch == '[')
            {
                FlushLiteral(literals, builder);
                if (!TrySkipCharacterClass(pattern, ref i))
                    return Array.Empty<string>();

                ConsumeTrailingQuantifier(pattern, ref i);
                continue;
            }

            if (IsRegexBoundary(ch))
            {
                FlushLiteral(literals, builder);
                continue;
            }

            AppendLiteralWithOptionalQuantifier(pattern, ref i, ch, literals, builder);
        }

        FlushLiteral(literals, builder);
        return literals;
    }

    private static void AppendLiteralWithOptionalQuantifier(
        string pattern,
        ref int index,
        char literal,
        List<string> literals,
        StringBuilder builder)
    {
        if (!TryReadQuantifier(pattern, index + 1, out var minCount, out var nextIndex))
        {
            builder.Append(literal);
            return;
        }

        if (minCount == 0)
        {
            FlushLiteral(literals, builder);
        }
        else
        {
            builder.Append(literal);
            FlushLiteral(literals, builder);
        }

        index = nextIndex - 1;
    }

    private static bool TryReadEscapedLiteral(string pattern, ref int index, out char literal)
    {
        literal = '\0';
        if (index + 1 >= pattern.Length)
            return false;

        var escaped = pattern[++index];
        switch (escaped)
        {
            case '\\':
            case '.':
            case '^':
            case '$':
            case '|':
            case '?':
            case '*':
            case '+':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
                literal = escaped;
                return true;
            case 'n':
                literal = '\n';
                return true;
            case 'r':
                literal = '\r';
                return true;
            case 't':
                literal = '\t';
                return true;
            default:
                return false;
        }
    }

    private static bool TrySkipCharacterClass(string pattern, ref int index)
    {
        for (var i = index + 1; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\')
            {
                i++;
                continue;
            }

            if (pattern[i] == ']')
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static void ConsumeTrailingQuantifier(string pattern, ref int index)
    {
        if (TryReadQuantifier(pattern, index + 1, out _, out var nextIndex))
            index = nextIndex - 1;
    }

    private static bool TryReadQuantifier(string pattern, int index, out int minCount, out int nextIndex)
    {
        minCount = 1;
        nextIndex = index;
        if (index >= pattern.Length)
            return false;

        switch (pattern[index])
        {
            case '?':
            case '*':
                minCount = 0;
                nextIndex = SkipLazyModifier(pattern, index + 1);
                return true;
            case '+':
                minCount = 1;
                nextIndex = SkipLazyModifier(pattern, index + 1);
                return true;
            case '{':
                return TryReadBracedQuantifier(pattern, index, out minCount, out nextIndex);
            default:
                return false;
        }
    }

    private static bool TryReadBracedQuantifier(string pattern, int index, out int minCount, out int nextIndex)
    {
        minCount = 1;
        nextIndex = index;
        var cursor = index + 1;
        var minStart = cursor;
        while (cursor < pattern.Length && char.IsDigit(pattern[cursor]))
            cursor++;

        if (cursor == minStart || !int.TryParse(pattern.AsSpan(minStart, cursor - minStart), out minCount))
            return false;

        if (cursor < pattern.Length && pattern[cursor] == ',')
        {
            cursor++;
            while (cursor < pattern.Length && char.IsDigit(pattern[cursor]))
                cursor++;
        }

        if (cursor >= pattern.Length || pattern[cursor] != '}')
            return false;

        nextIndex = SkipLazyModifier(pattern, cursor + 1);
        return true;
    }

    private static int SkipLazyModifier(string pattern, int index) =>
        index < pattern.Length && pattern[index] == '?'
            ? index + 1
            : index;

    private static bool IsRegexBoundary(char ch) =>
        ch is '.' or '^' or '$' or '*' or '+' or '?' or '{' or '}';

    private static void FlushLiteral(List<string> literals, StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            literals.Add(builder.ToString());
            builder.Clear();
        }
    }

    private sealed class TrigramClauseComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static readonly TrigramClauseComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null || x.Count != y.Count)
                return false;

            for (var i = 0; i < x.Count; i++)
            {
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = new HashCode();
            for (var i = 0; i < obj.Count; i++)
                hash.Add(obj[i], StringComparer.Ordinal);

            return hash.ToHashCode();
        }
    }
}
