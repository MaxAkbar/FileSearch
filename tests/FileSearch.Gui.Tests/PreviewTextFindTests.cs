using System.Globalization;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class PreviewTextFindTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("john smith john other smith", "john smith", 0)]
    [InlineData("john OR smith john smith", "john OR smith", 0)]
    [InlineData("john smith \"john smith\"", "\"john smith\"", 11)]
    [InlineData("invoice\r\nrenewal invoice renewal", "invoice\r\nrenewal", 0)]
    [InlineData("abc  def", "  ", 3)]
    public void QueryRemainsOneLiteralPhrase(string text, string query, int start)
    {
        var result = PreviewTextFind.Find(text, query, false, Token);
        var match = Assert.Single(result.Matches);
        Assert.Equal(start, match.Start);
        Assert.Equal(query.Length, match.Length);
        Assert.Equal(query, text[match.Start..match.End]);
        Assert.True(result.IsComplete);
        Assert.Null(result.LimitReason);
    }

    [Fact]
    public void OrdinalCaseMatchingDoesNotFollowTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            const string text = "FILE file FİLE fıle";
            Assert.Equal(new[] { 0, 5 }, PreviewTextFind.Find(text, "file", false, Token).Matches.Select(match => match.Start));
            Assert.Equal(5, Assert.Single(PreviewTextFind.Find(text, "file", true, Token).Matches).Start);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MatchesAreOrderedNonOverlappingAndIncludeTheFinalPosition()
    {
        var result = PreviewTextFind.Find("aaaaaa", "aa", true, Token);
        Assert.Equal(new[] { new PreviewTextMatch(0, 2), new PreviewTextMatch(2, 2), new PreviewTextMatch(4, 2) }, result.Matches);
        Assert.Equal(6, result.TextCharactersSearched);
        Assert.True(result.IsComplete);
    }

    [Theory]
    [InlineData("", "needle")]
    [InlineData("short", "longer query")]
    [InlineData("ordinary", "needle")]
    public void EmptyOrNonmatchingTextReturnsACompleteEmptyResult(string text, string query)
    {
        var result = PreviewTextFind.Find(text, query, false, Token);
        Assert.Empty(result.Matches);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void EmptyQueryDoesNotPublishEveryCharacterAsAMatch()
    {
        var result = PreviewTextFind.Find(new string('x', PreviewTextFind.MaxTextCharacters + 10), "", false, Token);
        Assert.Empty(result.Matches);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.TextCharactersSearched);
    }

    [Fact]
    public void MatchesCrossingSearchWindowsAreNeitherMissedNorDuplicated()
    {
        const string query = "renewal 東京";
        string text = new string('x', 16 * 1024 - 4) + query + new string('x', 16 * 1024 - 4) + query;
        var result = PreviewTextFind.Find(text, query, true, Token);
        Assert.Equal(new[] { 16 * 1024 - 4, 2 * (16 * 1024 - 4) + query.Length }, result.Matches.Select(match => match.Start));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void LongQueryAcrossAWindowBoundaryRetainsItsExactRange()
    {
        string query = "prefix-" + new string('q', PreviewTextFind.MaxQueryCharacters - 14) + "-suffix";
        string text = new string('x', 16 * 1024 - 1) + query;
        var match = Assert.Single(PreviewTextFind.Find(text, query, true, Token).Matches);
        Assert.Equal(16 * 1024 - 1, match.Start);
        Assert.Equal(PreviewTextFind.MaxQueryCharacters, match.Length);
    }

    [Fact]
    public void UnicodeMatchesUseUtf16OffsetsWithoutSplittingSurrogatePairs()
    {
        const string text = "😀東京 😀東京";
        var result = PreviewTextFind.Find(text, "😀東京", true, Token);
        Assert.Equal(new[] { new PreviewTextMatch(0, 4), new PreviewTextMatch(5, 4) }, result.Matches);
        Assert.Empty(PreviewTextFind.Find(text, "\uD83D", true, Token).Matches);
        Assert.Empty(PreviewTextFind.Find(text, "\uDE00", true, Token).Matches);
    }

    [Fact]
    public void WholeSurrogatesRemainSearchableAcrossAWindowBoundary()
    {
        string text = new string('x', 16 * 1024 - 1) + "😀";
        var match = Assert.Single(PreviewTextFind.Find(text, "😀", true, Token).Matches);
        Assert.Equal(16 * 1024 - 1, match.Start);
        Assert.Equal(2, match.Length);
    }

    [Fact]
    public void TextBudgetExcludesASplitPairAndKeepsPartialCoverageExplicit()
    {
        string text = new string('x', PreviewTextFind.MaxTextCharacters - 1) + "😀 needle";
        var result = PreviewTextFind.Find(text, "needle", true, Token);
        Assert.Empty(result.Matches);
        Assert.False(result.IsComplete);
        Assert.Equal(PreviewTextFindLimits.TextLength, result.Limits);
        Assert.Equal(PreviewTextFind.MaxTextCharacters - 1, result.TextCharactersSearched);
        Assert.NotNull(result.LimitReason);
    }

    [Fact]
    public void AHitAtTheTextLimitIsIncludedButLaterHitsAreNot()
    {
        const string query = "needle";
        string text = new string('x', PreviewTextFind.MaxTextCharacters - query.Length) + query + " " + query;
        var result = PreviewTextFind.Find(text, query, true, Token);
        Assert.Equal(PreviewTextFind.MaxTextCharacters - query.Length, Assert.Single(result.Matches).Start);
        Assert.Equal(PreviewTextFindLimits.TextLength, result.Limits);
    }

    [Fact]
    public void ExactlyTheMatchLimitRemainsComplete()
    {
        var result = PreviewTextFind.Find(new string('x', PreviewTextFind.MaxMatches), "x", true, Token);
        Assert.Equal(PreviewTextFind.MaxMatches, result.Matches.Count);
        Assert.True(result.IsComplete);
        Assert.Equal(PreviewTextFind.MaxMatches - 1, result.Matches[^1].Start);
    }

    [Fact]
    public void AdditionalMatchesReportPartialCoverageWithoutPublishingBeyondTheLimit()
    {
        var result = PreviewTextFind.Find(new string('x', PreviewTextFind.MaxMatches + 100), "x", true, Token);
        Assert.Equal(PreviewTextFind.MaxMatches, result.Matches.Count);
        Assert.False(result.IsComplete);
        Assert.Equal(PreviewTextFindLimits.MatchCount, result.Limits);
        Assert.Equal(PreviewTextFind.MaxMatches + 1, result.TextCharactersSearched);
        Assert.NotNull(result.LimitReason);
    }

    [Fact]
    public void TextAndMatchLimitsAreBothRetained()
    {
        var result = PreviewTextFind.Find(new string('x', PreviewTextFind.MaxTextCharacters + 1), "x", true, Token);
        Assert.Equal(PreviewTextFindLimits.TextLength | PreviewTextFindLimits.MatchCount, result.Limits);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void QueryLengthLimitRejectsAnOversizedQuery()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => PreviewTextFind.Find("ordinary source", new string('x', PreviewTextFind.MaxQueryCharacters + 1), false, Token));
        Assert.Equal("query", error.ParamName);
    }

    [Fact]
    public void CanceledSearchDoesNotReturnAPartialSuccessEvenForAnEmptyQuery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PreviewTextFind.Find("ordinary source", "", false, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => PreviewTextFind.Find(new string('x', PreviewTextFind.MaxTextCharacters), "needle", false, cancellation.Token));
    }
}
