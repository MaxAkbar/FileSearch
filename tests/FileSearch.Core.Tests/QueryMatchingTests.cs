using System.Collections.Generic;
using FileSearch.Core.Queries;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class QueryMatchingTests
{
    [Fact]
    public void TermQuery_CaseInsensitive_ByDefault()
    {
        var q = new TermQuery("Hello");
        Assert.True(q.IsMatch("say hello world"));
        Assert.True(q.IsMatch("HELLO"));
    }

    [Fact]
    public void TermQuery_CaseSensitive_RespectsCase()
    {
        var q = new TermQuery("Hello", caseSensitive: true);
        Assert.True(q.IsMatch("say Hello world"));
        Assert.False(q.IsMatch("say hello world"));
    }

    [Fact]
    public void TermQuery_CollectHighlights_FindsAllOccurrences()
    {
        var q = new TermQuery("ab");
        var spans = new List<MatchSpan>();
        q.CollectHighlights("ababab", spans);
        Assert.Equal(3, spans.Count);
        Assert.Equal(new MatchSpan(0, 2), spans[0]);
        Assert.Equal(new MatchSpan(2, 2), spans[1]);
        Assert.Equal(new MatchSpan(4, 2), spans[2]);
    }

    [Fact]
    public void RegexQuery_MatchesPattern()
    {
        var q = new RegexQuery(@"\d+");
        Assert.True(q.IsMatch("abc 123 def"));
        Assert.False(q.IsMatch("abc def"));
    }

    [Fact]
    public void AndQuery_RequiresAllChildren()
    {
        var q = new AndQuery(new Query[] { new TermQuery("foo"), new TermQuery("bar") });
        Assert.True(q.IsMatch("foo bar baz"));
        Assert.False(q.IsMatch("foo only"));
        Assert.False(q.IsMatch("bar only"));
    }

    [Fact]
    public void OrQuery_RequiresAnyChild()
    {
        var q = new OrQuery(new Query[] { new TermQuery("foo"), new TermQuery("bar") });
        Assert.True(q.IsMatch("foo only"));
        Assert.True(q.IsMatch("bar only"));
        Assert.True(q.IsMatch("foo bar"));
        Assert.False(q.IsMatch("none of them"));
    }

    [Fact]
    public void NotQuery_InvertsChild()
    {
        var q = new NotQuery(new TermQuery("foo"));
        Assert.True(q.IsMatch("no match here"));
        Assert.False(q.IsMatch("contains foo"));
    }

    [Fact]
    public void NotQuery_ContributesNoHighlights()
    {
        var q = new NotQuery(new TermQuery("foo"));
        var spans = new List<MatchSpan>();
        q.CollectHighlights("no match here", spans);
        Assert.Empty(spans);
    }

    [Fact]
    public void Composite_Highlights_ComeFromMatchingBranches()
    {
        // (foo OR bar) → both branches contribute when both match
        var q = new OrQuery(new Query[] { new TermQuery("foo"), new TermQuery("bar") });
        var spans = new List<MatchSpan>();
        q.CollectHighlights("foo and bar", spans);
        Assert.Equal(2, spans.Count);
    }
}
