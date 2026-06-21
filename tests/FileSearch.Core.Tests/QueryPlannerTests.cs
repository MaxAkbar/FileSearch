using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class QueryPlannerTests
{
    [Fact]
    public void CreatePlan_TermContent_UsesInstantMetadataAndDeepLexical()
    {
        var plan = CreatePlan(new TermQuery("needle"));

        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Metadata));
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Lexical));
        Assert.False(plan.HasProvider(CandidateProviderKind.Regex));
        Assert.True(plan.EnabledLayers.HasFlag(RetrievalLayer.Instant));
        Assert.True(plan.EnabledLayers.HasFlag(RetrievalLayer.Deep));
    }

    [Fact]
    public void CreatePlan_FileNameTarget_UsesMetadataOnly()
    {
        var plan = CreatePlan(new TermQuery("report"), SearchTarget.FileNames);

        Assert.Equal(CandidateProviderKind.Metadata, plan.EnabledProviders);
        Assert.Equal(RetrievalLayer.Instant, plan.EnabledLayers);
        Assert.Contains(plan.Explanations, explanation => explanation.Code == "metadata-target");
    }

    [Fact]
    public void CreatePlan_RegexMode_UsesRegexProvider()
    {
        var plan = CreatePlan(new TermQuery("TODO|FIXME"), mode: QueryMode.Regex);

        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Metadata));
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Lexical));
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Regex));
    }

    [Fact]
    public void CreatePlan_RegexQuery_UsesRegexProvider()
    {
        var plan = CreatePlan(new RegexQuery("TODO|FIXME"));

        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Metadata));
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Regex));
        Assert.False(plan.HasEnabledProvider(CandidateProviderKind.Lexical));
    }

    [Fact]
    public void CreatePlan_FuzzyQuery_UsesFuzzyProvider()
    {
        var plan = CreatePlan(new FuzzyQuery("invoice"));

        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Metadata));
        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Fuzzy));
        Assert.False(plan.HasEnabledProvider(CandidateProviderKind.Lexical));
    }

    [Fact]
    public void CreatePlan_UnavailableSemantic_AddsDisabledSmartProvider()
    {
        var query = new UnifiedQueryParser().Parse("semantic:\"authentication migration\"");

        var plan = CreatePlan(query);
        var semanticProvider = plan.GetProvider(CandidateProviderKind.Semantic);

        Assert.NotNull(semanticProvider);
        Assert.False(semanticProvider.IsEnabled);
        Assert.True(plan.HasProvider(CandidateProviderKind.Semantic));
        Assert.False(plan.HasEnabledProvider(CandidateProviderKind.Semantic));
        Assert.Equal(UnifiedQuery.SemanticUnavailableMessage, semanticProvider.Explanation);
        Assert.Contains(
            plan.Explanations,
            explanation =>
                explanation.Code == "semantic-unavailable" &&
                explanation.Severity == SearchExplanationSeverity.Disabled);
    }

    [Fact]
    public void CreatePlan_OcrEnabled_AddsSmartOcrProvider()
    {
        var plan = CreatePlan(new TermQuery("needle"), options: new WalkerOptions { EnableOcr = true });

        Assert.True(plan.HasEnabledProvider(CandidateProviderKind.Ocr));
        Assert.True(plan.EnabledLayers.HasFlag(RetrievalLayer.Smart));
        Assert.Contains(plan.Explanations, explanation => explanation.Code == "ocr-enabled");
    }

    [Fact]
    public void FromHit_PreservesResultData()
    {
        var highlights = new[] { new MatchSpan(3, 6) };
        var anchor = new SourceAnchor(SourceAnchorKind.Text, "line 42", Line: 42, Column: 7);
        var hit = new Hit(
            @"C:\docs\report.txt",
            42,
            "the needle line",
            highlights,
            Score: 12.5,
            SizeBytes: 1024,
            ModifiedUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Route: HitRoute.Indexed,
            Anchor: anchor);

        var candidate = SearchCandidate.FromHit(hit, CandidateProviderKind.Lexical, "fts");

        Assert.Equal(CandidateProviderKind.Lexical, candidate.Provider);
        Assert.Equal("fts", candidate.ProviderId);
        Assert.Equal(hit.Path, candidate.Path);
        Assert.Equal(hit.LineContent, candidate.DisplayText);
        Assert.Equal(hit.Score, candidate.Score);
        Assert.Equal(hit.LineNumber, candidate.LineNumber);
        Assert.Same(highlights, candidate.Highlights);
        Assert.Equal(hit.SizeBytes, candidate.SizeBytes);
        Assert.Equal(hit.ModifiedUtc, candidate.ModifiedUtc);
        Assert.Equal(hit.Route, candidate.Route);
        Assert.Equal(anchor, candidate.Anchor);
    }

    [Fact]
    public void RankedSearchResult_BestCandidate_UsesHighestScore()
    {
        var low = new SearchCandidate(CandidateProviderKind.Metadata, "metadata", "a.txt", "name", 0.25);
        var high = new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "a.txt", "content", 0.75);

        var result = new RankedSearchResult(1, "a.txt", 1, new[] { low, high });

        Assert.Same(high, result.BestCandidate);
    }

    [Fact]
    public void AddFileSearchCore_RegistersQueryPlanner()
    {
        var services = new ServiceCollection().AddFileSearchCore();

        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IQueryPlanner) &&
                service.ImplementationType == typeof(QueryPlanner));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IHybridRetrievalPipeline) &&
                service.ImplementationType == typeof(HybridRetrievalPipeline));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IHybridSearcher) &&
                service.ImplementationType == typeof(HybridSearcher));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ISearcher) &&
                service.ImplementationFactory is not null);
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IResultFusion) &&
                service.ImplementationType == typeof(WeightedResultFusion));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IReranker) &&
                service.ImplementationType == typeof(PassthroughReranker));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(MetadataCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(LexicalCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(RegexCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(FuzzyCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(IndexedMetadataCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(IndexedLexicalCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(IndexedRegexCandidateProvider));
        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(ICandidateProvider) &&
                service.ImplementationType == typeof(IndexedFuzzyCandidateProvider));
    }

    private static SearchPlan CreatePlan(
        Query query,
        SearchTarget target = SearchTarget.Content,
        QueryMode? mode = null,
        WalkerOptions? options = null)
    {
        var request = new SearchRequest(
            query,
            new[] { @"C:\docs" },
            options ?? new WalkerOptions(),
            Mode: mode,
            SearchTarget: target);

        return new QueryPlanner().CreatePlan(request);
    }
}
