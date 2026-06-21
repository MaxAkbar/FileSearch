using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class CandidateProviderTests : IDisposable
{
    private readonly string _root;
    private readonly Searcher _searcher;

    public CandidateProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filesearch-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        _searcher = new Searcher(new FileWalker(), registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task MetadataProvider_FindsFileNameMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "needle-report.txt");
        await File.WriteAllTextAsync(path, "ordinary content", cancellationToken);

        var candidates = await CollectAsync(
            new MetadataCandidateProvider(_searcher),
            CreatePlan(new TermQuery("needle")),
            cancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateProviderKind.Metadata, candidate.Provider);
        Assert.Equal(HitKind.Metadata, candidate.Kind);
        Assert.Equal(path, candidate.Path);
    }

    [Fact]
    public async Task LexicalProvider_FindsContentMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "body.txt");
        await File.WriteAllTextAsync(path, "first line\nneedle content\n", cancellationToken);

        var candidates = await CollectAsync(
            new LexicalCandidateProvider(_searcher),
            CreatePlan(new TermQuery("needle")),
            cancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateProviderKind.Lexical, candidate.Provider);
        Assert.Equal(HitKind.Content, candidate.Kind);
        Assert.Equal(2, candidate.LineNumber);
        Assert.Equal(path, candidate.Path);
    }

    [Fact]
    public async Task RegexProvider_FindsRegexContentMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "errors.txt");
        await File.WriteAllTextAsync(path, "ERR42 happened\n", cancellationToken);

        var candidates = await CollectAsync(
            new RegexCandidateProvider(_searcher),
            CreatePlan(new RegexQuery(@"ERR\d+")),
            cancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateProviderKind.Regex, candidate.Provider);
        Assert.Equal(path, candidate.Path);
    }

    [Fact]
    public async Task FuzzyProvider_FindsFuzzyContentMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "invoice.txt");
        await File.WriteAllTextAsync(path, "invoce received\n", cancellationToken);

        var candidates = await CollectAsync(
            new FuzzyCandidateProvider(_searcher),
            CreatePlan(new FuzzyQuery("invoice")),
            cancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateProviderKind.Fuzzy, candidate.Provider);
        Assert.Equal(path, candidate.Path);
    }

    [Fact]
    public async Task Provider_SkipsWhenPlanDoesNotEnableProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "body.txt"), "needle\n", cancellationToken);

        var candidates = await CollectAsync(
            new RegexCandidateProvider(_searcher),
            CreatePlan(new TermQuery("needle")),
            cancellationToken);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task IndexedLexicalProvider_ReturnsIndexedCandidatesWhenCovered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var index = new StubIndexSearch(
            covered: true,
            new Hit("indexed.txt", 3, "needle", Array.Empty<MatchSpan>(), Route: HitRoute.Indexed));
        var provider = new IndexedLexicalCandidateProvider(index, new IndexCoverageService(index));

        var candidates = await CollectAsync(
            provider,
            CreatePlan(new TermQuery("needle"), useIndex: true),
            cancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(CandidateProviderKind.Lexical, candidate.Provider);
        Assert.Equal("indexed-lexical", candidate.ProviderId);
        Assert.Equal(HitRoute.Indexed, candidate.Route);
        Assert.True(index.SearchWasUsed);
    }

    [Fact]
    public async Task IndexedLexicalProvider_SkipsWhenCoverageIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var index = new StubIndexSearch(
            covered: false,
            new Hit("indexed.txt", 3, "needle", Array.Empty<MatchSpan>(), Route: HitRoute.Indexed));
        var provider = new IndexedLexicalCandidateProvider(index, new IndexCoverageService(index));

        var candidates = await CollectAsync(
            provider,
            CreatePlan(new TermQuery("needle"), useIndex: true),
            cancellationToken);

        Assert.Empty(candidates);
        Assert.False(index.SearchWasUsed);
    }

    [Fact]
    public void WeightedFusion_GroupsCandidatesByPathAndAppliesWeights()
    {
        var request = CreateRequest(new TermQuery("needle"));
        var plan = new SearchPlan(
            request,
            new[]
            {
                new SearchProviderPlan(CandidateProviderKind.Metadata, RetrievalLayer.Instant, Weight: 2),
                new SearchProviderPlan(CandidateProviderKind.Lexical, RetrievalLayer.Deep),
            });
        var candidates = new[]
        {
            new SearchCandidate(CandidateProviderKind.Metadata, "metadata", "a.txt", "name", 10),
            new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "a.txt", "content", 5),
            new SearchCandidate(CandidateProviderKind.Lexical, "lexical", "b.txt", "content", 20),
        };

        var results = new WeightedResultFusion().Fuse(plan, candidates);

        Assert.Equal(2, results.Count);
        Assert.Equal("a.txt", results[0].Path);
        Assert.Equal(25, results[0].Score);
        Assert.Equal(2, results[0].Candidates.Count);
        Assert.Equal(1, results[0].Rank);
        Assert.Equal(2, results[1].Rank);
    }

    [Fact]
    public async Task HybridPipeline_RunsOnlyEnabledProvidersAndFusesResults()
    {
        var metadata = new StubCandidateProvider(
            CandidateProviderKind.Metadata,
            new SearchCandidate(CandidateProviderKind.Metadata, "metadata", "a.txt", "name", 10));
        var fuzzy = new StubCandidateProvider(
            CandidateProviderKind.Fuzzy,
            new SearchCandidate(CandidateProviderKind.Fuzzy, "fuzzy", "b.txt", "content", 10));
        var pipeline = new HybridRetrievalPipeline(
            new QueryPlanner(),
            new ICandidateProvider[] { metadata, fuzzy },
            new WeightedResultFusion(),
            new PassthroughReranker());

        var results = await pipeline.SearchAsync(
            CreateRequest(new TermQuery("needle")),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("a.txt", result.Path);
        Assert.True(metadata.WasCalled);
        Assert.False(fuzzy.WasCalled);
    }

    [Fact]
    public async Task HybridPipeline_PrefersIndexedProviderWhenIndexIsAvailable()
    {
        var live = new StubCandidateProvider(
            CandidateProviderKind.Lexical,
            CandidateProviderRoute.Live,
            CandidateProviderAvailability.Available,
            new SearchCandidate(CandidateProviderKind.Lexical, "live", "live.txt", "content", 10));
        var indexed = new StubCandidateProvider(
            CandidateProviderKind.Lexical,
            CandidateProviderRoute.Indexed,
            CandidateProviderAvailability.Available,
            new SearchCandidate(CandidateProviderKind.Lexical, "indexed", "indexed.txt", "content", 10));
        var pipeline = new HybridRetrievalPipeline(
            new QueryPlanner(),
            new ICandidateProvider[] { live, indexed },
            new WeightedResultFusion(),
            new PassthroughReranker());

        var results = await pipeline.SearchAsync(
            CreateRequest(new TermQuery("needle"), useIndex: true),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("indexed.txt", result.Path);
        Assert.False(live.WasCalled);
        Assert.True(indexed.WasCalled);
    }

    [Fact]
    public async Task HybridPipeline_FallsBackToLiveProviderWhenIndexIsUnavailable()
    {
        var live = new StubCandidateProvider(
            CandidateProviderKind.Lexical,
            CandidateProviderRoute.Live,
            CandidateProviderAvailability.Available,
            new SearchCandidate(CandidateProviderKind.Lexical, "live", "live.txt", "content", 10));
        var indexed = new StubCandidateProvider(
            CandidateProviderKind.Lexical,
            CandidateProviderRoute.Indexed,
            CandidateProviderAvailability.Unavailable("index missing"),
            new SearchCandidate(CandidateProviderKind.Lexical, "indexed", "indexed.txt", "content", 10));
        var pipeline = new HybridRetrievalPipeline(
            new QueryPlanner(),
            new ICandidateProvider[] { live, indexed },
            new WeightedResultFusion(),
            new PassthroughReranker());

        var results = await pipeline.SearchAsync(
            CreateRequest(new TermQuery("needle"), useIndex: true),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("live.txt", result.Path);
        Assert.True(live.WasCalled);
        Assert.False(indexed.WasCalled);
    }

    private SearchPlan CreatePlan(Query query, bool useIndex = false) =>
        new QueryPlanner().CreatePlan(CreateRequest(query, useIndex));

    private SearchRequest CreateRequest(Query query, bool useIndex = false) =>
        new(query, new[] { _root }, new WalkerOptions(), UseIndex: useIndex);

    private static async Task<IReadOnlyList<SearchCandidate>> CollectAsync(
        ICandidateProvider provider,
        SearchPlan plan,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SearchCandidate>();
        await foreach (var candidate in provider.FindAsync(plan, cancellationToken))
            candidates.Add(candidate);
        return candidates;
    }

    private sealed class StubCandidateProvider : IRoutedCandidateProvider
    {
        private readonly IReadOnlyList<SearchCandidate> _candidates;
        private readonly CandidateProviderAvailability _availability;

        public StubCandidateProvider(CandidateProviderKind provider, params SearchCandidate[] candidates)
            : this(provider, CandidateProviderRoute.Live, CandidateProviderAvailability.Available, candidates)
        {
        }

        public StubCandidateProvider(
            CandidateProviderKind provider,
            CandidateProviderRoute route,
            CandidateProviderAvailability availability,
            params SearchCandidate[] candidates)
        {
            Provider = provider;
            Route = route;
            _availability = availability;
            _candidates = candidates;
        }

        public CandidateProviderKind Provider { get; }

        public CandidateProviderRoute Route { get; }

        public bool WasCalled { get; private set; }

        public Task<CandidateProviderAvailability> GetAvailabilityAsync(
            SearchPlan plan,
            CancellationToken cancellationToken) =>
            Task.FromResult(_availability);

        public async IAsyncEnumerable<SearchCandidate> FindAsync(
            SearchPlan plan,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WasCalled = true;
            await Task.Yield();

            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
            }
        }
    }

    private sealed class StubIndexSearch : IIndexSearch
    {
        private readonly bool _covered;
        private readonly IReadOnlyList<Hit> _hits;

        public StubIndexSearch(bool covered, params Hit[] hits)
        {
            _covered = covered;
            _hits = hits;
        }

        public bool SearchWasUsed { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchWasUsed = true;
            await Task.Yield();

            foreach (var hit in _hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return hit;
            }
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_covered
                ? new IndexCoverage(IndexCoverageStatus.Covered, "stub covered")
                : new IndexCoverage(IndexCoverageStatus.Missing, "stub missing"));
    }
}
