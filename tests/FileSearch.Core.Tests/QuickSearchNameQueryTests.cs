using FileSearch.Core.Queries;

namespace FileSearch.Core.Tests;

public sealed class QuickSearchNameQueryTests
{
    [Fact]
    public void EverySpaceSeparatedTermMustMatchRegardlessOfCaseOrOrder()
    {
        var query = new QuickSearchNameQuery("  smith   JOHN  ");
        Assert.True(query.IsMatch(@"C:\John\smith-budget.txt"));
        Assert.True(query.IsMatch("SMITH_report_JoHn.pdf"));
        Assert.False(query.IsMatch("john-report.pdf"));
        Assert.False(query.IsMatch("smith-report.pdf"));
    }

    [Fact]
    public void ExplicitMatchCaseAppliesToEveryTermAndKeepsCaseDistinctTerms()
    {
        var query = new QuickSearchNameQuery("john SMITH", caseSensitive: true);
        Assert.True(query.IsMatch(@"C:\john\SMITH-budget.txt"));
        Assert.False(query.IsMatch(@"C:\John\SMITH-budget.txt"));
        Assert.False(query.IsMatch(@"C:\john\Smith-budget.txt"));

        var distinct = new QuickSearchNameQuery("alpha ALPHA", caseSensitive: true);
        Assert.False(distinct.IsMatch("alpha-only.txt"));
        var spans = new List<MatchSpan>();
        Assert.True(distinct.TryCollectHighlights("alpha-ALPHA.txt", spans));
        Assert.Equal([new MatchSpan(0, 5), new MatchSpan(6, 5)], spans);
        Assert.True(new QuickSearchNameQuery("alpha ALPHA").IsMatch("ALPHA.txt"));
    }

    [Fact]
    public void QuotesOperatorsAndRegularExpressionCharactersRemainLiteral()
    {
        var query = new QuickSearchNameQuery("\"john OR smith\" .*");
        Assert.True(query.IsMatch("literal .* \"JOHN and OR SMITH\""));
        Assert.False(query.IsMatch("john smith .*"));
        Assert.False(query.IsMatch("\"john smith\" .*"));
        Assert.False(query.IsMatch("\"john OR smith\" report.txt"));
    }

    [Fact]
    public void HighlightsContainOnlySuccessfulAllTermMatchesAndKeepTheExistingSink()
    {
        const string line = @"C:\John\smith-JOHN.txt";
        var query = new QuickSearchNameQuery("john smith john");
        var spans = new List<MatchSpan> { new(0, 1) };
        Assert.True(query.TryCollectHighlights(line, spans));
        Assert.Equal(new MatchSpan(0, 1), spans[0]);
        Assert.Equal(["John", "JOHN", "smith"], spans.Skip(1).Select(span => line.Substring(span.Start, span.Length)));

        var failed = new List<MatchSpan> { new(2, 3) };
        Assert.False(query.TryCollectHighlights("john only", failed));
        Assert.Equal([new MatchSpan(2, 3)], failed);
    }

    [Fact]
    public void TokenizationMatchesQuickSearchSpacesRatherThanInterpretingTabsAsOperators()
    {
        var query = new QuickSearchNameQuery(" alpha\tbeta gamma ");
        Assert.True(query.IsMatch("gamma_alpha\tbeta"));
        Assert.False(query.IsMatch("alpha_beta_gamma"));
    }

    [Fact]
    public void EmptyAndExcessiveInputsFailExplicitlyWithoutTruncatingTerms()
    {
        Assert.Throws<ArgumentException>(() => new QuickSearchNameQuery("  \t\r\n "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickSearchNameQuery(new string('x', QuickSearchNameQuery.MaxInputCharacters + 1)));
        string terms = string.Join(' ', Enumerable.Range(0, QuickSearchNameQuery.MaxTerms + 1).Select(number => "term" + number));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickSearchNameQuery(terms));
        Assert.True(new QuickSearchNameQuery(new string('x', QuickSearchNameQuery.MaxInputCharacters)).IsMatch(new string('x', QuickSearchNameQuery.MaxInputCharacters)));
    }
}
