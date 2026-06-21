using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class SnippetGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_UsesNeighboringContentUnits()
    {
        var locator = new SourceLocator(StartLine: 20, EndLine: 20, DisplayText: "line 20");
        var reader = new StubContentUnitReader(
            new ContentUnit(10, 5, ContentUnitKind.Text, new SourceLocator(StartLine: 19), "before context", "a", "", "", ""),
            new ContentUnit(11, 5, ContentUnitKind.Text, locator, "needle appears here", "b", "", "", ""),
            new ContentUnit(12, 5, ContentUnitKind.Text, new SourceLocator(StartLine: 21), "after context", "c", "", "", ""));
        var generator = new ContentUnitSnippetGenerator(reader);
        var request = new SearchRequest(new TermQuery("needle"), new[] { @"C:\docs" }, new WalkerOptions());
        var hit = new Hit("a.txt", 20, "needle appears here", Array.Empty<MatchSpan>(), ContentUnitId: 11);

        var snippet = await generator.GenerateAsync(request, hit, TestContext.Current.CancellationToken);

        Assert.Equal(
            string.Join(Environment.NewLine, "before context", "needle appears here", "after context"),
            snippet.Text);
        Assert.Equal(locator, snippet.Locator);
        Assert.Equal(11, snippet.ContentUnitId);
        Assert.Equal(new long[] { 10, 11, 12 }, snippet.ContentUnitIds);
        var highlight = Assert.Single(snippet.Highlights);
        Assert.Equal(snippet.Text.IndexOf("needle", StringComparison.Ordinal), highlight.Start);
        Assert.Equal(6, highlight.Length);
    }

    [Fact]
    public async Task GenerateAsync_FallsBackToHitLineWithoutContentUnit()
    {
        var generator = new ContentUnitSnippetGenerator(new StubContentUnitReader());
        var request = new SearchRequest(new TermQuery("needle"), new[] { @"C:\docs" }, new WalkerOptions());
        var hit = new Hit("a.txt", 3, "the needle line", Array.Empty<MatchSpan>());

        var snippet = await generator.GenerateAsync(request, hit, TestContext.Current.CancellationToken);

        Assert.Equal("the needle line", snippet.Text);
        Assert.Null(snippet.ContentUnitId);
        Assert.Empty(snippet.ContentUnitIds);
        Assert.Single(snippet.Highlights);
    }

    private sealed class StubContentUnitReader : IContentUnitReader
    {
        private readonly IReadOnlyList<ContentUnit> _units;

        public StubContentUnitReader(params ContentUnit[] units) =>
            _units = units;

        public Task<ContentUnit?> GetContentUnitAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(_units.FirstOrDefault(unit => unit.Id == id));

        public Task<IReadOnlyList<ContentUnit>> GetContentUnitsForFileAsync(
            long fileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContentUnit>>(
                _units.Where(unit => unit.FileId == fileId).ToArray());

        public Task<IReadOnlyList<ContentUnit>> GetNeighboringUnitsAsync(
            long contentUnitId,
            int before,
            int after,
            CancellationToken cancellationToken) =>
            Task.FromResult(_units);
    }
}
