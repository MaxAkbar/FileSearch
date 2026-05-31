using FileSearch.Core.Queries;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class QueryParserTests
{
    private readonly QueryParser _parser = new();

    [Fact]
    public void Parse_BareWord_ProducesTermQuery()
    {
        var q = _parser.Parse("hello");
        Assert.IsType<TermQuery>(q);
        Assert.Equal("hello", ((TermQuery)q).Term);
    }

    [Fact]
    public void Parse_QuotedString_PreservesSpaces()
    {
        var q = _parser.Parse("\"hello world\"");
        Assert.Equal("hello world", Assert.IsType<TermQuery>(q).Term);
    }

    [Fact]
    public void Parse_RegexLiteral_ProducesRegexQuery()
    {
        var q = _parser.Parse("/^class\\s+\\w+/");
        var rx = Assert.IsType<RegexQuery>(q);
        Assert.Equal("^class\\s+\\w+", rx.Pattern);
    }

    [Fact]
    public void Parse_ExplicitAnd_ProducesAndQuery()
    {
        var q = _parser.Parse("foo AND bar");
        var and = Assert.IsType<AndQuery>(q);
        Assert.Equal(2, and.Children.Count);
    }

    [Fact]
    public void Parse_ImplicitAnd_BetweenAdjacentTerms()
    {
        var q = _parser.Parse("foo bar baz");
        var and = Assert.IsType<AndQuery>(q);
        Assert.Equal(3, and.Children.Count);
    }

    [Fact]
    public void Parse_Or_HigherInTree_ThanAnd()
    {
        // "a AND b OR c" = (a AND b) OR c
        var q = _parser.Parse("a AND b OR c");
        var or = Assert.IsType<OrQuery>(q);
        Assert.Equal(2, or.Children.Count);
        Assert.IsType<AndQuery>(or.Children[0]);
        Assert.IsType<TermQuery>(or.Children[1]);
    }

    [Fact]
    public void Parse_Parentheses_OverridePrecedence()
    {
        // "a AND (b OR c)" should put OR under AND
        var q = _parser.Parse("a AND (b OR c)");
        var and = Assert.IsType<AndQuery>(q);
        Assert.IsType<TermQuery>(and.Children[0]);
        Assert.IsType<OrQuery>(and.Children[1]);
    }

    [Fact]
    public void Parse_Not_NegatesChild()
    {
        var q = _parser.Parse("NOT foo");
        var not = Assert.IsType<NotQuery>(q);
        Assert.IsType<TermQuery>(not.Child);
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse(""));
        Assert.Throws<ArgumentException>(() => _parser.Parse("   "));
    }

    [Fact]
    public void Parse_UnterminatedQuote_Throws()
    {
        Assert.Throws<FormatException>(() => _parser.Parse("\"unterminated"));
    }

    [Fact]
    public void Parse_UnmatchedParen_Throws()
    {
        Assert.Throws<FormatException>(() => _parser.Parse("(foo"));
    }
}
