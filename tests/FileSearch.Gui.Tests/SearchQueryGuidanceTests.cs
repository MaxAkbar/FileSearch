using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class SearchQueryGuidanceTests
{
    [Theory]
    [InlineData("from:alice subject:invoice", "Mail fields only")]
    [InlineData("from:alice invoice", "Mail fields and content")]
    [InlineData("sent:2026-11-07", "Mail fields only")]
    public void MailGuidanceExplainsMessageScopeAndPhysicalFileDates(string input, string expected)
    {
        string description = SearchQueryGuidance.Describe(input, QueryMode.Unified, false);
        Assert.StartsWith(expected, description);
        Assert.Contains("one message", description);
        Assert.Contains("UTC", description);
        Assert.Contains("physical file", description);
        Assert.DoesNotContain("File filters only", description);
    }

    [Theory]
    [InlineData(QueryMode.Unified, "john smith", "All terms (AND): john, smith", "smith john", "john alone")]
    [InlineData(QueryMode.Boolean, "john smith", "All terms (AND): john, smith", "smith john", "john alone")]
    [InlineData(QueryMode.Unified, "john OR smith", "Either term (OR): john, smith", "smith alone", "neither name")]
    [InlineData(QueryMode.Boolean, "john OR smith", "Either term (OR): john, smith", "smith alone", "neither name")]
    [InlineData(QueryMode.Unified, "\"john smith\"", "Phrase: john smith", "hello john smith", "smith john")]
    [InlineData(QueryMode.Boolean, "\"john smith\"", "Phrase: john smith", "hello john smith", "smith john")]
    public void CommonInterpretationsDescribeTheActualParser(QueryMode mode, string input, string description,
        string matching, string nonmatching)
    {
        Assert.Equal(description, SearchQueryGuidance.Describe(input, mode, false));
        var expression = new QueryFactory().Build(input, mode, false);
        Assert.True(expression.IsMatch(matching));
        Assert.False(expression.IsMatch(nonmatching));
    }

    [Theory]
    [InlineData("john smith", "john smith", "john other smith")]
    [InlineData("john OR smith", "john OR smith", "smith alone")]
    [InlineData("\"john smith\"", "\"john smith\"", "john smith")]
    public void PlainTextKeepsSpacesQuotesAndOperatorsLiteral(string input, string matching, string nonmatching)
    {
        string description = SearchQueryGuidance.Describe(input, QueryMode.PlainText, false);
        Assert.StartsWith("Literal text: " + input, description);
        Assert.Contains("Quotes and operators are text", description);
        var expression = new QueryFactory().Build(input, QueryMode.PlainText, false);
        Assert.True(expression.IsMatch(matching));
        Assert.False(expression.IsMatch(nonmatching));
    }

    [Theory]
    [InlineData(QueryMode.Unified)]
    [InlineData(QueryMode.Boolean)]
    public void GroupsAndNotAreNotFlattenedIntoAnAllTermsList(QueryMode mode)
    {
        const string input = "(john OR smith) AND NOT archived";
        string description = SearchQueryGuidance.Describe(input, mode, false);
        Assert.StartsWith("Combined expression", description);
        Assert.Contains("groups and NOT", description);
        Assert.DoesNotContain("All terms (AND): john, smith", description);
        var expression = new QueryFactory().Build(input, mode, false);
        Assert.True(expression.IsMatch("smith active"));
        Assert.False(expression.IsMatch("john archived"));
    }

    [Fact]
    public void StructuredFiltersRemainSeparateFromTheContentMeaning()
    {
        string content = SearchQueryGuidance.Describe("type:pdf john smith", QueryMode.Unified, false);
        Assert.StartsWith("All terms (AND): john, smith", content);
        Assert.Contains("File filters apply", content);
        Assert.Equal("File filters only; no content text is required.",
            SearchQueryGuidance.Describe("type:pdf", QueryMode.Unified, false));
    }

    [Fact]
    public void BareWildcardDescribesTargetTextRatherThanInventingAFileFilter()
    {
        string description = SearchQueryGuidance.Describe("*.db", QueryMode.Unified, false);
        Assert.Equal("Wildcard text: *.db", description);
        var expression = Assert.IsType<UnifiedQuery>(new QueryFactory().Build("*.db", QueryMode.Unified, false));
        Assert.False(expression.Filters.HasFileFilters);
        Assert.IsType<RegexQuery>(expression.ContentQuery);
    }

    [Theory]
    [InlineData(QueryMode.Unified, "\"john")]
    [InlineData(QueryMode.Boolean, "john OR")]
    [InlineData(QueryMode.Regex, "[")]
    public void PartialInvalidInputReturnsGuidanceWithoutThrowing(QueryMode mode, string input) =>
        Assert.StartsWith("Incomplete or invalid query:", SearchQueryGuidance.Describe(input, mode, false));

    [Theory]
    [InlineData(QueryMode.Unified, "AND")]
    [InlineData(QueryMode.Boolean, "AND")]
    [InlineData(QueryMode.PlainText, "literally")]
    [InlineData(QueryMode.Regex, "Regular expression")]
    [InlineData(QueryMode.Semantic, "model")]
    public void EmptyInputExplainsTheSelectedMode(QueryMode mode, string expected) =>
        Assert.Contains(expected, SearchQueryGuidance.Describe(" ", mode, false));

    [Theory]
    [InlineData(QueryMode.PlainText)]
    [InlineData(QueryMode.Unified)]
    [InlineData(QueryMode.Boolean)]
    [InlineData(QueryMode.Regex)]
    [InlineData(QueryMode.Semantic)]
    public void LargeInputCannotMakeTheGuidanceUnbounded(QueryMode mode)
    {
        string input = string.Concat(Enumerable.Repeat("john OR smith ", 1_000));
        string description = SearchQueryGuidance.Describe(input, mode, false);
        Assert.InRange(description.Length, 1, 240);
    }

    [Fact]
    public void DeeplyNestedInputUsesTheModeHintWithoutRecursingThroughTheParser()
    {
        string input = new string('(', 100) + "john" + new string(')', 100);
        Assert.StartsWith("Boolean:", SearchQueryGuidance.Describe(input, QueryMode.Boolean, false));
    }

    [Theory]
    [InlineData(QueryMode.Semantic, "john OR smith")]
    [InlineData(QueryMode.Unified, "semantic:renewal")]
    public void MeaningGuidanceStatesPrerequisitesWithoutClaimingModelReadiness(QueryMode mode, string input)
    {
        string description = SearchQueryGuidance.Describe(input, mode, false);
        Assert.Contains("Meaning search", description);
        Assert.Contains("model", description);
        Assert.Contains("indexed vectors", description);
        Assert.DoesNotContain("ready", description, StringComparison.OrdinalIgnoreCase);
    }
}
