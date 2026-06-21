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
    public async Task IndexedLexicalProvider_AttachesGeneratedSnippet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snippet = new SearchSnippet("before\nneedle\nafter", contentUnitId: 15, contentUnitIds: new long[] { 14, 15, 16 });
        var index = new StubIndexSearch(
            covered: true,
            new Hit("indexed.txt", 3, "needle", Array.Empty<MatchSpan>(), Route: HitRoute.Indexed, ContentUnitId: 15));
        var provider = new IndexedLexicalCandidateProvider(
            index,
            new IndexCoverageService(index),
            new StubSnippetGenerator(snippet));

        var candidate = Assert.Single(await CollectAsync(
            provider,
            CreatePlan(new TermQuery("needle"), useIndex: true),
            cancellationToken));

        Assert.Same(snippet, candidate.Snippet);
        Assert.Equal(15, candidate.ContentUnitId);
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
    public async Task SemanticProvider_ReportsUnavailableWhenNoEmbedderIsConfigured()
    {
        var provider = new SemanticCandidateProvider(
            new UnavailableTextEmbedder(),
            new InMemoryVectorIndex(),
            new StubContentUnitReader());

        var availability = await provider.GetAvailabilityAsync(
            CreateSemanticPlan(),
            TestContext.Current.CancellationToken);

        Assert.False(availability.IsAvailable);
        Assert.Contains("No local embedding model", availability.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SemanticProvider_ReturnsVectorCandidatesWhenEmbedderIsAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var model = new EmbeddingModelInfo("test-embedding", "1", 2);
        var locator = new SourceLocator(StartLine: 8, EndLine: 8, DisplayText: "line 8");
        var vectorIndex = new InMemoryVectorIndex();
        await vectorIndex.UpsertAsync(
            new[]
            {
                new VectorDocument(
                    "chunk-auth",
                    VectorDocumentKind.ContentChunk,
                    fileId: 42,
                    new long[] { 7 },
                    new float[] { 1, 0 },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "checksum",
                    locator),
            },
            cancellationToken);
        var provider = new SemanticCandidateProvider(
            new StubTextEmbedder(model, new float[] { 1, 0 }),
            vectorIndex,
            new StubContentUnitReader(
                new Dictionary<long, string> { [42] = @"C:\docs\auth.md" },
                new ContentUnit(
                    7,
                    42,
                    ContentUnitKind.Text,
                    locator,
                    "authentication migration plan",
                    "hash",
                    "en",
                    "plain",
                    "1")));

        var candidate = Assert.Single(await CollectAsync(
            provider,
            CreateSemanticPlan(),
            cancellationToken));

        Assert.Equal(CandidateProviderKind.Semantic, candidate.Provider);
        Assert.Equal("semantic-vector", candidate.ProviderId);
        Assert.Equal(@"C:\docs\auth.md", candidate.Path);
        Assert.Equal("authentication migration plan", candidate.DisplayText);
        Assert.Equal(8, candidate.LineNumber);
        Assert.Equal(7, candidate.ContentUnitId);
        Assert.Equal(locator, candidate.Locator);
        Assert.Equal(HitRoute.Indexed, candidate.Route);
        Assert.Single(candidate.Explanations);
    }

    [Fact]
    public async Task SemanticProvider_SearchesChunksWithinBestFileVectorsFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var model = new EmbeddingModelInfo("test-embedding", "1", 2);
        var authLocator = new SourceLocator(StartLine: 4, EndLine: 4, DisplayText: "line 4");
        var billingLocator = new SourceLocator(StartLine: 9, EndLine: 9, DisplayText: "line 9");
        var vectorIndex = new InMemoryVectorIndex();
        await vectorIndex.UpsertAsync(
            new[]
            {
                new VectorDocument(
                    "file-auth",
                    VectorDocumentKind.File,
                    fileId: 1,
                    new long[] { 11 },
                    new float[] { 1, 0 },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "file-auth-checksum"),
                new VectorDocument(
                    "chunk-auth",
                    VectorDocumentKind.ContentChunk,
                    fileId: 1,
                    new long[] { 11 },
                    new float[] { 0.7f, 0.3f },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "chunk-auth-checksum",
                    authLocator),
                new VectorDocument(
                    "file-billing",
                    VectorDocumentKind.File,
                    fileId: 2,
                    new long[] { 22 },
                    new float[] { 0, 1 },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "file-billing-checksum"),
                new VectorDocument(
                    "chunk-billing",
                    VectorDocumentKind.ContentChunk,
                    fileId: 2,
                    new long[] { 22 },
                    new float[] { 1, 0 },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "chunk-billing-checksum",
                    billingLocator),
            },
            cancellationToken);
        var provider = new SemanticCandidateProvider(
            new StubTextEmbedder(model, new float[] { 1, 0 }),
            vectorIndex,
            new StubContentUnitReader(
                new Dictionary<long, string>
                {
                    [1] = @"C:\docs\auth.md",
                    [2] = @"C:\docs\billing.md",
                },
                new ContentUnit(
                    11,
                    1,
                    ContentUnitKind.Text,
                    authLocator,
                    "authentication migration plan",
                    "hash-11",
                    "en",
                    "plain",
                    "1"),
                new ContentUnit(
                    22,
                    2,
                    ContentUnitKind.Text,
                    billingLocator,
                    "billing migration plan",
                    "hash-22",
                    "en",
                    "plain",
                    "1")));

        var candidate = Assert.Single(await CollectAsync(
            provider,
            CreateSemanticPlan(),
            cancellationToken));

        Assert.Equal(@"C:\docs\auth.md", candidate.Path);
        Assert.Equal(11, candidate.ContentUnitId);
        Assert.Equal("authentication migration plan", candidate.DisplayText);
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

    private SearchPlan CreateSemanticPlan() =>
        new(
            CreateRequest(new UnifiedQueryParser().Parse("semantic:\"authentication migration\""), useIndex: true),
            new[]
            {
                new SearchProviderPlan(CandidateProviderKind.Semantic, RetrievalLayer.Smart),
            });

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

    private sealed class StubSnippetGenerator : ISnippetGenerator
    {
        private readonly SearchSnippet _snippet;

        public StubSnippetGenerator(SearchSnippet snippet) =>
            _snippet = snippet;

        public Task<SearchSnippet> GenerateAsync(
            SearchRequest request,
            Hit hit,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snippet);
    }

    private sealed class StubTextEmbedder : ITextEmbedder
    {
        private readonly EmbeddingModelInfo _model;
        private readonly float[] _vector;

        public StubTextEmbedder(EmbeddingModelInfo model, float[] vector)
        {
            _model = model;
            _vector = vector;
        }

        public Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TextEmbedderAvailability.Available);

        public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(new TextEmbedding(_vector, _model));
    }

    private sealed class StubContentUnitReader : IContentUnitReader
    {
        private readonly IReadOnlyDictionary<long, string> _paths;
        private readonly IReadOnlyDictionary<long, ContentUnit> _units;

        public StubContentUnitReader()
            : this(new Dictionary<long, string>(), Array.Empty<ContentUnit>())
        {
        }

        public StubContentUnitReader(
            IReadOnlyDictionary<long, string> paths,
            params ContentUnit[] units)
        {
            _paths = paths;
            _units = units.ToDictionary(unit => unit.Id);
        }

        public Task<ContentUnit?> GetContentUnitAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(_units.TryGetValue(id, out var unit) ? unit : null);

        public Task<string?> GetFilePathAsync(long fileId, CancellationToken cancellationToken) =>
            Task.FromResult(_paths.TryGetValue(fileId, out var path) ? path : null);
    }
}
