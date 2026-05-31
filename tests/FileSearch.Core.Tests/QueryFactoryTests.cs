using FileSearch.Core.Queries;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class QueryFactoryTests
{
    private readonly QueryFactory _factory = new();

    [Fact]
    public void PlainText_WrapsInputAsTermQuery_NotBooleanParsed()
    {
        // In Boolean mode this would parse as (foo AND bar). In PlainText
        // mode the whole phrase is one literal substring search.
        var query = _factory.Build("foo AND bar", QueryMode.PlainText, caseSensitive: false);
        var term = Assert.IsType<TermQuery>(query);
        Assert.Equal("foo AND bar", term.Term);
    }

    [Fact]
    public void Regex_WrapsInputAsRegexQuery()
    {
        var query = _factory.Build(@"\bfoo\d+\b", QueryMode.Regex, caseSensitive: false);
        var regex = Assert.IsType<RegexQuery>(query);
        Assert.Equal(@"\bfoo\d+\b", regex.Pattern);
    }

    [Fact]
    public void Boolean_DelegatesToQueryParser()
    {
        var query = _factory.Build("foo AND bar", QueryMode.Boolean, caseSensitive: false);
        Assert.IsType<AndQuery>(query);
    }

    [Fact]
    public void CaseSensitive_Propagates()
    {
        var query = _factory.Build("Hello", QueryMode.PlainText, caseSensitive: true);
        var term = Assert.IsType<TermQuery>(query);
        Assert.True(term.CaseSensitive);
    }

    [Fact]
    public void Empty_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _factory.Build("", QueryMode.PlainText, false));
        Assert.Throws<ArgumentException>(
            () => _factory.Build("   ", QueryMode.Regex, false));
    }
}
