using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ConfigurableSearcherTests
{
    [Fact]
    public async Task SearchAsync_UsesLegacySearcherByDefault()
    {
        var legacy = new StubSearcher(new Hit("legacy.txt", 1, "legacy", Array.Empty<MatchSpan>()));
        var hybrid = new StubHybridSearcher(new Hit("hybrid.txt", 1, "hybrid", Array.Empty<MatchSpan>()));
        var searcher = new ConfigurableSearcher(legacy, hybrid);

        var hit = Assert.Single(await CollectAsync(searcher));

        Assert.Equal("legacy.txt", hit.Path);
        Assert.True(legacy.WasCalled);
        Assert.False(hybrid.WasCalled);
    }

    [Fact]
    public async Task SearchAsync_UsesHybridSearcherWhenConfigured()
    {
        var legacy = new StubSearcher(new Hit("legacy.txt", 1, "legacy", Array.Empty<MatchSpan>()));
        var hybrid = new StubHybridSearcher(new Hit("hybrid.txt", 1, "hybrid", Array.Empty<MatchSpan>()));
        var searcher = new ConfigurableSearcher(
            legacy,
            hybrid,
            new SearchOptions { EngineMode = SearchEngineMode.Hybrid });

        var hit = Assert.Single(await CollectAsync(searcher));

        Assert.Equal("hybrid.txt", hit.Path);
        Assert.False(legacy.WasCalled);
        Assert.True(hybrid.WasCalled);
    }

    [Fact]
    public void SearchOptions_DefaultsToLegacyEngine()
    {
        Assert.Equal(SearchEngineMode.Legacy, new SearchOptions().EngineMode);
    }

    private static async Task<IReadOnlyList<Hit>> CollectAsync(ISearcher searcher)
    {
        var request = new SearchRequest(new TermQuery("match"), new[] { @"C:\docs" }, new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private class StubSearcher : ISearcher
    {
        private readonly IReadOnlyList<Hit> _hits;

        public StubSearcher(params Hit[] hits) => _hits = hits;

        public bool WasCalled { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WasCalled = true;
            await Task.Yield();

            foreach (var hit in _hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return hit;
            }
        }
    }

    private sealed class StubHybridSearcher : StubSearcher, IHybridSearcher
    {
        public StubHybridSearcher(params Hit[] hits)
            : base(hits)
        {
        }
    }
}
