using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Mcp;

namespace FileSearch.Mcp.Tests;

public sealed class ResultShaperTests
{
    [Theory]
    [InlineData(null, ResultShaper.DefaultMaxResults)]
    [InlineData(0, 1)]
    [InlineData(25, 25)]
    [InlineData(9_999, ResultShaper.MaxResultsCeiling)]
    public void ClampMaxResults_EnforcesBounds(int? requested, int expected)
    {
        Assert.Equal(expected, ResultShaper.ClampMaxResults(requested));
    }

    [Theory]
    [InlineData(null, ResultShaper.DefaultTimeoutSeconds)]
    [InlineData(1, ResultShaper.MinTimeoutSeconds)]
    [InlineData(600, ResultShaper.MaxTimeoutSeconds)]
    public void ClampTimeout_EnforcesBounds(int? requested, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ResultShaper.ClampTimeout(requested));
    }

    [Fact]
    public void ShapeLine_ShortLine_IsReturnedTrimmedAndUnflagged()
    {
        var (line, truncated) = ResultShaper.ShapeLine("  hello world  ", [new MatchSpan(2, 5)], 320);

        Assert.Equal("hello world", line);
        Assert.False(truncated);
    }

    [Fact]
    public void ShapeLine_LongLine_WindowsAroundFirstHighlight()
    {
        var content = new string('a', 5000) + "NEEDLE" + new string('b', 5000);
        var highlight = new MatchSpan(5000, 6);

        var (line, truncated) = ResultShaper.ShapeLine(content, [highlight], 320);

        Assert.True(truncated);
        Assert.Contains("NEEDLE", line, StringComparison.Ordinal);
        Assert.StartsWith("…", line, StringComparison.Ordinal);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.True(line.Length <= 322, $"window length {line.Length}");
    }

    [Fact]
    public void ShapeLine_NoHighlights_KeepsLineStart()
    {
        var content = "start-of-line " + new string('x', 1000);

        var (line, truncated) = ResultShaper.ShapeLine(content, [], 320);

        Assert.True(truncated);
        Assert.StartsWith("start-of-line", line, StringComparison.Ordinal);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_StopsAtMaxResults_AndReportsTruncation()
    {
        var collected = await ResultShaper.CollectAsync(
            HitStream(count: 100, cancellationToken: TestContext.Current.CancellationToken),
            maxResults: 10,
            maxResultsPerFile: 0,
            timeout: TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(10, collected.Hits.Count);
        Assert.True(collected.Truncated);
        Assert.False(collected.TimedOut);
    }

    [Fact]
    public async Task Collect_PerFileCap_SuppressesRepeatHits()
    {
        var collected = await ResultShaper.CollectAsync(
            HitStream(count: 30, path: @"C:\same\file.txt", cancellationToken: TestContext.Current.CancellationToken),
            maxResults: 100,
            maxResultsPerFile: 3,
            timeout: TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, collected.Hits.Count);
        Assert.Equal(30, collected.TotalMatches);
        Assert.True(collected.Truncated);
    }

    [Fact]
    public async Task Collect_Timeout_ReturnsPartialResults()
    {
        var collected = await ResultShaper.CollectAsync(
            SlowHitStream(TestContext.Current.CancellationToken),
            maxResults: 100,
            maxResultsPerFile: 0,
            timeout: TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);

        Assert.True(collected.TimedOut);
        Assert.True(collected.Hits.Count >= 1, "hits yielded before the deadline are kept");
    }

    [Fact]
    public async Task Collect_ClientCancellation_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResultShaper.CollectAsync(
                SlowHitStream(cancelled.Token),
                maxResults: 10,
                maxResultsPerFile: 0,
                timeout: TimeSpan.FromSeconds(30),
                cancelled.Token));
    }

    [Fact]
    public void ToDocument_MapsHitFields()
    {
        var modified = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        var hit = new Hit(
            @"C:\data\a.txt",
            7,
            "needle in line",
            [new MatchSpan(0, 6)],
            HitKind.Content,
            42.5,
            123,
            modified,
            HitRoute.Indexed);

        var document = ResultShaper.ToDocument(hit);

        Assert.Equal(@"C:\data\a.txt", document.Path);
        Assert.Equal(7, document.LineNumber);
        Assert.Equal("needle in line", document.Line);
        Assert.False(document.LineTruncated);
        Assert.Equal("Content", document.Kind);
        Assert.Equal("Indexed", document.Route);
        Assert.Equal(42.5, document.Score);
        Assert.Equal(123, document.SizeBytes);
        Assert.Equal(modified, document.ModifiedUtc);
        Assert.Null(document.Anchor);
    }

    private static async IAsyncEnumerable<Hit> HitStream(
        int count,
        string path = @"C:\data\file.txt",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Hit(path, i, $"line {i}", []);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<Hit> SlowHitStream(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new Hit(@"C:\data\slow.txt", 1, "first", []);
        for (var i = 2; i <= 1000; i++)
        {
            await Task.Delay(50, cancellationToken);
            yield return new Hit(@"C:\data\slow.txt", i, $"line {i}", []);
        }
    }
}
