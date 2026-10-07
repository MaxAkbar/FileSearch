using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WeightedResultFusionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(50)]
    public async Task Fuse_RepeatedWeakSemanticPassagesDoNotOutrankStrongerMatch(int weakPassageCount)
    {
        var request = new SearchRequest(new QueryFactory().Build("query", QueryMode.Semantic, false),
            new[] { @"C:\docs" }, new WalkerOptions(), UseIndex: true, Mode: QueryMode.Semantic);
        var plan = new QueryPlanner().CreatePlan(request);
        var candidates = Enumerable.Range(0, weakPassageCount)
            .Select(line => new SearchCandidate(CandidateProviderKind.Semantic, "semantic-vector",
                @"C:\docs\license.txt", "license conditions", 0.56, lineNumber: line + 1))
            .Append(new SearchCandidate(CandidateProviderKind.Semantic, "semantic-vector",
                @"C:\docs\license.txt", "license permissions", 0.57, lineNumber: 99))
            .Append(new SearchCandidate(CandidateProviderKind.Semantic, "semantic-vector",
                @"C:\docs\BENCHMARKS.md", "query performance", 0.59, lineNumber: 32))
            .ToArray();

        var fused = new WeightedResultFusion().Fuse(plan, candidates);
        var ranked = await new LocalHeuristicReranker().RerankAsync(plan, fused, TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\docs\BENCHMARKS.md", fused[0].Path);
        Assert.Equal(@"C:\docs\BENCHMARKS.md", ranked[0].Path);
        Assert.Equal(0.59, fused[0].Score, precision: 10);
        Assert.Equal(0.57, fused[1].Score, precision: 10);
        Assert.Single(fused[1].Candidates);
        Assert.Equal(99, fused[1].BestCandidate!.LineNumber);
        Assert.Equal(0.57, ranked[1].Score, precision: 10);
    }

    [Fact]
    public void Fuse_KeepsLexicalMatchesAndProviderWeightsWhileCappingSemanticContribution()
    {
        var request = new SearchRequest(new TermQuery("query"), new[] { @"C:\docs" }, new WalkerOptions());
        var plan = new SearchPlan(request,
        [
            new SearchProviderPlan(CandidateProviderKind.Metadata, RetrievalLayer.Instant, Weight: 2),
            new SearchProviderPlan(CandidateProviderKind.Lexical, RetrievalLayer.Deep),
            new SearchProviderPlan(CandidateProviderKind.Semantic, RetrievalLayer.Smart, Weight: 2),
        ]);
        var candidates = new[]
        {
            new SearchCandidate(CandidateProviderKind.Metadata, "metadata", "document.txt", "name", 0.5),
            new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "document.txt", "query one", 1),
            new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "document.txt", "query two", 2),
            new SearchCandidate(CandidateProviderKind.Semantic, "semantic-vector", "document.txt", "weaker passage", 0.4),
            new SearchCandidate(CandidateProviderKind.Semantic, "semantic-vector", "DOCUMENT.TXT", "stronger passage", 0.7),
        };

        var result = Assert.Single(new WeightedResultFusion().Fuse(plan, candidates));

        Assert.Equal(5.4, result.Score, precision: 10);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(2, result.Candidates.Count(candidate => candidate.Provider == CandidateProviderKind.Lexical));
        Assert.Equal("stronger passage", Assert.Single(result.Candidates, candidate => candidate.Provider == CandidateProviderKind.Semantic).DisplayText);
    }
}
