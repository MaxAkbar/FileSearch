using System.Linq;
using FileSearch.Core.Queries;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class UnifiedQueryParserTests
{
    private readonly UnifiedQueryParser _parser = new();

    [Fact]
    public void Parse_UnfieldedTerms_BuildsContentQuery()
    {
        var query = _parser.Parse("invoice acme");

        Assert.True(query.HasContentCriteria);
        Assert.IsType<AndQuery>(query.ContentQuery);
        Assert.Equal(new[] { "Content", "Content" }, query.Chips.Select(chip => chip.Field).ToArray());
        Assert.True(query.IsMatch("ACME invoice 123"));
        Assert.False(query.IsMatch("invoice only"));
    }

    [Fact]
    public void Parse_FieldFilters_DoNotBecomeContentTerms()
    {
        var query = _parser.Parse("name:report path:finance size:>5mb");

        Assert.False(query.HasContentCriteria);
        Assert.IsType<MatchAllQuery>(query.ContentQuery);
        Assert.Equal(new[] { "report" }, query.Filters.NameTerms);
        Assert.Equal(new[] { "finance" }, query.Filters.PathTerms);
        Assert.Equal(5L * 1024 * 1024, query.Filters.MinSizeBytes);
        Assert.Equal(3, query.Chips.Count);
    }

    [Fact]
    public void Parse_TypeAndModifiedFilters_BuildsStructuredFilters()
    {
        var query = _parser.Parse("invoice acme modified:last-year type:pdf");

        Assert.True(query.HasContentCriteria);
        Assert.Contains(".pdf", query.Filters.Extensions);
        Assert.NotNull(query.Filters.ModifiedAfterUtc);
        Assert.NotNull(query.Filters.ModifiedBeforeUtc);
        Assert.Contains(query.Chips, chip => chip.Field == "Type" && chip.Value == "pdf");
        Assert.Contains(query.Chips, chip => chip.Field == "Modified" && chip.Value == "last-year");
        Assert.True(query.IsMatch("invoice for acme"));
    }

    [Fact]
    public void Parse_FieldedPhraseAndNear_BuildsProximityQuery()
    {
        var query = _parser.Parse("content:\"termination clause\" NEAR/2 notice");

        Assert.True(query.HasContentCriteria);
        Assert.IsType<NearQuery>(query.ContentQuery);
        Assert.Contains(query.Chips, chip => chip.Field == "Content" && chip.Value == "termination clause");
        Assert.True(query.IsMatch("notice period and termination clause"));
        Assert.False(query.IsMatch("notice one two three termination clause"));
        Assert.False(query.IsMatch("termination clause only"));
    }

    [Fact]
    public void Parse_FuzzyTerm_BuildsFuzzyQuery()
    {
        var query = _parser.Parse("invoice~");

        var fuzzy = Assert.IsType<FuzzyQuery>(query.ContentQuery);
        Assert.Equal("invoice", fuzzy.Term);
        Assert.Equal(1, fuzzy.MaxEdits);
        Assert.Contains(query.Chips, chip => chip.Field == "Fuzzy" && chip.Value == "invoice");
        Assert.True(query.IsMatch("invoce"));
    }

    [Fact]
    public void Parse_FuzzyTermWithDistance_UsesConfiguredDistance()
    {
        var query = _parser.Parse("invoice~2");

        var fuzzy = Assert.IsType<FuzzyQuery>(query.ContentQuery);
        Assert.Equal(2, fuzzy.MaxEdits);
        Assert.Contains(query.Chips, chip => chip.Field == "Fuzzy 2" && chip.Value == "invoice");
        Assert.True(query.IsMatch("invioce"));
    }

    [Fact]
    public void Parse_RegexField_BuildsRegexContentQuery()
    {
        var query = _parser.Parse("regex:\"TODO|FIXME\"");

        Assert.True(query.HasContentCriteria);
        Assert.True(query.IsMatch("FIXME: repair this"));
        Assert.False(query.IsMatch("done"));
    }

    [Fact]
    public void Parse_CreatedStatusExtractorFields_AreCaptured()
    {
        var query = _parser.Parse("created:2026 status:ok extractor:plain");

        Assert.False(query.HasContentCriteria);
        Assert.NotNull(query.Filters.CreatedAfterUtc);
        Assert.NotNull(query.Filters.CreatedBeforeUtc);
        Assert.Equal(new[] { "ok" }, query.Filters.StatusTerms);
        Assert.Equal(new[] { "plain" }, query.Filters.ExtractorTerms);
    }

    [Fact]
    public void Parse_SemanticField_CreatesDisabledSemanticChip()
    {
        var query = _parser.Parse("semantic:\"authentication migration\"");

        Assert.False(query.HasContentCriteria);
        Assert.True(query.HasUnavailableSemantic);
        Assert.Equal(new[] { "authentication migration" }, query.Filters.SemanticTerms);
        var chip = Assert.Single(query.Chips);
        Assert.Equal("Semantic", chip.Field);
        Assert.Equal("authentication migration", chip.Value);
        Assert.False(chip.IsEnabled);
        Assert.Equal(UnifiedQuery.SemanticUnavailableMessage, chip.Explanation);
    }

    [Fact]
    public void Parse_NaturalLanguagePdfInvoices_InfersTypeDateAndContent()
    {
        var query = _parser.Parse("PDF invoices from Acme modified last summer");

        Assert.True(query.HasContentCriteria);
        Assert.Contains(".pdf", query.Filters.Extensions);
        Assert.NotNull(query.Filters.ModifiedAfterUtc);
        Assert.NotNull(query.Filters.ModifiedBeforeUtc);
        Assert.Contains(query.Chips, chip => chip.Field == "Type" && chip.Value == "pdf");
        Assert.Contains(query.Chips, chip => chip.Field == "Modified" && chip.Value == "last-summer");
        Assert.Contains(query.Chips, chip => chip.Field == "Content" && chip.Value == "invoice");
        Assert.Contains(query.Chips, chip => chip.Field == "Content" && chip.Value == "Acme");
        Assert.True(query.IsMatch("Acme invoice"));
        Assert.False(query.IsMatch("invoice only"));
    }

    [Fact]
    public void Parse_NaturalLanguageSize_InfersSizeFilter()
    {
        var query = _parser.Parse("reports larger than 5mb");

        Assert.Equal(5L * 1024 * 1024, query.Filters.MinSizeBytes);
        Assert.Contains(query.Chips, chip => chip.Field == "Size" && chip.Value == ">5mb");
        Assert.True(query.IsMatch("report"));
    }

    [Fact]
    public void Parse_NaturalLanguageSemanticPhrase_CreatesDisabledSemanticChip()
    {
        var query = _parser.Parse("PDFs about authentication migration modified last year");

        Assert.True(query.HasUnavailableSemantic);
        Assert.Contains(".pdf", query.Filters.Extensions);
        Assert.NotNull(query.Filters.ModifiedAfterUtc);
        Assert.Equal(new[] { "authentication migration" }, query.Filters.SemanticTerms);
        var chip = Assert.Single(query.Chips, chip => chip.Field == "Semantic");
        Assert.False(chip.IsEnabled);
        Assert.Equal(UnifiedQuery.SemanticUnavailableMessage, chip.Explanation);
    }

    [Fact]
    public void Parse_ExplicitSyntaxDoesNotUseNaturalLanguageInterpretation()
    {
        var query = _parser.Parse("type:pdf invoices from Acme");

        Assert.Contains(".pdf", query.Filters.Extensions);
        Assert.Contains(query.Chips, chip => chip.Field == "Type" && chip.RawText == "type:pdf");
        Assert.True(query.IsMatch("invoices from Acme"));
        Assert.False(query.IsMatch("invoice Acme"));
    }
}
