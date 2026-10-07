using System;

namespace FileSearch.Core.Queries;

public sealed class QueryFactory : IQueryFactory
{
    public Query Build(string input, QueryMode mode, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Query is empty.", nameof(input));

        return mode switch
        {
            QueryMode.PlainText => new TermQuery(input, caseSensitive),
            QueryMode.Regex => new RegexQuery(input, caseSensitive),
            QueryMode.Boolean => new QueryParser(caseSensitive).Parse(input),
            QueryMode.Unified => new UnifiedQueryParser(caseSensitive).Parse(input),
            QueryMode.Semantic => new UnifiedQuery(
                MatchAllQuery.Instance,
                UnifiedQueryFilters.Empty with { SemanticTerms = new[] { input } },
                Array.Empty<UnifiedQueryChip>()),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }
}
