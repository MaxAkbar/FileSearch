using FileSearch.Core.Engine;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ContentChunkerTests
{
    [Fact]
    public void CreateChunks_GroupsUnitsWithOverlapAndLineCoverage()
    {
        var units = new[]
        {
            CreateUnit(1, new string('a', 80), line: 1),
            CreateUnit(2, new string('b', 80), line: 2),
            CreateUnit(3, new string('c', 80), line: 3),
            CreateUnit(4, new string('d', 80), line: 4),
        };
        var chunker = new ContentUnitChunker();

        var chunks = chunker.CreateChunks(
            units,
            new ContentChunkingOptions(TargetCharacters: 160, MaxCharacters: 200, OverlapUnits: 1));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new long[] { 1, 2 }, chunks[0].ContentUnitIds);
        Assert.Equal(new long[] { 2, 3 }, chunks[1].ContentUnitIds);
        Assert.Equal(new long[] { 3, 4 }, chunks[2].ContentUnitIds);
        Assert.Equal(new string('a', 80) + Environment.NewLine + new string('b', 80), chunks[0].Text);
        Assert.Equal(1, chunks[0].Locator.StartLine);
        Assert.Equal(2, chunks[0].Locator.EndLine);
        Assert.Equal("lines 1-2", chunks[0].Locator.DisplayText);
        Assert.Equal(ContentUnitChunker.ChunkerId, chunks[0].ChunkerId);
        Assert.Equal(ContentUnitChunker.ChunkerVersion, chunks[0].ChunkerVersion);
    }

    [Fact]
    public void CreateChunks_KeepsOversizedSingleUnit()
    {
        var units = new[] { CreateUnit(1, new string('x', 80), line: 1) };
        var chunker = new ContentUnitChunker();

        var chunk = Assert.Single(chunker.CreateChunks(
            units,
            new ContentChunkingOptions(TargetCharacters: 10, MaxCharacters: 20)));

        Assert.Equal(new long[] { 1 }, chunk.ContentUnitIds);
        Assert.Equal(80, chunk.Text.Length);
    }

    [Fact]
    public void CreateChunks_ProducesStableKeysAndHashes()
    {
        var units = new[]
        {
            CreateUnit(1, "alpha", line: 1),
            CreateUnit(2, "bravo", line: 2),
        };
        var chunker = new ContentUnitChunker();

        var first = Assert.Single(chunker.CreateChunks(units));
        var second = Assert.Single(chunker.CreateChunks(units));

        Assert.Equal(64, first.ContentHash.Length);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.ChunkKey, second.ChunkKey);
        Assert.StartsWith("10:1-2:", first.ChunkKey, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateChunks_MergesSamePageOcrRegions()
    {
        var units = new[]
        {
            CreateUnit(1, "top", new SourceLocator(Page: 3, StartLine: 1, EndLine: 1, X: 10, Y: 20, Width: 30, Height: 10, SourceWidth: 100, SourceHeight: 200)),
            CreateUnit(2, "bottom", new SourceLocator(Page: 3, StartLine: 2, EndLine: 2, X: 5, Y: 45, Width: 50, Height: 20, SourceWidth: 100, SourceHeight: 200)),
        };
        var chunker = new ContentUnitChunker();

        var chunk = Assert.Single(chunker.CreateChunks(units));

        Assert.Equal(3, chunk.Locator.Page);
        Assert.Equal(5, chunk.Locator.X);
        Assert.Equal(20, chunk.Locator.Y);
        Assert.Equal(50, chunk.Locator.Width);
        Assert.Equal(45, chunk.Locator.Height);
        Assert.Equal("page 3 lines 1-2", chunk.Locator.DisplayText);
    }

    private static ContentUnit CreateUnit(long id, string text, int line) =>
        CreateUnit(id, text, new SourceLocator(StartLine: line, EndLine: line, DisplayText: $"line {line}"));

    private static ContentUnit CreateUnit(long id, string text, SourceLocator locator) =>
        new(
            id,
            FileId: 10,
            ContentUnitKind.Text,
            locator,
            text,
            $"hash-{id}",
            "en",
            "plain",
            "1");
}
