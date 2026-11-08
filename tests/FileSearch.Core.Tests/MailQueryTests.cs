using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Tests;

public sealed class MailQueryTests
{
    private static MailMessageMetadata Message => new("message-1", "Quarterly budget review", "Alice <alice@example.com>",
        "Bob <bob@example.com>; Carol <carol@example.com>", "Dora <dora@example.com>",
        new DateTime(2026, 11, 7, 13, 30, 0, DateTimeKind.Utc), "Inbox", "store-1")
    {
        SentUtc = new DateTime(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc),
    };

    [Theory]
    [InlineData("from:alice", true)]
    [InlineData("from:ALICE@EXAMPLE.COM", true)]
    [InlineData("from:body-only", false)]
    [InlineData("to:carol", true)]
    [InlineData("to:dora", false)]
    [InlineData("cc:dora", true)]
    [InlineData("cc:bob", false)]
    [InlineData("recipient:carol", true)]
    [InlineData("recipient:dora", true)]
    [InlineData("recipient:alice", false)]
    [InlineData("subject:\"budget review\"", true)]
    [InlineData("subject:\"budget report\"", false)]
    public void FieldsMatchTheirOwnMetadata(string text, bool expected)
    {
        var query = new UnifiedQueryParser().Parse(text);
        Assert.True(query.HasMailCriteria);
        Assert.True(query.HasContentCriteria);
        Assert.False(query.HasTextCriteria);
        Assert.IsType<MailFieldQuery>(query.ContentQuery);
        var spans = new List<MatchSpan>();
        Assert.Equal(expected, query.TryCollectHighlights("body-only budget report from dora", Message, spans));
        Assert.Empty(spans);
    }

    [Theory]
    [InlineData("from:alice subject:budget", true)]
    [InlineData("from:bob OR cc:dora", true)]
    [InlineData("from:alice AND NOT to:dora", true)]
    [InlineData("NOT from:alice", false)]
    [InlineData("NOT (from:alice OR to:carol)", false)]
    [InlineData("(from:bob OR cc:dora) AND subject:budget", true)]
    [InlineData("from:bob AND cc:dora OR subject:missing", false)]
    [InlineData("NOT NOT from:alice", true)]
    public void MailPredicatesPreserveBooleanStructure(string text, bool expected)
    {
        var query = new UnifiedQueryParser().Parse(text);
        Assert.Equal(expected, query.TryCollectHighlights("unrelated", Message, []));
    }

    [Fact]
    public void MixedMailAndContentExpressionsKeepOnlyMatchedTextHighlights()
    {
        var query = new UnifiedQueryParser().Parse("(from:bob AND leaked) OR (from:alice AND invoice)");
        var spans = new List<MatchSpan> { new(99, 1) };
        Assert.True(query.HasTextCriteria);
        Assert.True(query.TryCollectHighlights("leaked invoice", Message, spans));
        Assert.Equal([new MatchSpan(99, 1), new MatchSpan(7, 7)], spans);
        spans.Clear();
        Assert.False(query.TryCollectHighlights("leaked only", Message, spans));
        Assert.Empty(spans);
    }

    [Fact]
    public void FailedAndAndNegatedSubtreesDoNotLeakHighlights()
    {
        var failedAnd = new UnifiedQueryParser().Parse("invoice from:bob");
        var negated = new UnifiedQueryParser().Parse("NOT (invoice AND from:bob)");
        var spans = new List<MatchSpan> { new(90, 2) };
        Assert.False(failedAnd.TryCollectHighlights("invoice", Message, spans));
        Assert.Equal([new MatchSpan(90, 2)], spans);
        Assert.True(negated.TryCollectHighlights("invoice", Message, spans));
        Assert.Equal([new MatchSpan(90, 2)], spans);
    }

    [Theory]
    [InlineData("NOT from:alice")]
    [InlineData("from:alice OR invoice")]
    [InlineData("NOT sent:2026-11-07")]
    [InlineData("from:alice OR type:email")]
    public void MissingMetadataCannotMatchEvenThroughNegationOrContentBranches(string text)
    {
        var query = new UnifiedQueryParser().Parse(text);
        Assert.True(query.HasMailCriteria);
        var spans = new List<MatchSpan>();
        Assert.False(query.TryCollectHighlights("invoice", null, spans));
        Assert.False(query.TryCollectHighlights("invoice", spans));
        Assert.False(query.IsMatch("invoice"));
        query.CollectHighlights("invoice", spans);
        Assert.Empty(spans);
    }

    [Fact]
    public void TextCaseSensitivityAppliesToFieldsAndContent()
    {
        var parser = new UnifiedQueryParser(caseSensitive: true);
        Assert.True(parser.Parse("from:\"Alice <\" AND Invoice").TryCollectHighlights("Invoice", Message, []));
        Assert.False(parser.Parse("from:\"alice <\" AND Invoice").TryCollectHighlights("Invoice", Message, []));
        Assert.False(parser.Parse("from:\"Alice <\" AND invoice").TryCollectHighlights("Invoice", Message, []));
    }

    [Fact]
    public void QuotedAddressAndChipSourcePositionsArePreserved()
    {
        const string text = "from:\"Alice <alice@example.com>\" subject:\"budget review\"";
        var query = new UnifiedQueryParser().Parse(text);
        Assert.True(query.TryCollectHighlights("unrelated", Message, []));
        Assert.Collection(query.Chips,
            chip => Assert.Equal("from:\"Alice <alice@example.com>\"", text.Substring(chip.Position, chip.Length)),
            chip => Assert.Equal("subject:\"budget review\"", text.Substring(chip.Position, chip.Length)));
    }

