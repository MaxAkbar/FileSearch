using System;
using System.Linq;
using System.Text.RegularExpressions;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Volumes;

/// <summary>
/// A <see cref="Query"/> compiled to match names as spans, so scanning
/// millions of index entries allocates nothing for the common query shapes.
/// Query nodes without a span form fall back to <see cref="Query.IsMatch"/>
/// on a materialized string, so results always equal the live searcher's.
/// </summary>
internal abstract class VolumeNameMatcher
{
    public abstract bool IsMatch(ReadOnlySpan<char> name);

    public static VolumeNameMatcher Compile(Query query) => query switch
    {
        UnifiedQuery unified => Compile(unified.ContentQuery),
        MatchAllQuery => AllMatcher.Instance,
        TermQuery term => new TermMatcher(term.Term, term.CaseSensitive),
        RegexQuery regex => new RegexMatcher(regex.Regex),
        AndQuery and => new AndMatcher(and.Children.Select(Compile).ToArray()),
        OrQuery or => new OrMatcher(or.Children.Select(Compile).ToArray()),
        NotQuery not => new NotMatcher(Compile(not.Child)),
        _ => new StringMatcher(query),
    };

    private sealed class AllMatcher : VolumeNameMatcher
    {
        public static readonly AllMatcher Instance = new();

        public override bool IsMatch(ReadOnlySpan<char> name) => true;
    }

    private sealed class TermMatcher(string term, bool caseSensitive) : VolumeNameMatcher
    {
        private readonly StringComparison _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        public override bool IsMatch(ReadOnlySpan<char> name) => name.Contains(term, _comparison);
    }

    private sealed class RegexMatcher(Regex regex) : VolumeNameMatcher
    {
        public override bool IsMatch(ReadOnlySpan<char> name) => regex.IsMatch(name);
    }

    private sealed class AndMatcher(VolumeNameMatcher[] children) : VolumeNameMatcher
    {
        public override bool IsMatch(ReadOnlySpan<char> name)
        {
            foreach (var child in children)
            {
                if (!child.IsMatch(name))
                    return false;
            }

            return true;
        }
    }

    private sealed class OrMatcher(VolumeNameMatcher[] children) : VolumeNameMatcher
    {
        public override bool IsMatch(ReadOnlySpan<char> name)
        {
            foreach (var child in children)
            {
                if (child.IsMatch(name))
                    return true;
            }

            return false;
        }
    }

    private sealed class NotMatcher(VolumeNameMatcher child) : VolumeNameMatcher
    {
        public override bool IsMatch(ReadOnlySpan<char> name) => !child.IsMatch(name);
    }

    private sealed class StringMatcher(Query query) : VolumeNameMatcher
    {
        public override bool IsMatch(ReadOnlySpan<char> name) => query.IsMatch(new string(name));
    }
}
