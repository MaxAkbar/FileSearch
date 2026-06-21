using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class HybridSearcherTests
{
    [Fact]
    public async Task SearchAsync_EmitsBestCandidateForEachRankedResult()
    {
        var anchor = new SourceAnchor(SourceAnchorKind.Text, "line 7", Line: 7, Column: 3);
        var modifiedUtc = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
        var pipeline = new StubHybridRetrievalPipeline(new[]
        {
            new RankedSearchResult(
                1,
                "b.txt",
                50,
                new[]
                {
                    new SearchCandidate(
                        CandidateProviderKind.Metadata,
                        "metadata",
                        "b.txt",
                        "name match",
                        5,
                        HitKind.Metadata),
                    new SearchCandidate(
                        CandidateProviderKind.Lexical,
                        "lexical",
                        "b.txt",
                        "content match",
                        10,
                        HitKind.Content,
                        7,
                        new[] { new MatchSpan(0, 7) },
                        sizeBytes: 1024,
                        modifiedUtc: modifiedUtc,
                        route: HitRoute.Indexed,
                        anchor: anchor),
                }),
            new RankedSearchResult(
                2,
                "a.txt",
                25,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "a.txt", "other match", 8),
                }),
        });
        var searcher = new HybridSearcher(pipeline);

        var hits = await CollectAsync(searcher);

        Assert.Equal(2, hits.Count);
        Assert.Equal("b.txt", hits[0].Path);
        Assert.Equal(7, hits[0].LineNumber);
        Assert.Equal("content match", hits[0].LineContent);
        Assert.Equal(50, hits[0].Score);
        Assert.Equal(HitKind.Content, hits[0].Kind);
        Assert.Equal(HitRoute.Indexed, hits[0].Route);
        Assert.Equal(1024, hits[0].SizeBytes);
        Assert.Equal(modifiedUtc, hits[0].ModifiedUtc);
        Assert.Equal(anchor, hits[0].Anchor);
        Assert.Single(hits[0].Highlights);
        Assert.Equal("a.txt", hits[1].Path);
        Assert.Equal(25, hits[1].Score);
    }

    [Fact]
    public async Task SearchAsync_SkipsRankedResultsWithoutCandidates()
    {
        var pipeline = new StubHybridRetrievalPipeline(new[]
        {
            new RankedSearchResult(1, "empty.txt", 50, Array.Empty<SearchCandidate>()),
            new RankedSearchResult(
                2,
                "hit.txt",
                25,
                new[] { new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "hit.txt", "match", 8) }),
        });
        var searcher = new HybridSearcher(pipeline);

        var hit = Assert.Single(await CollectAsync(searcher));

        Assert.Equal("hit.txt", hit.Path);
    }

    private static async Task<IReadOnlyList<Hit>> CollectAsync(IHybridSearcher searcher)
    {
        var request = new SearchRequest(new TermQuery("match"), new[] { @"C:\docs" }, new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private sealed class StubHybridRetrievalPipeline : IHybridRetrievalPipeline
    {
        private readonly IReadOnlyList<RankedSearchResult> _results;

        public StubHybridRetrievalPipeline(IReadOnlyList<RankedSearchResult> results) =>
            _results = results;

        public Task<IReadOnlyList<RankedSearchResult>> SearchAsync(
            SearchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_results);
    }
}