    [Theory]
    [InlineData("from:alice invoices", "invoices", true)]
    [InlineData("subject:budget reports", "reports", true)]
    [InlineData("sent:2026-11-07 reports", "reports", true)]
    [InlineData("to:bob invoices", "invoice", false)]
    public void ExplicitMailSyntaxIsNeverRewrittenAsNaturalLanguage(string text, string line, bool expected)
    {
        var query = new UnifiedQueryParser().Parse(text);
        Assert.True(query.HasMailCriteria);
        Assert.Equal(expected, query.TryCollectHighlights(line, Message, []));
        Assert.All(query.Chips, chip => Assert.True(chip.Position >= 0));
    }

    [Theory]
    [InlineData("sent:2026-11-07", true)]
    [InlineData("sent:2026-11-06", false)]
    [InlineData("sent:2026-11-01..2026-11-07", true)]
    [InlineData("sent:..2026-11-07", true)]
    [InlineData("sent:2026-11-07..", true)]
    [InlineData("sent:>=2026-11-07", true)]
    [InlineData("sent:>2026-11-07", false)]
    [InlineData("sent:<=2026-11-07", true)]
    [InlineData("sent:<2026-11-07", false)]
    [InlineData("sent:2026-11-07T12:30:00Z", true)]
    [InlineData("sent:2026-11-07T04:30:00-08:00", true)]
    [InlineData("sent:2026-11-07T14:30:00+02:00", true)]
    [InlineData("sent:>=2026-11-07T12:30Z", true)]
    [InlineData("sent:>2026-11-07T12:30Z", false)]
    [InlineData("sent:<=2026-11-07T12:30Z", true)]
    [InlineData("sent:<2026-11-07T12:30Z", false)]
    [InlineData("sent:2026-11-07T12:29Z..2026-11-07T12:31Z", true)]
    public void SentDateUsesUtcDaysAndPreciseInstantBoundaries(string text, bool expected)
    {
        var query = new UnifiedQueryParser().Parse(text);
        Assert.Equal(expected, query.TryCollectHighlights("unrelated", Message, []));
        Assert.False(query.TryCollectHighlights("unrelated", Message with { SentUtc = null }, []));
    }

    [Fact]
    public void UtcDayIncludesFirstAndLastTicksWithoutSpillingIntoNextDay()
    {
        var query = new UnifiedQueryParser().Parse("sent:2026-11-07");
        var start = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(query.TryCollectHighlights("unrelated", Message with { SentUtc = start }, []));
        Assert.True(query.TryCollectHighlights("unrelated", Message with { SentUtc = start.AddDays(1).AddTicks(-1) }, []));
        Assert.False(query.TryCollectHighlights("unrelated", Message with { SentUtc = start.AddTicks(-1) }, []));
        Assert.False(query.TryCollectHighlights("unrelated", Message with { SentUtc = start.AddDays(1) }, []));
    }

    [Fact]
    public void FractionalInstantKeepsItsExactTicksAndOffsetsNormalizeAcrossDays()
    {
        var exact = new DateTime(2026, 11, 8, 0, 30, 0, DateTimeKind.Utc).AddTicks(1234567);
        var query = new UnifiedQueryParser().Parse("sent:2026-11-07T16:30:00.1234567-08:00");
        Assert.True(query.TryCollectHighlights("", Message with { SentUtc = exact }, []));
        Assert.False(query.TryCollectHighlights("", Message with { SentUtc = exact.AddTicks(1) }, []));
        Assert.False(query.TryCollectHighlights("", Message with { SentUtc = exact.AddTicks(-1) }, []));
    }

    [Theory]
    [InlineData("sent:2026-11-08..2026-11-07")]
    [InlineData("sent:2026-11-07T12:30Z..2026-11-07T12:30+02:00")]
    [InlineData("sent:2026-11")]
    [InlineData("sent:11/07/2026")]
    [InlineData("sent:2026-02-30")]
    [InlineData("sent:today")]
    [InlineData("sent:2026-11-07T12:30")]
    [InlineData("sent:2026-11-07T12:30+25:00")]
    [InlineData("sent:2026-11-07..2026-11-08..2026-11-09")]
    [InlineData("sent:..")]
    [InlineData("sent:")]
    [InlineData("from:\"\"")]
    [InlineData("subject:\"   \"")]
    public void InvalidOrAmbiguousValuesAreRejected(string text) =>
        Assert.Throws<FormatException>(() => new UnifiedQueryParser().Parse(text));

    [Theory]
    [InlineData("from:alice NEAR/2 invoice")]
    [InlineData("invoice NEAR/2 subject:budget")]
    [InlineData("(from:alice OR invoice) NEAR/2 note")]
    [InlineData("from:alice NEAR/2 type:email")]
    [InlineData("from:alice semantic:invoice")]
    [InlineData("from:alice OR semantic:invoice")]
    public void UnsupportedProximityAndSemanticCombinationsAreRejected(string text) =>
        Assert.Throws<FormatException>(() => new UnifiedQueryParser().Parse(text));

    [Fact]
    public void ContentWithoutMailFieldsKeepsExistingBehavior()
    {
        var query = new UnifiedQueryParser().Parse("invoice OR receipt");
        var spans = new List<MatchSpan>();
        Assert.False(query.HasMailCriteria);
        Assert.True(query.HasTextCriteria);
        Assert.True(query.TryCollectHighlights("invoice", null, spans));
        Assert.Equal([new MatchSpan(0, 7)], spans);
        Assert.True(query.IsMatch("receipt"));
        Assert.False(new UnifiedQueryParser().Parse("type:email").HasTextCriteria);
    }
}
