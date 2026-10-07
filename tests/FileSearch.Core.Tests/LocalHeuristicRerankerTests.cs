using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class LocalHeuristicRerankerTests
{
    [Fact]
    public async Task RerankAsync_SemanticModePreservesSimilarityDespiteFilenameKeywordBoost()
    {
        var plan = new QueryPlanner().CreatePlan(new SearchRequest(
            new QueryFactory().Build("query", QueryMode.Semantic, false),
            new[] { @"C:\Docs" }, new WalkerOptions(), UseIndex: true, Mode: QueryMode.Semantic));
        var results = new[]
        {
            new RankedSearchResult(1, @"C:\Docs\BENCHMARKS.md", 0.59,
            [
                new SearchCandidate(CandidateProviderKind.Semantic, "semantic", @"C:\Docs\BENCHMARKS.md", "search performance", 0.59),
            ]),
            new RankedSearchResult(2, @"C:\Docs\query.txt", 0.50,
            [
                new SearchCandidate(CandidateProviderKind.Semantic, "semantic", @"C:\Docs\query.txt", "unrelated query", 0.50),
            ]),
        };

        var reranked = await new LocalHeuristicReranker().RerankAsync(plan, results, TestContext.Current.CancellationToken);

        Assert.Same(results, reranked);
        Assert.Equal(@"C:\Docs\BENCHMARKS.md", reranked[0].Path);
        Assert.DoesNotContain(reranked.SelectMany(result => result.Explanations), explanation => explanation.Code == "local-reranker");
    }

    [Fact]
    public async Task RerankAsync_BoostsFilenameMatchesAndRenumbersResults()
    {
        var reranker = new LocalHeuristicReranker();
        var plan = new QueryPlanner().CreatePlan(new SearchRequest(
            new TermQuery("invoice"),
            new[] { @"C:\Docs" },
            new WalkerOptions()));
        var results = new[]
        {
            new RankedSearchResult(
                1,
                @"C:\Docs\notes.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Lexical, "lexical", @"C:\Docs\notes.txt", "ordinary text", 1),
                }),
            new RankedSearchResult(
                2,
                @"C:\Docs\invoice.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Metadata, "metadata", @"C:\Docs\invoice.txt", "invoice.txt", 1),
                }),
        };

        var reranked = await reranker.RerankAsync(
            plan,
            results,
            TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Docs\invoice.txt", reranked[0].Path);
        Assert.Equal(1, reranked[0].Rank);
        Assert.Equal(2, reranked[1].Rank);
        Assert.Contains(reranked[0].Explanations, explanation => explanation.Code == "local-reranker");
    }

    [Fact]
    public async Task RerankAsync_UsesUnifiedMetadataAndSemanticTerms()
    {
        var reranker = new LocalHeuristicReranker();
        var plan = new QueryPlanner().CreatePlan(new SearchRequest(
            new UnifiedQueryParser().Parse("name:auth semantic:\"migration plan\""),
            new[] { @"C:\Docs" },
            new WalkerOptions(),
            UseIndex: true));
        var results = new[]
        {
            new RankedSearchResult(
                1,
                @"C:\Docs\billing.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Semantic, "semantic", @"C:\Docs\billing.txt", "migration plan", 1),
                }),
            new RankedSearchResult(
                2,
                @"C:\Docs\auth-notes.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Metadata, "metadata", @"C:\Docs\auth-notes.txt", "auth-notes.txt", 1),
                }),
        };

        var reranked = await reranker.RerankAsync(
            plan,
            results,
            TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Docs\auth-notes.txt", reranked[0].Path);
    }

    [Fact]
    public async Task RerankAsync_WhenDisabled_ReturnsFusedOrder()
    {
        var reranker = new LocalHeuristicReranker(new LocalRerankerOptions { IsEnabled = false });
        var plan = new QueryPlanner().CreatePlan(new SearchRequest(
            new TermQuery("invoice"),
            new[] { @"C:\Docs" },
            new WalkerOptions()));
        var results = new[]
        {
            new RankedSearchResult(
                1,
                @"C:\Docs\notes.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Lexical, "lexical", @"C:\Docs\notes.txt", "ordinary text", 1),
                }),
            new RankedSearchResult(
                2,
                @"C:\Docs\invoice.txt",
                1,
                new[]
                {
                    new SearchCandidate(CandidateProviderKind.Metadata, "metadata", @"C:\Docs\invoice.txt", "invoice.txt", 1),
                }),
        };

        var reranked = await reranker.RerankAsync(
            plan,
            results,
            TestContext.Current.CancellationToken);

        Assert.Same(results, reranked);
    }
}
